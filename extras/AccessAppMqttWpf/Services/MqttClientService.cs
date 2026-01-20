using MQTTnet;
using MQTTnet.Client;
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
    private static bool IsLeaf(string topic, string leaf)
    {
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(leaf)) return false;
        var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        return string.Equals(parts[^1], leaf, StringComparison.OrdinalIgnoreCase);
    }


    private readonly IMqttClient _client;

    // --- High-frequency topic coalescing (UI throttling) ---
    // We receive up to a few thousand messages/min. The UI does not need per-message updates for these topics.
    // We keep only the latest payload per topic and emit at a fixed cadence.
    private readonly ConcurrentDictionary<string, string> _latestProgressByTopicMac = new(StringComparer.OrdinalIgnoreCase); // key = topic|mac
    private readonly ConcurrentDictionary<string, string> _latestDiscoveredDeviceByTopicMac = new(StringComparer.OrdinalIgnoreCase); // key = topic|mac, value = device json
    private readonly ConcurrentDictionary<string, DateTimeOffset> _latestDiscoveredTimeByTopic = new(StringComparer.OrdinalIgnoreCase);

    private readonly Timer _progressFlushTimer;
    private readonly Timer _discoveredFlushTimer;

    private readonly MqttFactory _factory = new();
    private CancellationTokenSource? _cts;

    public bool IsConnected => _client?.IsConnected == true;

    public event Action<string, string>? Message;              // topic, payload
    public event Action<bool, string>? ConnectionChanged;      // isConnected, status text

    public MqttClientService()
    {
        _client = _factory.CreateMqttClient();

        // Flush coalesced high-frequency topics on a fixed cadence.
        _progressFlushTimer = new Timer(_ => FlushProgress(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        _discoveredFlushTimer = new Timer(_ => FlushDiscovered(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));


        _client.ConnectedAsync += _ =>
        {
            ConnectionChanged?.Invoke(true, "Connected");
            return Task.CompletedTask;
        };

        _client.DisconnectedAsync += e =>
        {
            ConnectionChanged?.Invoke(false, $"Disconnected: {e.Reason} {e.ReasonString}".Trim());
            return Task.CompletedTask;
        };

        _client.ApplicationMessageReceivedAsync += e =>
        {
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
                        _latestProgressByTopicMac[$"{topic}|{mac}"] = payload;
                    else
                        Message?.Invoke(topic, payload); // unknown shape, pass through
                }
                else if (IsLeaf(topic, "discovered"))
                {
                    TryBufferDiscoveredPerMac(topic, payload);
                }
                else
                {
                    Message?.Invoke(topic, payload);
                }
            }
            catch { /* ignore */ }

            return Task.CompletedTask;
        };
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

        var builder = new MqttClientOptionsBuilder()
            .WithClientId($"accessapp-wpf-{Environment.MachineName}-{Guid.NewGuid():N}".Substring(0, 32))
            .WithTcpServer(host, port)
            .WithCleanSession();

        if (!string.IsNullOrWhiteSpace(user))
            builder = builder.WithCredentials(user, pass);

        if (useTls)
            TryConfigureTls(builder, ignoreTlsErrors);

        var options = builder.Build();

        ConnectionChanged?.Invoke(false, "Connecting…");
        await _client.ConnectAsync(options, _cts.Token).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(subscribeTopic))
            await _client.SubscribeAsync(subscribeTopic).ConfigureAwait(false);
    }

    /// <summary>
    /// Subscribe after connecting (or to add additional topic filters).
    /// No-op if not connected.
    /// </summary>
    public async Task SubscribeAsync(string topicFilter)
    {
        if (!IsConnected) return;
        if (string.IsNullOrWhiteSpace(topicFilter)) return;
        await _client.SubscribeAsync(topicFilter).ConfigureAwait(false);
    }

    /// <summary>
    /// Enables TLS and optionally ignores certificate validation.
    /// Uses reflection/expressions to avoid binding to MQTTnet's TLS builder types at compile time.
    /// </summary>
    private static void TryConfigureTls(MqttClientOptionsBuilder builder, bool ignoreTlsErrors)
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

                var del = BuildTlsConfiguratorDelegate(actionType, tlsBuilderType, ignoreTlsErrors);
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

    private static object BuildTlsConfiguratorDelegate(Type actionType, Type tlsBuilderType, bool ignoreTlsErrors)
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

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        if (_client.IsConnected)
            await _client.DisconnectAsync().ConfigureAwait(false);
    }

    public async Task PublishJsonAsync(string topic, object payload, bool retain = false, int qos = 1, CancellationToken ct = default)
    {
        if (!_client.IsConnected) return;

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(json)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel((MQTTnet.Protocol.MqttQualityOfServiceLevel)qos)
            .Build();

        await _client.PublishAsync(msg, ct).ConfigureAwait(false);
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
        catch { }
        return "";
    }

    private void TryBufferDiscoveredPerMac(string topic, string payload)
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
            _latestDiscoveredTimeByTopic[topic] = ts;

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

                _latestDiscoveredDeviceByTopicMac[$"{topic}|{mac}"] = dev.GetRawText();
            }
        }
        catch
        {
            // If payload isn't JSON, pass through (better to show something than drop it)
            Message?.Invoke(topic, payload);
        }
    }

    private void FlushProgress()
    {
        try
        {
            if (_latestProgressByTopicMac.IsEmpty) return;

            // Snapshot & clear so we don't block the receive thread.
            var items = _latestProgressByTopicMac.ToArray();
            _latestProgressByTopicMac.Clear();

            foreach (var kv in items)
            {
                var key = kv.Key;
                var payload = kv.Value;

                var sep = key.IndexOf('|');
                var topic = sep > 0 ? key.Substring(0, sep) : key;

                Message?.Invoke(topic, payload);
            }
        }
        catch { }
    }

    private void FlushDiscovered()
    {
        try
        {
            if (_latestDiscoveredDeviceByTopicMac.IsEmpty) return;

            var items = _latestDiscoveredDeviceByTopicMac.ToArray();
            _latestDiscoveredDeviceByTopicMac.Clear();

            // group by topic
            var groups = items
                .Select(kv =>
                {
                    var key = kv.Key;
                    var sep = key.IndexOf('|');
                    var topic = sep > 0 ? key.Substring(0, sep) : key;
                    return (topic, devJson: kv.Value);
                })
                .GroupBy(x => x.topic, StringComparer.OrdinalIgnoreCase);

            foreach (var g in groups)
            {
                var topic = g.Key;

                // build JSON in the SAME shape MainViewModel already expects:
                // { time: "...", devices: [ ... ] }
                var ts = _latestDiscoveredTimeByTopic.TryGetValue(topic, out var dto) ? dto : DateTimeOffset.UtcNow;

                var devicesJson = string.Join(",", g.Select(x => x.devJson));
                var outPayload = $"{{\"time\":\"{ts:O}\",\"devices\":[{devicesJson}]}}";

                Message?.Invoke(topic, outPayload);
            }
        }
        catch { }
    }

public async Task PublishAsync(string topic, string payload, bool retain = false)
    {
        if (_client == null || !_client.IsConnected) return;

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        await _client.PublishAsync(msg);
    }
    public void Dispose()
    {
        try { _progressFlushTimer?.Dispose(); } catch { }
        try { _discoveredFlushTimer?.Dispose(); } catch { }

        _cts?.Cancel();
        _cts?.Dispose();
        _client?.Dispose();
    }
}