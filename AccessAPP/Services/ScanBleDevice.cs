using AccessAPP.Models;
using AccessAPP.Services;
using AccessAPP.Services.HelperClasses;
using System.Text.Json;

public class ScanBleDevice : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _scanSourceUrl;
    private static bool _isScanning = false;
    private static Task _scanningTask;
    private static readonly object _lock = new();
    private readonly ILogger<ScanBleDevice> _logger;
    private readonly CassiaConnectService _connectService;
    private readonly DeviceStorageService _deviceStorageService;
    private readonly string _targetMacAddressPrefix = "10:B9:F7";
    private readonly string _targetMacAddress = "10:B9:F7*";
    private static bool _buttonPressed = false; // Track if the button has been pressed
    private CassiaFirmwareUpgradeService _firmUpgradeService;

    public ScanBleDevice(HttpClient httpClient, IConfiguration configuration, ILogger<ScanBleDevice> logger, CassiaConnectService connectService, DeviceStorageService deviceStorageService, CassiaFirmwareUpgradeService firmUpgradeService)
    {
        _connectService = connectService;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _deviceStorageService = deviceStorageService;
        _firmUpgradeService = firmUpgradeService;
        // Read IP and Port from appsettings.json
        string gatewayIpAddress = configuration.GetValue<string>("GatewayConfiguration:IpAddress");
        int gatewayPort = configuration.GetValue<int>("GatewayConfiguration:Port");

        _scanSourceUrl = $"http://{gatewayIpAddress}:{gatewayPort}/gap/nodes?event=1&filter_duplicates=1&filter_mac={_targetMacAddress}&active=1";

        StartScanListener();
    }

    private void StartScanListener()
    {
        lock (_lock)
        {
            if (_isScanning) return;

            _isScanning = true;
            _scanningTask = Task.Run(() => StartScanning());
        }
    }

    private async Task StartScanning()
    {
        while (true)
        {
            if (_firmUpgradeService.UpgradeDevicesInProgress > 0)
            {
                await Task.Delay(10000); // Retry delay
                continue;
            }

            try
            {
                _logger.LogInformation("SSE BLE scan listener started.");
                HttpResponseMessage response = await _httpClient.GetAsync(_scanSourceUrl, HttpCompletionOption.ResponseHeadersRead);

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new System.IO.StreamReader(stream);

                while (true)
                {
                    if (_firmUpgradeService.UpgradeDevicesInProgress > 0)
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

                        //Console.WriteLine(line);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in BLE scanning: {ex.Message}");
                await Task.Delay(5000); // Retry delay
            }
        }
    }

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
                _logger.LogInformation($"Device MAC={mac}, Locked={isLocked}, Product={productNumber}");
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

