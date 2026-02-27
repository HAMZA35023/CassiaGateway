using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tmds.DBus;

namespace AccessAPP.Services.LinuxBle;

/// <summary>
/// Shared connection + object-discovery helpers used by all Linux BLE services.
/// Holds a single D-Bus system-bus connection for the process lifetime.
///
/// All proxy instances (IAdapter1, IObjectManager, IDevice1, IGattCharacteristic1)
/// are cached so that Tmds.DBus's dynamic proxy-type emitter is only invoked once
/// per interface type.  Re-calling CreateProxy&lt;T&gt; for the same interface type on
/// a subsequent ScanAsync() retry would otherwise throw
/// "Duplicate type name within an assembly".
/// </summary>
internal static class BlueZHelpers
{
    // ── D-Bus connection singleton ────────────────────────────────────────────

    private static Connection? _connection;
    private static readonly SemaphoreSlim _connectSem = new(1, 1);

    /// <summary>Return (or lazily create) the process-lifetime D-Bus system connection.</summary>
    public static async Task<Connection> GetConnectionAsync()
    {
        if (_connection != null) return _connection;

        await _connectSem.WaitAsync();
        try
        {
            if (_connection != null) return _connection;
            var conn = new Connection(Address.System);
            await conn.ConnectAsync();
            _connection = conn;
            return _connection;
        }
        finally
        {
            _connectSem.Release();
        }
    }

    // ── Cached top-level proxies ──────────────────────────────────────────────

    // Per-adapter proxy cache: adapter name (e.g. "hci0") → IAdapter1 proxy.
    private static readonly ConcurrentDictionary<string, IAdapter1> _adapterProxies = new();
    private static IObjectManager? _objectManagerProxy;
    private static readonly SemaphoreSlim _proxySem = new(1, 1);

    // Serialize GetManagedObjectsAsync to reduce transient decode issues under load.
    private static readonly SemaphoreSlim _managedObjectsSem = new(1, 1);

    // ── MAC → adapter tracking ────────────────────────────────────────────────
    // Updated by the scanner whenever a device advertisement is received.
    // Read by the connection service to determine the correct device object path.
    private static readonly ConcurrentDictionary<string, string> _macToAdapter =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Record which HCI adapter last saw a given MAC (called from the scanner).
    /// Does not override an adapter that was pinned by an active connection.
    /// </summary>
    public static void RegisterDeviceAdapter(string mac, string adapter) =>
        _macToAdapter[mac] = adapter;

    // Adapters pinned by an active connection: MAC → adapter used for ConnectAsync.
    // While an entry exists here, GetDeviceAdapter() returns this value regardless
    // of what the scanner has most recently written to _macToAdapter.
    private static readonly ConcurrentDictionary<string, string> _connectedAdapters =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pin the HCI adapter for a device that is now actively connected.
    /// Call this immediately after a successful ConnectAsync so that the
    /// scanner cannot race and overwrite the mapping while the session is live.
    /// </summary>
    public static void SetConnectedAdapter(string mac, string adapter) =>
        _connectedAdapters[mac] = adapter;

    /// <summary>
    /// Release the pinned adapter for a device (call after DisconnectAsync).
    /// </summary>
    public static void ClearConnectedAdapter(string mac) =>
        _connectedAdapters.TryRemove(mac, out _);

