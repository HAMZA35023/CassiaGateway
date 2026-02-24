using AccessAPP;
using AccessAPP.Models;
using AccessAPP.Services;
using AccessAPP.Services.HelperClasses;
using System.Collections.Concurrent;
using System.Text.Json;

public class ScanBleDevice : IDisposable
{
    private readonly HttpClient _httpClient;
    private static bool _isScanning = false;
    private static List<Task> _scanningTasks = new();
    private static readonly object _lock = new();
    private readonly ILogger<ScanBleDevice> _logger;
    private readonly CassiaConnectService _connectService;
    private readonly DeviceStorageService _deviceStorageService;
    private readonly string _targetMacAddressPrefix = "10:B9:F7";
    private readonly string _targetMacAddress = "10:B9:F7*";
    private readonly string _gatewayIpAddress;
    private readonly int _gatewayPort;
    private static bool _buttonPressed = false; // Track if the button has been pressed
    private CassiaFirmwareUpgradeService _firmUpgradeService;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastProcessedPerMac
    = new ConcurrentDictionary<string, DateTimeOffset>();

    private static readonly TimeSpan PerMacParseInterval = TimeSpan.FromSeconds(10);
    public ScanBleDevice(HttpClient httpClient, IConfiguration configuration, ILogger<ScanBleDevice> logger, CassiaConnectService connectService, DeviceStorageService deviceStorageService, CassiaFirmwareUpgradeService firmUpgradeService)
    {
        _connectService = connectService;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _deviceStorageService = deviceStorageService;
        _firmUpgradeService = firmUpgradeService;
        // Read IP and Port from appsettings.json
        _gatewayIpAddress = configuration.GetValue<string>("GatewayConfiguration:IpAddress") ?? string.Empty;
        _gatewayPort = configuration.GetValue<int>("GatewayConfiguration:Port");

        StartScanListener();
    }

    private void StartScanListener()
    {
        lock (_lock)
        {
            if (_isScanning) return;

            _isScanning = true;
            _scanningTasks = new List<Task>
            {
                Task.Run(() => StartScanning(null)),
                Task.Run(() => StartScanning(0)),
                Task.Run(() => StartScanning(1)),
                Task.Run(() => StartScanning(2))
            };
        }
    }

    private async Task StartScanning(int? chip)
    {
        while (true)
        {
            if (!ShouldRunScanListener(chip) || ShouldPauseScan())
            {
                await Task.Delay(2000); // Retry delay
                continue;
            }

            try
            {
                var scanSourceUrl = BuildScanSourceUrl(chip);
                _logger.LogInformation($"SSE BLE scan listener started. chip={ChipLabel(chip)}, mode={RuntimeVariables.BLE_SCAN_CHIP_MODE}, url={scanSourceUrl}");
                HttpResponseMessage response = await _httpClient.GetAsync(scanSourceUrl, HttpCompletionOption.ResponseHeadersRead);

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new System.IO.StreamReader(stream);

                while (true)
                {
                    if (!ShouldRunScanListener(chip) || ShouldPauseScan())
                    {
                        break;
                    }

                    string line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line) || line.Equals(":keep-alive"))
                    {
                        continue;
                    }

                    if (line.StartsWith("data:"))
                    {
                        line = line.Substring("data:".Length).Trim();
                        Task.Run(() => ProcessScannedDevice(line));

                        // AppLog.Verbose(line);
}
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in BLE scanning (chip={ChipLabel(chip)}): {ex.Message}");
                await Task.Delay(5000); // Retry delay
            }
        }
    }

    private bool ShouldPauseScan()
        => _firmUpgradeService.UpgradeDevicesInProgress > 1 && !RuntimeVariables.BLE_SCAN_UNDER_PROGRAMMING;

    private bool ShouldRunScanListener(int? chip)
    {
        var mode = NormalizeScanChipMode(RuntimeVariables.BLE_SCAN_CHIP_MODE);
        return mode switch
        {
            -1 => chip == null,
            0 => chip == 0,
            1 => chip == 1,
            2 => chip == 2,
            _ => chip == 0
        };
    }

    private static int NormalizeScanChipMode(int mode)
        => mode is -1 or 0 or 1 or 2 ? mode : 0;

    private string BuildScanSourceUrl(int? chip)
    {
        var url = $"http://{_gatewayIpAddress}:{_gatewayPort}/gap/nodes?event=1&filter_duplicates=0&filter_mac={_targetMacAddress}&active=1";
        if (chip.HasValue)
        {
            if (chip.Value == 2)
                url = AppendQueryParam(url, "chip", "all");
            else
                url = AppendQueryParam(url, "chip", chip.Value.ToString());
        }
        return url;
    }

    private static string AppendQueryParam(string url, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var sep = url.Contains("?") ? "&" : "?";
        return $"{url}{sep}{key}={Uri.EscapeDataString(value)}";
    }

    private static string ChipLabel(int? chip) => chip.HasValue ? chip.Value.ToString() : "default";

    private void ProcessScannedDevice(string line)
    {
        try
        {
            var eventData = JsonSerializer.Deserialize<ScannedDevicesView>(line, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (eventData?.bdaddrs == null || eventData.bdaddrs.Count == 0)
                return;

            var mac = eventData.bdaddrs.First().Bdaddr;
            if (string.IsNullOrWhiteSpace(mac)) return;

            string productNumber = null;
            string lockedHex = null;
            bool? isLocked = null;
            string name = eventData.name;
            DetectorMeta meta = new(); // Default empty

            if (!string.IsNullOrEmpty(eventData.scanData))
            {

                var now = DateTimeOffset.UtcNow;

                // ---- PER-MAC 10s DEBOUNCE ----
                if (_lastProcessedPerMac.TryGetValue(mac, out var lastTs) &&
                    now - lastTs < PerMacParseInterval)
                {
                    return;
                }

                _lastProcessedPerMac[mac] = now;

                productNumber = ScanDataParser.ExtractProductNumber(eventData.scanData);
                if (eventData.scanData.Length >= 50)
                {
                    name = ScanDataParser.GetName(eventData.scanData.Substring(20, 30));
                }
                else
                {
                    name = "";
                    _logger.LogWarning($"Scan data too short for name extraction: {eventData.scanData.Length} chars. Data: {eventData.scanData}");
                }


                lockedHex = ScanDataParser.GetLockedInfo(eventData.scanData);
                isLocked = ScanDataParser.IsLocked(eventData.scanData);
                meta = ScanDataParser.GetDetectorMeta(eventData.scanData);
            }

            var enrichedDevice = new ScannedDevicesView
            {
                bdaddrs = eventData.bdaddrs,
                chipId = eventData.chipId,
                evtType = eventData.evtType,
                rssi = eventData.rssi,
                adData = eventData.adData,
                scanData = eventData.scanData,
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


            if (!string.IsNullOrEmpty(eventData.scanData))
            {
                _deviceStorageService.AddOrUpdateDevice(enrichedDevice, enrichedDevice.rssi);
                //_logger.LogInformation($"Device MAC={mac}, Locked={isLocked}, Product={productNumber}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error processing scanned device: {ex.Message}");
        }
    }

    public void Dispose()
    {
        //_httpClient?.Dispose();
    }
}

