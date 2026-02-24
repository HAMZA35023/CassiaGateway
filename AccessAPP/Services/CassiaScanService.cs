using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace AccessAPP.Services
{
    public class CassiaScanService
    {
        private readonly HttpClient _httpClient;
        private readonly List<ScannedDevicesView> _eventDataList;
        private readonly CassiaConnectService _cassiaConnectService;
        private readonly DeviceStorageService _deviceStorageService;
        private readonly CassiaNotificationService _notificationService;
        private readonly IConfiguration _configuration;

        public CassiaScanService(HttpClient httpClient, DeviceStorageService deviceStorageService, IConfiguration configuration, CassiaNotificationService notificationService)
        {
            _httpClient = httpClient;
            _eventDataList = new List<ScannedDevicesView>();
            _notificationService = notificationService;
            _cassiaConnectService = new CassiaConnectService(httpClient, configuration, notificationService);
            _deviceStorageService = deviceStorageService;
            _configuration = configuration;
        }

        public async Task<List<ScannedDevicesView>> ScanForBleDevices(string gatewayIpAddress, int gatewayPort)
        {
            try
            {
                const int scanDurationMs = 5000;
                var uniqueDevices = new ConcurrentDictionary<string, ScannedDevicesView>(StringComparer.OrdinalIgnoreCase);
                var baseSseEndpoint = $"http://{gatewayIpAddress}:{gatewayPort}/gap/nodes?event=1&filter_duplicates=1&filter_mac=10:B9:F7*&active=1";
                var sseEndpoints = BuildScanEndpoints(baseSseEndpoint);
                using var cancellationTokenSource = new CancellationTokenSource(scanDurationMs);

                var scanTasks = sseEndpoints.Select(endpoint =>
                    ProcessSSEEndpoint(endpoint, eventData =>
                    {
                        var mac = eventData.bdaddrs.FirstOrDefault()?.Bdaddr;
                        if (string.IsNullOrWhiteSpace(mac))
                            return;

                        // Use scanData if present, fallback to adData.
                        var scanPayload = !string.IsNullOrEmpty(eventData.scanData)
                            ? eventData.scanData
                            : eventData.adData;

                        var productNumber = ScanDataParser.ExtractProductNumber(scanPayload);
                        var lockedHex = ScanDataParser.GetLockedInfo(scanPayload);
                        var isLocked = ScanDataParser.IsLocked(scanPayload);
                        var meta = ScanDataParser.GetDetectorMeta(scanPayload);
                        string name = "";

                        if (eventData.evtType == 4 && !string.IsNullOrEmpty(scanPayload) && scanPayload.Length >= 50)
                            name = ScanDataParser.GetName(scanPayload.Substring(20, 30));

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

                        uniqueDevices[mac] = enrichedDevice;
                    }, cancellationTokenSource.Token)
                ).ToArray();

                await Task.WhenAll(scanTasks);
                return uniqueDevices.Values.ToList();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error: {ex.Message + ex.StackTrace}");
            }
        }

        public async Task FetchNearbyDevices(string gatewayIpAddress, int gatewayPort, int minRssi)
        {
            try
            {
                var baseSseEndpoint = $"http://{gatewayIpAddress}:{gatewayPort}/gap/nodes?event=1";
                var sseEndpoints = BuildScanEndpoints(baseSseEndpoint);
                var tasks = sseEndpoints.Select(endpoint => ProcessNearbyDevicesEndpoint(endpoint, minRssi)).ToArray();
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error: {ex.Message + ex.StackTrace}");
            }
        }

        private async Task ProcessNearbyDevicesEndpoint(string sseEndpoint, int minRssi)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, sseEndpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await ProcessSSEEvents(response, eventData =>
            {
                // Get the MAC address.
                var macAddress = eventData.bdaddrs.FirstOrDefault()?.Bdaddr;
                if (string.IsNullOrEmpty(macAddress))
                    return;

                // Check if the MAC address starts with the desired prefix (e.g., "10:B9:F7").
                if (!macAddress.StartsWith("10:B9:F7", StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Info($"Skipping MAC={macAddress} as it does not match the required prefix.");
                    return;
                }

                AppLog.Info($"Received Event: MAC={macAddress}, RSSI={eventData.rssi}");
                _deviceStorageService.AddOrUpdateDevice(eventData, minRssi);
            }, CancellationToken.None);
        }

        private async Task ProcessSSEEndpoint(string sseEndpoint, Action<ScannedDevicesView> callback, CancellationToken cancellationToken)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, sseEndpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await ProcessSSEEvents(response, callback, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal timeout path for short /scan requests.
            }
        }

        private static List<string> BuildScanEndpoints(string baseSseEndpoint)
        {
            var mode = NormalizeScanChipMode(RuntimeVariables.BLE_SCAN_CHIP_MODE);
            var endpoints = new List<string>();

            if (mode == 2)
            {
                endpoints.Add(AppendQueryParam(baseSseEndpoint, "chip", "all"));
            }
            else if (mode is 0 or 1)
            {
                endpoints.Add(AppendQueryParam(baseSseEndpoint, "chip", mode.ToString()));
            }
            else
            {
                endpoints.Add(baseSseEndpoint);
            }

            return endpoints;
        }

        private static int NormalizeScanChipMode(int mode)
            => mode is -1 or 0 or 1 or 2 ? mode : 0;

        private static string AppendQueryParam(string url, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            var sep = url.Contains("?") ? "&" : "?";
            return $"{url}{sep}{key}={Uri.EscapeDataString(value)}";
        }

        private async Task ProcessSSEEvents(HttpResponseMessage response, Action<ScannedDevicesView> callback, CancellationToken cancellationToken)
        {
            try
            {
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var reader = new System.IO.StreamReader(stream))
                {
                    while (!reader.EndOfStream)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string line = await reader.ReadLineAsync();
                        AppLog.Verbose(line);
                        if (!string.IsNullOrEmpty(line) && line != ":keep-alive")
                        {
                            if (line.StartsWith("data:"))
                            {
                                line = line.Substring("data:".Length).Trim();
                            }

                            var eventData = JsonConvert.DeserializeObject<ScannedDevicesView>(line);
                            if (eventData != null)
                            {
                                callback(eventData);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                AppLog.Warn("SSE event processing canceled due to timeout.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error processing SSE events: {ex.Message + ex.StackTrace}");
            }
        }
    }
}
