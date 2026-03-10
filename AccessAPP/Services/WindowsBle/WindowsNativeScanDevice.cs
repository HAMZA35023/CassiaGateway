#if WINDOWS
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using Windows.Devices.Bluetooth.Advertisement;

namespace AccessAPP.Services.WindowsBle;

/// <summary>
/// Windows-native BLE device scanner using <see cref="BluetoothLEAdvertisementWatcher"/>.
/// Drop-in replacement for <see cref="ScanBleDevice"/> when <c>BLE_BACKEND = "windows-native"</c>.
///
/// Behaviour:
///   • Starts an active BLE scan via the Windows BLE stack.
///   • Filters advertisements for the Cassia manufacturer OUI prefix (default <c>10:B9:F7</c>).
///   • Parses the 24-byte manufacturer data payload (same format as Linux native / Cassia SSE).
///   • Feeds enriched <see cref="ScannedDevicesView"/> entries into <see cref="DeviceStorageService"/>.
/// </summary>
public class WindowsNativeScanDevice : IDisposable
{
    private readonly DeviceStorageService _deviceStorageService;
    private readonly CassiaFirmwareUpgradeService _firmUpgradeService;
    private readonly ILogger<WindowsNativeScanDevice> _logger;

    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly string _macPrefix;
    private bool _disposed;

    // ── Company ID used by Cassia in the BLE advertisement ManufacturerData ─
    // The 16-bit company ID (little-endian in the wire format) must be matched
    // to identify the correct ManufacturerData entry in multi-company payloads.
    // 0xB910 corresponds to the Cassia OUI prefix 10:B9 (MSB/LSB swapped).
    private const ushort CassiaCompanyId = 0xB910;

    public WindowsNativeScanDevice(
        DeviceStorageService deviceStorageService,
        CassiaFirmwareUpgradeService firmUpgradeService,
        ILogger<WindowsNativeScanDevice> logger)
    {
        _deviceStorageService = deviceStorageService;
        _firmUpgradeService = firmUpgradeService;
        _logger = logger;
        _macPrefix = RuntimeVariables.WINDOWS_BLE_MAC_PREFIX;

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        _watcher.Received += OnAdvertisementReceived;
        _watcher.Stopped += OnWatcherStopped;

        _watcher.Start();
        _logger.LogInformation("WinBLE scan: watcher started (macPrefix={Prefix})", _macPrefix);
    }

    // ── Watcher events ────────────────────────────────────────────────────────

