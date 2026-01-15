using AccessAPP.Controllers;
using AccessAPP.Models;
using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using System.Buffers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AccessAPP.Services;

public sealed class MqttService : IMqttService, IUpgradeMqttPublisher
{
    private readonly MqttConfigStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly TimeSpan StatusHeartbeatInterval = TimeSpan.FromSeconds(10);

    private MQTTnet.IMqttClient? _client;
    private CancellationTokenSource? _runCts;
    private Task? _runLoop;

    private bool _subscribed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public MqttOptions CurrentOptions { get; private set; }

    public event Func<StartUpdateCommand, Task>? StartUpdateRequested;
    public event Func<GetFwVersionCommand, Task>? GetFwVersionRequested;

    // NEW
    public event Func<GetFirmwareManifestCommand, Task>? GetFirmwareManifestRequested;

    public MqttService(MqttConfigStore store)
    {
        _store = store;
        CurrentOptions = _store.LoadOrCreateDefault();
    }

    // ---------------- Public API ----------------

    public async Task StartAsync(CancellationToken ct = default)
    {
        Log("Starting MQTT service");

        // Wire UpgradeLogger to MQTT + topic shape used by this service
        UpgradeLogger.Mqtt = this;

        // Use the SAME topic pattern as your TeleTopic() => .../tele/{name}/{leaf}
        UpgradeLogger.TopicResolver = _ => TeleTopic("upgrade-log");

        // Keep network id in sync (used by saved-log replay if needed)
        UpgradeLogger.NetworkId = CurrentOptions.NetworkId;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_runLoop is not null)
            {
                Log("Already running");
                return;
            }

            _runCts = new CancellationTokenSource();
            _runLoop = Task.Run(() => RunLoopAsync(_runCts.Token), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        Log("Stopping MQTT service");

        Task? loop;
        MQTTnet.IMqttClient? client;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_runLoop is null) return;

            _runCts?.Cancel();
            loop = _runLoop;
            _runLoop = null;

