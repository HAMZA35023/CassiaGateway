using System.Collections.Concurrent;
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
            _objectManagerProxy = conn.CreateProxy<IObjectManager>("org.bluez", "/");
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
        if (_charCache.TryGetValue(devicePath, out var cached) && cached.TryGetValue(handle, out var path))
            return path;

        var objMgr = await GetObjectManagerAsync();
        var objects = await objMgr.GetManagedObjectsAsync();

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
        if (props.TryGetValue("ManufacturerData", out var mfRaw) &&
            mfRaw is IDictionary<ushort, object> mfDict)
        {
            foreach (var (mfId, dataObj) in mfDict)
            {
                byte[] data = dataObj as byte[] ?? Array.Empty<byte>();
                // AD entry: [length] [0xFF] [id_lo] [id_hi] [data...]
                byte length = (byte)(1 + 2 + data.Length); // type + 2 id bytes + data
                adBytes.Add(length);
                adBytes.Add(0xFF);
                adBytes.Add((byte)(mfId & 0xFF));
                adBytes.Add((byte)((mfId >> 8) & 0xFF));
                adBytes.AddRange(data);
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
}
