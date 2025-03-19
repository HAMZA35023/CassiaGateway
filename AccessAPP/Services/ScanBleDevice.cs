using AccessAPP.Models;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public class ScanBleDevice : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _scanSourceUrl;
    private static bool _isScanning = false;
    private static Task _scanningTask;
    private static readonly object _lock = new();
    private readonly ILogger<ScanBleDevice> _logger;

    private readonly string _targetMacAddressPrefix = "E2:15:00";
    private readonly string _targetMacAddress = "E2:15:00*";
    private static bool _buttonPressed = false; // Track if the button has been pressed

    public ScanBleDevice(HttpClient httpClient, IConfiguration configuration, ILogger<ScanBleDevice> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Read IP and Port from appsettings.json
        string gatewayIpAddress = configuration.GetValue<string>("GatewayConfiguration:IpAddress");
        int gatewayPort = configuration.GetValue<int>("GatewayConfiguration:Port");

        _scanSourceUrl = $"http://{gatewayIpAddress}:{gatewayPort}/gap/nodes?event=1&filter_mac={_targetMacAddress}";

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
            try
            {
                _logger.LogInformation("SSE BLE scan listener started.");
                HttpResponseMessage response = await _httpClient.GetAsync(_scanSourceUrl, HttpCompletionOption.ResponseHeadersRead);

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new System.IO.StreamReader(stream);

                while (true)
                {
                    string line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line) || line.Equals(":keep-alive"))
                    {
                        continue;
                    }

                    if (line.StartsWith("data:"))
                    {
                        line = line.Substring("data:".Length).Trim();
                        Task.Run(() => ProcessScannedDevice(line));

                        Console.WriteLine(line);
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

    private void ProcessScannedDevice(string scanData)
    {
        try
        {
            var scannedDevice = JsonSerializer.Deserialize<ScannedBleDevicesView>(scanData);
            if (scannedDevice == null || scannedDevice.Bdaddrs == null || scannedDevice.Bdaddrs.Length == 0)
            {
                _logger.LogWarning("Invalid BLE scan event data.");
                return;
            }

            string macAddress = scannedDevice.Bdaddrs[0].Bdaddr;
            string adData = scannedDevice.AdData;
            PrintButtonState(macAddress, adData);
            if (macAddress.StartsWith(_targetMacAddressPrefix))
            {
                if (!_buttonPressed)
                {
                    // First valid button press detected
                    _logger.LogInformation($"Processing BLE Button Press for {macAddress}");
                    _buttonPressed = true; // Lock until timeout expires
                    HandleDetectedDeviceAsync(macAddress);

                    // Reset after 5 seconds to allow next press
                    Task.Run(async () =>
                    {
                        await Task.Delay(5000); // Wait 5 seconds
                        _buttonPressed = false;
                        _logger.LogInformation("Button press reset, ready for next press.");
                    });
                }
                else
                {
                    _logger.LogInformation($"⏳ Ignoring duplicate event for {macAddress}, button is still locked.");
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError($"Error parsing JSON in BLE scan event: {ex.Message}");
        }
    }

    private void PrintButtonState(string macAddress, string adData)
    {
        if (adData.Length < 24)
        {
            Console.WriteLine($"Invalid BLE advertisement data: {adData}");
            return;
        }

        // Extract Switch Status byte (position based on telegram format)
        string switchStatusHex = adData.Substring(20, 2); // Example offset, adjust if needed
        int switchStatus = Convert.ToInt32(switchStatusHex, 16);

        // Check bit 0 for press/release event
        bool isPressed = (switchStatus & 0x01) == 1;

        // Print event type
        if (isPressed)
        {
            Console.WriteLine($"Button Pressed: {macAddress}");
        }
        else
        {
            Console.WriteLine($"Button Released: {macAddress}");
        }
    }
    private async Task HandleDetectedDeviceAsync(string macAddress)
    {
        _logger.LogInformation($"Handling BLE Button Press for device {macAddress}");
        if (macAddress == "E2:15:00:00:54:A1")
        {
            Console.WriteLine("Detected Special BLE Button for Light Control!");

            // **Call the API directly**
            //await CallBatchLightControlApi();
        }
        else if (macAddress == "E2:15:00:00:F8:C3")
        {
            PlayPauseMusic();
        }

    }

    /// <summary>
    /// Calls the BatchLightControl API endpoint
    /// </summary>
    private async Task CallBatchLightControlApi()
    {
        try
        {
            using (var httpClient = new HttpClient())
            {
                string apiUrl = "http://localhost:60000/Cassia/batchlightcontrol"; // Change to your actual API URL

                // **Hardcoded request body**
                var request = new BatchLightControlRequest
                {
                    MacAddresses = new List<string> { "10:B9:F7:12:A2:8A", "10:B9:F7:0F:CB:23", "10:B9:F7:0F:CB:40", "10:B9:F7:0F:CB:80", "10:B9:F7:0F:CB:67", "10:B9:F7:0F:CB:72" },
                    HexLoginValue = "012A0210003206D20400012E02000000"
                };

                // Serialize the request
                var jsonContent = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

                // Send the HTTP request
                HttpResponseMessage response = await httpClient.PostAsync(apiUrl, jsonContent);

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine("Successfully triggered BatchLightControl API!");
                }
                else
                {
                    Console.WriteLine($"Failed to trigger API. Status Code: {response.StatusCode}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error calling API: {ex.Message}");
        }
    }
    private void PlayPauseMusic()
    {
        _logger.LogInformation("Windows: Simulating Play/Pause Media Key.");

        // Simulate Play/Pause Key on Windows
        keybd_event(VK_MEDIA_PLAY_PAUSE, 0, 0, 0);
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    // Windows Media Key Simulation
    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, IntPtr extraInfo);

    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
}

// Data structure for scanned BLE devices
public class ScannedBleDevicesView
{
    [JsonPropertyName("chipId")]
    public int ChipId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("evtType")]
    public int EvtType { get; set; }

    [JsonPropertyName("rssi")]
    public int Rssi { get; set; }

    [JsonPropertyName("adData")]
    public string AdData { get; set; }

    [JsonPropertyName("bdaddrs")]
    public BdaddrInfo[] Bdaddrs { get; set; }
}

// Data structure for MAC addresses in the BLE scan event
public class BdaddrInfo
{
    [JsonPropertyName("bdaddr")]
    public string Bdaddr { get; set; }

    [JsonPropertyName("bdaddrType")]
    public string BdaddrType { get; set; }
}