            client = _client;
            _client = null;
            _subscribed = false;
        }
        finally
        {
            _gate.Release();
        }

        if (client is not null)
        {
            try
            {
                if (client.IsConnected)
                    await DisconnectAsyncViaReflection(client, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"Disconnect error: {ex.Message}");
            }

            try { client.Dispose(); } catch { /* ignore */ }
        }

        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    public async Task UpdateIdentityAsync(string name, string networkId, bool persist = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name cannot be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(networkId)) throw new ArgumentException("NetworkId cannot be empty.", nameof(networkId));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CurrentOptions.Name = name.Trim();
            CurrentOptions.NetworkId = networkId.Trim();
            if (persist) _store.Save(CurrentOptions);
        }
        finally
        {
            _gate.Release();
        }

        await RestartAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateBrokerAsync(string host, int port, string? username, string? password, bool useTls, bool persist = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host cannot be empty.", nameof(host));
        if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CurrentOptions.Host = host.Trim();
            CurrentOptions.Port = port;
            CurrentOptions.Username = string.IsNullOrWhiteSpace(username) ? null : username;
            CurrentOptions.Password = string.IsNullOrWhiteSpace(password) ? null : password;
            CurrentOptions.UseTls = useTls;
            if (persist) _store.Save(CurrentOptions);
        }
        finally
        {
            _gate.Release();
        }

        await RestartAsync(ct).ConfigureAwait(false);
    }

    public async Task PublishDiscoveredDevicesAsync(DiscoveredDevicesMessage msg, CancellationToken ct = default)
    {
        msg.Name = CurrentOptions.Name;
        msg.NetworkId = CurrentOptions.NetworkId;
        if (msg.Time == default) msg.Time = DateTimeOffset.UtcNow;

        await PublishJsonAsync(TeleTopic("discovered"), msg, retain: false, ct).ConfigureAwait(false);
    }

    public async Task PublishUpdateProgressAsync(UpdateProgressMessage msg, CancellationToken ct = default)
    {
        msg.Name = CurrentOptions.Name;
        msg.NetworkId = CurrentOptions.NetworkId;
        if (msg.Time == default) msg.Time = DateTimeOffset.UtcNow;

        await PublishJsonAsync(TeleTopic("progress"), msg, retain: false, ct).ConfigureAwait(false);
    }

    public async Task PublishLogAsync(LogMessage msg, CancellationToken ct = default)
    {
        msg.Name = CurrentOptions.Name;
        msg.NetworkId = CurrentOptions.NetworkId;
        if (msg.Time == default) msg.Time = DateTimeOffset.UtcNow;

        await PublishJsonAsync(TeleTopic("log"), msg, retain: false, ct).ConfigureAwait(false);
    }

    // NEW: publish manifest response on a dedicated leaf
    public async Task PublishFirmwareManifestAsync(FirmwareManifestResponse msg, CancellationToken ct = default)
    {
        // FirmwareManifestResponse does not contain Name/NetworkId/Time in your project,
        // so we publish it as-is.
        await PublishJsonAsync(TeleTopic("fw-manifest"), msg, retain: false, ct).ConfigureAwait(false);
    }

    public async Task PublishRespAsync(string msg, CancellationToken ct = default)
    {
        await PublishJsonAsync(TeleTopic("resp"), msg, retain: false, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
        _runCts?.Dispose();
    }

    // ---------------- Run loop / connection ----------------

    private async Task RestartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var running = _runLoop is not null;
        _gate.Release();

        if (!running) return;

        await StopAsync(ct).ConfigureAwait(false);
        await StartAsync(ct).ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        Log("Run loop started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Log($"Ensuring connection to {CurrentOptions.Host}:{CurrentOptions.Port} (clientId={CurrentOptions.ClientId}, network={CurrentOptions.NetworkId})");
                await EnsureConnectedAndSubscribedAsync(ct).ConfigureAwait(false);

                // retained online status
                var online = new StatusMessage
                {
                    Name = CurrentOptions.Name,
                    NetworkId = CurrentOptions.NetworkId,
                    Time = DateTimeOffset.UtcNow,
                    State = "online",
                    queue = CassiaFirmwareUpgradeService.inQueue,
                    programming = CassiaFirmwareUpgradeService.GetProgrammingCount(),
                    totalSpeedpct = CassiaFirmwareUpgradeService.totalSpeed

                };
                await PublishJsonAsync(TeleTopic("status"), online, retain: false, ct).ConfigureAwait(false);
                Log("Published retained online status");

                var nextHeartbeat = DateTimeOffset.UtcNow + StatusHeartbeatInterval;

                while (!ct.IsCancellationRequested && _client is not null && _client.IsConnected)
                {
                    var now = DateTimeOffset.UtcNow;

                    if (now >= nextHeartbeat)
                    {
                        var heartbeat = new StatusMessage
                        {
                            Name = CurrentOptions.Name,
                            NetworkId = CurrentOptions.NetworkId,
                            Time = now,
                            State = "online",
                            queue = CassiaFirmwareUpgradeService.inQueue,
                    programming = CassiaFirmwareUpgradeService.GetProgrammingCount(),
                            totalSpeedpct = CassiaFirmwareUpgradeService.totalSpeed
                        };

                        await PublishJsonAsync(TeleTopic("status"), heartbeat, retain: false, ct)
                            .ConfigureAwait(false);

                        nextHeartbeat = now + StatusHeartbeatInterval;
                    }

                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                Log("Run loop cancelled");
            }
            catch (Exception ex)
            {
                Log($"Connection error: {ex.Message}");
            }

            if (!ct.IsCancellationRequested)
            {
                var delay = Math.Max(1, CurrentOptions.ReconnectDelaySeconds);
                Log($"Reconnecting in {delay}s...");
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false); }
                catch { /* ignore */ }
            }
        }

        Log("Run loop exited");
    }

    private async Task EnsureConnectedAndSubscribedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is null)
            {
                _client = new MqttClientFactory().CreateMqttClient();
                _subscribed = false;

                _client.ConnectedAsync += e =>
                {
                    Log("ConnectedAsync event");
                    return Task.CompletedTask;
                };

                _client.DisconnectedAsync += e =>
                {
                    Log($"DisconnectedAsync event reason={e.Reason} reasonString='{e.ReasonString}' ex={(e.Exception != null ? e.Exception.Message : "null")}");
                    _subscribed = false;
                    return Task.CompletedTask;
                };

                _client.ApplicationMessageReceivedAsync += e =>
                {
                    Log($"RX topic: {e.ApplicationMessage.Topic}");

                    byte[] payload = e.ApplicationMessage.Payload.ToArray();

                    var text = payload.Length == 0
                        ? string.Empty
                        : Encoding.UTF8.GetString(payload);

                    Log($"RX payload: {text}");

                    return HandleCommandAsync(e.ApplicationMessage.Topic, text);
                };
            }

            if (_client.IsConnected) return;

            _subscribed = false;

            Log("Connecting to broker...");
            var opts = BuildOptionsObject();
            await ConnectAsyncViaReflection(_client, opts, ct).ConfigureAwait(false);
            Log("Connected");

            await SubscribeTopicsAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public sealed class SendUpgradeLogCommand
    {
        public string? LogId { get; set; }          // optional filter
        public int MaxLines { get; set; } = 5000;   // last N lines (after filter)
        public int ChunkLines { get; set; } = 100;  // lines per MQTT message
    }

    private Task HandleCommandAsync(string topic, string payload)
    {
        try
        {
            // parse topic: accessapp/{network}/cmd/{target}/{command}
            var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);

            Log($"HandleCommandAsync: parts.Length={parts.Length}");
            for (int i = 0; i < parts.Length; i++)
                Log($"  parts[{i}]='{parts[i]}'");

            if (parts.Length < 5)
            {
                Log("HandleCommandAsync: ignored (too few parts)");
                return Task.CompletedTask;
            }

            var baseTopic = parts[0];
            var networkId = parts[1];
            var cmdLiteral = parts[2];
            var target = parts[3];
            var command = parts[4];

            Log($"HandleCommandAsync parsed: baseTopic='{baseTopic}', networkId='{networkId}', cmdLiteral='{cmdLiteral}', target='{target}', command='{command}'");
            Log($"Options: BaseTopic='{CurrentOptions.BaseTopic}', NetworkId='{CurrentOptions.NetworkId}', Name='{CurrentOptions.Name}'");

            if (!string.Equals(baseTopic, CurrentOptions.BaseTopic, StringComparison.OrdinalIgnoreCase))
            {
                Log("HandleCommandAsync: ignored (baseTopic mismatch)");
                return Task.CompletedTask;
            }

            if (!string.Equals(cmdLiteral, "cmd", StringComparison.OrdinalIgnoreCase))
            {
                Log("HandleCommandAsync: ignored (not cmd)");
                return Task.CompletedTask;
            }

            if (!string.Equals(networkId, CurrentOptions.NetworkId, StringComparison.OrdinalIgnoreCase))
            {
                Log("HandleCommandAsync: ignored (networkId mismatch)");
                return Task.CompletedTask;
            }

            // Accept commands for THIS gateway, and optionally broadcast target "all".
            if (!string.Equals(target, CurrentOptions.Name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
            {
                Log("HandleCommandAsync: ignored (target mismatch)");
                return Task.CompletedTask;
            }

            if (string.Equals(command, "start-update", StringComparison.OrdinalIgnoreCase))
            {
                Log("HandleCommandAsync: dispatch start-update");

                var reqs = JsonSerializer.Deserialize<List<StartUpdateRequest>>(payload, JsonOptions)
                           ?? new List<StartUpdateRequest>();

                if (reqs.Count > 0)
                {
                    Log($"DEBUG first req: DetectorType='{reqs[0].DetectorType}', FW='{reqs[0].FirmwareVersion}', MAC='{reqs[0].MacAddress}', Pin='{reqs[0].Pincode}'");
                }

                var dto = new StartUpdateCommand
                {
                    Requests = reqs,
                    Sensors = reqs
                        .Select(r => r.MacAddress)
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .Select(m => m!.Trim())
                        .ToList()
                };

                Log($"start-update parsed OK: {dto.Requests.Count} request(s)");
                return StartUpdateRequested?.Invoke(dto) ?? Task.CompletedTask;
            }

            if (string.Equals(command, "get-fw-version", StringComparison.OrdinalIgnoreCase))
            {
                Log("HandleCommandAsync: dispatch get-fw-version");
                var dto = JsonSerializer.Deserialize<GetFwVersionCommand>(payload, JsonOptions) ?? new GetFwVersionCommand();
                return GetFwVersionRequested?.Invoke(dto) ?? Task.CompletedTask;
            }

            if (string.Equals(command, "send-upgrade-log", StringComparison.OrdinalIgnoreCase))
            {
                Log("HandleCommandAsync: dispatch send-upgrade-log");

                SendUpgradeLogCommand dto;
                try
                {
                    dto = string.IsNullOrWhiteSpace(payload)
                        ? new SendUpgradeLogCommand()
                        : (JsonSerializer.Deserialize<SendUpgradeLogCommand>(payload, JsonOptions) ?? new SendUpgradeLogCommand());
                }
                catch
                {
                    dto = new SendUpgradeLogCommand();
                }

                UpgradeLogger.NetworkId = CurrentOptions.NetworkId;

                return UpgradeLogger.PublishSavedLogAsync(
                    logIdFilter: dto.LogId,
                    maxLines: dto.MaxLines <= 0 ? 5000 : dto.MaxLines,
                    chunkLines: dto.ChunkLines <= 0 ? 100 : dto.ChunkLines,
                    ct: CancellationToken.None
                );
            }

            // NEW: request firmware manifest
            if (string.Equals(command, "get-fw-manifest", StringComparison.OrdinalIgnoreCase))
            {
                Log("HandleCommandAsync: dispatch get-fw-manifest");

                GetFirmwareManifestCommand dto;
                try
                {
                    dto = string.IsNullOrWhiteSpace(payload)
                        ? new GetFirmwareManifestCommand()
                        : (JsonSerializer.Deserialize<GetFirmwareManifestCommand>(payload, JsonOptions) ?? new GetFirmwareManifestCommand());
                }
                catch
                {
                    dto = new GetFirmwareManifestCommand();
                }

                return GetFirmwareManifestRequested?.Invoke(dto) ?? Task.CompletedTask;
            }

            
if (string.Equals(command, "get-queue-list", StringComparison.OrdinalIgnoreCase))
{
    Log("HandleCommandAsync: dispatch get-queue-list");

    // payload can be {} / empty / include requestId, but we keep it tolerant
    string? requestId = null;
    try
    {
        if (!string.IsNullOrWhiteSpace(payload))
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("requestId", out var rid) &&
                rid.ValueKind == JsonValueKind.String)
                requestId = rid.GetString();
        }
    }
    catch { /* ignore */ }

    var items = CassiaFirmwareUpgradeService.GetQueueListSnapshot()
        .Select(x => new { mac = x.Mac, detectorType = x.DetectorType, firmwareVersion = x.FirmwareVersion })
        .ToList();

    var resp = new
    {
        success = true,
        message = "Queue list retrieved successfully.",
        requestId,
        name = CurrentOptions.Name,
        networkId = CurrentOptions.NetworkId,
        time = DateTimeOffset.UtcNow,
        count = items.Count,
        queueList = items
    };

    return PublishJsonAsync(TeleTopic("queue-list"), resp, retain: false, CancellationToken.None);
}

if (string.Equals(command, "get-programming-list", StringComparison.OrdinalIgnoreCase))
{
    Log("HandleCommandAsync: dispatch get-programming-list");

    string? requestId = null;
    try
    {
        if (!string.IsNullOrWhiteSpace(payload))
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("requestId", out var rid) &&
                rid.ValueKind == JsonValueKind.String)
                requestId = rid.GetString();
        }
    }
    catch { /* ignore */ }

    var items = CassiaFirmwareUpgradeService.GetProgrammingListSnapshot()
        .Select(x => new { mac = x.Mac, detectorType = x.DetectorType, firmwareVersion = x.FirmwareVersion })
        .ToList();

    var resp = new
    {
        success = true,
        message = "Programming list retrieved successfully.",
        requestId,
        name = CurrentOptions.Name,
        networkId = CurrentOptions.NetworkId,
        time = DateTimeOffset.UtcNow,
        count = items.Count,
        programmingList = items
    };

    return PublishJsonAsync(TeleTopic("programming-list"), resp, retain: false, CancellationToken.None);
}

