using System.Collections.Concurrent;
using System.Text.Json;
using AccessAPP.Services;
using Amazon.Runtime.Internal;
using Windows.Media.Protection.PlayReady;

public class CassiaNotificationService : IDisposable
{
    private static readonly HttpClient _httpClient = new HttpClient();
    private readonly ConcurrentDictionary<string, EventHandler<string>> _eventHandlers;
    private readonly ConcurrentDictionary<string, string> _lastEventData;
    private readonly string _eventSourceUrl;
    private static bool _isListening = false;
    private static Task _listeningTask;
    private static readonly object _lock = new();
    private readonly ILogger<CassiaNotificationService> _logger;
    public SemaphoreSlim semaphore = null;
    public bool forcedRestartedSSE = false;

    // Singleton instance managed by DI
    private static CassiaNotificationService _instance;

    // Constructor with DI dependencies
    public CassiaNotificationService(HttpClient httpClient, IConfiguration configuration, ILogger<CassiaNotificationService> logger)
    {
        //_httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _eventHandlers = new ConcurrentDictionary<string, EventHandler<string>>();
        _lastEventData = new ConcurrentDictionary<string, string>();

        // Read IP from appsettings.json
        string gatewayIpAddress = configuration.GetValue<string>("GatewayConfiguration:IpAddress");
        _eventSourceUrl = $"http://{gatewayIpAddress}/gatt/nodes?event=1";

        StartSseListener();
    }

    private void StartSseListener()
    {
        lock (_lock)
        {
            if (_isListening) return;

            _isListening = true;
            _listeningTask = Task.Run(() => StartListening());
        }
    }

    private async Task StartListening()
    {
        while (true)
        {
            try
            {
                _logger.LogInformation("SSE event listener started.");
                HttpResponseMessage response = await _httpClient.GetAsync(_eventSourceUrl, HttpCompletionOption.ResponseHeadersRead);

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
                        Console.WriteLine("SSE Raw data: " + line);
                        line = line.Substring("data:".Length).Trim();
                        Task.Run(() => InvokeHandlers(line));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in StartListening: {ex.Message}");
                await Task.Delay(5000); // Retry delay
            }
        }
    }

    private void InvokeHandlers(string eventData)
    {
        //_logger.LogInformation($"Raw SSE Event Received: {eventData}");

        try
        {
            var eventObject = JsonSerializer.Deserialize<EventData>(eventData);
            if (eventObject != null && !string.IsNullOrEmpty(eventObject.value))
            {
                string macAddress = eventObject.id;
                //_logger.LogInformation($"Extracted MAC Address: {macAddress}");

                if (_eventHandlers.TryGetValue(macAddress, out var handler))
                {
                    //_logger.LogInformation($"Invoking handler for MAC {macAddress} with data: {eventObject.value}");
                    handler?.Invoke(this, eventObject.value);
                }
                else
                {
                   // _logger.LogWarning($"No handler found for MAC {macAddress}");
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError($"Error parsing JSON in SSE event: {ex.Message}");
        }
    }

    public async Task<bool> EnableNotificationAsync(string gatewayIpAddress, string nodeMac, bool bActor)
    {
        try
        {
            HttpClient _httpClientTmp = new HttpClient();
            string url = $"http://{gatewayIpAddress}/gatt/nodes/{nodeMac}/handle/15/value/0100";
            if (bActor)
            {
                url = $"http://{gatewayIpAddress}/gatt/nodes/{nodeMac}/handle/16/value/0100";
            }

            HttpResponseMessage response = null;

            await semaphore.WaitAsync(); //lock connect requests

            try
            {
                // Send the requestDevice is already in boot mode.
                response = await _httpClientTmp.GetAsync(url);
            }
            catch (Exception e)
            {
                _logger.LogError($"Vinti {nodeMac}");
                throw e;
            }
            finally
            {
                semaphore.Release();
            }

            if (response != null && response.IsSuccessStatusCode)
            {
                _logger.LogInformation($"Notification enabled successfully for {nodeMac}.");
                return true;
            }

            _logger.LogWarning($"Failed to enable notification for {nodeMac}. Status code: {response.StatusCode}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Exception occurred while enabling notification for {nodeMac}: {ex.Message}");
            return false;
        }
    }

    public void Subscribe(string macAddress, EventHandler<string> handler)
    {
        _eventHandlers.AddOrUpdate(macAddress, handler, (key, existingHandler) =>
        {
            _logger.LogInformation($"Replacing existing handler for {macAddress}");
            return handler;
        });
    }

    public void Unsubscribe(string macAddress)
    {
        _eventHandlers.TryRemove(macAddress, out _);
    }

    public void Dispose()
    {
        //_httpClient?.Dispose();
    }

    private class EventData
    {
        public string value { get; set; }
        public int handle { get; set; }
        public string id { get; set; }
        public string dataType { get; set; }
    }
}