    /// <summary>
    /// Best-effort: disconnect any pending BlueZ connect attempt and remove the device
    /// from BlueZ's object tree, forcing a fresh re-discovery on the next scan.
    /// Call this after repeated connect failures to clear stale HCI / D-Bus state.
    /// All errors are swallowed — this is purely opportunistic cleanup.
    /// </summary>
    public static async Task TryRemoveDeviceAsync(string macAddress)
    {
        try
        {
            var adapter = GetDeviceAdapter(macAddress);
            var dp = DevicePath(adapter, macAddress);

            // 1. Cancel any pending BlueZ ConnectAsync by issuing a Disconnect.
            try
            {
                var dev = await GetDeviceAsync(dp);
                using var dCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await dev.DisconnectAsync().WaitAsync(dCts.Token);
            }
            catch { /* device may already be gone or disconnected — ignore */ }

            // 2. Wipe all C# caches for this device path.
            ClearConnectedAdapter(macAddress);
            InvalidateCharCache(dp);

            // 3. Ask BlueZ to remove the device object from its D-Bus tree.
            //    After removal BlueZ will re-add the device as soon as it sees
            //    another advertisement — which is exactly what we want on the
            //    next connect attempt.
            try
            {
                var adp = await GetAdapterAsync(adapter);
                using var rCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await adp.RemoveDeviceAsync(new ObjectPath(dp)).WaitAsync(rCts.Token);
                Console.WriteLine($"[BlueZHelpers] TryRemoveDevice: removed {macAddress} from {adapter}");
            }
            catch (Tmds.DBus.DBusException dbex)
            {
                // "Does Not Exist" is expected if the device was already removed or
                // never discovered in this session — not an error worth logging loudly.
                if (dbex.ErrorName == "org.bluez.Error.DoesNotExist" ||
                    dbex.ErrorName == "org.freedesktop.DBus.Error.UnknownObject")
                {
                    Console.WriteLine($"[BlueZHelpers] TryRemoveDevice: {macAddress} already absent in {adapter} ({dbex.ErrorName})");
                }
                else
                {
                    Console.WriteLine($"[BlueZHelpers] TryRemoveDevice: D-Bus error removing {macAddress}: {dbex.ErrorName} - {dbex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BlueZHelpers] TryRemoveDevice: unexpected exception for {macAddress}: {ex.Message}");
        }
    }

    /// <summary>
    /// Return the HCI adapter to use for <paramref name="mac"/>.
    /// Prefers the connection-pinned adapter (set by <see cref="SetConnectedAdapter"/>),
    /// then the last-scanned adapter, then <see cref="RuntimeVariables.LINUX_BLE_ADAPTER"/>.
    /// </summary>
    public static string GetDeviceAdapter(string mac) =>
        _connectedAdapters.TryGetValue(mac, out var pinned) ? pinned :
        _macToAdapter.TryGetValue(mac, out var scanned) ? scanned :
        RuntimeVariables.LINUX_BLE_ADAPTER;

    // Round-robin counter for spreading parallel connect attempts across HCI adapters.
    private static int _connectRoundRobin = -1;

    /// <summary>
    /// Select the HCI adapter for a new outgoing connect request.
    /// <para>
    /// Priority:
    /// <list type="number">
    ///   <item>Active-connection pin (<see cref="SetConnectedAdapter"/>) — never disturb a live session.</item>
    ///   <item>Least-loaded adapter among <see cref="RuntimeVariables.LINUX_BLE_ADAPTERS"/>, based on
    ///         active connection pins in <c>_connectedAdapters</c>.</item>
    ///   <item>If load ties, prefer the last-scanned adapter for this MAC when available, else
    ///         round-robin across the tied set.</item>
    ///   <item>Last-scanned adapter (<c>_macToAdapter</c>) / global default — single-adapter fallback.</item>
    /// </list>
    /// </para>
    /// </summary>
    public static string GetNextConnectAdapter(string mac)
    {
        // Never change the adapter for a device that is already connected.
        if (_connectedAdapters.TryGetValue(mac, out var pinned))
            return pinned;

        var configuredAdapters = RuntimeVariables.GetLinuxBleAdapterList();
        if (configuredAdapters.Length <= 1)
        {
            // Single-adapter config: keep existing behaviour (last-scanned or default).
            return _macToAdapter.TryGetValue(mac, out var scanned)
                ? scanned
                : RuntimeVariables.LINUX_BLE_ADAPTER;
        }

        var adapters = new List<string>(configuredAdapters.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in configuredAdapters)
        {
            if (string.IsNullOrWhiteSpace(adapter))
                continue;
            if (seen.Add(adapter))
                adapters.Add(adapter);
        }

        if (adapters.Count == 0)
            return RuntimeVariables.LINUX_BLE_ADAPTER;

        if (adapters.Count == 1)
            return adapters[0];

        // Count active pinned sessions per configured adapter.
        var loadByAdapter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
            loadByAdapter[adapter] = 0;

        foreach (var (_, connectedAdapter) in _connectedAdapters)
        {
            if (!string.IsNullOrWhiteSpace(connectedAdapter) &&
                loadByAdapter.TryGetValue(connectedAdapter, out var inUse))
            {
                loadByAdapter[connectedAdapter] = inUse + 1;
            }
        }

        var minLoad = int.MaxValue;
        foreach (var adapter in adapters)
        {
            int inUse = loadByAdapter[adapter];
            if (inUse < minLoad)
                minLoad = inUse;
        }

        var leastLoaded = new List<string>(adapters.Count);
        foreach (var adapter in adapters)
        {
            if (loadByAdapter[adapter] == minLoad)
                leastLoaded.Add(adapter);
        }

        // If the scanner already sees this MAC on one of the least-loaded adapters,
        // keep that path to reduce adapter hopping between retries.
        if (_macToAdapter.TryGetValue(mac, out var scannedAdapter))
        {
            foreach (var adapter in leastLoaded)
            {
                if (string.Equals(adapter, scannedAdapter, StringComparison.OrdinalIgnoreCase))
                    return adapter;
            }
        }

        // Tie-break with round-robin among least-loaded adapters.
        var idx = (uint)Interlocked.Increment(ref _connectRoundRobin) % (uint)leastLoaded.Count;
        return leastLoaded[(int)idx];
    }

    /// <summary>
    /// Return the cached IAdapter1 proxy for the given HCI adapter name.
    /// Omit <paramref name="adapter"/> to use <see cref="RuntimeVariables.LINUX_BLE_ADAPTER"/>.
    /// Created once per adapter name; CreateProxy is never called a second time.
    /// </summary>
    public static async Task<IAdapter1> GetAdapterAsync(string adapter = null)
    {
        adapter ??= RuntimeVariables.LINUX_BLE_ADAPTER;
        if (_adapterProxies.TryGetValue(adapter, out var cached)) return cached;
        await _proxySem.WaitAsync();
        try
        {
            if (_adapterProxies.TryGetValue(adapter, out cached)) return cached;
            var conn = await GetConnectionAsync();
            var adapterPath = new ObjectPath($"/org/bluez/{adapter}");
            var proxy = conn.CreateProxy<IAdapter1>("org.bluez", adapterPath);
            _adapterProxies[adapter] = proxy;
            return proxy;
        }
        finally { _proxySem.Release(); }
    }

    private static void InvalidateAdapterProxy(string adapter)
    {
        if (string.IsNullOrWhiteSpace(adapter))
            return;
        _adapterProxies.TryRemove(adapter, out _);
    }

    /// <summary>
    /// Best-effort guard to ensure LE discovery is running on the adapter.
    /// Useful when BlueZ reports a device path as UnknownObject and we need
    /// ObjectManager to rediscover the device path quickly.
    /// </summary>
    public static async Task EnsureDiscoveryRunningAsync(
        string adapter,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        adapter = string.IsNullOrWhiteSpace(adapter)
            ? RuntimeVariables.LINUX_BLE_ADAPTER
            : adapter;

        var adapterObj = await GetAdapterAsync(adapter);

        static bool IsRecoverableStartError(Tmds.DBus.DBusException ex) =>
            ex.ErrorName == "org.bluez.Error.Failed" ||
            ex.ErrorName == "org.bluez.Error.NotReady" ||
            ex.ErrorName == "org.bluez.Error.NotAuthorized";

        async Task<bool> IsAlreadyDiscoveringAsync(IAdapter1 adp)
        {
            try
            {
                using var discoveringCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                discoveringCts.CancelAfter(TimeSpan.FromSeconds(2));
                return await adp.GetAsync<bool>("Discovering").WaitAsync(discoveringCts.Token);
            }
            catch
            {
                return false;
            }
        }

        async Task TrySetDiscoveryFilterAsync(IAdapter1 adp)
        {
            try
            {
                await adp.SetDiscoveryFilterAsync(new Dictionary<string, object>
                {
                    ["Transport"] = "le",
                    ["DuplicateData"] = true
                });
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "LinuxBLE scan: SetDiscoveryFilter failed on {Adapter}; continuing", adapter);
            }
        }

        // Fast path: avoid churn when discovery is already active.
        if (await IsAlreadyDiscoveringAsync(adapterObj))
        {
            logger?.LogDebug("LinuxBLE scan: discovery already active on {Adapter}", adapter);
            return;
        }

        await TrySetDiscoveryFilterAsync(adapterObj);

        try
        {
            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startCts.CancelAfter(TimeSpan.FromSeconds(5));
            await adapterObj.StartDiscoveryAsync().WaitAsync(startCts.Token);
            logger?.LogInformation("LinuxBLE scan: discovery started on {Adapter}", adapter);
            return;
        }
        catch (Tmds.DBus.DBusException ex) when (ex.ErrorName == "org.bluez.Error.InProgress")
        {
            if (await IsAlreadyDiscoveringAsync(adapterObj))
            {
                logger?.LogDebug("LinuxBLE scan: discovery already active on {Adapter}", adapter);
                return;
            }

            logger?.LogWarning(
                "LinuxBLE scan: StartDiscovery returned InProgress but Discovering=false on {Adapter}; forcing restart",
                adapter);
        }
        catch (Tmds.DBus.DBusException ex) when (IsRecoverableStartError(ex))
        {
            if (await IsAlreadyDiscoveringAsync(adapterObj))
            {
                logger?.LogDebug(
                    "LinuxBLE scan: StartDiscovery returned {ErrorName} on {Adapter} but Discovering=true; continuing",
                    ex.ErrorName, adapter);
                return;
            }

            logger?.LogWarning(
                "LinuxBLE scan: StartDiscovery transient failure on {Adapter} ({ErrorName}) — forcing restart",
                adapter, ex.ErrorName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "LinuxBLE scan: StartDiscovery failed on {Adapter}; forcing restart", adapter);
        }

        // Restart path (best effort): stop, refresh adapter proxy, and start again.
        try
        {
            using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stopCts.CancelAfter(TimeSpan.FromSeconds(2));
            await adapterObj.StopDiscoveryAsync().WaitAsync(stopCts.Token);
        }
        catch
        {
            // best-effort
        }

        try
        {
            await Task.Delay(150, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        InvalidateAdapterProxy(adapter);
        adapterObj = await GetAdapterAsync(adapter);
        await TrySetDiscoveryFilterAsync(adapterObj);

        using var restartCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        restartCts.CancelAfter(TimeSpan.FromSeconds(5));
        await adapterObj.StartDiscoveryAsync().WaitAsync(restartCts.Token);

        if (await IsAlreadyDiscoveringAsync(adapterObj))
            logger?.LogInformation("LinuxBLE scan: discovery restarted on {Adapter}", adapter);
        else
            logger?.LogWarning("LinuxBLE scan: discovery restart issued on {Adapter} but Discovering=false", adapter);
    }

    /// <summary>
    /// Return the cached IObjectManager proxy for the BlueZ root ("/").
    /// Created once; CreateProxy is never called a second time for this interface.
    /// </summary>
    public static async Task<IObjectManager> GetObjectManagerAsync()
    {
        if (_objectManagerProxy != null) return _objectManagerProxy;
        await _proxySem.WaitAsync();
        try
        {
            if (_objectManagerProxy != null) return _objectManagerProxy;
            var conn = await GetConnectionAsync();
            _objectManagerProxy = conn.CreateProxy<IObjectManager>("org.bluez", new ObjectPath("/"));
            return _objectManagerProxy;
        }
        finally { _proxySem.Release(); }
    }

    /// <summary>
    /// Safely fetch a snapshot of BlueZ managed objects.
    /// This call is expensive; we serialize it to avoid transient decode corruption
    /// when multiple threads query the object tree concurrently.
    /// </summary>
    public static async Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>>
        GetManagedObjectsSafeAsync(CancellationToken ct = default)
    {
        var objMgr = await GetObjectManagerAsync();
        await _managedObjectsSem.WaitAsync(ct);
        try
        {
            return await objMgr.GetManagedObjectsAsync();
        }
        finally
        {
            _managedObjectsSem.Release();
        }
    }

    // ── Per-device proxy cache ────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, IDevice1> _deviceProxies = new();

    /// <summary>
    /// Return a cached IDevice1 proxy for the given BlueZ device object path.
    /// </summary>
    public static async Task<IDevice1> GetDeviceAsync(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            throw new ArgumentNullException(nameof(devicePath));
        if (_deviceProxies.TryGetValue(devicePath, out var cached)) return cached;
        var conn = await GetConnectionAsync();
        var proxy = conn.CreateProxy<IDevice1>("org.bluez", new ObjectPath(devicePath));
        _deviceProxies[devicePath] = proxy;
        return proxy;
    }

    // ── Per-characteristic proxy cache ────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, IGattCharacteristic1> _charProxies = new();

    // characteristic handle cache: devicePath → (handle → objectPath)
    private static readonly ConcurrentDictionary<string, Dictionary<int, ObjectPath>> _charCache = new();

    /// <summary>
    /// Find the D-Bus object path of a GATT characteristic by its GATT handle number.
    /// Results are cached per device path and invalidated by <see cref="InvalidateCharCache"/>.
    /// </summary>
    public static async Task<ObjectPath?> FindCharacteristicByHandleAsync(string devicePath, int handle)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return null;
        if (handle <= 0) return null;
        if (_charCache.TryGetValue(devicePath, out var cached) && cached.TryGetValue(handle, out var path))
            return path;

        var objMgr = await GetObjectManagerAsync();
        // BlueZ may transiently expose an invalid/empty object path while services are being
        // added/removed (e.g., right after enabling notifications or switching modes). Tmds.DBus
        // will throw ArgumentNullException when decoding such entries. Retry briefly.
        IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>> objects;
        const int maxAttempts = 5;
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                objects = await GetManagedObjectsSafeAsync();
                break;
            }
            catch (ArgumentNullException ex) when (attempt < maxAttempts)
            {
                Console.WriteLine($"[BlueZHelpers] GetManagedObjectsAsync decode failed (attempt {attempt}/{maxAttempts}) for {devicePath}: {ex.Message}");
                // Clear any partial mappings and retry.
                ClearDeviceCache(devicePath);
                await Task.Delay(100);
            }
        }

        var map = new Dictionary<int, ObjectPath>();
        foreach (var (objPath, interfaces) in objects)
        {
            var pathStr = objPath.ToString();
            if (!pathStr.StartsWith(devicePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!interfaces.TryGetValue("org.bluez.GattCharacteristic1", out var props)) continue;

            if (props.TryGetValue("Handle", out var rawHandle))
            {
                try
                {
                    int h = Convert.ToInt32(rawHandle);
                    map[h] = objPath;
                }
                catch { /* skip non-numeric handles */ }
            }
        }

        _charCache[devicePath] = map;
        map.TryGetValue(handle, out var result);
        return result == default ? null : result;
    }

    /// <summary>
    /// Return a proxy for the GATT characteristic with the given handle on a device,
    /// or null if not found.
    /// </summary>
    public static async Task<IGattCharacteristic1?> GetCharacteristicAsync(string devicePath, int handle)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return null;
        if (handle <= 0) return null;
        var charPath = await FindCharacteristicByHandleAsync(devicePath, handle);
        if (charPath == null) return null;

        var pathStr = charPath.Value.ToString();
        if (_charProxies.TryGetValue(pathStr, out var cached)) return cached;

        var conn = await GetConnectionAsync();
        var proxy = conn.CreateProxy<IGattCharacteristic1>("org.bluez", charPath.Value);
        _charProxies[pathStr] = proxy;
        return proxy;
    }

