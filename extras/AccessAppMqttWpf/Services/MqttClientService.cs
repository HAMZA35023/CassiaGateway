using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAppMqttWpf.Services;

public sealed class MqttClientService : IDisposable
{
    private readonly record struct BufferedPayload(long SessionId, string Payload);
    private readonly record struct BufferedTimestamp(long SessionId, DateTimeOffset Timestamp);

    private static bool IsLeaf(string topic, string leaf)
    {
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(leaf)) return false;
        var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        return string.Equals(parts[^1], leaf, StringComparison.OrdinalIgnoreCase);
    }

    private IMqttClient? _client;

    // --- High-frequency topic coalescing (UI throttling) ---
    // We receive up to a few thousand messages/min. The UI does not need per-message updates for these topics.
    // We keep only the latest payload per topic and emit at a fixed cadence.
    private readonly ConcurrentDictionary<string, BufferedPayload> _latestProgressByTopicMac = new(StringComparer.OrdinalIgnoreCase); // key = topic|mac
    private readonly ConcurrentDictionary<string, BufferedPayload> _latestDiscoveredDeviceByTopicMac = new(StringComparer.OrdinalIgnoreCase); // key = topic|mac, value = device json
    private readonly ConcurrentDictionary<string, BufferedTimestamp> _latestDiscoveredTimeByTopic = new(StringComparer.OrdinalIgnoreCase);

    // Pending flags for event-driven flush.
    // Progress: truly immediate — flushed as soon as a message arrives (cheap, queue-only refresh).
    // Discovered: minimum 500ms batch window — collects all cassias before flushing (expensive,
    //   triggers Dispatcher.Invoke + FilteredDevices.Refresh on the full device list).
    private volatile bool _progressPending;
    private volatile bool _discoveredPending;
    private long _lastDiscoveredFlushMs;
    private const int DiscoveredMinIntervalMs = 500;

    private readonly MqttFactory _factory = new();
    private CancellationTokenSource? _cts;
    private long _sessionCounter;
    private long _activeSessionId;
    private string _activeBrokerLabel = "none";

    public bool IsConnected => _client?.IsConnected == true;

    public event Action<string, string>? Message;              // topic, payload
    public event Action<bool, string>? ConnectionChanged;      // isConnected, status text

    private long ActiveSessionId => Interlocked.Read(ref _activeSessionId);

    private static string BuildBrokerLabel(string host, int port, bool useTls)
        => $"{(useTls ? "mqtts" : "mqtt")}://{host}:{port}";

    private bool IsCurrentSession(long sessionId)
        => sessionId == ActiveSessionId;

    private void ClearBufferedTelemetry()
    {
        _latestProgressByTopicMac.Clear();
        _latestDiscoveredDeviceByTopicMac.Clear();
        _latestDiscoveredTimeByTopic.Clear();
    }

    private void SetActiveSession(long sessionId, string brokerLabel)
    {
        Interlocked.Exchange(ref _activeSessionId, sessionId);
        _activeBrokerLabel = brokerLabel;
    }

    private void InvalidateCurrentSession(string brokerLabel = "none")
    {
        var invalidatedSessionId = Interlocked.Increment(ref _sessionCounter);
        SetActiveSession(invalidatedSessionId, brokerLabel);
    }

    private void AttachClientHandlers(IMqttClient client, long sessionId, string brokerLabel)
    {
        client.ConnectedAsync += _ =>
        {
            if (!IsCurrentSession(sessionId)) return Task.CompletedTask;

            ConnectionChanged?.Invoke(true, $"Connected [{sessionId}] {brokerLabel}");
            return Task.CompletedTask;
        };

        client.DisconnectedAsync += e =>
        {
            if (!IsCurrentSession(sessionId)) return Task.CompletedTask;

            ClearBufferedTelemetry();
            ConnectionChanged?.Invoke(false, $"Disconnected [{sessionId}] {brokerLabel}: {e.Reason} {e.ReasonString}".Trim());
            return Task.CompletedTask;
        };

        client.ApplicationMessageReceivedAsync += e =>
        {
            if (!IsCurrentSession(sessionId)) return Task.CompletedTask;

            try
            {
                var payload = e.ApplicationMessage.PayloadSegment.Array == null
                    ? string.Empty
                    : Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

                var topic = e.ApplicationMessage.Topic ?? string.Empty;

                // Coalesce chatty topics (progress/discovered) so the UI doesn't get hammered.
                // IMPORTANT: we still parse EVERY message. We coalesce PER MAC (not per topic), so we don't drop updates
                // for different devices. For the same MAC, only the latest state in the interval is rendered.
                if (IsLeaf(topic, "progress"))
                {
                    var mac = TryExtractMacFromProgress(payload);
                    if (!string.IsNullOrWhiteSpace(mac))
                    {
                        _latestProgressByTopicMac[$"{topic}|{mac}"] = new BufferedPayload(sessionId, payload);
                        ScheduleProgressFlush();
                    }
                    else
                        Message?.Invoke(topic, payload); // unknown shape, pass through
                }
                else if (IsLeaf(topic, "discovered"))
                {
                    TryBufferDiscoveredPerMac(sessionId, topic, payload);
                    ScheduleDiscoveredFlush();
                }
                else
                {
                    Message?.Invoke(topic, payload);
                }
            }
            catch
            {
                // ignore
            }

            return Task.CompletedTask;
        };
    }

    public MqttClientService()
    {
        InvalidateCurrentSession();
    }

    public async Task ConnectAsync(
        string host,
        int port,
        string user,
        string pass,
        bool useTls,
        bool ignoreTlsErrors,
        string subscribeTopic,
        CancellationToken ct)
    {
        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ClearBufferedTelemetry();

        var brokerLabel = BuildBrokerLabel(host, port, useTls);
        var sessionId = Interlocked.Increment(ref _sessionCounter);
        SetActiveSession(sessionId, brokerLabel);

        var oldClient = _client;
        var client = _factory.CreateMqttClient();
        AttachClientHandlers(client, sessionId, brokerLabel);
        _client = client;

        try { oldClient?.Dispose(); } catch { }

        var builder = new MqttClientOptionsBuilder()
            .WithClientId($"accessapp-wpf-{Environment.MachineName}-{Guid.NewGuid():N}".Substring(0, 32))
            .WithTcpServer(host, port)
            .WithCleanSession();

        if (!string.IsNullOrWhiteSpace(user))
            builder = builder.WithCredentials(user, pass);

        if (useTls)
            TryConfigureTls(builder, host, ignoreTlsErrors);

        var options = builder.Build();

        ConnectionChanged?.Invoke(false, $"Connecting [{sessionId}] {brokerLabel}...");
        await client.ConnectAsync(options, _cts.Token).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(subscribeTopic))
            await client.SubscribeAsync(subscribeTopic).ConfigureAwait(false);
    }

    /// <summary>
    /// Subscribe after connecting (or to add additional topic filters).
    /// No-op if not connected.
    /// </summary>
    public async Task SubscribeAsync(string topicFilter, int qos = 0)
    {
        var client = _client;
        if (client?.IsConnected != true) return;
        if (string.IsNullOrWhiteSpace(topicFilter)) return;

        var level = qos <= 0
            ? MqttQualityOfServiceLevel.AtMostOnce
            : qos == 1
                ? MqttQualityOfServiceLevel.AtLeastOnce
                : MqttQualityOfServiceLevel.ExactlyOnce;

        var filter = new MqttTopicFilterBuilder()
            .WithTopic(topicFilter)
            .WithQualityOfServiceLevel(level)
            .Build();

        await client.SubscribeAsync(filter).ConfigureAwait(false);
    }

    /// <summary>
    /// Enables TLS and optionally ignores certificate validation.
    /// Uses reflection/expressions to avoid binding to MQTTnet's TLS builder types at compile time.
    /// </summary>
    private static void TryConfigureTls(MqttClientOptionsBuilder builder, string host, bool ignoreTlsErrors)
    {
        try
        {
            var bt = builder.GetType();

            // Look for: WithTlsOptions(Action<TlsOptionsBuilder>)
            var withTlsOptions = bt.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    m.Name == "WithTlsOptions" &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType.IsGenericType &&
                    m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Action<>));

            if (withTlsOptions != null)
            {
                var actionType = withTlsOptions.GetParameters()[0].ParameterType;
                var tlsBuilderType = actionType.GetGenericArguments()[0];

                var del = BuildTlsConfiguratorDelegate(actionType, tlsBuilderType, host, ignoreTlsErrors);
                withTlsOptions.Invoke(builder, new object[] { del });
                return;
            }

            // Some builds expose WithTls() (parameterless)
            var withTls = bt.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "WithTls" && m.GetParameters().Length == 0);

            withTls?.Invoke(builder, Array.Empty<object>());
        }
        catch
        {
            // best-effort; if TLS API differs, app still runs (but TLS won't be enabled)
        }
    }

    private static object BuildTlsConfiguratorDelegate(Type actionType, Type tlsBuilderType, string host, bool ignoreTlsErrors)
    {
        // Build: (TlsBuilder x) => { x.UseTls = true / x.UseTls(true); set validation handler => true; set ignore flags; }
        var p = Expression.Parameter(tlsBuilderType, "x");
        var block = new System.Collections.Generic.List<Expression>();

        // UseTls property or method
        var useTlsProp = tlsBuilderType.GetProperty("UseTls", BindingFlags.Public | BindingFlags.Instance);
        if (useTlsProp != null && useTlsProp.CanWrite && useTlsProp.PropertyType == typeof(bool))
        {
            block.Add(Expression.Assign(Expression.Property(p, useTlsProp), Expression.Constant(true)));
        }
        else
        {
            var useTlsMethod = tlsBuilderType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "UseTls" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(bool));
            if (useTlsMethod != null)
                block.Add(Expression.Call(p, useTlsMethod, Expression.Constant(true)));
        }

        // SNI/certificate hostname target (if available in this MQTTnet build).
        AddStringAssignIfExists(tlsBuilderType, p, block, "TargetHost", host);
        AddStringAssignIfExists(tlsBuilderType, p, block, "SslHost", host);

        // CertificateValidationHandler: Func<Ctx,bool> that returns true
        var certValProp = tlsBuilderType.GetProperty("CertificateValidationHandler", BindingFlags.Public | BindingFlags.Instance);
        if (certValProp != null && certValProp.CanWrite)
        {
            var t = certValProp.PropertyType;
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Func<,>))
            {
                var args = t.GetGenericArguments();
                if (args.Length == 2 && args[1] == typeof(bool))
                {
                    var ctxParam = Expression.Parameter(args[0], "ctx");
                    var alwaysTrue = Expression.Lambda(t, Expression.Constant(true), ctxParam);
                    block.Add(Expression.Assign(Expression.Property(p, certValProp), alwaysTrue));
                }
            }
        }

        // Optional bool flags (only if they exist in your MQTTnet build)
        if (ignoreTlsErrors)
        {
            AddBoolAssignIfExists(tlsBuilderType, p, block, "AllowUntrustedCertificates", true);
            AddBoolAssignIfExists(tlsBuilderType, p, block, "IgnoreCertificateChainErrors", true);
            AddBoolAssignIfExists(tlsBuilderType, p, block, "IgnoreCertificateRevocationErrors", true);
        }

        Expression body = block.Count == 0 ? Expression.Empty() : Expression.Block(block);
        var lambda = Expression.Lambda(actionType, body, p);
        return lambda.Compile();
    }

    private static void AddBoolAssignIfExists(Type tlsBuilderType, ParameterExpression p, System.Collections.Generic.List<Expression> block, string propName, bool value)
    {
        var prop = tlsBuilderType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
        {
            block.Add(Expression.Assign(Expression.Property(p, prop), Expression.Constant(value)));
        }
    }

    private static void AddStringAssignIfExists(Type tlsBuilderType, ParameterExpression p, System.Collections.Generic.List<Expression> block, string propName, string value)
    {
        var prop = tlsBuilderType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string))
        {
            block.Add(Expression.Assign(Expression.Property(p, prop), Expression.Constant(value)));
        }
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        ClearBufferedTelemetry();

        var client = _client;
        var previousBrokerLabel = _activeBrokerLabel;
        InvalidateCurrentSession(previousBrokerLabel);
        _client = null;

        ConnectionChanged?.Invoke(false, $"Disconnected {previousBrokerLabel}");

        if (client?.IsConnected == true)
            await client.DisconnectAsync().ConfigureAwait(false);

        try { client?.Dispose(); } catch { }
    }

    public async Task PublishJsonAsync(string topic, object payload, bool retain = false, int qos = 1, CancellationToken ct = default)
    {
        var client = _client;
        if (client?.IsConnected != true) return;

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(json)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)
            .Build();

        await client.PublishAsync(msg, ct).ConfigureAwait(false);
    }

    private static string NormalizeMac(string mac)
        => (mac ?? "").Trim();

    private static string TryExtractMacFromProgress(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return "";
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "";
            if (root.TryGetProperty("mac", out var macEl) && macEl.ValueKind == JsonValueKind.String)
                return NormalizeMac(macEl.GetString() ?? "");
            if (root.TryGetProperty("MacAddress", out var macEl2) && macEl2.ValueKind == JsonValueKind.String)
                return NormalizeMac(macEl2.GetString() ?? "");
        }
        catch
        {
            // ignore
        }
        return "";
    }

    private void TryBufferDiscoveredPerMac(long sessionId, string topic, string payload)
    {
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(payload)) return;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            // keep last timestamp per topic (if present)
            var ts = DateTimeOffset.UtcNow;
            if (root.TryGetProperty("time", out var tEl))
            {
                if (tEl.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(tEl.GetString(), out var dto))
                    ts = dto;
                else if (tEl.TryGetDateTimeOffset(out var dto2))
                    ts = dto2;
            }
            _latestDiscoveredTimeByTopic[topic] = new BufferedTimestamp(sessionId, ts);

            if (!root.TryGetProperty("devices", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return;

            foreach (var dev in arr.EnumerateArray())
            {
                if (dev.ValueKind != JsonValueKind.Object) continue;

                var mac =
                    (dev.TryGetProperty("mac", out var macEl) && macEl.ValueKind == JsonValueKind.String) ? (macEl.GetString() ?? "") :
                    (dev.TryGetProperty("MacAddress", out var macEl2) && macEl2.ValueKind == JsonValueKind.String) ? (macEl2.GetString() ?? "") :
                    "";

                mac = NormalizeMac(mac);
                if (string.IsNullOrWhiteSpace(mac)) continue;

                _latestDiscoveredDeviceByTopicMac[$"{topic}|{mac}"] = new BufferedPayload(sessionId, dev.GetRawText());
            }
        }
        catch
        {
            // If payload isn't JSON, pass through (better to show something than drop it)
            Message?.Invoke(topic, payload);
        }
    }

    private void ScheduleProgressFlush()
    {
        if (_progressPending) return;
        _progressPending = true;
        Task.Run(() => { _progressPending = false; FlushProgress(); });
    }

    private void ScheduleDiscoveredFlush()
    {
        if (_discoveredPending) return;
        _discoveredPending = true;
        var sinceLastMs = (int)Math.Min(DiscoveredMinIntervalMs, Environment.TickCount64 - Interlocked.Read(ref _lastDiscoveredFlushMs));
        var delayMs = Math.Max(0, DiscoveredMinIntervalMs - sinceLastMs);
        Task.Run(async () =>
        {
            if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);
            _discoveredPending = false;
            Interlocked.Exchange(ref _lastDiscoveredFlushMs, Environment.TickCount64);
            FlushDiscovered();
        });
    }

    private void FlushProgress()
    {
        try
        {
            if (_latestProgressByTopicMac.IsEmpty) return;
            var activeSessionId = ActiveSessionId;

            // Snapshot & clear so we don't block the receive thread.
            var items = _latestProgressByTopicMac.ToArray();
            _latestProgressByTopicMac.Clear();

            foreach (var kv in items)
            {
                var key = kv.Key;
                var entry = kv.Value;
                if (entry.SessionId != activeSessionId) continue;

                var sep = key.IndexOf('|');
                var topic = sep > 0 ? key.Substring(0, sep) : key;

                Message?.Invoke(topic, entry.Payload);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void FlushDiscovered()
    {
        try
        {
            if (_latestDiscoveredDeviceByTopicMac.IsEmpty) return;
            var activeSessionId = ActiveSessionId;

            var items = _latestDiscoveredDeviceByTopicMac.ToArray();
            _latestDiscoveredDeviceByTopicMac.Clear();

            // group by topic
            var groups = items
                .Select(kv =>
                {
                    var key = kv.Key;
                    var sep = key.IndexOf('|');
                    var topic = sep > 0 ? key.Substring(0, sep) : key;
                    return (topic, entry: kv.Value);
                })
                .Where(x => x.entry.SessionId == activeSessionId)
                .GroupBy(x => x.topic, StringComparer.OrdinalIgnoreCase);

            foreach (var g in groups)
            {
                var topic = g.Key;

                // build JSON in the SAME shape MainViewModel already expects:
                // { time: "...", devices: [ ... ] }
                var ts = _latestDiscoveredTimeByTopic.TryGetValue(topic, out var dto) && dto.SessionId == activeSessionId
                    ? dto.Timestamp
                    : DateTimeOffset.UtcNow;

                var devicesJson = string.Join(",", g.Select(x => x.entry.Payload));
                var outPayload = $"{{\"time\":\"{ts:O}\",\"devices\":[{devicesJson}]}}";

                Message?.Invoke(topic, outPayload);
            }
        }
        catch
        {
            // ignore
        }
    }

    public async Task PublishAsync(string topic, string payload, bool retain = false)
    {
        var client = _client;
        if (client?.IsConnected != true) return;

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        await client.PublishAsync(msg).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        try { _client?.Dispose(); } catch { }
    }
}
