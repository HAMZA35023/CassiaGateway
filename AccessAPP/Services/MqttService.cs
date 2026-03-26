using AccessAPP.Controllers;
using AccessAPP.Models;
using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AccessAPP.Services;

public sealed class MqttService : IMqttService, IUpgradeMqttPublisher
{
    private readonly MqttConfigStore _store;
    private readonly RuntimeVariablesStore _runtimeStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Publish resilience (prevents one stalled publish from freezing all telemetry)
    private int _publishFailures;
    private DateTime _publishFailureWindowStartUtc = DateTime.MinValue;
    private DateTime _lastPublishOkUtc = DateTime.MinValue;
    private int _reconnectRequested;
    private DateTime _lastReconnectAttemptUtc = DateTime.MinValue;

    private static readonly TimeSpan DefaultStatusHeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinStatusHeartbeatInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxStatusHeartbeatInterval = TimeSpan.FromSeconds(3600);

    private MQTTnet.IMqttClient? _client;
    private CancellationTokenSource? _runCts;
    private Task? _runLoop;

    private bool _subscribed;

    // Token required in the 'exec-shell' MQTT command payload to authorize remote shell execution.
    private const string ShellExecToken = "Cassia@Shell#2025";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public MqttOptions CurrentOptions { get; private set; }
    private readonly Modem4GStatusService _modem4G;
    private readonly CassiaWebSettingsService _cassiaWebSettings;

    public event Func<StartUpdateCommand, Task>? StartUpdateRequested;
    public event Func<GetFwVersionCommand, Task>? GetFwVersionRequested;
    public event Func<DetectorSettingsCommand, Task>? GetDetectorSettingsRequested;
    public event Func<DetectorSettingsCommand, Task>? SetDetectorSettingsRequested;
    public event Func<DisconnectDevicesCommand, Task>? DisconnectDevicesRequested;

    // Identify device (connect/login/wait/disconnect)
    public event Func<IdentifyCommand, Task>? IdentifyRequested;

    // LED range visualization
    public event Func<LedRangeVisualizeCommand, Task>? LedRangeVisualizeRequested;
    public event Func<LedRangeDisconnectCommand, Task>? LedRangeDisconnectRequested;

    // NEW
    public event Func<GetFirmwareManifestCommand, Task>? GetFirmwareManifestRequested;
    public event Func<SelfUpdateCommand, Task>? SelfUpdateRequested;
    public event Func<SetUpdateChannelCommand, Task>? SetUpdateChannelRequested;
    public event Func<RebootCommand, Task>? RebootRequested;

    // PIR peak status polling
    public event Func<GetPirPeakCommand, Task>? GetPirPeakRequested;

    // Walk-test enable/disable
    public event Func<SetWalktestCommand, Task>? SetWalktestRequested;

    /// <summary>
    /// Fired after each successful MQTT publish (topic, raw payload bytes).
    /// Used by <see cref="LocalBrokerDiscoveryService"/> to mirror telemetry to discovered local brokers.
    /// </summary>
    public event Action<string, byte[]>? MessagePublished;

    /// <summary>
    /// Injects an externally received command (e.g. from a discovered local broker)
    /// into the same command pipeline as messages received from the primary broker.
    /// </summary>
    public Task FeedCommandAsync(string topic, string payload) => HandleCommandAsync(topic, payload);

    public MqttService(
        MqttConfigStore store,
        RuntimeVariablesStore runtimeStore,
        Modem4GStatusService modem4G,
        CassiaWebSettingsService cassiaWebSettings)
    {
        _store = store;
        _runtimeStore = runtimeStore;
        _modem4G = modem4G;
        _cassiaWebSettings = cassiaWebSettings;
        CurrentOptions = _store.LoadOrCreateDefault();
    }

    // ---------------- Public API ----------------

    public async Task StartAsync(CancellationToken ct = default)
    {
        AppLog.Info("Starting MQTT service");
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
                AppLog.Debug("Already running");
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
        AppLog.Info("Stopping MQTT service");
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
                AppLog.Warn($"Disconnect error: {ex.Message}");
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

    public async Task UpdateScopeAsync(string networkId, bool persist = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(networkId)) throw new ArgumentException("NetworkId cannot be empty.", nameof(networkId));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CurrentOptions.NetworkId = networkId.Trim();
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

    public async Task PublishTeleJsonAsync(string leaf, object payload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(leaf))
            leaf = "resp";
        await PublishJsonAsync(TeleTopic(leaf), payload, retain: false, ct).ConfigureAwait(false);
    }