if (string.Equals(command, "get-device-list", StringComparison.OrdinalIgnoreCase))
{
    Log("HandleCommandAsync: dispatch get-device-list");

    string? requestId = null;
    try
    {
        if (!string.IsNullOrWhiteSpace(payload))
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("requestId", out var rid) &&
                rid.ValueKind == JsonValueKind.String)
                requestId = rid.GetString();
        }
    }
    catch { /* ignore */ }

    var items = DeviceStorageService.GetDeviceListSnapshot();

    var resp = new
    {
        success = true,
        message = "Device list retrieved successfully.",
        requestId,
        name = CurrentOptions.Name,
        networkId = CurrentOptions.NetworkId,
        time = DateTimeOffset.UtcNow,
        count = items.Count,
        deviceList = items
    };

    // One single message with full list.
    return PublishJsonAsync(TeleTopic("device-list"), resp, retain: false, CancellationToken.None);
}

if (string.Equals(command, "remove-from-queue", StringComparison.OrdinalIgnoreCase))
{
    Log("HandleCommandAsync: dispatch remove-from-queue");

    // Accept payload as:
    //  - "10:B9:F7:..."
    //  - {"macAddress":"..."} / {"mac":"..."}
    //  - {"macAddresses":["...","..."]} / {"macs":[...]}
    //  - ["...","..."]
    var macs = new List<string>();

    try
    {
        if (!string.IsNullOrWhiteSpace(payload))
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.String)
            {
                var m = root.GetString();
                if (!string.IsNullOrWhiteSpace(m)) macs.Add(m);
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var m = el.GetString();
                        if (!string.IsNullOrWhiteSpace(m)) macs.Add(m);
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("macAddress", out var ma) && ma.ValueKind == JsonValueKind.String)
                {
                    var m = ma.GetString();
                    if (!string.IsNullOrWhiteSpace(m)) macs.Add(m);
                }
                if (root.TryGetProperty("mac", out var mac) && mac.ValueKind == JsonValueKind.String)
                {
                    var m = mac.GetString();
                    if (!string.IsNullOrWhiteSpace(m)) macs.Add(m);
                }

                if (root.TryGetProperty("macAddresses", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            var m = el.GetString();
                            if (!string.IsNullOrWhiteSpace(m)) macs.Add(m);
                        }
                    }
                }
                if (root.TryGetProperty("macs", out var arr2) && arr2.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr2.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            var m = el.GetString();
                            if (!string.IsNullOrWhiteSpace(m)) macs.Add(m);
                        }
                    }
                }
            }
        }
    }
    catch
    {
        // If parsing fails, treat it as raw string
        if (!string.IsNullOrWhiteSpace(payload))
            macs.Add(payload.Trim('"', ' ', '\t', '\r', '\n'));
    }

    macs = macs
        .Where(m => !string.IsNullOrWhiteSpace(m))
        .Select(m => m.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    int removed = 0;
    foreach (var m in macs)
        removed += CassiaFirmwareUpgradeService.RemoveFromUpgradeQueuePending(m);

    var resp = new
    {
        success = true,
        message = removed > 0 ? "Removed device(s) from pending queue." : "No matching pending devices found in queue.",
        name = CurrentOptions.Name,
        networkId = CurrentOptions.NetworkId,
        time = DateTimeOffset.UtcNow,
        requested = macs,
        removed
    };

    return PublishJsonAsync(TeleTopic("queue-remove"), resp, retain: false, CancellationToken.None);
}

