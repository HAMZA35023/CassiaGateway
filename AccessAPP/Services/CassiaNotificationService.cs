using System.Collections.Concurrent;
using System.Text.Json;

public class CassiaNotificationService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, EventHandler<string>> _eventHandlers;

    public CassiaNotificationService()
    {
        _httpClient = new HttpClient();
        _eventHandlers = new ConcurrentDictionary<string, EventHandler<string>>();

        // Start listening for events
        Task.Run(() => StartListening());
    }

    private async Task StartListening()
    {
        string eventSourceUrl = "http://192.168.0.20/gatt/nodes?event=1";

        try
        {
            HttpResponseMessage response = await _httpClient.GetAsync(eventSourceUrl, HttpCompletionOption.ResponseHeadersRead);

            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var reader = new System.IO.StreamReader(stream))
            {
                while (true/*!reader.EndOfStream*/)
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
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    private void InvokeHandlers(string eventData)
    {
        // Debugging: Log the received event data
        Console.WriteLine($"Received event data: {eventData}");

        // Parse JSON data
        try
        {
            var eventObject = JsonSerializer.Deserialize<EventData>(eventData);

            // Debugging: Log parsed event object
            Console.WriteLine($"Parsed event object: {eventObject}");

            // Check if the event data matches the required criteria
            if (eventObject != null && eventObject.value != null)
            {
                // Extract MAC address
                string macAddress = eventObject.id;

                // Debugging: Log the extracted MAC address
                Console.WriteLine($"Extracted MAC address: {macAddress}");

                // Check if any registered event handlers match the MAC address
                if (_eventHandlers.TryGetValue(macAddress, out var handler))
                {
                    // Invoke the handler with the event data
                    handler?.Invoke(this, eventObject.value);
                }
            }
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Error parsing JSON: {ex.Message}");
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

    // Model to represent the SSE event data
    private class EventData
    {
        public string value { get; set; }
        public int handle { get; set; }
        public string id { get; set; }
        public string dataType { get; set; }
    }
}