    private void OnAdvertisementReceived(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (_disposed || ShouldPauseScan()) return;

        try
        {
            string mac = WindowsBleHelpers.UlongToMacString(args.BluetoothAddress);

            // Apply MAC prefix filter.
            if (!string.IsNullOrEmpty(_macPrefix) &&
                !mac.StartsWith(_macPrefix, StringComparison.OrdinalIgnoreCase))
                return;

            int rssi = args.RawSignalStrengthInDBm;

            // Extract manufacturer data bytes from the advertisement.
            byte[]? mfBytes = ExtractManufacturerDataBytes(args.Advertisement, mac);
            if (mfBytes == null) return;

            // Validate OUI prefix 10:B9:F7
            if (mfBytes.Length >= 3 && (mfBytes[0] != 0x10 || mfBytes[1] != 0xB9 || mfBytes[2] != 0xF7))
                return;

            string name = string.Empty;
            TryParseManufacturerData(
                mfBytes, mac,
                ref name,
                out string? productNumber,
                out string? lockedHex,
                out bool? isLocked,
                out DetectorMeta meta);

            if (string.IsNullOrWhiteSpace(productNumber))
            {
                _logger.LogDebug("WinBLE scan: unknown product for {Mac}, MfData={Hex}",
                    mac, Convert.ToHexString(mfBytes));
                return;
            }

            if (string.IsNullOrEmpty(meta.DetectorType))
                meta = ScanDataParser.GetDetectorMeta(productNumber);

            if (productNumber.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return;

            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(productNumber))
                name = productNumber;

            // Build a minimal ad-data hex string from the raw manufacturer payload.
            string adData = Convert.ToHexString(mfBytes);

            var device = new ScannedDevicesView
            {
                bdaddrs = new List<Bdaddre> { new() { Bdaddr = mac } },
                chipId = 0,
                evtType = 0,
                rssi = rssi,
                adData = adData,
                scanData = adData,
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
                Adapter = "windows"
            };

            _deviceStorageService.AddOrUpdateDevice(device, rssi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WinBLE scan: error processing advertisement");
        }
    }

    private void OnWatcherStopped(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (_disposed) return;
        _logger.LogWarning("WinBLE scan: watcher stopped (error={Error}) — restarting", args.Error);
        try { _watcher.Start(); } catch (Exception ex)
        {
            _logger.LogError(ex, "WinBLE scan: failed to restart watcher");
        }
    }

    // ── Manufacturer data extraction ─────────────────────────────────────────

    /// <summary>
    /// Extract the raw 24-byte Cassia manufacturer payload from the advertisement.
    ///
    /// Windows BLE advertisement delivers ManufacturerData entries as
    /// <see cref="BluetoothLEManufacturerData"/> where <c>CompanyId</c> is the 16-bit
    /// company identifier and <c>Data</c> is the payload WITHOUT the company ID bytes.
    ///
    /// The full on-wire payload is [companyId_lo, companyId_hi, ...payload...].
    /// We reconstruct the full 24-byte buffer (matching the Linux/Cassia format where
    /// bytes [0..5] = MAC address) by prepending the OUI bytes from the MAC address.
    /// </summary>
    private static byte[]? ExtractManufacturerDataBytes(BluetoothLEAdvertisement advertisement, string mac)
    {
        foreach (var mfEntry in advertisement.ManufacturerData)
        {
            byte[] payload = mfEntry.Data.ToArray();
            // Some stacks omit the 0xF7 third byte from the company ID field;
            // reconstruct the 24-byte buffer from the MAC + remaining payload.
            // Accept both 22-byte payload (+ 2 company-ID bytes = 24 total) and
            // direct 24-byte payloads where the full MAC is embedded.

            // Attempt 1: the full 24-byte MAC-first format is already in the payload.
            if (payload.Length == 24)
                return payload;

            // Attempt 2: reconstruct from MAC + 22-byte payload (company ID stripped).
            if (payload.Length == 22)
            {
                try
                {
                    var macParts = mac.Split(':');
                    if (macParts.Length != 6) continue;
                    var full = new byte[24];
                    for (int i = 0; i < 6; i++)
                        full[i] = Convert.ToByte(macParts[i], 16);
                    Array.Copy(payload, 0, full, 6, 16);
                    // bytes [22..23] are networkId + locked; copy remaining payload bytes.
                    if (payload.Length >= 18)
                    {
                        full[22] = payload[16];
                        full[23] = payload[17];
                    }
                    return full;
                }
                catch { /* ignore malformed entries */ }
            }
        }

        return null;
    }

    // ── Manufacturer data parsing (mirrors LinuxNativeScanDevice) ─────────────
    //
    // Layout (0-indexed) of the 24-byte payload:
    //   [0-5]   MAC address (6 bytes)
    //   [6]     Type/Index byte (0x33 = product-number-only variant)
    //   [7-21]  15-byte ASCII field (product number or sensor name)
    //   [22]    Network ID
    //   [23]    Locked info (0x01 = locked)

    private static void TryParseManufacturerData(
        byte[] bytes,
        string expectedMac,
        ref string name,
        out string? productNumber,
        out string? lockedHex,
        out bool? isLocked,
        out DetectorMeta meta)
    {
        productNumber = null;
        lockedHex = null;
        isLocked = null;
        meta = new DetectorMeta();

        if (bytes == null || bytes.Length != 24) return;

        if (bytes[0] != 0x10 || bytes[1] != 0xB9 || bytes[2] != 0xF7) return;

        if (!string.IsNullOrWhiteSpace(expectedMac))
        {
            var payloadMac = $"{bytes[0]:X2}:{bytes[1]:X2}:{bytes[2]:X2}:{bytes[3]:X2}:{bytes[4]:X2}:{bytes[5]:X2}";
            if (!payloadMac.Equals(expectedMac, StringComparison.OrdinalIgnoreCase))
                return;
        }

        byte lockedByte = bytes[23];
        lockedHex = lockedByte.ToString("X2");
        isLocked = lockedByte == 0x01;

        byte typeOrProductFirst = bytes[6];

        if (typeOrProductFirst == 0x33)
        {
            var pn = Encoding.ASCII.GetString(bytes, 6, 15).TrimEnd('\0').Trim();
            if (!string.IsNullOrWhiteSpace(pn))
            {
                productNumber = pn;
                if (string.IsNullOrWhiteSpace(name)) name = pn;
                meta = ScanDataParser.GetDetectorMeta(productNumber);
            }
            return;
        }

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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool ShouldPauseScan()
        => _firmUpgradeService.UpgradeDevicesInProgress > 1 && !RuntimeVariables.BLE_SCAN_UNDER_PROGRAMMING;

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _watcher.Received -= OnAdvertisementReceived;
            _watcher.Stopped -= OnWatcherStopped;
            _watcher.Stop();
        }
        catch { /* best-effort */ }
    }
}
#endif