if (string.Equals(command, "get-parallel-programmers", StringComparison.OrdinalIgnoreCase))
{
    Log("HandleCommandAsync: dispatch get-parallel-programmers");

    var current = CassiaFirmwareUpgradeService.GetParallelProgrammers();
    var resp = new
    {
        success = true,
        message = "Parallel programmers value retrieved successfully.",
        name = CurrentOptions.Name,
        networkId = CurrentOptions.NetworkId,
        time = DateTimeOffset.UtcNow,
        value = current
    };

    return PublishJsonAsync(TeleTopic("parallel-programmers"), resp, retain: false, CancellationToken.None);
}

if (string.Equals(command, "set-parallel-programmers", StringComparison.OrdinalIgnoreCase))
{
    Log("HandleCommandAsync: dispatch set-parallel-programmers");

    int? requested = null;
    try
    {
        if (!string.IsNullOrWhiteSpace(payload))
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Number &&
                doc.RootElement.TryGetInt32(out var v1))
                requested = v1;
            else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("value", out var v2) &&
                v2.ValueKind == JsonValueKind.Number &&
                v2.TryGetInt32(out var vObj))
                requested = vObj;
        }
    }
    catch { /* ignore */ }

    if (requested is null)
    {
        var bad = new
        {
            success = false,
            message = "Missing integer value. Send payload like {\"value\":3} or just 3."
        };
        return PublishJsonAsync(TeleTopic("parallel-programmers"), bad, retain: false, CancellationToken.None);
    }

    var setTo = CassiaFirmwareUpgradeService.SetParallelProgrammers(requested.Value);

    var resp = new
    {
        success = true,
        message = "Parallel programmers value updated (runtime only; resets on restart).",
        name = CurrentOptions.Name,
        networkId = CurrentOptions.NetworkId,
        time = DateTimeOffset.UtcNow,
        requested = requested.Value,
        value = setTo
    };

    return PublishJsonAsync(TeleTopic("parallel-programmers"), resp, retain: false, CancellationToken.None);
}

