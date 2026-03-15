#if WINDOWS
using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace AccessAPP.Services.WindowsBle;

/// <summary>
/// Shared static helpers for the Windows-native BLE backend.
/// Manages <see cref="BluetoothLEDevice"/> session lifetime, GATT characteristic
/// caching, and GATT mode detection.  Mirrors the role of BlueZHelpers in the
/// Linux-native backend.
///
/// LIFETIME RULE: GattDeviceService proxies are NEVER disposed outside of
/// <see cref="DisposeDevice"/>.  In WinRT, .NET may use the same COM RCW for
/// all proxy objects pointing to the same underlying service; calling Dispose
/// on one invalidates all others.  Services are cached in <see cref="_svcCache"/>
/// and cleaned up only when the BLE session is fully torn down.
/// </summary>
internal static class WindowsBleHelpers
{
    // ── GATT service / characteristic UUIDs ─────────────────────────────────
    // Must match the values in BlueZHelpers.cs.

    public static readonly Guid AppServiceUuid  = Guid.Parse("0003cdd0-0000-1000-8000-00805f9b0131");
    public static readonly Guid AppNotifyUuid   = Guid.Parse("0003cdd1-0000-1000-8000-00805f9b0131");
    public static readonly Guid AppWriteUuid    = Guid.Parse("0003cdd2-0000-1000-8000-00805f9b0131");

    public static readonly Guid BootServiceUuid = Guid.Parse("00060000-f8ce-11e4-abf4-0002a5d5c51b");
    public static readonly Guid BootNotifyUuid  = Guid.Parse("00060001-f8ce-11e4-abf4-0002a5d5c51b");
    public static readonly Guid BootWriteUuid   = Guid.Parse("00060002-f8ce-11e4-abf4-0002a5d5c51b");

    public enum BleMode { Unknown, Application, Bootloader }

    // ── Device session cache ─────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, BluetoothLEDevice> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    // Characteristic cache: MAC → (serviceUuid+charUuid) → characteristic
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GattCharacteristic>> _charCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Service cache: MAC → serviceUuid → GattDeviceService
    // Services MUST be kept alive — see lifetime rule in class summary.
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GattDeviceService>> _svcCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Mode cache: MAC → last known mode
    private static readonly ConcurrentDictionary<string, BleMode> _modeCache =
        new(StringComparer.OrdinalIgnoreCase);

    // ── MAC conversion ───────────────────────────────────────────────────────

    /// <summary>Convert "AA:BB:CC:DD:EE:FF" to the ulong address used by WinRT.</summary>
    public static ulong MacStringToUlong(string mac)
    {
        var parts = mac.Split(':');
        if (parts.Length != 6)
            throw new ArgumentException($"Invalid MAC address: {mac}");
        ulong result = 0;
        foreach (var part in parts)
            result = (result << 8) | Convert.ToByte(part, 16);
        return result;
    }

