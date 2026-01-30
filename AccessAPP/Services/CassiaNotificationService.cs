using System.Collections.Concurrent;
using System.Text.Json;
using AccessAPP.Services;
using AccessAPP;

public class CassiaNotificationService : IDisposable
{

    const bool _DEBUG = false;
    const bool _VERBOSE = false;

    private static readonly HttpClient _httpClient = new HttpClient();
    // Allow multiple concurrent waiters per MAC (login, read, backup steps, etc.).
    // Using only a single handler caused "Replacing existing handler" and timeouts.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, EventHandler<string>>> _eventHandlers;
    private readonly ConcurrentDictionary<string, string> _lastEventData;
    private readonly List<string> _eventSourceUrls;
    private static bool _isListening = false;
    private static List<Task> _listeningTasks;
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
        _eventHandlers = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, EventHandler<string>>>();
        _lastEventData = new ConcurrentDictionary<string, string>();

        // Read IP from appsettings.json
        string gatewayIpAddress = configuration.GetValue<string>("GatewayConfiguration:IpAddress");

        // Cassia X2000 has two BLE chips. When enabled, listen on both chips so
        // notifications arrive regardless of which chip a connection is using.
        _eventSourceUrls = new List<string>();
        // IMPORTANT:
        // Cassia SSE /gatt/nodes?event=1 does not include a chip field in the JSON payload,
        // and in practice the gateway may ignore the chip query for SSE. Two SSE streams can therefore duplicate
        // every notification. Duplicate notifications break the Cypress bootloader protocol (CYRET_ERR_DATA).
        // Keep SSE as a SINGLE stream (original working approach), and use chip selection only on REST calls.
        _eventSourceUrls.Add($"http://{gatewayIpAddress}/gatt/nodes?event=1");

        // NOTE: Do NOT add a second SSE stream with chip=0/1 here.
        // The SSE payload does not include a chip id, and duplicate events will break the bootloader protocol.

        StartSseListener();
    }

    private void StartSseListener()
    {
        lock (_lock)
        {
            if (_isListening) return;

            _isListening = true;
            _listeningTasks = _eventSourceUrls.Select(url => Task.Run(() => StartListening(url))).ToList();
        }
    }

    private async Task StartListening(string eventSourceUrl)
    {
        while (true)
        {
            try
            {
                _logger.LogInformation($"SSE event listener started: {eventSourceUrl}");
                HttpResponseMessage response = await _httpClient.GetAsync(eventSourceUrl, HttpCompletionOption.ResponseHeadersRead);

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
                        if (_DEBUG)
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

                if (_eventHandlers.TryGetValue(macAddress, out var handlerMap))
                {
                    foreach (var kv in handlerMap)
                    {
                        try
                        {
                            kv.Value?.Invoke(this, eventObject.value);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Handler error for {macAddress}: {ex.Message}");
                        }
                    }
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

    public async Task<bool> EnableNotificationAsync(string gatewayIpAddress, string nodeMac, bool bActor, int chip = -1)
    {
        try
        {
            HttpClient _httpClientTmp = new HttpClient();
            string url = $"http://{gatewayIpAddress}/gatt/nodes/{nodeMac}/handle/15/value/0100";
            if (bActor)
            {
                url = $"http://{gatewayIpAddress}/gatt/nodes/{nodeMac}/handle/16/value/0100";
            }

            if (chip >= 0)
            {
                url += url.Contains('?') ? $"&chip={chip}" : $"?chip={chip}";
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

    /// <summary>
    /// Subscribe to notifications for a MAC. Returns a token that can be used to unsubscribe.
    /// </summary>
    public Guid Subscribe(string macAddress, EventHandler<string> handler)
    {
        var token = Guid.NewGuid();
        var map = _eventHandlers.GetOrAdd(macAddress, _ => new ConcurrentDictionary<Guid, EventHandler<string>>());
        map[token] = handler;
        return token;
    }

    /// <summary>
    /// Unsubscribe a specific token for a MAC.
    /// </summary>
    public void Unsubscribe(string macAddress, Guid token)
    {
        if (_eventHandlers.TryGetValue(macAddress, out var map))
        {
            map.TryRemove(token, out _);
            if (map.IsEmpty)
                _eventHandlers.TryRemove(macAddress, out _);
        }
    }

    /// <summary>
    /// Unsubscribe ALL handlers for a MAC.
    /// </summary>
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