if (string.Equals(command, "clear-upgrade-log", StringComparison.OrdinalIgnoreCase))
            {
                Log("HandleCommandAsync: dispatch clear-upgrade-log");

                var currentDir = Directory.GetCurrentDirectory();
                Console.WriteLine("Current Directory: " + currentDir);

                var logPath = Path.Combine(currentDir, "Logs", "upgrade_logs.txt");

                if (!System.IO.File.Exists(logPath))
                {
                    Log("No upgrade log file to clear.");
                    return Task.CompletedTask;
                }

                try
                {
                    System.IO.File.Delete(logPath);
                    Log("Upgrade log cleared successfully.");
                    return Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    Log($"Error clearing upgrade log: {ex.Message}");
                    return Task.CompletedTask;

                }
            }
            Log($"HandleCommandAsync: ignored (unknown command '{command}')");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log($"HandleCommandAsync ERROR: {ex}");
            Log($"topic was: {topic}");
            Log($"payload was: {payload}");
            return Task.CompletedTask;
        }
    }

    private async Task SubscribeTopicsAsync(CancellationToken ct)
    {
        if (_client is null) return;
        if (_subscribed) return;

        var topicMine = CmdTopic(CurrentOptions.Name, "#");
        Log($"Subscribing: {topicMine}");
        await _client.SubscribeAsync(new MqttTopicFilter
        {
            Topic = topicMine,
            QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce
        }, ct).ConfigureAwait(false);

        if (CurrentOptions.SubscribeToAllTarget)
        {
            var topicAll = CmdTopic("all", "#");
            Log($"Subscribing: {topicAll}");
            await _client.SubscribeAsync(new MqttTopicFilter
            {
                Topic = topicAll,
                QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce
            }, ct).ConfigureAwait(false);
        }

        _subscribed = true;
        Log("Subscriptions active");
    }

    // ---------------- Publish helpers ----------------

    private async Task PublishJsonAsync(string topic, object payload, bool retain, CancellationToken ct)
    {
        await EnsureConnectedAndSubscribedAsync(ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(payload, JsonOptions);

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(json))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        var client = _client;
        if (client is not null && client.IsConnected)
            await client.PublishAsync(msg, ct).ConfigureAwait(false);
    }

    public async Task PublishAsync(string topic, string payload, bool retain = false, int qos = 0, CancellationToken ct = default)
    {
        await EnsureConnectedAndSubscribedAsync(ct).ConfigureAwait(false);

        var level = qos <= 0
            ? MqttQualityOfServiceLevel.AtMostOnce
            : qos == 1
                ? MqttQualityOfServiceLevel.AtLeastOnce
                : MqttQualityOfServiceLevel.ExactlyOnce;

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload ?? string.Empty))
            .WithQualityOfServiceLevel(level)
            .WithRetainFlag(retain)
            .Build();

        var client = _client;
        if (client is not null && client.IsConnected)
            await client.PublishAsync(msg, ct).ConfigureAwait(false);
    }

    // ---------------- Topic helpers ----------------

    private string TeleTopic(string leaf)
        => $"{CurrentOptions.BaseTopic}/{CurrentOptions.NetworkId}/tele/{CurrentOptions.Name}/{leaf}";

    private string CmdTopic(string target, string leaf)
        => $"{CurrentOptions.BaseTopic}/{CurrentOptions.NetworkId}/cmd/{target}/{leaf}";

    // ---------------- Options construction (reflection based) ----------------

    private object BuildOptionsObject()
    {
        var o = CurrentOptions;
        var mqttAsm = typeof(MqttClientFactory).Assembly;

        var optionsType = mqttAsm.GetType("MQTTnet.MqttClientOptions")
                         ?? mqttAsm.GetTypes().FirstOrDefault(t => t.FullName == "MQTTnet.MqttClientOptions");

        if (optionsType == null)
            throw new InvalidOperationException("Cannot find MQTTnet.MqttClientOptions type in current MQTTnet assembly.");

        var options = Activator.CreateInstance(optionsType)
                      ?? throw new InvalidOperationException("Failed to create MQTT client options instance.");

        TrySetProp(options, "ClientId", o.ClientId);
        TrySetProp(options, "KeepAlivePeriod", TimeSpan.FromSeconds(Math.Max(5, o.KeepAliveSeconds)));
        TrySetProp(options, "CleanSession", true);

        var tcpType = mqttAsm.GetType("MQTTnet.MqttClientTcpOptions")
                     ?? mqttAsm.GetTypes().FirstOrDefault(t => t.FullName == "MQTTnet.MqttClientTcpOptions");

        if (tcpType == null)
            throw new InvalidOperationException("Cannot find MQTTnet.MqttClientTcpOptions type in current MQTTnet assembly.");

        var tcp = Activator.CreateInstance(tcpType)
                  ?? throw new InvalidOperationException("Failed to create TCP options instance.");
        Log("TCP options props: " + string.Join(", ", tcp.GetType().GetProperties().Select(p => p.Name)));

        ApplyTcpEndpoint(tcp, o.Host, o.Port);

        if (!TrySetProp(options, "ChannelOptions", tcp))
            TrySetProp(options, "TransportOptions", tcp);

        if (!string.IsNullOrWhiteSpace(o.Username))
        {
            ApplyCredentialsProvider(options, o.Username!, o.Password ?? "");
        }

        return options;
    }

    private static void ApplyTcpEndpoint(object tcpOptions, string host, int port)
    {
        var t = tcpOptions.GetType();

        var pRemote = t.GetProperty("RemoteEndpoint");
        if (pRemote != null && pRemote.CanWrite)
        {
            if (typeof(System.Net.EndPoint).IsAssignableFrom(pRemote.PropertyType))
            {
                System.Net.IPAddress? ip = null;
                if (!System.Net.IPAddress.TryParse(host, out var parsedIp))
                {
                    try
                    {
                        var addrs = System.Net.Dns.GetHostAddresses(host);
                        ip = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                             ?? addrs.FirstOrDefault();
                    }
                    catch
                    {
                        ip = null;
                    }
                }
                else
                {
                    ip = parsedIp;
                }

                if (ip == null)
                    throw new InvalidOperationException($"Cannot resolve MQTT broker host '{host}' to an IP address.");

                var ep = new System.Net.IPEndPoint(ip, port);
                pRemote.SetValue(tcpOptions, ep);
                return;
            }
        }

        TrySetProp(tcpOptions, "RemoteEndPoint", $"{host}:{port}");
        TrySetProp(tcpOptions, "Endpoint", $"{host}:{port}");
    }

    private static void ApplyCredentialsProvider(object options, string username, string password)
    {
        var optType = options.GetType();
        var credProp =
            optType.GetProperty("CredentialsProvider") ??
            optType.GetProperty("CredentialProvider") ??
            optType.GetProperty("Credentials");

        if (credProp == null || !credProp.CanWrite)
        {
            TrySetProp(options, "Username", username);
            TrySetProp(options, "Password", password);
            return;
        }

        var iface = credProp.PropertyType;
        var mqttAsm = typeof(MqttClientFactory).Assembly;

        var candidates = mqttAsm
            .GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && iface.IsAssignableFrom(t))
            .ToList();

        foreach (var t in candidates)
        {
            var c1 = t.GetConstructor(new[] { typeof(string), typeof(string) });
            if (c1 != null)
            {
                var inst = c1.Invoke(new object[] { username, password });
                credProp.SetValue(options, inst);
                return;
            }

            var c2 = t.GetConstructor(new[] { typeof(string), typeof(byte[]) });
            if (c2 != null)
            {
                var inst = c2.Invoke(new object[] { username, Encoding.UTF8.GetBytes(password) });
                credProp.SetValue(options, inst);
                return;
            }
        }

        throw new InvalidOperationException(
            $"MQTTnet credentials provider interface found ({iface.FullName}) but no concrete provider with expected ctor was found.");
    }

    private static bool TrySetProp(object obj, string name, object? value)
    {
        var p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (p == null || !p.CanWrite) return false;

        try
        {
            if (value == null)
            {
                p.SetValue(obj, null);
                return true;
            }

            var targetType = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;

            if (targetType.IsAssignableFrom(value.GetType()))
            {
                p.SetValue(obj, value);
                return true;
            }

            var converted = Convert.ChangeType(value, targetType);
            p.SetValue(obj, converted);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Task ConnectAsyncViaReflection(MQTTnet.IMqttClient client, object options, CancellationToken ct)
    {
        var clientType = client.GetType();
        var optionsType = options.GetType();

        var mi = clientType.GetMethod("ConnectAsync", new[] { optionsType, typeof(CancellationToken) });
        if (mi == null)
        {
            mi = clientType.GetMethod("ConnectAsync", new[] { optionsType });
            if (mi == null)
            {
                var sigs = string.Join(" | ",
                    clientType.GetMethods().Where(m => m.Name == "ConnectAsync").Select(m => m.ToString()));
                throw new InvalidOperationException($"Could not find ConnectAsync overload for options type {optionsType.FullName}. Overloads: {sigs}");
            }

            var r0 = mi.Invoke(client, new[] { options });
            return r0 as Task ?? throw new InvalidOperationException("ConnectAsync returned unexpected type.");
        }

        var r = mi.Invoke(client, new[] { options, ct });
        return r as Task ?? throw new InvalidOperationException("ConnectAsync returned unexpected type.");
    }

    private static Task DisconnectAsyncViaReflection(MQTTnet.IMqttClient client, CancellationToken ct)
    {
        var t = client.GetType();

        var mi = t.GetMethod("DisconnectAsync", new[] { typeof(CancellationToken) });
        if (mi != null)
        {
            var r = mi.Invoke(client, new object[] { ct });
            return r as Task ?? Task.CompletedTask;
        }

        mi = t.GetMethod("DisconnectAsync", Type.EmptyTypes);
        if (mi != null)
        {
            var r = mi.Invoke(client, Array.Empty<object>());
            return r as Task ?? Task.CompletedTask;
        }

        return Task.CompletedTask;
    }

    // ---------------- Logging ----------------

    private void Log(string msg)
    {
        Console.WriteLine($"[MQTT {DateTime.Now:HH:mm:ss}] {msg}");
    }
}
