using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

public class CassiaNotificationService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, EventHandler<string>> _eventHandlers;
    private readonly string _eventSourceUrl;

    public CassiaNotificationService(IConfiguration configuration)
    {
        _httpClient = new HttpClient();
        _eventHandlers = new ConcurrentDictionary<string, EventHandler<string>>();

        // Read IP from appsettings.json
        string gatewayIpAddress = configuration.GetValue<string>("GatewayConfiguration:IpAddress");
        _eventSourceUrl = $"http://{gatewayIpAddress}/gatt/nodes?event=1";

        // Start listening for events
        Task.Run(() => StartListening());
    }

    public async Task<bool> EnableNotificationAsync(string gatewayIpAddress, string nodeMac)
    {
        try
        {

            string url = $"http://{gatewayIpAddress}/gatt/nodes/{nodeMac}/handle/15/value/0100";
            HttpResponseMessage response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("Notification enabled successfully.");
                return true;
            }

            Console.WriteLine("Failed to enable notification.", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception occurred while enabling notification: {ex.Message}");
            return false;
        }
    }
    private async Task StartListening()
    {
        try
        {
            HttpResponseMessage response = await _httpClient.GetAsync(_eventSourceUrl, HttpCompletionOption.ResponseHeadersRead);

            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var reader = new System.IO.StreamReader(stream))
            {
                while (true)
                {
                    string line = await reader.ReadLineAsync();

                    // Ignore keep-alive messages and empty lines
                    if (string.IsNullOrWhiteSpace(line) || line.Equals(":keep-alive"))
                    {
                        continue;
                    }

                    // Invoke handlers for each event asynchronously
                    if (line.StartsWith("data:"))
                    {
                        line = line.Substring("data:".Length).Trim();
                        Task.Run(() => InvokeHandlers(line));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message + ex.StackTrace}");
        }
    }

    private void InvokeHandlers(string eventData)
    {
        Console.WriteLine($"Received event data: {eventData}");

        try
        {
            var eventObject = JsonSerializer.Deserialize<EventData>(eventData);

            Console.WriteLine($"Parsed event object: {eventObject}");

            if (eventObject != null && eventObject.value != null)
            {
                string macAddress = eventObject.id;

                Console.WriteLine($"Extracted MAC address: {macAddress}");

                if (_eventHandlers.TryGetValue(macAddress, out var handler))
                {
                    handler?.Invoke(this, eventObject.value);
                }
            }
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Error parsing JSON: {ex.Message + ex.StackTrace}");
        }
    }

    public void Subscribe(string macAddress, EventHandler<string> handler)
    {
        _eventHandlers.TryAdd(macAddress, handler);
    }

    public void Unsubscribe(string macAddress)
    {
        _eventHandlers.TryRemove(macAddress, out _);
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    private class EventData
    {
        public string value { get; set; }
        public int handle { get; set; }
        public string id { get; set; }
        public string dataType { get; set; }
    }
}