    private async Task PublishTeleJsonAsync(string leaf, object payload, string? networkIdOverride, string? nameOverride, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(leaf))
            leaf = "resp";
        await PublishJsonAsync(TeleTopic(leaf, networkIdOverride, nameOverride), payload, retain: false, ct).ConfigureAwait(false);
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
        AppLog.Debug("Run loop started");
        var lastStatusPublishAt = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested)
        {
            // ── Try to connect to the primary broker ─────────────────────────────
            bool primaryConnected = false;
            try
            {
                AppLog.Debug($"Ensuring connection to {CurrentOptions.Host}:{CurrentOptions.Port} (clientId={CurrentOptions.ClientId}, network={CurrentOptions.NetworkId})");
                await EnsureConnectedAndSubscribedAsync(ct).ConfigureAwait(false);
                primaryConnected = true;
            }
            catch (OperationCanceledException)
            {
                AppLog.Debug("Run loop cancelled");
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Connection error: {ex.Message}");
            }

            if (primaryConnected)
            {
                // ── Connected path: publish initial status then heartbeat loop ───
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    var online = await BuildStatusMessageAsync(now, ct).ConfigureAwait(false);
                    await PublishJsonAsync(TeleTopic("status"), online, retain: false, ct).ConfigureAwait(false);
                    AppLog.Info("Published retained online status");
                    lastStatusPublishAt = now;

                    while (!ct.IsCancellationRequested && _client is not null && _client.IsConnected)
                    {
                        now = DateTimeOffset.UtcNow;
                        var heartbeatInterval = GetStatusHeartbeatInterval();

                        if ((now - lastStatusPublishAt) >= heartbeatInterval)
                        {
                            var heartbeat = await BuildStatusMessageAsync(now, ct).ConfigureAwait(false);
                            await PublishJsonAsync(TeleTopic("status"), heartbeat, retain: false, ct)
                                .ConfigureAwait(false);
                            lastStatusPublishAt = now;
                        }

                        await Task.Delay(500, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { AppLog.Warn($"Heartbeat error: {ex.Message}"); }
            }
            else
            {
                // ── Offline path: still heartbeat to local brokers at the configured interval ──
                // PublishJsonAsync fires MessagePublished before attempting primary, so local
                // brokers receive status updates even without a public MQTT connection.
                var now = DateTimeOffset.UtcNow;
                var heartbeatInterval = GetStatusHeartbeatInterval();
                if ((now - lastStatusPublishAt) >= heartbeatInterval)
                {
                    try
                    {
                        var status = await BuildStatusMessageAsync(now, ct).ConfigureAwait(false);
                        await PublishJsonAsync(TeleTopic("status"), status, retain: false, ct).ConfigureAwait(false);
                        lastStatusPublishAt = now;
                    }
                    catch (OperationCanceledException) { return; }
                    catch { /* best-effort */ }
                }
            }

            if (!ct.IsCancellationRequested)
            {
                var delay = Math.Max(1, CurrentOptions.ReconnectDelaySeconds);
                AppLog.Warn($"Reconnecting in {delay}s...");
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false); }
                catch { /* ignore */ }
            }
        }

        AppLog.Debug("Run loop exited");
    }

    private static TimeSpan GetStatusHeartbeatInterval()
    {
        var seconds = RuntimeVariables.MQTT_STATUS_HEARTBEAT_SECONDS;
        if (seconds <= 0)
            return DefaultStatusHeartbeatInterval;

        return TimeSpan.FromSeconds(Math.Clamp(seconds, (int)MinStatusHeartbeatInterval.TotalSeconds, (int)MaxStatusHeartbeatInterval.TotalSeconds));
    }

    private async Task<StatusMessage> BuildStatusMessageAsync(DateTimeOffset now, CancellationToken ct)
    {
        Modem4GSnapshot? modem = null;
        try
        {
            modem = await _modem4G.GetLatestAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to read 4G status: {ex.Message}");
        }

        return new StatusMessage
        {
            Name = CurrentOptions.Name,
            NetworkId = CurrentOptions.NetworkId,
            Time = now,
            State = "online",
            queue = CassiaFirmwareUpgradeService.inQueue,
            programming = CassiaFirmwareUpgradeService.GetProgrammingCount(),
            totalSpeedpct = CassiaFirmwareUpgradeService.totalSpeed,
            uptimeSeconds = Math.Max(0, Environment.TickCount64 / 1000),
            backend = RuntimeVariables.BLE_BACKEND,
            cellularState = modem?.State,
            cellularNetworkType = modem?.NetworkType,
            cellularSignalBar = modem?.SignalBar,
            cellularRssiDbm = modem?.RssiDbm,
            cellularLteRsrpDbm = modem?.LteRsrpDbm,
            cellularLteRsrqDb = modem?.LteRsrqDb,
            cellularLteSnrDb = modem?.LteSnrDb,
            cellularProvider = modem?.Provider,
            cellularPolledAtUtc = modem?.PolledAtUtc
        };
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
                    AppLog.Debug("ConnectedAsync event");
                    return Task.CompletedTask;
                };

                _client.DisconnectedAsync += e =>
                {
                    AppLog.Error($"DisconnectedAsync event reason={e.Reason} reasonString='{e.ReasonString}' ex={(e.Exception != null ? e.Exception.Message : "null")}");
                    _subscribed = false;
                    return Task.CompletedTask;
                };

                _client.ApplicationMessageReceivedAsync += e =>
                {
                    AppLog.Debug($"RX topic: {e.ApplicationMessage.Topic}");
                    byte[] payload = e.ApplicationMessage.Payload.ToArray();

                    var text = payload.Length == 0
                        ? string.Empty
                        : Encoding.UTF8.GetString(payload);

                    AppLog.Verbose($"RX payload: {text}");
                    return HandleCommandAsync(e.ApplicationMessage.Topic, text);
                };
            }

            if (_client.IsConnected) return;

            _subscribed = false;

            AppLog.Debug("Connecting to broker...");
            var opts = BuildOptionsObject();
            await ConnectAsyncViaReflection(_client, opts, ct).ConfigureAwait(false);
            AppLog.Info("Connected");
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
        public int MaxLines { get; set; } = 20000;   // last N lines (after filter)
        public bool Compressed { get; set; } = true;
    }

    private Task HandleCommandAsync(string topic, string payload)
    {
        try
        {
            // parse topic: accessapp/{network}/cmd/{target}/{command}
            var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);

            AppLog.Verbose($"HandleCommandAsync: parts.Length={parts.Length}");
            for (int i = 0; i < parts.Length; i++)
                AppLog.Verbose($"  parts[{i}]='{parts[i]}'");
            if (parts.Length < 5)
            {
                AppLog.Warn("HandleCommandAsync: ignored (too few parts)");
                return Task.CompletedTask;
            }

            var baseTopic = parts[0];
            var networkId = parts[1];
            var cmdLiteral = parts[2];
            var target = parts[3];
            var command = parts[4];

            AppLog.Debug($"HandleCommandAsync parsed: baseTopic='{baseTopic}', networkId='{networkId}', cmdLiteral='{cmdLiteral}', target='{target}', command='{command}'");
            AppLog.Info($"Options: BaseTopic='{CurrentOptions.BaseTopic}', NetworkId='{CurrentOptions.NetworkId}', Name='{CurrentOptions.Name}'");
            if (!string.Equals(baseTopic, CurrentOptions.BaseTopic, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn("HandleCommandAsync: ignored (baseTopic mismatch)");
                return Task.CompletedTask;
            }

            if (!string.Equals(cmdLiteral, "cmd", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn("HandleCommandAsync: ignored (not cmd)");
                return Task.CompletedTask;
            }

            if (!string.Equals(networkId, CurrentOptions.NetworkId, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn("HandleCommandAsync: ignored (networkId mismatch)");
                return Task.CompletedTask;
            }

            // Accept commands for THIS gateway, and optionally broadcast target "all".
            if (!string.Equals(target, CurrentOptions.Name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn("HandleCommandAsync: ignored (target mismatch)");
                return Task.CompletedTask;
            }

            if (string.Equals(command, "start-update", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch start-update");
                var reqs = JsonSerializer.Deserialize<List<StartUpdateRequest>>(payload, JsonOptions)
                           ?? new List<StartUpdateRequest>();

                if (reqs.Count > 0)
                {
                    AppLog.Info($"DEBUG first req: DetectorType='{reqs[0].DetectorType}', FW='{reqs[0].FirmwareVersion}', MAC='{reqs[0].MacAddress}', Pin='{reqs[0].Pincode}'");
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

                AppLog.Info($"start-update parsed OK: {dto.Requests.Count} request(s)");
                return StartUpdateRequested?.Invoke(dto) ?? Task.CompletedTask;
            }

            if (string.Equals(command, "get-fw-version", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch get-fw-version");
                var dto = JsonSerializer.Deserialize<GetFwVersionCommand>(payload, JsonOptions) ?? new GetFwVersionCommand();
                return GetFwVersionRequested?.Invoke(dto) ?? Task.CompletedTask;
            }

            if (string.Equals(command, "get-detector-settings", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "get-device-settings", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch get-detector-settings");
                var dto = ParseDetectorSettingsCommandPayload(payload, requireSettings: false);
                return GetDetectorSettingsRequested?.Invoke(dto) ?? Task.CompletedTask;
            }

            if (string.Equals(command, "set-detector-settings", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "set-device-settings", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "apply-detector-settings", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch set-detector-settings");
                var dto = ParseDetectorSettingsCommandPayload(payload, requireSettings: true);
                return SetDetectorSettingsRequested?.Invoke(dto) ?? Task.CompletedTask;
            }

            if (string.Equals(command, "identify", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "identify-device", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch identify");
                IdentifyCommand dto;
                try
                {
                    if (string.IsNullOrWhiteSpace(payload))
                    {
                        dto = new IdentifyCommand();
                    }
                    else
                    {
                        // Accept: "AA:BB" | ["AA", ...] | {"sensors":[...],"pincode":"...","seconds":15,"maxConnectAttempts":1}
                        var p = payload.Trim();
                        if (p.StartsWith("["))
                        {
                            dto = new IdentifyCommand { Sensors = JsonSerializer.Deserialize<List<string>>(payload, JsonOptions) ?? new List<string>() };
                        }
                        else if (p.StartsWith("\""))
                        {
                            var mac = JsonSerializer.Deserialize<string>(payload, JsonOptions) ?? p.Trim('"');
                            dto = new IdentifyCommand { Sensors = new List<string> { mac } };
                        }
                        else
                        {
                            dto = JsonSerializer.Deserialize<IdentifyCommand>(payload, JsonOptions) ?? new IdentifyCommand();
                        }
                    }
                }
                catch
                {
                    dto = new IdentifyCommand();
                }

                return IdentifyRequested?.Invoke(dto) ?? Task.CompletedTask;
            }

            if (string.Equals(command, "led-range-visualize", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "led-range-start", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch led-range-visualize");
                LedRangeVisualizeCommand dto;
                try
                {
                    if (string.IsNullOrWhiteSpace(payload))
                    {
                        dto = new LedRangeVisualizeCommand();
                    }
                    else
                    {
                        dto = JsonSerializer.Deserialize<LedRangeVisualizeCommand>(payload, JsonOptions) ?? new LedRangeVisualizeCommand();
                    }
                }
                catch
                {
                    dto = new LedRangeVisualizeCommand();
                }

                return LedRangeVisualizeRequested?.Invoke(dto) ?? Task.CompletedTask;
            }

            if (string.Equals(command, "led-range-disconnect", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch led-range-disconnect");
                LedRangeDisconnectCommand dto;
                try
                {
                    if (string.IsNullOrWhiteSpace(payload))
                        dto = new LedRangeDisconnectCommand();
                    else if (payload.TrimStart().StartsWith("["))
                        dto = new LedRangeDisconnectCommand { Sensors = JsonSerializer.Deserialize<List<string>>(payload, JsonOptions) ?? new List<string>() };
                    else
                        dto = JsonSerializer.Deserialize<LedRangeDisconnectCommand>(payload, JsonOptions) ?? new LedRangeDisconnectCommand();
                }
                catch
                {
                    dto = new LedRangeDisconnectCommand();
                }

                return LedRangeDisconnectRequested?.Invoke(dto) ?? Task.CompletedTask;
            }

            if (string.Equals(command, "disconnect-devices", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "disconnect", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch disconnect-devices");
                DisconnectDevicesCommand dto;
                try
                {
                    // Accept: "AA:BB"  |  ["AA",... ]  |  {"sensors":[...]}
                    if (string.IsNullOrWhiteSpace(payload))
                        dto = new DisconnectDevicesCommand();
                    else if (payload.TrimStart().StartsWith("["))
                        dto = new DisconnectDevicesCommand { Sensors = JsonSerializer.Deserialize<List<string>>(payload, JsonOptions) ?? new List<string>() };
                    else if (payload.TrimStart().StartsWith("\""))
                        dto = new DisconnectDevicesCommand { Sensors = new List<string> { JsonSerializer.Deserialize<string>(payload, JsonOptions) ?? payload.Trim('"') } };
                    else
                        dto = JsonSerializer.Deserialize<DisconnectDevicesCommand>(payload, JsonOptions) ?? new DisconnectDevicesCommand();
                }
                catch
                {
                    dto = new DisconnectDevicesCommand();
                }

                return DisconnectDevicesRequested?.Invoke(dto) ?? Task.CompletedTask;
            }

            if (string.Equals(command, "set-scope", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "set-network", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "set-mqtt-scope", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch set-scope");

                SetMqttScopeCommand dto;
                try
                {
                    dto = string.IsNullOrWhiteSpace(payload)
                        ? new SetMqttScopeCommand()
                        : (JsonSerializer.Deserialize<SetMqttScopeCommand>(payload, JsonOptions) ?? new SetMqttScopeCommand());
                }
                catch
                {
                    dto = new SetMqttScopeCommand();
                }

                if (string.IsNullOrWhiteSpace(dto.NetworkId))
                {
                    var bad = new
                    {
                        success = false,
                        message = "Missing networkId. Send payload like {\"networkId\":\"my-net\"}.",
                        networkId = CurrentOptions.NetworkId,
                        name = CurrentOptions.Name
                    };
                    return PublishTeleJsonAsync("scope", bad, CancellationToken.None);
                }

                // Publish ACK on the *old* scope so the sender definitely sees it.
                var oldNetworkId = CurrentOptions.NetworkId;
                var oldName = CurrentOptions.Name;

                return Task.Run(async () =>
                {
                    try
                    {
                        // Update + persist without immediately restarting (so we can ACK first).
                        await _gate.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            CurrentOptions.NetworkId = dto.NetworkId!.Trim();
                            _store.Save(CurrentOptions);
                            UpgradeLogger.NetworkId = CurrentOptions.NetworkId;
                        }
                        finally
                        {
                            _gate.Release();
                        }

                        var ok = new
                        {
                            success = true,
                            message = "NetworkId updated. Restarting MQTT connection...",
                            previousNetworkId = oldNetworkId,
                            networkId = CurrentOptions.NetworkId,
                            name = CurrentOptions.Name
                        };

                        await PublishTeleJsonAsync("scope", ok, networkIdOverride: oldNetworkId, nameOverride: oldName, ct: CancellationToken.None).ConfigureAwait(false);

                        await RestartAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        var bad = new
                        {
                            success = false,
                            message = $"Failed to update networkId: {ex.Message}",
                            networkId = CurrentOptions.NetworkId,
                            name = CurrentOptions.Name
                        };

                        await PublishTeleJsonAsync("scope", bad, networkIdOverride: oldNetworkId, nameOverride: oldName, ct: CancellationToken.None).ConfigureAwait(false);
                    }
                });
            }

            if (string.Equals(command, "set-mqtt-broker", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "set-mqtt-config", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch set-mqtt-broker");

                if (string.IsNullOrWhiteSpace(payload))
                {
                    var bad = new
                    {
                        success = false,
                        message = "Missing payload. Send e.g. {\"host\":\"broker\", \"port\":1883, \"useTls\":true}.",
                        applyAfterRestart = true
                    };
                    return PublishTeleJsonAsync("mqtt-config", bad, CancellationToken.None);
                }

                return Task.Run(async () =>
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(payload);
                        var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object)
                        {
                            var bad = new
                            {
                                success = false,
                                message = "Expected a JSON object payload.",
                                applyAfterRestart = true
                            };
                            await PublishTeleJsonAsync("mqtt-config", bad, CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                        string? host = null;
                        int? port = null;
                        bool? useTls = null;

                        if (root.TryGetProperty("host", out var hostEl) && hostEl.ValueKind == JsonValueKind.String)
                            host = hostEl.GetString();

                        if (root.TryGetProperty("port", out var portEl))
                        {
                            if (portEl.ValueKind == JsonValueKind.Number && portEl.TryGetInt32(out var p))
                                port = p;
                            else if (portEl.ValueKind == JsonValueKind.String && int.TryParse(portEl.GetString(), out var p2))
                                port = p2;
                        }

                        if (root.TryGetProperty("useTls", out var tlsEl) || root.TryGetProperty("tls", out tlsEl))
                        {
                            if (tlsEl.ValueKind == JsonValueKind.True) useTls = true;
                            else if (tlsEl.ValueKind == JsonValueKind.False) useTls = false;
                            else if (tlsEl.ValueKind == JsonValueKind.String && bool.TryParse(tlsEl.GetString(), out var b))
                                useTls = b;
                        }

                        if (string.IsNullOrWhiteSpace(host) && port == null && useTls == null)
                        {
                            var bad = new
                            {
                                success = false,
                                message = "No changes provided. Include host, port, and/or useTls.",
                                applyAfterRestart = true
                            };
                            await PublishTeleJsonAsync("mqtt-config", bad, CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                        if (port.HasValue && (port.Value <= 0 || port.Value > 65535))
                        {
                            var bad = new
                            {
                                success = false,
                                message = $"Invalid port '{port}'. Must be 1-65535.",
                                applyAfterRestart = true
                            };
                            await PublishTeleJsonAsync("mqtt-config", bad, CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                        var opts = _store.LoadOrCreateDefault();
                        if (!string.IsNullOrWhiteSpace(host)) opts.Host = host.Trim();
                        if (port.HasValue) opts.Port = port.Value;
                        if (useTls.HasValue) opts.UseTls = useTls.Value;
                        _store.Save(opts);

                        var ok = new
                        {
                            success = true,
                            message = "mqtt.json saved. Changes apply after restart/reboot.",
                            host = opts.Host,
                            port = opts.Port,
                            useTls = opts.UseTls,
                            applyAfterRestart = true
                        };

                        await PublishTeleJsonAsync("mqtt-config", ok, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        var bad = new
                        {
                            success = false,
                            message = $"Failed to update mqtt.json: {ex.Message}",
                            applyAfterRestart = true
                        };
                        await PublishTeleJsonAsync("mqtt-config", bad, CancellationToken.None).ConfigureAwait(false);
                    }
                });
            }

            // NEW: Set gateway/cassia name via MQTT (persisted)
            if (string.Equals(command, "set-name", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "set-cassia-name", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "set-gateway-name", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch set-name");

                string? newName = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(payload))
                    {
                        var p = payload.Trim();
                        if (p.StartsWith("\""))
                        {
                            newName = JsonSerializer.Deserialize<string>(payload, JsonOptions);
                        }
                        else
                        {
                            using var doc = JsonDocument.Parse(payload);
                            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                                doc.RootElement.TryGetProperty("name", out var n) &&
                                n.ValueKind == JsonValueKind.String)
                            {
                                newName = n.GetString();
                            }
                        }
                    }
                }
                catch { /* ignored */ }

                if (string.IsNullOrWhiteSpace(newName))
                {
                    var bad = new
                    {
                        success = false,
                        message = "Missing name. Send payload like {\"name\":\"cassia-01\"} (or a raw JSON string).",
                        networkId = CurrentOptions.NetworkId,
                        name = CurrentOptions.Name
                    };
                    return PublishTeleJsonAsync("identity", bad, CancellationToken.None);
                }

                var oldNetworkId = CurrentOptions.NetworkId;
                var oldName = CurrentOptions.Name;

                return Task.Run(async () =>
                {
                    try
                    {
                        await _gate.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            CurrentOptions.Name = newName!.Trim();
                            _store.Save(CurrentOptions);
                            UpgradeLogger.NetworkId = CurrentOptions.NetworkId;
                        }
                        finally
                        {
                            _gate.Release();
                        }

                        var ok = new
                        {
                            success = true,
                            message = "Name updated. Restarting MQTT connection...",
                            previousName = oldName,
                            name = CurrentOptions.Name,
                            networkId = CurrentOptions.NetworkId
                        };

                        await PublishTeleJsonAsync("identity", ok, networkIdOverride: oldNetworkId, nameOverride: oldName, ct: CancellationToken.None).ConfigureAwait(false);

                        await RestartAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        var bad = new
                        {
                            success = false,
                            message = $"Failed to update name: {ex.Message}",
                            networkId = CurrentOptions.NetworkId,
                            name = CurrentOptions.Name
                        };

                        await PublishTeleJsonAsync("identity", bad, networkIdOverride: oldNetworkId, nameOverride: oldName, ct: CancellationToken.None).ConfigureAwait(false);
                    }
                });
            }

            // NEW: Set one or more runtime variables via MQTT (persist to runtime.json by default)
            if (string.Equals(command, "set-runtime", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "set-runtime-variables", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "set-runtime-vars", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "set-var", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "set-vars", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch set-runtime-vars");

                if (string.IsNullOrWhiteSpace(payload))
                {
                    var bad = new
                    {
                        success = false,
                        message = "Missing payload. Send e.g. {\"WRITE_SLEEP_MS\":50,\"USE_BOTH_CASSIA_CHIPS\":true} or {\"name\":\"WRITE_SLEEP_MS\",\"value\":50}. Optional: {\"persist\":true}.",
                        variables = _runtimeStore.GetAll()
                    };
                    return PublishTeleJsonAsync("runtime", bad, CancellationToken.None);
                }

                return Task.Run(async () =>
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(payload);
                        var root = doc.RootElement;

                        int applied = 0;
                        List<string> appliedNames = new();
                        List<string> errors = new();
                        bool persist = true;
                        bool persisted = false;

                        if (root.ValueKind == JsonValueKind.Object &&
                            root.TryGetProperty("persist", out var persistEl))
                        {
                            if (persistEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                                persist = persistEl.GetBoolean();
                            else if (persistEl.ValueKind == JsonValueKind.String &&
                                     bool.TryParse(persistEl.GetString(), out var p))
                                persist = p;
                        }

                        // Shape A: {"name":"X","value":...}
                        if (root.ValueKind == JsonValueKind.Object &&
                            root.TryGetProperty("name", out var n) &&
                            n.ValueKind == JsonValueKind.String &&
                            root.TryGetProperty("value", out var v))
                        {
                            var varName = n.GetString() ?? "";
                            if (_runtimeStore.SetSingle(varName, v, out var err))
                            {
                                appliedNames.Add(varName);
                                applied = 1;
                            }
                            else if (!string.IsNullOrWhiteSpace(err))
                                errors.Add(err);
                        }
                        else
                        {
                            // Shape B: {"X":..., "Y":...}
                            var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "persist" };
                            var (names, errorMap) = _runtimeStore.SetFromJsonObject(root, ignore);
                            appliedNames = names;
                            applied = names.Count;
                            foreach (var kv in errorMap)
                                errors.Add($"{kv.Key}: {kv.Value}");
                        }

                        if (persist && applied > 0)
                            persisted = _runtimeStore.SaveToDisk();

                        var restartRequiredVars = appliedNames
                            .Where(fn => RuntimeVariablesStore.RestartRequiredFields.Contains(fn))
                            .ToList();

                        var resp = new
                        {
                            success = applied > 0 && errors.Count == 0,
                            applied,
                            errors,
                            persisted,
                            persistPath = _runtimeStore.FilePath,
                            restartRequired = restartRequiredVars.Count > 0,
                            restartRequiredVars,
                            variables = _runtimeStore.GetAll()
                        };

                        await PublishTeleJsonAsync("runtime", resp, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        var bad = new
                        {
                            success = false,
                            message = $"Failed to set runtime variables: {ex.Message}",
                            persisted = false,
                            persistPath = _runtimeStore.FilePath,
                            variables = _runtimeStore.GetAll()
                        };

                        await PublishTeleJsonAsync("runtime", bad, CancellationToken.None).ConfigureAwait(false);
                    }
                });
            }

            // NEW: Get all runtime variables (current values)
            if (string.Equals(command, "get-runtime", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "get-runtime-variables", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "get-runtime-vars", StringComparison.OrdinalIgnoreCase))
            {
                var resp = new
                {
                    success = true,
                    variables = _runtimeStore.GetAll(),
                    name = CurrentOptions.Name,
                    networkId = CurrentOptions.NetworkId,
                    time = DateTimeOffset.UtcNow
                };
                return PublishTeleJsonAsync("runtime", resp, CancellationToken.None);
            }

            // Get raw runtime.json file content from disk (shows only persisted overrides, not all defaults)
            if (string.Equals(command, "get-runtime-file", StringComparison.OrdinalIgnoreCase))
            {
                string? rawJson = null;
                bool fileExists = File.Exists(_runtimeStore.FilePath);
                if (fileExists)
                {
                    try { rawJson = File.ReadAllText(_runtimeStore.FilePath); }
                    catch (Exception ex) { rawJson = $"(read error: {ex.Message})"; }
                }

                var resp = new
                {
                    success = true,
                    filePath = _runtimeStore.FilePath,
                    fileExists,
                    rawJson,
                    time = DateTimeOffset.UtcNow
                };
                return PublishTeleJsonAsync("runtime", resp, CancellationToken.None);
            }

            // Execute a shell command on the device. Requires HMAC-SHA256 token to prevent unauthorized use.
            // Payload: { "cmd": "...", "ts": <unix_epoch_seconds>, "hmac": "<hex>" }
            // HMAC key = ShellExecToken + this gateway's MQTT name; message = cmd + ts
            if (string.Equals(command, "exec-shell", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "shell", StringComparison.OrdinalIgnoreCase))
            {
                return Task.Run(async () =>
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(payload))
                        {
                            await PublishTeleJsonAsync("shell", new { success = false, message = "Missing payload. Expected {\"cmd\":\"...\",\"ts\":unix_epoch,\"hmac\":\"sha256hex\"}." }, CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                        using var doc = JsonDocument.Parse(payload);
                        var root = doc.RootElement;

                        var cmd = root.TryGetProperty("cmd", out var cmdEl) ? cmdEl.GetString() ?? "" : "";
                        var ts = root.TryGetProperty("ts", out var tsEl) && tsEl.TryGetInt64(out var tsVal) ? tsVal : 0L;
                        var hmac = root.TryGetProperty("hmac", out var hmacEl) ? hmacEl.GetString() ?? "" : "";

                        if (string.IsNullOrWhiteSpace(cmd))
                        {
                            await PublishTeleJsonAsync("shell", new { success = false, message = "Missing 'cmd' field." }, CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                        // Validate timestamp within ±60 seconds to prevent replay attacks.
                        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        if (Math.Abs(nowTs - ts) > 60)
                        {
                            await PublishTeleJsonAsync("shell", new { success = false, message = $"Token expired or clock skew too large (server={nowTs}, received={ts})." }, CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                        // Validate HMAC.
                        var expected = ComputeShellHmac(cmd, ts);
                        if (!string.Equals(expected, hmac, StringComparison.OrdinalIgnoreCase))
                        {
                            AppLog.Warn($"[Shell] HMAC verification failed for cmd='{cmd}'");
                            await PublishTeleJsonAsync("shell", new { success = false, message = "Invalid token." }, CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                        AppLog.Warn($"[Shell] Executing: {cmd}");

                        var psi = new ProcessStartInfo
                        {
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash"
                        };
                        psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
                        psi.ArgumentList.Add(cmd);

                        var sw = Stopwatch.StartNew();
                        using var proc = new Process { StartInfo = psi };
                        proc.Start();

                        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                        var stderrTask = proc.StandardError.ReadToEndAsync();

                        var exited = proc.WaitForExit(30_000);
                        if (!exited)
                        {
                            try { proc.Kill(entireProcessTree: true); } catch { }
                        }

                        sw.Stop();
                        var stdout = await stdoutTask.ConfigureAwait(false);
                        var stderr = await stderrTask.ConfigureAwait(false);

                        // Cap output to prevent oversized MQTT messages.
                        const int maxChars = 50_000;
                        if (stdout.Length > maxChars) stdout = stdout[..maxChars] + "\n[truncated]";
                        if (stderr.Length > maxChars) stderr = stderr[..maxChars] + "\n[truncated]";

                        var exitCode = exited ? proc.ExitCode : -1;
                        await PublishTeleJsonAsync("shell", new
                        {
                            success = exited && exitCode == 0,
                            cmd,
                            exitCode,
                            stdout,
                            stderr,
                            timedOut = !exited,
                            durationMs = sw.ElapsedMilliseconds
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error($"[Shell] Error: {ex.Message}");
                        await PublishTeleJsonAsync("shell", new { success = false, message = ex.Message }, CancellationToken.None).ConfigureAwait(false);
                    }
                });
            }

            if (string.Equals(command, "get-cassia-settings", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "get-gateway-settings", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch get-cassia-settings");

                return Task.Run(async () =>
                {
                    string requestId = Guid.NewGuid().ToString("N");
                    string? gatewayIp = null;
                    string? username = null;
                    string? password = null;
                    string? passwordEncrypted = null;

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(payload))
                        {
                            using var doc = JsonDocument.Parse(payload);
                            var root = doc.RootElement;
                            if (root.ValueKind == JsonValueKind.Object)
                            {
                                requestId = ReadString(root, "requestId") ?? requestId;
                                gatewayIp = ReadString(root, "gatewayIp");
                                username = ReadString(root, "username");
                                password = ReadString(root, "password");
                                passwordEncrypted = ReadString(root, "passwordEncrypted");
                            }
                        }

                        if (string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(gatewayIp))
                                AppLog.Info("get-cassia-settings: target=all, ignoring payload gatewayIp and using local gateway configuration.");
                            gatewayIp = null;
                        }

                        var req = new CassiaWebSettingsRequest(
                            GatewayIp: gatewayIp,
                            Username: username,
                            Password: password,
                            PasswordEncrypted: passwordEncrypted);

                        var snapshot = await _cassiaWebSettings.GetSettingsAsync(req, CancellationToken.None).ConfigureAwait(false);

                        var ok = new
                        {
                            success = true,
                            action = "get",
                            requestId,
                            message = "Cassia settings loaded.",
                            cassia = target,
                            gatewayIp = snapshot.GatewayIp,
                            csrf = snapshot.Csrf,
                            settings = snapshot.Settings,
                            savePayloadTemplate = snapshot.SavePayloadTemplate,
                            name = CurrentOptions.Name,
                            networkId = CurrentOptions.NetworkId,
                            time = DateTimeOffset.UtcNow
                        };

                        await PublishTeleJsonAsync("cassia-settings", ok, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        var bad = new
                        {
                            success = false,
                            action = "get",
                            requestId,
                            message = $"Failed to load Cassia settings: {ex.Message}",
                            cassia = target,
                            name = CurrentOptions.Name,
                            networkId = CurrentOptions.NetworkId,
                            time = DateTimeOffset.UtcNow
                        };

                        await PublishTeleJsonAsync("cassia-settings", bad, CancellationToken.None).ConfigureAwait(false);
                    }

                    static string? ReadString(JsonElement obj, string propertyName)
                    {
                        if (!obj.TryGetProperty(propertyName, out var value))
                            return null;

                        if (value.ValueKind == JsonValueKind.String)
                            return value.GetString();

                        if (value.ValueKind == JsonValueKind.Number)
                            return value.ToString();

                        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            return value.GetBoolean() ? "true" : "false";

                        return null;
                    }
                });
            }

            if (string.Equals(command, "set-cassia-settings", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "save-cassia-settings", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch set-cassia-settings");

                return Task.Run(async () =>
                {
                    string requestId = Guid.NewGuid().ToString("N");
                    string? gatewayIp = null;
                    string? username = null;
                    string? password = null;
                    string? passwordEncrypted = null;
                    JsonObject? settingsPayload = null;

                    try
                    {
                        if (string.IsNullOrWhiteSpace(payload))
                        {
                            var bad = new
                            {
                                success = false,
                                action = "set",
                                requestId,
                                message = "Missing payload. Send {\"settings\":{...}}.",
                                cassia = target,
                                name = CurrentOptions.Name,
                                networkId = CurrentOptions.NetworkId,
                                time = DateTimeOffset.UtcNow
                            };
                            await PublishTeleJsonAsync("cassia-settings", bad, CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                        using var doc = JsonDocument.Parse(payload);
                        var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object)
                            throw new InvalidOperationException("Payload must be a JSON object.");

                        requestId = ReadString(root, "requestId") ?? requestId;
                        gatewayIp = ReadString(root, "gatewayIp");
                        username = ReadString(root, "username");
                        password = ReadString(root, "password");
                        passwordEncrypted = ReadString(root, "passwordEncrypted");

                        if (root.TryGetProperty("settings", out var settingsEl) && settingsEl.ValueKind == JsonValueKind.Object)
                        {
                            settingsPayload = JsonNode.Parse(settingsEl.GetRawText()) as JsonObject;
                        }
                        else
                        {
                            settingsPayload = JsonNode.Parse(root.GetRawText()) as JsonObject;
                            if (settingsPayload != null)
                            {
                                var metaKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                {
                                    "requestId",
                                    "gatewayIp",
                                    "username",
                                    "password",
                                    "passwordEncrypted",
                                    "includeCurrent"
                                };

                                foreach (var key in settingsPayload.Select(p => p.Key).ToList())
                                {
                                    if (metaKeys.Contains(key))
                                        settingsPayload.Remove(key);
                                }
                            }
                        }

                        if (settingsPayload == null || settingsPayload.Count == 0)
                            throw new InvalidOperationException("No settings object found in payload.");

                        if (string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(gatewayIp))
                                AppLog.Info("set-cassia-settings: target=all, ignoring payload gatewayIp and using local gateway configuration.");
                            gatewayIp = null;
                        }

                        var req = new CassiaWebSettingsRequest(
                            GatewayIp: gatewayIp,
                            Username: username,
                            Password: password,
                            PasswordEncrypted: passwordEncrypted);

                        var snapshot = await _cassiaWebSettings.SaveSettingsAsync(req, settingsPayload, CancellationToken.None).ConfigureAwait(false);

                        var ok = new
                        {
                            success = true,
                            action = "set",
                            requestId,
                            message = "Cassia settings saved.",
                            cassia = target,
                            gatewayIp = snapshot.GatewayIp,
                            csrf = snapshot.Csrf,
                            settings = snapshot.Settings,
                            savePayloadTemplate = snapshot.SavePayloadTemplate,
                            name = CurrentOptions.Name,
                            networkId = CurrentOptions.NetworkId,
                            time = DateTimeOffset.UtcNow
                        };

                        await PublishTeleJsonAsync("cassia-settings", ok, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        var bad = new
                        {
                            success = false,
                            action = "set",
                            requestId,
                            message = $"Failed to save Cassia settings: {ex.Message}",
                            cassia = target,
                            name = CurrentOptions.Name,
                            networkId = CurrentOptions.NetworkId,
                            time = DateTimeOffset.UtcNow
                        };

                        await PublishTeleJsonAsync("cassia-settings", bad, CancellationToken.None).ConfigureAwait(false);
                    }

                    static string? ReadString(JsonElement obj, string propertyName)
                    {
                        if (!obj.TryGetProperty(propertyName, out var value))
                            return null;

                        if (value.ValueKind == JsonValueKind.String)
                            return value.GetString();

                        if (value.ValueKind == JsonValueKind.Number)
                            return value.ToString();

                        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            return value.GetBoolean() ? "true" : "false";

                        return null;
                    }
                });
            }

            if (string.Equals(command, "send-upgrade-log", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch send-upgrade-log");
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
                    ct: CancellationToken.None
                );
            }

            if (string.Equals(command, "set-write-sleep-ms", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(command, "write-sleep-ms", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch set-write-sleep-ms");
                var raw = (payload ?? string.Empty).Trim();

                // accept raw "40"
                if (!int.TryParse(raw, out var ms))
                {
                    // (optional) accept {"value":40} too, without breaking raw mode
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                            doc.RootElement.TryGetProperty("value", out var v) &&
                            v.ValueKind == JsonValueKind.Number &&
                            v.TryGetInt32(out var jsonMs))
                        {
                            ms = jsonMs;
                        }
                        else
                        {
                            ms = int.MinValue;
                        }
                    }
                    catch
                    {
                        ms = int.MinValue;
                    }

                    if (ms == int.MinValue)
                    {
                        var bad = new
                        {
                            success = false,
                            message = "Missing integer value. Send payload like {\"value\":40} or just 40.",
                            currentValue = RuntimeVariables.WRITE_SLEEP_MS
                        };
                        return PublishTeleJsonAsync("write-sleep-ms", bad, CancellationToken.None);
                    }
                }

                // sanity limits (adjust if you want)
                if (ms < 0 || ms > 1000)
                {
                    var bad = new
                    {
                        success = false,
                        message = "Value out of range. Allowed: 0..1000 ms.",
                        currentValue = RuntimeVariables.WRITE_SLEEP_MS
                    };
                    return PublishTeleJsonAsync("write-sleep-ms", bad, CancellationToken.None);
                }

                RuntimeVariables.WRITE_SLEEP_MS = ms;

                var ok = new
                {
                    success = true,
                    message = "WRITE_SLEEP_MS updated.",
                    value = RuntimeVariables.WRITE_SLEEP_MS
                };
                return PublishTeleJsonAsync("write-sleep-ms", ok, CancellationToken.None);
            }


            // NEW: request firmware manifest
            if (string.Equals(command, "get-fw-manifest", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch get-fw-manifest");
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

            if (string.Equals(command, "self-update", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "app-self-update", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch self-update");
                SelfUpdateCommand dto;

                try
                {
                    dto = string.IsNullOrWhiteSpace(payload)
                        ? new SelfUpdateCommand()
                        : JsonSerializer.Deserialize<SelfUpdateCommand>(payload, JsonOptions) ?? new SelfUpdateCommand();
                }
                catch
                {
                    dto = new SelfUpdateCommand();
                }

                return SelfUpdateRequested?.Invoke(dto) ?? Task.CompletedTask;
            }

            if (string.Equals(command, "set-update-channel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "set-channel", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch set-update-channel");
                SetUpdateChannelCommand dto;

                try
                {
                    dto = string.IsNullOrWhiteSpace(payload)
                        ? new SetUpdateChannelCommand()
                        : JsonSerializer.Deserialize<SetUpdateChannelCommand>(payload, JsonOptions) ?? new SetUpdateChannelCommand();

                    if (string.IsNullOrWhiteSpace(dto.Channel) && !string.IsNullOrWhiteSpace(payload))
                    {
                        using var doc = JsonDocument.Parse(payload);
                        if (doc.RootElement.ValueKind == JsonValueKind.String)
                        {
                            dto.Channel = doc.RootElement.GetString();
                        }
                        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            if (doc.RootElement.TryGetProperty("value", out var chEl) && chEl.ValueKind == JsonValueKind.String)
                                dto.Channel = chEl.GetString();
                        }
                    }
                }
                catch
                {
                    dto = new SetUpdateChannelCommand();
                }

                return SetUpdateChannelRequested?.Invoke(dto) ?? Task.CompletedTask;
            }

            if (string.Equals(command, "reboot", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "system-reboot", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch reboot");
                RebootCommand dto;

                try
                {
                    dto = string.IsNullOrWhiteSpace(payload)
                        ? new RebootCommand()
                        : JsonSerializer.Deserialize<RebootCommand>(payload, JsonOptions) ?? new RebootCommand();

                    if (!string.IsNullOrWhiteSpace(payload))
                    {
                        using var doc = JsonDocument.Parse(payload);
                        if (doc.RootElement.ValueKind == JsonValueKind.Number &&
                            doc.RootElement.TryGetInt32(out var delayFromNumber))
                        {
                            dto.DelaySeconds = delayFromNumber;
                        }
                        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            if (doc.RootElement.TryGetProperty("delay", out var delayEl) &&
                                delayEl.ValueKind == JsonValueKind.Number &&
                                delayEl.TryGetInt32(out var delayFromDelay))
                            {
                                dto.DelaySeconds = delayFromDelay;
                            }
                        }
                    }
                }
                catch
                {
                    dto = new RebootCommand();
                }

                return RebootRequested?.Invoke(dto) ?? Task.CompletedTask;
            }


            if (string.Equals(command, "get-queue-list", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch get-queue-list");
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
                AppLog.Debug("HandleCommandAsync: dispatch get-programming-list");
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
                AppLog.Debug("HandleCommandAsync: dispatch get-device-list");
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

            if (string.Equals(command, "clear-device-list", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch clear-device-list");
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

                var removed = DeviceStorageService.ClearAllDevices();

                var resp = new
                {
                    success = true,
                    message = "Device list cleared successfully.",
                    requestId,
                    name = CurrentOptions.Name,
                    networkId = CurrentOptions.NetworkId,
                    time = DateTimeOffset.UtcNow,
                    removed
                };

                return PublishJsonAsync(TeleTopic("clear-device-list"), resp, retain: false, CancellationToken.None);
            }

            if (string.Equals(command, "remove-from-queue", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch remove-from-queue");
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
                int removedInProgress = 0;
                foreach (var m in macs)
                {
                    removed += CassiaFirmwareUpgradeService.RemoveFromUpgradeQueuePending(m);
                    // Also force-clear from the active in-progress set so that a device stuck
                    // in a previous (hung) upgrade task can be re-queued without restarting.
                    if (CassiaFirmwareUpgradeService.ForceRemoveFromInProgress(m))
                        removedInProgress++;
                }

                var resp = new
                {
                    success = true,
                    message = (removed + removedInProgress) > 0
                        ? $"Removed device(s): {removed} from pending queue, {removedInProgress} forced from in-progress."
                        : "No matching devices found in queue or in-progress.",
                    name = CurrentOptions.Name,
                    networkId = CurrentOptions.NetworkId,
                    time = DateTimeOffset.UtcNow,
                    requested = macs,
                    removed,
                    removedInProgress
                };

                return PublishJsonAsync(TeleTopic("queue-remove"), resp, retain: false, CancellationToken.None);
            }

            if (string.Equals(command, "get-parallel-programmers", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch get-parallel-programmers");
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
                AppLog.Debug("HandleCommandAsync: dispatch set-parallel-programmers");
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
                AppLog.Debug("HandleCommandAsync: dispatch clear-upgrade-log");
                var logPath = AccessAppPaths.UpgradeLog;

                if (!System.IO.File.Exists(logPath))
                {
                    AppLog.Info("No upgrade log file to clear.");
                    return Task.CompletedTask;
                }

                try
                {
                    System.IO.File.Delete(logPath);
                    AppLog.Info("Upgrade log cleared successfully.");
                    return Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Error clearing upgrade log: {ex.Message}");
                    return Task.CompletedTask;

                }
            }

            if (string.Equals(command, "clear-device-settings-backups", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch clear-device-settings-backups");
                var currentDir = Directory.GetCurrentDirectory();
                var backupsDir = Path.Combine(currentDir, "device-settings-backups");

                if (!Directory.Exists(backupsDir))
                {
                    var bad = new
                    {
                        success = false,
                        message = "device-settings-backups directory does not exist."
                    };

                    return PublishJsonAsync(
                        TeleTopic("clear-device-settings-backups"),
                        bad,
                        retain: false,
                        CancellationToken.None);
                }

                try
                {
                    var files = Directory.GetFiles(backupsDir);
                    int deleted = 0;
                    int failed = 0;

                    foreach (var file in files)
                    {
                        try
                        {
                            System.IO.File.Delete(file);
                            deleted++;
                        }
                        catch
                        {
                            failed++;
                        }
                    }

                    var ok = new
                    {
                        success = true,
                        message = $"Device settings backups cleared. Deleted={deleted}, Failed={failed}."
                    };

                    AppLog.Info(ok.message);
                    return PublishJsonAsync(
                        TeleTopic("clear-device-settings-backups"),
                        ok,
                        retain: false,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    var bad = new
                    {
                        success = false,
                        message = $"Error clearing device settings backups: {ex.Message}"
                    };

                    AppLog.Info(bad.message);
                    return PublishJsonAsync(
                        TeleTopic("clear-device-settings-backups"),
                        bad,
                        retain: false,
                        CancellationToken.None);
                }
            }


            if (string.Equals(command, "get-pir-peak", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch get-pir-peak");
                GetPirPeakCommand dto;
                try
                {
                    dto = string.IsNullOrWhiteSpace(payload)
                        ? new GetPirPeakCommand()
                        : JsonSerializer.Deserialize<GetPirPeakCommand>(payload, JsonOptions) ?? new GetPirPeakCommand();
                }
                catch { dto = new GetPirPeakCommand(); }
                return GetPirPeakRequested?.Invoke(dto) ?? Task.CompletedTask;
            }

            if (string.Equals(command, "set-walktest", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Debug("HandleCommandAsync: dispatch set-walktest");
                SetWalktestCommand wt;
                try
                {
                    wt = string.IsNullOrWhiteSpace(payload)
                        ? new SetWalktestCommand()
                        : JsonSerializer.Deserialize<SetWalktestCommand>(payload, JsonOptions) ?? new SetWalktestCommand();
                }
                catch { wt = new SetWalktestCommand(); }
                return SetWalktestRequested?.Invoke(wt) ?? Task.CompletedTask;
            }

            AppLog.Warn($"HandleCommandAsync: ignored (unknown command '{command}')");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            AppLog.Error($"HandleCommandAsync ERROR: {ex}");
            AppLog.Info($"topic was: {topic}");
            AppLog.Info($"payload was: {payload}");
            return Task.CompletedTask;
        }
    }

    private static DetectorSettingsCommand ParseDetectorSettingsCommandPayload(string? payload, bool requireSettings)
    {
        var dto = new DetectorSettingsCommand();

        try
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                dto = new DetectorSettingsCommand();
            }
            else if (payload.TrimStart().StartsWith("["))
            {
                dto = new DetectorSettingsCommand
                {
                    Sensors = JsonSerializer.Deserialize<List<string>>(payload, JsonOptions) ?? new List<string>()
                };
            }
            else if (payload.TrimStart().StartsWith("\""))
            {
                dto = new DetectorSettingsCommand
                {
                    Sensors = new List<string> { JsonSerializer.Deserialize<string>(payload, JsonOptions) ?? payload.Trim('"') }
                };
            }
            else
            {
                dto = JsonSerializer.Deserialize<DetectorSettingsCommand>(payload, JsonOptions) ?? new DetectorSettingsCommand();
            }
        }
        catch
        {
            dto = new DetectorSettingsCommand();
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(payload))
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (dto.Sensors.Count == 0)
                    {
                        if (TryGetString(root, "macAddress", out var oneMac) && !string.IsNullOrWhiteSpace(oneMac))
                            dto.Sensors.Add(oneMac);
                        else if (TryGetString(root, "mac", out oneMac) && !string.IsNullOrWhiteSpace(oneMac))
                            dto.Sensors.Add(oneMac);
                        else if (TryGetString(root, "sensor", out oneMac) && !string.IsNullOrWhiteSpace(oneMac))
                            dto.Sensors.Add(oneMac);
                    }

                    if (dto.Settings == null)
                    {
                        if (root.TryGetProperty("settings", out var settingsEl) && settingsEl.ValueKind == JsonValueKind.Object)
                        {
                            dto.Settings = JsonSerializer.Deserialize<DetectorSettingsPatch>(settingsEl.GetRawText(), JsonOptions);
                        }
                        else
                        {
                            var patch = new DetectorSettingsPatch();
                            var hasPatch = false;

                            if (TryGetString(root, "userConfigHex", out var user))
                            {
                                patch.UserConfigHex = user;
                                hasPatch = true;
                            }
                            if (TryGetString(root, "userConfigMaskHex", out var userMask))
                            {
                                patch.UserConfigMaskHex = userMask;
                                hasPatch = true;
                            }
                            if (TryGetString(root, "pushButtonsHex", out var wired))
                            {
                                patch.PushButtonsHex = wired;
                                hasPatch = true;
                            }
                            if (TryGetString(root, "pushButtonsMaskHex", out var wiredMask))
                            {
                                patch.PushButtonsMaskHex = wiredMask;
                                hasPatch = true;
                            }
                            if (TryGetString(root, "daliPushButtonsHex", out var dali))
                            {
                                patch.DaliPushButtonsHex = dali;
                                hasPatch = true;
                            }
                            if (TryGetString(root, "daliPushButtonsMaskHex", out var daliMask))
                            {
                                patch.DaliPushButtonsMaskHex = daliMask;
                                hasPatch = true;
                            }
                            if (TryGetString(root, "daliDeviceCommonParamHex", out var daliCommon))
                            {
                                patch.DaliDeviceCommonParamHex = daliCommon;
                                hasPatch = true;
                            }
                            if (TryGetString(root, "daliDeviceCommonParamMaskHex", out var daliCommonMask))
                            {
                                patch.DaliDeviceCommonParamMaskHex = daliCommonMask;
                                hasPatch = true;
                            }
                            if (TryGetString(root, "blePushButtonsHex", out var ble))
                            {
                                patch.BlePushButtonsHex = ble;
                                hasPatch = true;
                            }
                            if (TryGetString(root, "blePushButtonsMaskHex", out var bleMask))
                            {
                                patch.BlePushButtonsMaskHex = bleMask;
                                hasPatch = true;
                            }
                            if (TryGetString(root, "tunableWhiteListHex", out var twList))
                            {
                                patch.TunableWhiteListHex = twList;
                                hasPatch = true;
                            }
                            if (TryGetString(root, "tunableWhitePresetHex", out var twPreset))
                            {
                                patch.TunableWhitePresetHex = twPreset;
                                hasPatch = true;
                            }
                            if (TryGetString(root, "tunableWhiteDefaultKelvinHex", out var twKelvin))
                            {
                                patch.TunableWhiteDefaultKelvinHex = twKelvin;
                                hasPatch = true;
                            }

                            if (hasPatch)
                                dto.Settings = patch;
                        }
                    }

                    if (dto.WriteOnlyChanged == null &&
                        root.TryGetProperty("writeOnlyChanged", out var woc))
                    {
                        if (woc.ValueKind == JsonValueKind.True) dto.WriteOnlyChanged = true;
                        else if (woc.ValueKind == JsonValueKind.False) dto.WriteOnlyChanged = false;
                        else if (woc.ValueKind == JsonValueKind.String && bool.TryParse(woc.GetString(), out var b)) dto.WriteOnlyChanged = b;
                    }
                }
            }
        }
        catch
        {
            // keep best-effort parsed dto
        }

        dto.Sensors = dto.Sensors
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requireSettings && dto.Settings == null)
            dto.Settings = new DetectorSettingsPatch();

        return dto;

        static bool TryGetString(JsonElement root, string name, out string value)
        {
            value = string.Empty;
            if (!root.TryGetProperty(name, out var el))
                return false;

            if (el.ValueKind == JsonValueKind.String)
            {
                value = el.GetString() ?? string.Empty;
                return true;
            }

            if (el.ValueKind == JsonValueKind.Number)
            {
                value = el.ToString();
                return true;
            }

            if (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            {
                value = el.GetBoolean() ? "true" : "false";
                return true;
            }

            return false;
        }
    }

    private async Task SubscribeTopicsAsync(CancellationToken ct)
    {
        if (_client is null) return;
        if (_subscribed) return;

        var topicMine = CmdTopic(CurrentOptions.Name, "#");
        AppLog.Info($"Subscribing: {topicMine}");
        await _client.SubscribeAsync(new MqttTopicFilter
        {
            Topic = topicMine,
            QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce
        }, ct).ConfigureAwait(false);

        if (CurrentOptions.SubscribeToAllTarget)
        {
            var topicAll = CmdTopic("all", "#");
            AppLog.Info($"Subscribing: {topicAll}");
            await _client.SubscribeAsync(new MqttTopicFilter
            {
                Topic = topicAll,
                QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce
            }, ct).ConfigureAwait(false);
        }

        _subscribed = true;
        AppLog.Info("Subscriptions active");
    }

    // ---------------- Publish helpers ----------------

    private async Task<bool> TryPublishMessageAsync(MqttApplicationMessage msg, CancellationToken ct)
    {
        var client = _client;
        if (client is null || !client.IsConnected) return false;

        // Hard timeout so a half-dead TCP connection cannot stall all publishes forever.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutSec = Math.Max(1, CurrentOptions.PublishTimeoutSeconds);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        try
        {
            await client.PublishAsync(msg, timeoutCts.Token).ConfigureAwait(false);
            NotePublishSuccess();
            return true;
        }
        catch (OperationCanceledException oce)
        {
            NotePublishFailure("publish timeout", oce);
            return false;
        }
        catch (Exception ex)
        {
            NotePublishFailure("publish error", ex);
            return false;
        }
    }

    private void NotePublishSuccess()
    {
        _publishFailures = 0;
        _publishFailureWindowStartUtc = DateTime.MinValue;
        _lastPublishOkUtc = DateTime.UtcNow;
    }

    private void NotePublishFailure(string reason, Exception? ex)
    {
        var now = DateTime.UtcNow;

        // Rolling failure window.
        var windowSec = Math.Max(1, CurrentOptions.PublishFailureWindowSeconds);
        if (_publishFailureWindowStartUtc == DateTime.MinValue || (now - _publishFailureWindowStartUtc) > TimeSpan.FromSeconds(windowSec))
        {
            _publishFailureWindowStartUtc = now;
            _publishFailures = 0;
        }

        _publishFailures++;

        AppLog.Info($"Publish failure ({_publishFailures}/{CurrentOptions.PublishFailureReconnectThreshold}) reason='{reason}' ex='{ex?.Message}'");
        var threshold = Math.Max(1, CurrentOptions.PublishFailureReconnectThreshold);
        if (_publishFailures < threshold) return;

        // Debounce reconnect attempts.
        var debounceSec = Math.Max(1, CurrentOptions.ReconnectDebounceSeconds);
        if ((now - _lastReconnectAttemptUtc) < TimeSpan.FromSeconds(debounceSec)) return;

        _lastReconnectAttemptUtc = now;

        if (Interlocked.Exchange(ref _reconnectRequested, 1) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                AppLog.Info("Publish failures exceeded threshold -> restarting MQTT connection");
                await RestartAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rex)
            {
                AppLog.Error($"Restart after publish failures failed: {rex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectRequested, 0);
            }
        });
    }

    private async Task PublishJsonAsync(string topic, object payload, bool retain, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        // Notify local broker mirrors at serialization time so they work even when the
        // primary broker is unreachable.
        MessagePublished?.Invoke(topic, bytes);

        // Best-effort publish to primary broker; do not let a primary failure prevent
        // local delivery (MessagePublished already fired above).
        try
        {
            await EnsureConnectedAndSubscribedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return; }

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(bytes)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        await TryPublishMessageAsync(msg, ct).ConfigureAwait(false);
    }

    public async Task PublishAsync(string topic, string payload, bool retain = false, int qos = 0, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(payload ?? string.Empty);

        // Mirror to local broker first — same pattern as PublishJsonAsync so that
        // local delivery works even when the primary broker is unreachable.
        MessagePublished?.Invoke(topic, bytes);

        try
        {
            await EnsureConnectedAndSubscribedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return; }

        var level = qos <= 0
            ? MqttQualityOfServiceLevel.AtMostOnce
            : qos == 1
                ? MqttQualityOfServiceLevel.AtLeastOnce
                : MqttQualityOfServiceLevel.ExactlyOnce;

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(bytes)
            .WithQualityOfServiceLevel(level)
            .WithRetainFlag(retain)
            .Build();

        await TryPublishMessageAsync(msg, ct).ConfigureAwait(false);
    }

    // ---------------- Topic helpers ----------------

    private string TeleTopic(string leaf, string? networkIdOverride = null, string? nameOverride = null)
        => $"{CurrentOptions.BaseTopic}/{(networkIdOverride ?? CurrentOptions.NetworkId)}/tele/{(nameOverride ?? CurrentOptions.Name)}/{leaf}";

    private string CmdTopic(string target, string leaf)
        => $"{CurrentOptions.BaseTopic}/{CurrentOptions.NetworkId}/cmd/{target}/{leaf}";

    // ---------------- Shell exec helpers ----------------

    /// <summary>
    /// Computes the HMAC-SHA256 token for an exec-shell command.
    /// Key  = ShellExecToken + this gateway's MQTT name (device-specific).
    /// Data = cmd + ts (Unix epoch seconds as decimal string).
    /// </summary>
    private string ComputeShellHmac(string cmd, long ts)
    {
        var key = Encoding.UTF8.GetBytes(ShellExecToken + CurrentOptions.Name);
        var data = Encoding.UTF8.GetBytes(cmd + ts.ToString());
        return Convert.ToHexString(HMACSHA256.HashData(key, data)).ToLowerInvariant();
    }

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
        AppLog.Info("TCP options props: " + string.Join(", ", tcp.GetType().GetProperties().Select(p => p.Name)));
        ApplyTcpEndpoint(tcp, o.Host, o.Port);
        ApplyTlsIfAvailable(options, tcp, o.UseTls, o.Host);

        if (!TrySetProp(options, "ChannelOptions", tcp))
            TrySetProp(options, "TransportOptions", tcp);

        var username = o.Username?.Trim();
        AppLog.Info($"MQTT connect config: host={o.Host}, port={o.Port}, useTls={o.UseTls}, usernameSet={!string.IsNullOrWhiteSpace(username)}");
        if (!string.IsNullOrWhiteSpace(username))
        {
            ApplyCredentialsProvider(options, username!, o.Password ?? "");
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
                System.Net.EndPoint ep = System.Net.IPAddress.TryParse(host, out var parsedIp)
                    ? new System.Net.IPEndPoint(parsedIp, port)
                    : new System.Net.DnsEndPoint(host, port);
                pRemote.SetValue(tcpOptions, ep);
                return;
            }
        }

        if (TrySetProp(tcpOptions, "Server", host))
        {
            TrySetProp(tcpOptions, "Port", port);
            return;
        }

        if (TrySetProp(tcpOptions, "Host", host))
        {
            TrySetProp(tcpOptions, "Port", port);
            return;
        }

        TrySetProp(tcpOptions, "RemoteEndPoint", $"{host}:{port}");
        TrySetProp(tcpOptions, "Endpoint", $"{host}:{port}");
    }

    private static void ApplyTlsIfAvailable(object options, object tcpOptions, bool useTls, string host)
    {
        var tlsConfigured = false;

        // MQTTnet v5 usually exposes TlsOptions on TCP options.
        tlsConfigured |= TryConfigureTlsOptionsObject(tcpOptions, useTls, host);

        // Some versions expose TLS options at the root options level.
        tlsConfigured |= TryConfigureTlsOptionsObject(options, useTls, host);

        if (useTls && !tlsConfigured)
        {
            AppLog.Warn("UseTls=true, but no writable TLS options were found on MQTT options object.");
        }
    }

    private static bool TryConfigureTlsOptionsObject(object container, bool useTls, string host)
    {
        var prop = container.GetType().GetProperty("TlsOptions", BindingFlags.Instance | BindingFlags.Public);
        if (prop == null || !prop.CanWrite) return false;

        var tls = prop.GetValue(container);
        if (tls == null)
        {
            try
            {
                tls = Activator.CreateInstance(prop.PropertyType);
            }
            catch
            {
                return false;
            }
        }

        if (tls == null) return false;

        var changed = false;
        changed |= TrySetProp(tls, "UseTls", useTls);
        if (useTls)
        {
            changed |= TrySetProp(tls, "TargetHost", host);
        }

        prop.SetValue(container, tls);
        return changed;
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

}
