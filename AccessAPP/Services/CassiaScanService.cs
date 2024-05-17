using AccessAPP.Models;
using System.Text.Json;

namespace AccessAPP.Services
{
    public class CassiaScanService
    {
        private readonly HttpClient _httpClient;
        private readonly List<ScannedDevicesView> _eventDataList;
        private readonly CassiaConnectService _cassiaConnectService;

        public CassiaScanService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _eventDataList = new List<ScannedDevicesView>();
            _cassiaConnectService = new CassiaConnectService(httpClient);
        }

        public async Task ScanForBleDevices(string gatewayIpAddress, int gatewayPort)
        {
            try
            {
                // Define the duration of each scan in milliseconds
                int scanDuration = 5000; // 5 seconds

                while (true)
                {
                    // Step 1: Subscribe to the Server-Sent Events (SSE) endpoint
                    string sseEndpoint = $"http://{gatewayIpAddress}:{gatewayPort}/gap/nodes?event=1";
                    var request = new HttpRequestMessage(HttpMethod.Get, sseEndpoint);
                    request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                    using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        // Step 2: Process SSE events
                        await ProcessSSEEvents(response, eventData =>
                        {
                            // Process each eventData as it comes
                            _eventDataList.Add(eventData);
                        });
                    }
                    // Pause execution for the scan duration
                    await Task.Delay(scanDuration);

                    
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error: {ex.Message}");
            }
        }

        private async Task ProcessSSEEvents(HttpResponseMessage response, Action<ScannedDevicesView> callback)
        {
            try
            {
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var reader = new System.IO.StreamReader(stream))
                {
                    while (!reader.EndOfStream)
                    {
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
                            var eventData = JsonSerializer.Deserialize<ScannedDevicesView>(line);
                            callback(eventData); // Call the callback with the parsed data
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error processing SSE events: {ex.Message}");
            }
        }
    }
}
