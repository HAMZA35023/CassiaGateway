using System.Net;
using System.Text;
using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;

namespace AccessAPP.Services.LinuxBle;

/// <summary>
/// Linux-native BLE device scanner using BlueZ via D-Bus.
/// Drop-in replacement for <see cref="ScanBleDevice"/> when BLE_BACKEND = "linux-native".
///
/// Behaviour:
///   • Starts BlueZ discovery on the configured HCI adapter.
///   • Listens for InterfacesAdded and Device1 PropertiesChanged signals to receive
///     advertisement data as devices appear or update.
///   • Parses Cassia manufacturer-data payload directly when possible.
///   • PropertiesChanged events do NOT carry "Address"; MAC is derived from the device path.
///   • Feeds enriched ScannedDevicesView entries into DeviceStorageService.
/// </summary>
public class LinuxNativeScanDevice : IDisposable
{
    private readonly DeviceStorageService _deviceStorageService;
    private readonly CassiaFirmwareUpgradeService _firmUpgradeService;
    private readonly ILogger<LinuxNativeScanDevice> _logger;
    private readonly object _devicePropsLock = new();
    private readonly Dictionary<string, Dictionary<string, object>> _devicePropsByPath = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _macPrefix;
    private bool _disposed;

    public LinuxNativeScanDevice(
        DeviceStorageService deviceStorageService,
        CassiaFirmwareUpgradeService firmUpgradeService,
        ILogger<LinuxNativeScanDevice> logger)
    {
        _deviceStorageService = deviceStorageService;
        _firmUpgradeService = firmUpgradeService;
        _logger = logger;

        _macPrefix = RuntimeVariables.LINUX_BLE_MAC_PREFIX;

        var adapters = RuntimeVariables.GetLinuxBleAdapterList();
        foreach (var a in adapters)
            _ = Task.Run(() => RunAdapterScanLoopAsync(a));
    }

    // ── Main scan loop ───────────────────────────────────────────────────────

    private async Task RunAdapterScanLoopAsync(string adapter)
    {
        while (!_disposed)
        {
            try
            {
                await ScanAsync(adapter);
            }
            catch (Exception ex) when (!_disposed)
            {
                _logger.LogError(ex, "LinuxBLE scan: error in scan loop for {Adapter}, retrying in 5s", adapter);
                await Task.Delay(5000);
            }
        }
    }

