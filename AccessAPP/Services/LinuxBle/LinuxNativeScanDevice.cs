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
///   • Reconstructs the adData/scanData hex fields from BlueZ typed advertisement
///     properties so that the existing ScanDataParser logic can run unchanged.
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
            ["DuplicateData"] = true   // receive updates for already-known devices
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

        // Watch for new or updated devices.
        using var addedSub = await objMgr.WatchInterfacesAddedAsync(
            args =>
            {
                var (path, interfaces) = args;
                if (interfaces.TryGetValue("org.bluez.Device1", out var props))
                    TryProcessDevice(path.ToString(), props);
            },
            ex => _logger.LogError(ex, "LinuxBLE scan: InterfacesAdded error"));

        // Also watch PropertiesChanged on all Device1 objects so we pick up
        // RSSI and advertisement-data updates as a device keeps advertising.
        var deviceWatchers = new List<IDisposable>();
        foreach (var (path, interfaces) in existing)
        {
            if (!interfaces.ContainsKey("org.bluez.Device1")) continue;
            var pathStr = path.ToString();
            var dev = await BlueZHelpers.GetDeviceAsync(pathStr);
            var sub = await dev.WatchPropertiesAsync(
                changes =>
                {
                    // Rebuild the full property dict from changes.Changed for this update.
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
            string mac = props.TryGetValue("Address", out var a) && a is string s ? s : string.Empty;
            if (string.IsNullOrEmpty(mac)) return;

            // Apply MAC prefix filter.
            if (!string.IsNullOrEmpty(_macPrefix) &&
                !mac.StartsWith(_macPrefix, StringComparison.OrdinalIgnoreCase))
                return;

            int rssi = props.TryGetValue("RSSI", out var r) ? Convert.ToInt32(r) : -127;

            // Reconstruct ad-data hex from BlueZ typed advertisement properties.
            string adData = BlueZHelpers.BuildAdDataFromBlueZProps(props);

            // BlueZ combines advert + scan-response into ManufacturerData etc.
            // Use the same hex for both adData and scanData so ScanDataParser
            // finds the product bytes regardless of which field it checks.
            string scanData = adData;

            string productNumber = null;
            string lockedHex = null;
            bool? isLocked = null;
            string name = props.TryGetValue("Name", out var n) && n is string nm ? nm : string.Empty;
            var meta = new DetectorMeta();

            if (!string.IsNullOrEmpty(scanData))
            {
                productNumber = ScanDataParser.ExtractProductNumber(scanData);
                if (scanData.Length >= 50)
                    name = ScanDataParser.GetName(scanData.Substring(20, 30));
                lockedHex = ScanDataParser.GetLockedInfo(scanData);
                isLocked = ScanDataParser.IsLocked(scanData);
                meta = ScanDataParser.GetDetectorMeta(scanData);
            }

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

    private bool ShouldPauseScan()
        => _firmUpgradeService.UpgradeDevicesInProgress > 1 && !RuntimeVariables.BLE_SCAN_UNDER_PROGRAMMING;

    public void Dispose() => _disposed = true;
}
