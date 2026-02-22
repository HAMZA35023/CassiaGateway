using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

    private static IAdapter1? _adapterProxy;
    private static IObjectManager? _objectManagerProxy;
    private static readonly SemaphoreSlim _proxySem = new(1, 1);

    /// <summary>
    /// Return the cached IAdapter1 proxy for the configured HCI adapter.
    /// Created once; CreateProxy is never called a second time for this interface.
    /// </summary>
    public static async Task<IAdapter1> GetAdapterAsync()
    {
        if (_adapterProxy != null) return _adapterProxy;
        await _proxySem.WaitAsync();
        try
        {
            if (_adapterProxy != null) return _adapterProxy;
            var conn = await GetConnectionAsync();
            var adapterPath = new ObjectPath($"/org/bluez/{RuntimeVariables.LINUX_BLE_ADAPTER}");
            _adapterProxy = conn.CreateProxy<IAdapter1>("org.bluez", adapterPath);
            return _adapterProxy;
        }
        finally { _proxySem.Release(); }
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
                objects = await objMgr.GetManagedObjectsAsync();
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


// ── GATT enumeration / UUID helpers ────────────────────────────────────────

// These UUIDs come from the Windows reference implementation.
public const string AppServiceUuid = "0003cdd0-0000-1000-8000-00805f9b0131";
public const string AppCharUuid    = "0003cdd1-0000-1000-8000-00805f9b0131";
public const string BootServiceUuid = "00060000-f8ce-11e4-abf4-0002a5d5c51b";
public const string BootCharUuid    = "00060001-f8ce-11e4-abf4-0002a5d5c51b";

// BlueZ ObjectManager decoding can glitch if called concurrently while the object tree is changing.
// Serialize it process-wide.
private static readonly SemaphoreSlim _getManagedObjectsSem = new(1, 1);

public sealed record GattSnapshot(HashSet<string> ServiceUuids, HashSet<string> CharacteristicUuids);

/// <summary>
/// Enumerate GATT services/characteristics currently exposed under a device path and return UUID sets.
/// This lets us detect Application vs Bootloader mode per MAC without relying on unstable handles.
/// </summary>
public static async Task<GattSnapshot> GetGattSnapshotAsync(string devicePath, CancellationToken ct = default)
{
    var svc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var chr = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    var objMgr = await GetObjectManagerAsync();

    await _getManagedObjectsSem.WaitAsync(ct);
    try
    {
        // Retry a few times in case BlueZ is mid-update and ObjectPath decoding fails.
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var objects = await objMgr.GetManagedObjectsAsync();
                foreach (var (objPath, ifaces) in objects)
                {
                    var pathStr = objPath.ToString();
                    if (!pathStr.StartsWith(devicePath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (ifaces.TryGetValue("org.bluez.GattService1", out var sprops) &&
                        sprops.TryGetValue("UUID", out var su) && su is string ss && ss.Length > 0)
                    {
                        svc.Add(ss);
                    }

                    if (ifaces.TryGetValue("org.bluez.GattCharacteristic1", out var cprops) &&
                        cprops.TryGetValue("UUID", out var cu) && cu is string cs && cs.Length > 0)
                    {
                        chr.Add(cs);
                    }
                }
                break;
            }
            catch (ArgumentNullException) when (attempt < maxAttempts)
            {
                ClearDeviceCache(devicePath);
                await Task.Delay(100, ct);
            }
        }
    }
    finally
    {
        _getManagedObjectsSem.Release();
    }

    return new GattSnapshot(svc, chr);
}

public enum BleGattMode
{
    Unknown = 0,
    Application = 1,
    Bootloader = 2
}

public static async Task<BleGattMode> DetectModeByGattAsync(string devicePath, CancellationToken ct = default)
{
    var snap = await GetGattSnapshotAsync(devicePath, ct);
    if (snap.ServiceUuids.Contains(BootServiceUuid) || snap.CharacteristicUuids.Contains(BootCharUuid))
        return BleGattMode.Bootloader;
    if (snap.ServiceUuids.Contains(AppServiceUuid) || snap.CharacteristicUuids.Contains(AppCharUuid))
        return BleGattMode.Application;
    return BleGattMode.Unknown;
}

/// <summary>
/// Find a characteristic object path by UUID under a specific device path. Returns null if not found.
/// </summary>
public static async Task<ObjectPath?> FindCharacteristicByUuidAsync(string devicePath, string characteristicUuid, CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(devicePath) || string.IsNullOrWhiteSpace(characteristicUuid))
        return null;

    characteristicUuid = characteristicUuid.Trim().ToLowerInvariant();

    var objMgr = await GetObjectManagerAsync();
    await _getManagedObjectsSem.WaitAsync(ct);
    try
    {
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var objects = await objMgr.GetManagedObjectsAsync();
                foreach (var (objPath, ifaces) in objects)
                {
                    var pathStr = objPath.ToString();
                    if (!pathStr.StartsWith(devicePath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (ifaces.TryGetValue("org.bluez.GattCharacteristic1", out var props) &&
                        props.TryGetValue("UUID", out var uo) && uo is string uuid &&
                        uuid.Equals(characteristicUuid, StringComparison.OrdinalIgnoreCase))
                    {
                        return objPath;
                    }
                }
                return null;
            }
            catch (ArgumentNullException) when (attempt < maxAttempts)
            {
                ClearDeviceCache(devicePath);
                await Task.Delay(100, ct);
            }
        }
        return null;
    }
    finally
    {
        _getManagedObjectsSem.Release();
    }
}

public static async Task<IGattCharacteristic1?> GetCharacteristicByUuidAsync(string devicePath, string characteristicUuid, CancellationToken ct = default)
{
    var p = await FindCharacteristicByUuidAsync(devicePath, characteristicUuid, ct);
    if (p == null) return null;
    var conn = await GetConnectionAsync();
    return conn.CreateProxy<IGattCharacteristic1>("org.bluez", p.Value);
}

public static async Task<bool> IsConnectedAsync(string devicePath, CancellationToken ct = default)
{
    try
    {
        var dev = await GetDeviceAsync(devicePath);
        return await dev.GetAsync<bool>("Connected");
    }
    catch { return false; }
}

public static async Task<bool> WaitForServicesResolvedAsync(string devicePath, int timeoutMs, CancellationToken ct = default)
{
    var dev = await GetDeviceAsync(devicePath);
    var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(250, timeoutMs));
    while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
    {
        try
        {
            if (await dev.GetAsync<bool>("ServicesResolved"))
                return true;
        }
        catch { /* ignore */ }
        await Task.Delay(100, ct);
    }
    return false;
}
    /// Clear cached proxies and characteristic-handle mappings for a given device path.
    /// Useful if BlueZ is mid-update and ObjectManager results become inconsistent.
    /// </summary>
    public static void ClearDeviceCache(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return;

        _charCache.TryRemove(devicePath, out _);
        _deviceProxies.TryRemove(devicePath, out _);

        // Remove characteristic proxies that belong to this device path.
        foreach (var key in _charProxies.Keys)
        {
            if (key.StartsWith(devicePath, StringComparison.OrdinalIgnoreCase))
                _charProxies.TryRemove(key, out _);
        }
    }


}