    /// <summary>Convert a WinRT ulong address to "AA:BB:CC:DD:EE:FF".</summary>
    public static string UlongToMacString(ulong address)
    {
        var bytes = new byte[6];
        for (int i = 5; i >= 0; i--)
        {
            bytes[i] = (byte)(address & 0xFF);
            address >>= 8;
        }
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    // ── Device session management ────────────────────────────────────────────

    /// <summary>
    /// Obtain (or create) a <see cref="BluetoothLEDevice"/> session for <paramref name="macAddress"/>.
    /// On Windows, <c>FromBluetoothAddressAsync</c> succeeds only while the device is advertising
    /// (or already paired / cached by the OS).  Returns <c>null</c> on failure.
    /// </summary>
    public static async Task<BluetoothLEDevice?> GetOrOpenDeviceAsync(
        string macAddress, CancellationToken ct = default)
    {
        // Return cached session if still valid.
        if (_sessions.TryGetValue(macAddress, out var existing))
        {
            if (existing.ConnectionStatus != BluetoothConnectionStatus.Disconnected)
                return existing;

            // Cached device has disconnected — dispose and re-open.
            DisposeDevice(macAddress);
        }

        ulong address = MacStringToUlong(macAddress);
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            .AsTask(ct)
            .ConfigureAwait(false);

        if (device == null)
            return null;

        _sessions[macAddress] = device;
        return device;
    }

    /// <summary>
    /// Dispose the <see cref="BluetoothLEDevice"/> for <paramref name="macAddress"/> and clear
    /// all associated caches.  This is the ONLY place where GattDeviceService.Dispose is called.
    /// </summary>
    public static void DisposeDevice(string macAddress)
    {
        if (_sessions.TryRemove(macAddress, out var dev))
        {
            try { dev.Dispose(); } catch { /* best-effort */ }
        }
        _charCache.TryRemove(macAddress, out _);
        _modeCache.TryRemove(macAddress, out _);
        if (_svcCache.TryRemove(macAddress, out var svcMap))
        {
            foreach (var svc in svcMap.Values)
                try { svc.Dispose(); } catch { /* best-effort */ }
        }
    }

    // ── GATT service caching from Uncached scans ─────────────────────────────

    /// <summary>
    /// Cache <paramref name="services"/> from an Uncached GATT scan into <see cref="_svcCache"/>,
    /// replacing any stale entries WITHOUT calling Dispose (see lifetime rule).
    /// Also clears the characteristic cache because char handles obtained via the previous
    /// Cached state may be stale after a fresh Uncached scan refreshes the OS GATT cache.
    /// </summary>
    public static void CacheDiscoveredServices(string macAddress, IEnumerable<GattDeviceService> services)
    {
        var svcMap = _svcCache.GetOrAdd(macAddress, _ =>
            new ConcurrentDictionary<string, GattDeviceService>(StringComparer.OrdinalIgnoreCase));

        foreach (var svc in services)
            svcMap[svc.Uuid.ToString()] = svc;  // Replace stale entry; old proxy abandoned to GC.

        // Chars obtained via Cached mode may be stale after the Uncached refresh.
        _charCache.TryRemove(macAddress, out _);
    }

    // ── GATT mode detection ──────────────────────────────────────────────────

    /// <summary>
    /// Detect whether the device is in Application or Bootloader mode by examining
    /// its exposed GATT services (Uncached so a post-jump mode switch is visible).
    /// Caches the returned services via <see cref="CacheDiscoveredServices"/> so they
    /// stay alive for subsequent characteristic lookups.
    /// </summary>
    public static async Task<BleMode> DetectModeAsync(
        BluetoothLEDevice device, CancellationToken ct = default)
    {
        try
        {
            var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached)
                .AsTask(ct).ConfigureAwait(false);

            if (result.Status != GattCommunicationStatus.Success)
                return BleMode.Unknown;

            var mac = UlongToMacString(device.BluetoothAddress);
            bool hasApp = false, hasBoot = false;

            // Cache services and detect mode in one pass.
            // Do NOT dispose — see lifetime rule in class summary.
            var svcMap = _svcCache.GetOrAdd(mac, _ =>
                new ConcurrentDictionary<string, GattDeviceService>(StringComparer.OrdinalIgnoreCase));
            foreach (var svc in result.Services)
            {
                if (svc.Uuid == AppServiceUuid)  hasApp  = true;
                if (svc.Uuid == BootServiceUuid) hasBoot = true;
                svcMap[svc.Uuid.ToString()] = svc;  // Replace stale; old proxy abandoned to GC.
            }
            // Uncached scan refreshed the OS GATT cache; old char handles are stale.
            _charCache.TryRemove(mac, out _);

            return hasBoot ? BleMode.Bootloader
                 : hasApp  ? BleMode.Application
                 :           BleMode.Unknown;
        }
        catch (OperationCanceledException) { throw; }
        catch { return BleMode.Unknown; }
    }