    private async Task ScanAsync(string adapter)
    {
        var adapterObj = await BlueZHelpers.GetAdapterAsync(adapter);
        var objMgr = await BlueZHelpers.GetObjectManagerAsync();
        var deviceWatchers = new Dictionary<string, IDisposable>(StringComparer.OrdinalIgnoreCase);
        var deviceWatchersLock = new object();

        // The BlueZ adapter path prefix used to filter devices belonging to this adapter.
        var adapterPath = $"/org/bluez/{adapter}/";

        // Set BLE-only discovery filter.
        await adapterObj.SetDiscoveryFilterAsync(new Dictionary<string, object>
        {
            ["Transport"] = "le",
            ["DuplicateData"] = true // receive updates for already-known devices
        });

        await adapterObj.StartDiscoveryAsync();
        _logger.LogInformation("LinuxBLE scan: discovery started on {Adapter}", adapter);

        // Process devices that were already cached in BlueZ from prior scans.
        var existing = await objMgr.GetManagedObjectsAsync();
        foreach (var (path, interfaces) in existing)
        {
            var pathStr = path.ToString();
            if (!pathStr.StartsWith(adapterPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (interfaces.TryGetValue("org.bluez.Device1", out var props))
            {
                var mergedProps = MergeDevicePropsSnapshot(pathStr, props);
                TryProcessDevice(pathStr, mergedProps, adapter);
                await EnsureDeviceWatcherAsync(pathStr, adapter, deviceWatchers, deviceWatchersLock);
            }
        }

        // Watch for new devices — filter to this adapter's path prefix.
        using var addedSub = await objMgr.WatchInterfacesAddedAsync(
            args =>
            {
                var (path, interfaces) = args;
                var pathStr = path.ToString();
                if (!pathStr.StartsWith(adapterPath, StringComparison.OrdinalIgnoreCase)) return;
                if (interfaces.TryGetValue("org.bluez.Device1", out var props))
                {
                    var mergedProps = MergeDevicePropsSnapshot(pathStr, props);
                    TryProcessDevice(pathStr, mergedProps, adapter);
                    _ = Task.Run(() => EnsureDeviceWatcherAsync(pathStr, adapter, deviceWatchers, deviceWatchersLock));
                }
            },
            ex => _logger.LogError(ex, "LinuxBLE scan: InterfacesAdded error on {Adapter}", adapter));

        using var removedSub = await objMgr.WatchInterfacesRemovedAsync(
            args =>
            {
                var (path, _) = args;
                var pathStr = path.ToString();
                if (!pathStr.StartsWith(adapterPath, StringComparison.OrdinalIgnoreCase))
                    return;

                IDisposable? watcher = null;
                lock (deviceWatchersLock)
                {
                    if (deviceWatchers.TryGetValue(pathStr, out watcher))
                        deviceWatchers.Remove(pathStr);
                }

                watcher?.Dispose();
                RemoveDevicePropsSnapshot(pathStr);
            },
            ex => _logger.LogDebug(ex, "LinuxBLE scan: InterfacesRemoved error on {Adapter}", adapter));

        // Keep scanning until paused or disposed.
        while (!_disposed)
        {
            if (ShouldPauseScan())
            {
                await Task.Delay(2000);
                continue;
            }

            await Task.Delay(500);
        }

        // Cleanup.
        List<IDisposable> watchersToDispose;
        lock (deviceWatchersLock)
        {
            watchersToDispose = deviceWatchers.Values.ToList();
            deviceWatchers.Clear();
        }

        foreach (var w in watchersToDispose)
        {
            try { w.Dispose(); } catch { /* best-effort */ }
        }

        ClearDevicePropsSnapshotForAdapter(adapterPath);
        try { await adapterObj.StopDiscoveryAsync(); } catch { /* best-effort */ }
    }

    // ── Device processing ────────────────────────────────────────────────────

    private void TryProcessDevice(string devicePath, IDictionary<string, object> props, string adapter)
    {
        try
        {
            // PropertiesChanged events only contain changed fields and do NOT carry "Address"
            // (address never changes). Derive MAC from the BlueZ device path in that case.
            string mac = props.TryGetValue("Address", out var a) && a is string s
                ? s
                : MacFromPath(devicePath);

            if (string.IsNullOrEmpty(mac)) return;

            // Record which adapter last saw this device so the connection service can
            // build the correct BlueZ object path (/org/bluez/<adapter>/dev_...).
            BlueZHelpers.RegisterDeviceAdapter(mac, adapter);

            // Apply MAC prefix filter.
            if (!string.IsNullOrEmpty(_macPrefix) &&
                !mac.StartsWith(_macPrefix, StringComparison.OrdinalIgnoreCase))
                return;

            int rssi = props.TryGetValue("RSSI", out var r) ? Convert.ToInt32(r) : -127;

            // Keep reconstructed ad-data for diagnostics/storage, but decode only manufacturer data.
            string adData = BlueZHelpers.BuildAdDataFromBlueZProps(props);
            string scanData = adData;

            string productNumber = null;
            string lockedHex = null;
            bool? isLocked = null;

            string name = string.Empty;
            var meta = new DetectorMeta();

            // Linux-native discovery must decode from ManufacturerData only.
            byte[] mfBytes = ExtractManufacturerDataBytes(props);
            if (mfBytes == null)
                return;

            if (mfBytes.Length >= 3 && (mfBytes[0] != 0x10 || mfBytes[1] != 0xB9 || mfBytes[2] != 0xF7))
            {
                return;
            }

            TryParseManufacturerData(
                mfBytes,
                mac,
                ref name,
                out productNumber,
                out lockedHex,
                out isLocked,
                out meta);

            // Log raw manufacturer data when product number is still unknown for a Cassia-OUI device —
            // helps diagnose payload format issues. Skip non-Cassia devices (different OUI).
            if (string.IsNullOrWhiteSpace(productNumber))
            {
                _logger.LogInformation("LinuxBLE scan: unknown product for {Mac}, MfData={Hex}",
                    mac, Convert.ToHexString(mfBytes));
                return;
            }
            // Ensure metadata is available even for product-number-only payloads.
            if (string.IsNullOrEmpty(meta.DetectorType))
                meta = ScanDataParser.GetDetectorMeta(productNumber);

            if (productNumber.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return;

            // Normalize: if still empty name but we do have product number, show it.
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(productNumber))
                name = productNumber;

            var device = new ScannedDevicesView
            {
                bdaddrs = new List<AccessAPP.Models.Bdaddre> { new() { Bdaddr = mac } },
                chipId = 0,
                evtType = 0,
                rssi = rssi,
                adData = adData,
                scanData = scanData,
                name = name,
                ProductNumber = productNumber,
                DetectorFamily = meta.DetectorFamily,
                DetectorType = meta.DetectorType,
                DetectorOutputInfo = meta.DetectorOutputInfo,
                DetectorDescription = meta.DetectorDescription,
                DetectorShortDescription = meta.DetectorShortDescription,
                Range = meta.Range,
                DetectorMountDescription = meta.DetectorMountDescription,
                LockedHex = lockedHex,
                IsLocked = isLocked,
                Adapter = adapter
            };

            _deviceStorageService.AddOrUpdateDevice(device, rssi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LinuxBLE scan: error processing device {Path}", devicePath);
        }
    }

    // ── Manufacturer data helpers ────────────────────────────────────────────

    private async Task EnsureDeviceWatcherAsync(
        string devicePath,
        string adapter,
        Dictionary<string, IDisposable> deviceWatchers,
        object deviceWatchersLock)
    {
        lock (deviceWatchersLock)
        {
            if (deviceWatchers.ContainsKey(devicePath))
                return;
        }

        try
        {
            var dev = await BlueZHelpers.GetDeviceAsync(devicePath);
            var sub = await dev.WatchPropertiesAsync(
                changes =>
                {
                    var updatedProps = changes.Changed.ToDictionary(kv => kv.Key, kv => kv.Value);
                    var mergedProps = MergeDevicePropsSnapshot(devicePath, updatedProps);
                    TryProcessDevice(devicePath, mergedProps, adapter);
                },
                ex => _logger.LogDebug(ex, "LinuxBLE scan: PropertiesChanged error on {Path}", devicePath));

            lock (deviceWatchersLock)
            {
                if (deviceWatchers.ContainsKey(devicePath))
                {
                    sub.Dispose();
                    return;
                }

                deviceWatchers[devicePath] = sub;
            }

            // Immediately process full properties once after watcher attach; InterfacesAdded can be partial.
            var fullProps = await dev.GetAllAsync();
            var mergedFullProps = MergeDevicePropsSnapshot(devicePath, fullProps);
            TryProcessDevice(devicePath, mergedFullProps, adapter);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LinuxBLE scan: failed to attach device watcher for {Path}", devicePath);
        }
    }

    private IDictionary<string, object> MergeDevicePropsSnapshot(string devicePath, IDictionary<string, object> incoming)
    {
        lock (_devicePropsLock)
        {
            if (!_devicePropsByPath.TryGetValue(devicePath, out var snapshot))
            {
                snapshot = new Dictionary<string, object>(incoming, StringComparer.OrdinalIgnoreCase);
                _devicePropsByPath[devicePath] = snapshot;
            }
            else
            {
                foreach (var kv in incoming)
                    snapshot[kv.Key] = kv.Value;
            }

            return new Dictionary<string, object>(snapshot, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void RemoveDevicePropsSnapshot(string devicePath)
    {
        lock (_devicePropsLock)
            _devicePropsByPath.Remove(devicePath);
    }

    private void ClearDevicePropsSnapshotForAdapter(string adapterPath)
    {
        lock (_devicePropsLock)
        {
            var toRemove = _devicePropsByPath.Keys
                .Where(path => path.StartsWith(adapterPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var path in toRemove)
                _devicePropsByPath.Remove(path);
        }
    }

    /// <summary>
    /// Extract the raw manufacturer data byte array from BlueZ Device1 properties.
    /// BlueZ stores ManufacturerData as dict{uint16 → byte[]} where the key is the
    /// Bluetooth company ID and the value is the payload WITHOUT the company ID bytes.
    ///
    /// Some devices (or logs) may expose 23 bytes; accept both 24 and 23.
    /// We later validate the first 6 bytes match the expected MAC before parsing.
    /// </summary>
    private static byte[] ExtractManufacturerDataBytes(IDictionary<string, object> props)
    {
        if (!props.TryGetValue("ManufacturerData", out var mfRaw)) return null;

        // Tmds.DBus may deserialise a{qv} as IDictionary<ushort,object> (variant unwrapped
        // to byte[]) or as IDictionary<ushort,byte[]> — handle both.
        IEnumerable<byte[]> payloads = null;

        if (mfRaw is IDictionary<ushort, object> dictObj)
            payloads = dictObj.Values.Select(v => v as byte[]).Where(b => b != null);
        else if (mfRaw is IDictionary<ushort, byte[]> dictBytes)
            payloads = dictBytes.Values;

        if (payloads == null) return null;

        // Prefer 24, then 23.
        return payloads
            .Where(b => b.Length == 24 || b.Length == 23)
            .OrderByDescending(b => b.Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// Parse the Cassia BLE advertisement manufacturer payload as observed in your logs:
    ///
    /// Layout (0-indexed) in the "good" payloads:
    ///   [0-5]   MAC address (6 bytes)  (must match the device MAC)
    ///   [6]     Type/Index byte (numeric detector index, e.g. 0x0B, 0x1D, 0x1E, 0x0D ...)
    ///   [7-21]  15-byte ASCII field (often product-number like "353-651021"/"353_651021",
    ///           or a friendly name like "PLO1" / "0235" / "Y", null-padded)
    ///   [22]    Network ID
    ///   [23]    Locked info (0x01 = locked), sometimes reserved/locked depending on variant
    ///
    /// NOTE: The ASCII '3' == 0x33 you saw is just the FIRST CHARACTER of the ASCII field,
    /// not a "type byte". So we always parse ASCII starting at byte[7].
    /// </summary>

private static void TryParseManufacturerData(
    byte[] bytes,
    string expectedMac,
    ref string name,
    out string productNumber,
    out string lockedHex,
    out bool? isLocked,
    out DetectorMeta meta)
{
    productNumber = null;
    lockedHex = null;
    isLocked = null;
    meta = new DetectorMeta();

    if (bytes == null || bytes.Length != 24)
        return;

    // HARD FILTER: must be Cassia OUI prefix 10:B9:F7
    if (bytes[0] != 0x10 || bytes[1] != 0xB9 || bytes[2] != 0xF7)
        return;

    // Validate payload MAC matches device MAC (rejects garbage / mismatched payloads)
    if (!string.IsNullOrWhiteSpace(expectedMac))
    {
        var payloadMac = $"{bytes[0]:X2}:{bytes[1]:X2}:{bytes[2]:X2}:{bytes[3]:X2}:{bytes[4]:X2}:{bytes[5]:X2}";
        if (!payloadMac.Equals(expectedMac, StringComparison.OrdinalIgnoreCase))
            return;
    }

    // Trailer (based on your examples): [22]=networkId, [23]=locked, [21]=reserved
    byte lockedByte = bytes[23];
    lockedHex = lockedByte.ToString("X2");
    isLocked = lockedByte == 0x01;

    byte typeOrProductFirst = bytes[6];

    // Discriminate variants by byte[6]:
    //   == 0x33 ('3') → product-number-only: bytes[6..20] = 15-byte ASCII product number starting with '3'
    //   anything else → named-sensor: byte[6] = type code, bytes[7..20] = 14-byte ASCII sensor name
    if (typeOrProductFirst == 0x33)
    {
        // Product-number-only variant:
        // [6..20] = 15-byte ASCII product number
        var pn = Encoding.ASCII.GetString(bytes, 6, 15).TrimEnd('\0').Trim();
        if (!string.IsNullOrWhiteSpace(pn))
        {
            productNumber = pn;
            if (string.IsNullOrWhiteSpace(name)) name = pn; // no custom name in this variant
            meta = ScanDataParser.GetDetectorMeta(productNumber);
        }
        return;
    }

    // Named-sensor variant:
    // [6] = type code → look up product number in DetectorMetaData.NumberToMetadata
    // [7..20] = 14-byte ASCII sensor name
    byte typeIndex = bytes[6];

    var parsedName = Encoding.ASCII.GetString(bytes, 7, 14).TrimEnd('\0').Trim();
    if (!string.IsNullOrWhiteSpace(parsedName))
        name = parsedName;

    if (DetectorMetaData.NumberToMetadata.TryGetValue(typeIndex, out var m))
    {
        meta = m;
        productNumber = meta.Name;
    }
}

 /// <summary>
    /// Derive the colon-separated MAC address from a BlueZ device object path.
    /// e.g. "/org/bluez/hci0/dev_10_B9_F7_0F_CB_90" → "10:B9:F7:0F:CB:90"
    /// </summary>
    private static string MacFromPath(string path)
    {
        const string devPrefix = "/dev_";
        int idx = path.LastIndexOf(devPrefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return string.Empty;
        return path.Substring(idx + devPrefix.Length).Replace("_", ":");
    }

    private bool ShouldPauseScan()
        => _firmUpgradeService.UpgradeDevicesInProgress > 1 && !RuntimeVariables.BLE_SCAN_UNDER_PROGRAMMING;

    public void Dispose() => _disposed = true;
}

