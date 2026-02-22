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

        _ = Task.Run(RunScanLoopAsync);
    }

    // ── Main scan loop ───────────────────────────────────────────────────────

    private async Task RunScanLoopAsync()
    {
        while (!_disposed)
        {
            try
            {
                await ScanAsync();
            }
            catch (Exception ex) when (!_disposed)
            {
                _logger.LogError(ex, "LinuxBLE scan: error in scan loop, retrying in 5s");
                await Task.Delay(5000);
            }
        }
    }

    private async Task ScanAsync()
    {
        var adapter = await BlueZHelpers.GetAdapterAsync();
        var objMgr = await BlueZHelpers.GetObjectManagerAsync();

        // Set BLE-only discovery filter.
        await adapter.SetDiscoveryFilterAsync(new Dictionary<string, object>
        {
            ["Transport"] = "le",
            ["DuplicateData"] = true // receive updates for already-known devices
        });

        await adapter.StartDiscoveryAsync();
        _logger.LogInformation("LinuxBLE scan: discovery started on {Adapter}", RuntimeVariables.LINUX_BLE_ADAPTER);

        // Process devices that were already cached in BlueZ from prior scans.
        var existing = await objMgr.GetManagedObjectsAsync();
        foreach (var (path, interfaces) in existing)
        {
            if (interfaces.TryGetValue("org.bluez.Device1", out var props))
                TryProcessDevice(path.ToString(), props);
        }

        // Watch for new devices.
        using var addedSub = await objMgr.WatchInterfacesAddedAsync(
            args =>
            {
                var (path, interfaces) = args;
                if (interfaces.TryGetValue("org.bluez.Device1", out var props))
                    TryProcessDevice(path.ToString(), props);
            },
            ex => _logger.LogError(ex, "LinuxBLE scan: InterfacesAdded error"));

        // Also watch PropertiesChanged on all Device1 objects so we pick up RSSI and ad updates.
        var deviceWatchers = new List<IDisposable>();
        foreach (var (path, interfaces) in existing)
        {
            if (!interfaces.ContainsKey("org.bluez.Device1")) continue;

            var pathStr = path.ToString();
            var dev = await BlueZHelpers.GetDeviceAsync(pathStr);

            var sub = await dev.WatchPropertiesAsync(
                changes =>
                {
                    // PropertiesChanged only carries the changed fields (no "Address").
                    var updatedProps = changes.Changed.ToDictionary(kv => kv.Key, kv => kv.Value);
                    TryProcessDevice(pathStr, updatedProps);
                },
                ex => _logger.LogDebug(ex, "LinuxBLE scan: PropertiesChanged error on {Path}", pathStr));

            deviceWatchers.Add(sub);
        }

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
        foreach (var w in deviceWatchers) w.Dispose();
        try { await adapter.StopDiscoveryAsync(); } catch { /* best-effort */ }
    }

    // ── Device processing ────────────────────────────────────────────────────

    private void TryProcessDevice(string devicePath, IDictionary<string, object> props)
    {
        try
        {
            // PropertiesChanged events only contain changed fields and do NOT carry "Address"
            // (address never changes). Derive MAC from the BlueZ device path in that case.
            string mac = props.TryGetValue("Address", out var a) && a is string s
                ? s
                : MacFromPath(devicePath);

            if (string.IsNullOrEmpty(mac)) return;

            // Apply MAC prefix filter.
            if (!string.IsNullOrEmpty(_macPrefix) &&
                !mac.StartsWith(_macPrefix, StringComparison.OrdinalIgnoreCase))
                return;

            int rssi = props.TryGetValue("RSSI", out var r) ? Convert.ToInt32(r) : -127;

            // Build ad-data hex for storage / ScanDataParser fallback.
            string adData = BlueZHelpers.BuildAdDataFromBlueZProps(props);
            string scanData = adData;

            string productNumber = null;
            string lockedHex = null;
            bool? isLocked = null;

            string name = props.TryGetValue("Name", out var n) && n is string nm ? nm : string.Empty;
            var meta = new DetectorMeta();

            // Prefer direct parsing of Cassia manufacturer payload when present + valid.
            byte[] mfBytes = ExtractManufacturerDataBytes(props);
            if (mfBytes != null)
            {
                TryParseManufacturerData(
                    mfBytes,
                    mac,
                    ref name,
                    out productNumber,
                    out lockedHex,
                    out isLocked,
                    out meta);
            }

            // If direct parsing didn't yield anything useful, fall back to ScanDataParser on TLV-wrapped hex.
            if ((string.IsNullOrEmpty(productNumber) && string.IsNullOrEmpty(name)) && !string.IsNullOrEmpty(scanData))
            {
                productNumber = ScanDataParser.ExtractProductNumber(scanData);

                if (scanData.Length >= 50)
                    name = ScanDataParser.GetName(scanData.Substring(20, 30));

                lockedHex = ScanDataParser.GetLockedInfo(scanData);
                isLocked = ScanDataParser.IsLocked(scanData);
                meta = ScanDataParser.GetDetectorMeta(scanData);
            }
            else
            {
                // Ensure meta is enriched if we ended up with a productNumber.
                if (!string.IsNullOrEmpty(productNumber) && string.IsNullOrEmpty(meta.DetectorType))
                    meta = ScanDataParser.GetDetectorMeta(productNumber);
            }

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
                IsLocked = isLocked
            };

            _deviceStorageService.AddOrUpdateDevice(device, rssi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LinuxBLE scan: error processing device {Path}", devicePath);
        }
    }

    // ── Manufacturer data helpers ────────────────────────────────────────────

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

    if (typeOrProductFirst == 0x33)
    {
        // Product-number-only variant:
        // [6..20] = 15-byte ASCII product number
        var pn = Encoding.ASCII.GetString(bytes, 6, 15).TrimEnd('\0').Trim();
        if (!string.IsNullOrWhiteSpace(pn))
        {
            productNumber = pn;
            name = pn; // no custom name in this variant
            meta = ScanDataParser.GetDetectorMeta(productNumber);
        }
        return;
    }

    // Named-sensor variant:
    // [6] = typeIndex
    // [7..21] = 15-byte ASCII name
    byte typeIndex = typeOrProductFirst;

    var parsedName = Encoding.ASCII.GetString(bytes, 7, 15).TrimEnd('\0').Trim();
    if (!string.IsNullOrWhiteSpace(parsedName))
        name = parsedName;

    // Product/meta from mapping (but DO NOT force productNumber to "unknown" if mapping fails)
    if (DetectorMetaData.NumberToMetadata.TryGetValue(typeIndex, out var m))
    {
        meta = m;
        productNumber = meta.Name;
    }

    if (!string.IsNullOrEmpty(productNumber) && string.IsNullOrEmpty(meta.DetectorType))
        meta = ScanDataParser.GetDetectorMeta(productNumber);
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