    /// <summary>
    /// Detect mode and cache the result.  Skips service enumeration when a cached
    /// value is available (callers that need a fresh read should call <see cref="DetectModeAsync"/> directly).
    /// </summary>
    public static async Task<BleMode> GetCachedModeAsync(
        string macAddress, BluetoothLEDevice device, CancellationToken ct = default)
    {
        if (_modeCache.TryGetValue(macAddress, out var cached) && cached != BleMode.Unknown)
            return cached;

        var mode = await DetectModeAsync(device, ct).ConfigureAwait(false);
        if (mode != BleMode.Unknown)
            _modeCache[macAddress] = mode;
        return mode;
    }

    /// <summary>Invalidate the cached mode for <paramref name="macAddress"/> (e.g. after jump to bootloader).</summary>
    public static void InvalidateModeCache(string macAddress)
        => _modeCache.TryRemove(macAddress, out _);

    /// <summary>Return the cached BLE mode for <paramref name="macAddress"/>, or <see cref="BleMode.Unknown"/> if not cached.</summary>
    public static BleMode GetCachedMode(string macAddress)
        => _modeCache.TryGetValue(macAddress, out var mode) ? mode : BleMode.Unknown;

    // ── Characteristic lookup ────────────────────────────────────────────────

    /// <summary>
    /// Return (and cache) the <see cref="GattCharacteristic"/> identified by
    /// <paramref name="serviceUuid"/> + <paramref name="charUuid"/>.
    /// Checks <see cref="_svcCache"/> first to avoid redundant OS queries.
    /// Returns <c>null</c> when the characteristic is not found on the device.
    /// </summary>
    public static async Task<GattCharacteristic?> GetCharacteristicAsync(
        string macAddress,
        BluetoothLEDevice device,
        Guid serviceUuid,
        Guid charUuid,
        CancellationToken ct = default)
    {
        var key = $"{serviceUuid}/{charUuid}";
        var macCache = _charCache.GetOrAdd(macAddress, _ =>
            new ConcurrentDictionary<string, GattCharacteristic>(StringComparer.OrdinalIgnoreCase));

        if (macCache.TryGetValue(key, out var cached))
            return cached;

        // Check service cache before hitting the OS.
        var svcMap = _svcCache.GetOrAdd(macAddress, _ =>
            new ConcurrentDictionary<string, GattDeviceService>(StringComparer.OrdinalIgnoreCase));
        var svcKey = serviceUuid.ToString();

        if (!svcMap.TryGetValue(svcKey, out var svc))
        {
            var svcResult = await device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Cached)
                .AsTask(ct).ConfigureAwait(false);

            if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
                return null;

            var fetchedSvc = svcResult.Services[0];
            if (svcMap.TryAdd(svcKey, fetchedSvc))
            {
                svc = fetchedSvc;
            }
            else
            {
                // Another thread beat us — use theirs.  Do NOT dispose fetchedSvc; see lifetime rule.
                svc = svcMap[svcKey];
            }
        }

        var charResult = await svc.GetCharacteristicsForUuidAsync(charUuid, BluetoothCacheMode.Cached)
            .AsTask(ct).ConfigureAwait(false);

        if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
            return null;

        var characteristic = charResult.Characteristics[0];
        macCache[key] = characteristic;
        return characteristic;
    }

    /// <summary>Clear the characteristic cache for <paramref name="macAddress"/>.</summary>
    public static void InvalidateCharCache(string macAddress)
        => _charCache.TryRemove(macAddress, out _);

    // ── Byte / hex helpers ───────────────────────────────────────────────────

    public static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
        int len = hex.Length / 2;
        var bytes = new byte[len];
        for (int i = 0; i < len; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    public static string BytesToHex(byte[] bytes)
        => bytes.Length == 0 ? string.Empty : Convert.ToHexString(bytes);
}
#endif