    /// <summary>
    /// Invalidate the characteristic cache for a device (call on disconnect).
    /// </summary>
    public static void InvalidateCharCache(string devicePath)
    {
        if (_charCache.TryRemove(devicePath, out var map))
        {
            foreach (var (_, objPath) in map)
                _charProxies.TryRemove(objPath.ToString(), out _);
        }
        _deviceProxies.TryRemove(devicePath, out _);

        // Clear UUID→path, mode, command-rejected, and negative-result caches so a mode switch
        // (App↔Bootloader) forces fresh GATT discovery instead of reusing stale data.
        _modeCache.TryRemove(devicePath, out _);
        _commandRejected.TryRemove(devicePath, out _);
        foreach (var key in _uuidCharPathCache.Keys)
        {
            if (key.devicePath == devicePath)
                _uuidCharPathCache.TryRemove(key, out _);
        }
        foreach (var key in _notFoundUuids.Keys)
        {
            if (key.devicePath == devicePath)
                _notFoundUuids.TryRemove(key, out _);
        }
        foreach (var key in _uuidCharProxyCache.Keys)
        {
            if (key.devicePath == devicePath)
                _uuidCharProxyCache.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Backwards compatible alias used by some call sites.
    /// </summary>

    // ── UUID based GATT helpers ─────────────────────────────────────────────

    public const string AppServiceUuid = "0003cdd0-0000-1000-8000-00805f9b0131";
    public const string AppNotifyUuid  = "0003cdd1-0000-1000-8000-00805f9b0131";
    public const string AppWriteUuid   = "0003cdd2-0000-1000-8000-00805f9b0131";

    public const string BootServiceUuid = "00060000-f8ce-11e4-abf4-0002a5d5c51b";
    public const string BootNotifyUuid  = "00060001-f8ce-11e4-abf4-0002a5d5c51b";
    public const string BootWriteUuid   = "00060002-f8ce-11e4-abf4-0002a5d5c51b"; // best-effort guess

    public enum BleMode
    {
        Unknown = 0,
        Application = 1,
        Bootloader = 2
    }

    public static async Task<BleMode> DetectModeByGattAsync(string devicePath, CancellationToken ct = default)
    {
        if (_modeCache.TryGetValue(devicePath, out var cached))
            return cached;

        // Guard: if BlueZ has not finished service discovery, the ObjectManager may still expose
        // GATT objects from the *previous* connection session (e.g. stale bootloader services after
        // a firmware upload and reboot). Only trust the GATT tree once ServicesResolved=true;
        // otherwise return Unknown so the caller retries without caching the wrong mode.
        try
        {
            var dev = await GetDeviceAsync(devicePath);
            if (!await dev.GetAsync<bool>("ServicesResolved"))
                return BleMode.Unknown;
        }
        catch
        {
            // Device gone or D-Bus error — cannot determine mode safely.
            return BleMode.Unknown;
        }

        var objects = await GetManagedObjectsSafeAsync(ct);
        bool hasApp = false;
        bool hasBoot = false;

        foreach (var (objPath, ifaces) in objects)
        {
            var p = objPath.ToString();
            if (!p.StartsWith(devicePath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (ifaces.TryGetValue("org.bluez.GattService1", out var svc) &&
                svc.TryGetValue("UUID", out var uuidObj) && uuidObj is string uuid)
            {
                if (uuid.Equals(AppServiceUuid, StringComparison.OrdinalIgnoreCase)) hasApp = true;
                if (uuid.Equals(BootServiceUuid, StringComparison.OrdinalIgnoreCase)) hasBoot = true;
            }
        }

        var mode = hasBoot ? BleMode.Bootloader : hasApp ? BleMode.Application : BleMode.Unknown;
        // Only cache a definitive result — Unknown means GATT isn't ready yet and should be re-checked.
        if (mode != BleMode.Unknown)
            _modeCache[devicePath] = mode;
        return mode;
    }

    private static readonly ConcurrentDictionary<string, BleMode> _modeCache = new();
    private static readonly ConcurrentDictionary<(string devicePath, string charUuid), (ObjectPath path, string[] flags)> _uuidCharPathCache = new();

    // Negative-result cache: UUIDs that were not found after a full scan.
    // Avoids repeated expensive GetManagedObjectsSafeAsync calls for non-existent characteristics.
    private static readonly ConcurrentDictionary<(string devicePath, string charUuid), bool> _notFoundUuids = new();

    // Cached characteristic proxies keyed by (devicePath, charUuid) – avoids CreateProxy on every write.
    private static readonly ConcurrentDictionary<(string devicePath, string charUuid), IGattCharacteristic1> _uuidCharProxyCache = new();

    // Tracks devices where BlueZ rejected write-without-response (type=command) so we stop
    // trying command mode and go straight to type=request for the rest of the session.
    private static readonly ConcurrentDictionary<string, bool> _commandRejected = new();

    public static bool IsCommandRejected(string devicePath) =>
        _commandRejected.ContainsKey(devicePath);

    public static void MarkCommandRejected(string devicePath) =>
        _commandRejected[devicePath] = true;

    /// <summary>
    /// Remove a specific UUID from the negative-result (not-found) cache for a device.
    /// Call this before retrying a UUID lookup after a mode switch or GATT update so the
    /// scan is not short-circuited by a stale "not found" entry from an earlier session.
    /// </summary>
    public static void ClearNotFoundUuidCache(string devicePath, string charUuid)
    {
        if (string.IsNullOrWhiteSpace(devicePath) || string.IsNullOrWhiteSpace(charUuid)) return;
        _notFoundUuids.TryRemove((devicePath, charUuid.ToLowerInvariant()), out _);
    }

    public static async Task<(ObjectPath? path, string[]? flags)> FindCharacteristicByUuidAsync(
        string devicePath,
        string serviceUuid,
        string charUuid,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return (null, null);
        if (string.IsNullOrWhiteSpace(serviceUuid) || string.IsNullOrWhiteSpace(charUuid)) return (null, null);

        serviceUuid = serviceUuid.ToLowerInvariant();
        charUuid = charUuid.ToLowerInvariant();

        // Fast path: positive result cached.
        if (_uuidCharPathCache.TryGetValue((devicePath, charUuid), out var cached))
            return (cached.path, cached.flags);

        // Fast path: negative result cached (e.g. BootWriteUuid that doesn't exist on device).
        // Avoids the expensive GetManagedObjectsSafeAsync scan on every subsequent call.
        if (_notFoundUuids.ContainsKey((devicePath, charUuid)))
        {
            Console.WriteLine($"[BlueZHelpers] UUID {charUuid} not-found cache hit for {devicePath} — skipping scan");
            return (null, null);
        }

        const int maxAttempts = 6;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>> objects;
            try
            {
                objects = await GetManagedObjectsSafeAsync(ct);
            }
            catch (ArgumentNullException)
            {
                ClearDeviceCache(devicePath);
                await Task.Delay(100, ct);
                continue;
            }

            ObjectPath? svcPath = null;
            foreach (var (objPath, ifaces) in objects)
            {
                var p = objPath.ToString();
                if (!p.StartsWith(devicePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!ifaces.TryGetValue("org.bluez.GattService1", out var svcProps)) continue;
                if (svcProps.TryGetValue("UUID", out var uuidObj) && uuidObj is string uuid &&
                    uuid.Equals(serviceUuid, StringComparison.OrdinalIgnoreCase))
                {
                    svcPath = objPath;
                    break;
                }
            }

            if (svcPath == null)
            {
                await Task.Delay(150, ct);
                continue;
            }

            foreach (var (objPath, ifaces) in objects)
            {
                var p = objPath.ToString();
                if (!p.StartsWith(svcPath.Value.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
                if (!ifaces.TryGetValue("org.bluez.GattCharacteristic1", out var chrProps)) continue;
                if (chrProps.TryGetValue("UUID", out var uuidObj) && uuidObj is string uuid &&
                    uuid.Equals(charUuid, StringComparison.OrdinalIgnoreCase))
                {
                    string[]? flags = null;
                    if (chrProps.TryGetValue("Flags", out var flagsObj) && flagsObj is string[] arr)
                        flags = arr;

                    _uuidCharPathCache[(devicePath, charUuid)] = (objPath, flags ?? []);
                    return (objPath, flags);
                }
            }

            await Task.Delay(150, ct);
        }

        // Cache the negative result so the next call returns instantly without scanning.
        _notFoundUuids[(devicePath, charUuid)] = true;
        return (null, null);
    }

    public static async Task<(IGattCharacteristic1? characteristic, string[]? flags)> GetCharacteristicByUuidAsync(
        string devicePath,
        string serviceUuid,
        string charUuid,
        CancellationToken ct = default)
    {
        charUuid = charUuid.ToLowerInvariant();
        var cacheKey = (devicePath, charUuid);

        // Return cached proxy if available (avoids CreateProxy on every write call).
        if (_uuidCharProxyCache.TryGetValue(cacheKey, out var cachedProxy))
        {
            _uuidCharPathCache.TryGetValue(cacheKey, out var cachedEntry);
            return (cachedProxy, cachedEntry.flags);
        }

        var (path, flags) = await FindCharacteristicByUuidAsync(devicePath, serviceUuid, charUuid, ct);
        if (path == null) return (null, flags);

        var conn = await GetConnectionAsync();
        var proxy = conn.CreateProxy<IGattCharacteristic1>("org.bluez", path.Value);
        _uuidCharProxyCache[cacheKey] = proxy;
        return (proxy, flags);
    }

    // ── MAC / path helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Convert a colon-separated MAC address to the BlueZ device object path.
    /// e.g. "10:B9:F7:AA:BB:CC" + "hci0" → "/org/bluez/hci0/dev_10_B9_F7_AA_BB_CC"
    /// </summary>
    public static string DevicePath(string adapter, string mac)
    {
        if (string.IsNullOrWhiteSpace(adapter)) throw new ArgumentNullException(nameof(adapter));
        if (string.IsNullOrWhiteSpace(mac)) throw new ArgumentNullException(nameof(mac));
        var normalized = mac.ToUpperInvariant().Replace(":", "_");
        return $"/org/bluez/{adapter}/dev_{normalized}";
    }

    // ── Hex helpers ───────────────────────────────────────────────────────────

    /// <summary>Convert a hex string (e.g. "0102AABB") to a byte array.</summary>
    public static byte[] HexToBytes(string hex)
    {
        hex = hex.Replace(" ", "").Replace("-", "");
        if (hex.Length % 2 != 0)
            hex = "0" + hex;
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    /// <summary>Convert a byte array to an uppercase hex string.</summary>
    public static string BytesToHex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

    // ── Advertisement data reconstruction ────────────────────────────────────

    /// <summary>
    /// Build the raw advertisement data hex string from BlueZ Device1 properties.
    /// Reconstructs TLV-encoded AD structures from ManufacturerData and ServiceData
    /// so the existing ScanDataParser can process them unchanged.
    /// </summary>
    public static string BuildAdDataFromBlueZProps(IDictionary<string, object> props)
    {
        var adBytes = new List<byte>();

        // Manufacturer Specific Data (AD type 0xFF)
        if (props.TryGetValue("ManufacturerData", out var mfRaw))
        {
            // Tmds.DBus may deserialise a{qv} as IDictionary<ushort,object> or IDictionary<ushort,byte[]>.
            IEnumerable<KeyValuePair<ushort, byte[]>> mfPairs = null;

            if (mfRaw is IDictionary<ushort, object> mfDictObj)
                mfPairs = mfDictObj.Select(kv => KeyValuePair.Create(kv.Key, kv.Value as byte[] ?? Array.Empty<byte>()));
            else if (mfRaw is IDictionary<ushort, byte[]> mfDictBytes)
                mfPairs = mfDictBytes.Select(kv => KeyValuePair.Create(kv.Key, kv.Value));

            if (mfPairs != null)
            {
                foreach (var (mfId, data) in mfPairs)
                {
                    // AD entry: [length] [0xFF] [id_lo] [id_hi] [data...]
                    byte length = (byte)(1 + 2 + data.Length); // type + 2 id bytes + data
                    adBytes.Add(length);
                    adBytes.Add(0xFF);
                    adBytes.Add((byte)(mfId & 0xFF));
                    adBytes.Add((byte)((mfId >> 8) & 0xFF));
                    adBytes.AddRange(data);
                }
            }
        }

        // Complete Local Name (AD type 0x09)
        if (props.TryGetValue("Name", out var nameObj) && nameObj is string name && name.Length > 0)
        {
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            adBytes.Add((byte)(1 + nameBytes.Length));
            adBytes.Add(0x09);
            adBytes.AddRange(nameBytes);
        }

        return adBytes.Count == 0 ? string.Empty : BytesToHex(adBytes.ToArray());
    }

    /// <summary>
    /// Clear cached proxies and characteristic-handle mappings for a given device path.
    /// Useful if BlueZ is mid-update and ObjectManager results become inconsistent.
    /// </summary>
public static void ClearDeviceCache(string devicePath)
{
    if (string.IsNullOrWhiteSpace(devicePath))
        return;

    // Remove handle->ObjectPath cache for this device
    _charCache.TryRemove(devicePath, out _);

    // Remove cached characteristic proxies under this device subtree
    // (keys are full object paths like /org/bluez/hci0/dev_XX/.../charYYYY)
    foreach (var key in _charProxies.Keys)
    {
        if (key.StartsWith(devicePath, StringComparison.Ordinal))
            _charProxies.TryRemove(key, out _);
    }

    // Optional: also drop cached IDevice1 proxy to avoid stale object paths
    _deviceProxies.TryRemove(devicePath, out _);

    // Also clear mode, command-rejected, UUID→path, negative-result, and proxy caches
    // so a mode switch forces fresh GATT discovery.
    _modeCache.TryRemove(devicePath, out _);
    _commandRejected.TryRemove(devicePath, out _);
    foreach (var key in _uuidCharPathCache.Keys)
    {
        if (key.devicePath == devicePath)
            _uuidCharPathCache.TryRemove(key, out _);
    }
    foreach (var key in _notFoundUuids.Keys)
    {
        if (key.devicePath == devicePath)
            _notFoundUuids.TryRemove(key, out _);
    }
    foreach (var key in _uuidCharProxyCache.Keys)
    {
        if (key.devicePath == devicePath)
            _uuidCharProxyCache.TryRemove(key, out _);
    }
}
public static async Task<bool> WaitForServicesResolvedAsync(
    string devicePath,
    int timeoutMs,
    int pollMs,
    CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(devicePath))
        return false;

    timeoutMs = Math.Max(200, timeoutMs);
    pollMs = Math.Clamp(pollMs, 50, 1000);

    var dev = await GetDeviceAsync(devicePath);
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

    while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
    {
        try
        {
            // Wrap each D-Bus property-get in a 2-second per-call timeout.
            // After a connection abort (e.g. le-connection-abort-by-local) BlueZ can
            // become temporarily unresponsive and these calls would otherwise hang
            // forever, holding _subLock in EnsureNotifyingAsync and blocking the
            // entire notification/login pipeline for 10+ minutes.
            using var pollCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var connected = await dev.GetAsync<bool>("Connected").WaitAsync(pollCts.Token);
            var resolved = await dev.GetAsync<bool>("ServicesResolved").WaitAsync(pollCts.Token);

            if (connected && resolved)
                return true;
        }
        catch
        {
            // Device can disappear mid-poll, or the 2-second per-call timeout fired;
            // either way keep waiting until the outer deadline expires.
        }

        await Task.Delay(pollMs, ct);
    }

    return false;
}

public static async Task<string> DumpGattAsync(string devicePath, CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(devicePath))
        return "<devicePath is null/empty>";

    // Serialize ObjectManager reads (important on your platform)
    try
    {
        var conn = await GetConnectionAsync();
        var objMgr = await GetObjectManagerAsync();

        var objects = await objMgr.GetManagedObjectsAsync();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"GATT dump for devicePath={devicePath}");
        sb.AppendLine($"Objects total={objects.Count}");

        // Only things under the device
        var underDevice = objects
            .Where(kvp => kvp.Key.ToString().StartsWith(devicePath, StringComparison.Ordinal))
            .ToList();

        sb.AppendLine($"Objects under device={underDevice.Count}");

        // Services first
        var services = underDevice
            .Where(o => o.Value.ContainsKey("org.bluez.GattService1"))
            .Select(o =>
            {
                var props = o.Value["org.bluez.GattService1"];
                props.TryGetValue("UUID", out var uuidObj);
                var uuid = uuidObj as string ?? "<no-uuid>";
                props.TryGetValue("Primary", out var primaryObj);
                var primary = primaryObj is bool b && b;
                return new { Path = o.Key.ToString(), Uuid = uuid, Primary = primary };
            })
            .OrderBy(s => s.Path, StringComparer.Ordinal)
            .ToList();

        sb.AppendLine($"Services found={services.Count}");
        foreach (var s in services)
        {
            sb.AppendLine($"  [Service] UUID={s.Uuid} Primary={s.Primary} Path={s.Path}");

            // Characteristics below that service path
            var chars = underDevice
                .Where(o => o.Value.ContainsKey("org.bluez.GattCharacteristic1") &&
                            o.Key.ToString().StartsWith(s.Path, StringComparison.Ordinal))
                .Select(o =>
                {
                    var props = o.Value["org.bluez.GattCharacteristic1"];

                    props.TryGetValue("UUID", out var uuidObj);
                    var uuid = uuidObj as string ?? "<no-uuid>";

                    // Flags is usually string[]
                    string flagsStr = "";
                    if (props.TryGetValue("Flags", out var flagsObj))
                    {
                        if (flagsObj is string[] arr) flagsStr = string.Join(",", arr);
                        else if (flagsObj is IEnumerable<string> ie) flagsStr = string.Join(",", ie);
                        else flagsStr = flagsObj?.ToString() ?? "";
                    }

                    // Handle is usually UInt16
                    string handleStr = "";
                    if (props.TryGetValue("Handle", out var handleObj))
                    {
                        if (handleObj is ushort us) handleStr = us.ToString();
                        else if (handleObj is UInt16 u16) handleStr = u16.ToString();
                        else handleStr = handleObj?.ToString() ?? "";
                    }

                    string notifyingStr = "";
                    if (props.TryGetValue("Notifying", out var nObj))
                        notifyingStr = (nObj is bool nb) ? nb.ToString() : (nObj?.ToString() ?? "");

                    // MTU property (BlueZ 5.62+): tells us the negotiated ATT MTU for this connection.
                    // With MTU=N the largest single-packet write is (N-3) bytes; anything larger
                    // triggers the Long Write (PrepareWrite) procedure.
                    string mtuStr = "";
                    if (props.TryGetValue("MTU", out var mtuObj))
                        mtuStr = mtuObj is ushort mtuVal ? mtuVal.ToString() : (mtuObj?.ToString() ?? "");

                    return new
                    {
                        Path = o.Key.ToString(),
                        Uuid = uuid,
                        Flags = flagsStr,
                        Handle = handleStr,
                        Notifying = notifyingStr,
                        Mtu = mtuStr
                    };
                })
                .OrderBy(c => c.Path, StringComparer.Ordinal)
                .ToList();

            sb.AppendLine($"    Characteristics={chars.Count}");
            foreach (var c in chars)
            {
                var mtuPart = string.IsNullOrEmpty(c.Mtu) ? "" : $" MTU={c.Mtu}";
                sb.AppendLine($"      [Char] UUID={c.Uuid} Handle={c.Handle} Flags=[{c.Flags}] Notifying={c.Notifying}{mtuPart} Path={c.Path}");
            }
        }

        // If no services, still show any characteristics directly under device
        if (services.Count == 0)
        {
            var chars = underDevice
                .Where(o => o.Value.ContainsKey("org.bluez.GattCharacteristic1"))
                .Select(o =>
                {
                    var props = o.Value["org.bluez.GattCharacteristic1"];
                    props.TryGetValue("UUID", out var uuidObj);
                    var uuid = uuidObj as string ?? "<no-uuid>";

                    string flagsStr = "";
                    if (props.TryGetValue("Flags", out var flagsObj))
                    {
                        if (flagsObj is string[] arr) flagsStr = string.Join(",", arr);
                        else flagsStr = flagsObj?.ToString() ?? "";
                    }

                    string handleStr = "";
                    if (props.TryGetValue("Handle", out var handleObj))
                    {
                        if (handleObj is ushort us) handleStr = us.ToString();
                        else handleStr = handleObj?.ToString() ?? "";
                    }

                    return new { Path = o.Key.ToString(), Uuid = uuid, Flags = flagsStr, Handle = handleStr };
                })
                .OrderBy(c => c.Path, StringComparer.Ordinal)
                .ToList();

            sb.AppendLine($"Chars under device (no services found)={chars.Count}");
            foreach (var c in chars)
                sb.AppendLine($"  [Char] UUID={c.Uuid} Handle={c.Handle} Flags=[{c.Flags}] Path={c.Path}");
        }

        return sb.ToString();
    }
    catch (Exception ex)
    {
        return $"GATT dump failed for devicePath={devicePath}: {ex}";
    }
    finally
    {
    }
}

/// <summary>
/// Best-effort request for a shorter BLE connection interval after connecting.
/// Tries btmgmt conn-update first (modern), then falls back to hcitool lecup.
/// Both commands require CAP_NET_RAW / root; failure is silently ignored.
///
/// Units: BLE connection interval unit = 1.25 ms.
///   intervalMin = 12  → 15 ms
///   intervalMax = 24  → 30 ms
/// Reducing CI from the default ~45 ms to 15-30 ms cuts multi-packet write
/// latency (Long Write PrepareWrite round-trips) proportionally.
/// </summary>
public static async Task TryRequestShortConnectionIntervalAsync(
    string adapter, string macAddress, ILogger? logger = null,
    int intervalMin = 12, int intervalMax = 24,
    int latency = 0, int supervisionTimeout = 400)
{
    if (!int.TryParse(adapter.Replace("hci", ""), out var hciIndex))
        hciIndex = 0;

    // ── 1. Try btmgmt conn-update (doesn't need connection handle) ──────────
    // Address type: 1 = LE Public, 2 = LE Random.  Try both; one will match.
    foreach (var addrType in new[] { "1", "2" })
    {
        try
        {
            var result = await RunProcessAsync(
                "btmgmt",
                $"--index {hciIndex} conn-update {macAddress} {addrType} {intervalMin} {intervalMax} {latency} {supervisionTimeout}",
                timeoutMs: 3000);

            // Check for explicit failure strings.  "Invalid command" means this
            // btmgmt build does not support conn-update — fall through to hcitool.
            bool ok = !string.IsNullOrWhiteSpace(result)
                      && !result.Contains("Invalid command", StringComparison.OrdinalIgnoreCase)
                      && !result.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                      && !result.Contains("failed", StringComparison.OrdinalIgnoreCase)
                      && !result.Contains("error", StringComparison.OrdinalIgnoreCase)
                      && !result.Contains("not found", StringComparison.OrdinalIgnoreCase)
                      && !result.Contains("not supported", StringComparison.OrdinalIgnoreCase);
            if (ok)
            {
                logger?.LogDebug("LinuxBLE: CI update via btmgmt (type={T}) for {Mac}: {R}",
                    addrType, macAddress, result.Trim());
                return;
            }
            logger?.LogDebug("LinuxBLE: btmgmt conn-update (type={T}) not usable for {Mac}: {R}",
                addrType, macAddress, result.Trim());
        }
        catch { /* btmgmt not available */ }
    }

    // ── 2. Fallback: hcitool lecup (deprecated but still available) ─────────
    // hcitool lecup requires the ACL connection handle, not the MAC address.
    try
    {
        var conOutput = await RunProcessAsync("hcitool", $"-i {adapter} con", timeoutMs: 2000);
        // Example line: "  < LE 10:B9:F7:10:00:9F handle 72 state 1 lm MASTER"
        var handleLine = conOutput
            .Split('\n')
            .FirstOrDefault(l => l.Contains(macAddress, StringComparison.OrdinalIgnoreCase)
                              && l.Contains("LE", StringComparison.OrdinalIgnoreCase));

        if (handleLine != null)
        {
            var parts = handleLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hi = Array.IndexOf(parts, "handle");
            if (hi >= 0 && hi + 1 < parts.Length && int.TryParse(parts[hi + 1], out var connHandle))
            {
                var lecupResult = await RunProcessAsync(
                    "hcitool",
                    $"-i {adapter} lecup {connHandle} {intervalMin} {intervalMax} {latency} {supervisionTimeout}",
                    timeoutMs: 3000);
                logger?.LogDebug("LinuxBLE: CI update via hcitool lecup handle={H} for {Mac}: {R}",
                    connHandle, macAddress, lecupResult.Trim());
                return;
            }
        }
    }
    catch (Exception ex)
    {
        logger?.LogDebug(ex, "LinuxBLE: hcitool lecup fallback failed for {Mac}", macAddress);
    }

    logger?.LogDebug("LinuxBLE: Could not update connection interval for {Mac} — using device default", macAddress);
}

private static async Task<string> RunProcessAsync(string command, string args, int timeoutMs = 5000)
{
    using var proc = new Process();
    proc.StartInfo = new ProcessStartInfo(command, args)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    proc.Start();
    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
    var stderrTask = proc.StandardError.ReadToEndAsync();

    using var cts = new CancellationTokenSource(timeoutMs);
    try { await proc.WaitForExitAsync(cts.Token); }
    catch (OperationCanceledException) { try { proc.Kill(); } catch { } }

    return await stdoutTask + await stderrTask;
}

}
