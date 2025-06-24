using AccessAPP.Models;
using Newtonsoft.Json;

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
                // Define the duration of each scan in milliseconds
                int scanDuration = 5000; // 5 seconds

                // Use dictionary to avoid duplicates based on MAC address
                var uniqueDevices = new Dictionary<string, ScannedDevicesView>();

                // Step 1: Subscribe to the Server-Sent Events (SSE) endpoint
                string sseEndpoint = $"http://{gatewayIpAddress}:{gatewayPort}/gap/nodes?event=1&filter_mac=10:B9:F7*";
                var request = new HttpRequestMessage(HttpMethod.Get, sseEndpoint);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    // Start a task to wait for the scan duration
                    var cancellationTokenSource = new CancellationTokenSource(scanDuration);
                    var delayTask = Task.Delay(scanDuration, cancellationTokenSource.Token);

                    // Step 2: Process SSE events and store only the latest per MAC
                    await ProcessSSEEvents(response, eventData =>
                    {
                        var device = eventData;
                        var mac = eventData.bdaddrs.FirstOrDefault()?.Bdaddr;

                        if (!string.IsNullOrWhiteSpace(mac))
                        {
                            uniqueDevices[mac] = device; // keep the latest seen device for each MAC
                        }

                    }, cancellationTokenSource.Token);

                    // Wait for scan duration to complete
                    await delayTask;
                }

                return uniqueDevices.Values.ToList(); // Return only unique, latest-scanned devices
            }
            catch (Exception ex)
            {
                throw new Exception($"Error: {ex.Message + ex.StackTrace}");
            }
        }


        /// <summary>
        /// / Older Version
        /// </summary>
        /// <param name="response"></param>
        /// <param name="callback"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        //public async Task<List<ScannedDevicesView>> FetchNearbyDevices(string gatewayIpAddress, int gatewayPort, int minRssi)
        //{
        //    try
        //    {
        //        // Define the duration of each scan in milliseconds
        //        int scanDuration = 5000; // 5 seconds
        //        var nearbyDevices = new List<ScannedDevicesView>();

        //        // Step 1: Subscribe to the Server-Sent Events (SSE) endpoint
        //        string sseEndpoint = $"http://{gatewayIpAddress}:{gatewayPort}/gap/nodes?event=1";
        //        var request = new HttpRequestMessage(HttpMethod.Get, sseEndpoint);
        //        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        //        using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        //        {
        //            response.EnsureSuccessStatusCode();

        //            // Start a task to wait for 5 seconds
        //            var cancellationTokenSource = new CancellationTokenSource(scanDuration);
        //            var delayTask = Task.Delay(scanDuration, cancellationTokenSource.Token);

        //            // Step 2: Process SSE events
        //            await ProcessSSEEvents(response, eventData =>
        //            {
        //                // Parse each eventData as a ScannedDevicesView and add it to the list
        //                var device = eventData;
        //                nearbyDevices.Add(device);
        //            }, cancellationTokenSource.Token);

        //            // Wait for the scan duration or until cancellation is requested
        //            await delayTask;
        //        }

        //        var filteredDevices = nearbyDevices.Where(device => device.rssi < minRssi).ToList();
        //        return filteredDevices;
        //    }
        //    catch (OperationCanceledException)
        //    {
        //        // Handle cancellation due to timeout
        //        return new List<ScannedDevicesView>();
        //    }
        //    catch (Exception ex)
        //    {
        //        throw new Exception($"Error: {ex.Message + ex.StackTrace}");
        //    }
        //}

        public async Task FetchNearbyDevices(string gatewayIpAddress, int gatewayPort, int minRssi)
        {
            try
            {
                // Step 1: Subscribe to the Server-Sent Events (SSE) endpoint
                string sseEndpoint = $"http://{gatewayIpAddress}:{gatewayPort}/gap/nodes?event=1";
                var request = new HttpRequestMessage(HttpMethod.Get, sseEndpoint);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    // Step 2: Process SSE events continuously
                    await ProcessSSEEvents(response, eventData =>
                    {
                        // Get the MAC address
                        var macAddress = eventData.bdaddrs.FirstOrDefault()?.Bdaddr;
                        if (string.IsNullOrEmpty(macAddress)) return;

                        // Check if the MAC address starts with the desired prefix (e.g., "10:B9:F7")
                        if (!macAddress.StartsWith("10:B9:F7", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"Skipping MAC={macAddress} as it does not match the required prefix.");
                            return; // Skip the processing for this MAC address
                        }


                        // Log the received event
                        Console.WriteLine($"Received Event: MAC={macAddress}, RSSI={eventData.rssi}");

                        // Add or update the device in the global storage if it meets the RSSI threshold
                        _deviceStorageService.AddOrUpdateDevice(eventData, minRssi);
                    }, CancellationToken.None);  // Using CancellationToken.None for infinite processing
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error: {ex.Message + ex.StackTrace}");
            }
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
                        // Check for cancellation before reading the next line
                        cancellationToken.ThrowIfCancellationRequested();

                        string line = await reader.ReadLineAsync();
                        // Process the SSE event
                        Console.WriteLine(line);

                        if (!string.IsNullOrEmpty(line) && line != "" && line != ":keep-alive")
                        {
                            if (line.StartsWith("data:"))
                            {
                                line = line.Substring("data:".Length).Trim();
                            }
                            // Parse the JSON data from the SSE event
                            var eventData = JsonConvert.DeserializeObject<ScannedDevicesView>(line);
                            callback(eventData); // Call the callback with the parsed data
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Handle cancellation due to timeout
                Console.WriteLine("SSE event processing canceled due to timeout.");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error processing SSE events: {ex.Message + ex.StackTrace}");
            }
        }
    }
}
