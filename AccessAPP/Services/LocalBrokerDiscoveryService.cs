using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAPP.Services;

/// <summary>
/// Listens on UDP port <see cref="BeaconPort"/> for "cassia-mqtt" discovery beacons
/// broadcast by an AccessAppMqttWpf client that is running a local MQTT server.
///
/// For each discovered broker a secondary <see cref="IMqttClient"/> is maintained:
/// <list type="bullet">
///   <item>All telemetry published by <see cref="MqttService"/> is mirrored to local brokers.</item>
///   <item>Commands received from local brokers are forwarded to <see cref="MqttService"/> via
///         <see cref="MqttService.FeedCommandAsync"/>.</item>
/// </list>
/// Brokers that stop sending beacons are removed after <see cref="BeaconTimeoutMs"/> ms.
/// </summary>
public sealed class LocalBrokerDiscoveryService : IDisposable
{
    public const int BeaconPort = 18884;
    private const int BeaconTimeoutMs = 15_000; // removed after 5× beacon interval silence

    /// <summary>Shared secret — must match LocalMqttServerService.LocalToken in the WPF app.</summary>
    private const string LocalToken = "cassia-local-3a7f2b9e1c4d8f06";

    private readonly MqttService _mqtt;
    private readonly ConcurrentDictionary<string, LocalBrokerEntry> _brokers =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Task? _cleanupTask;

    public LocalBrokerDiscoveryService(IMqttService mqtt)
    {
        // Concrete type needed for the publish-mirror hook and FeedCommandAsync.
        _mqtt = (MqttService)mqtt;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _mqtt.MessagePublished += OnMessagePublished;
        _listenTask  = Task.Run(() => ListenLoopAsync(_cts.Token));
        _cleanupTask = Task.Run(() => CleanupLoopAsync(_cts.Token));
        AppLog.Info("[LocalBrokerDiscovery] Started listening on UDP port " + BeaconPort);
    }

    public void Stop()
    {
        _mqtt.MessagePublished -= OnMessagePublished;
        _cts?.Cancel();

        foreach (var entry in _brokers.Values)
            entry.Dispose();
        _brokers.Clear();

        _cts?.Dispose();
        _cts = null;
        AppLog.Info("[LocalBrokerDiscovery] Stopped.");
    }

    public void Dispose() => Stop();

    // ── Telemetry mirror ─────────────────────────────────────────────────────

    private void OnMessagePublished(string topic, byte[] payload)
    {
        foreach (var entry in _brokers.Values)
            _ = entry.PublishAsync(topic, payload);
    }

    // ── UDP listen loop ───────────────────────────────────────────────────────

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));
        }
        catch (Exception ex)
        {
            AppLog.Error($"[LocalBrokerDiscovery] Cannot bind UDP port {BeaconPort}: {ex.Message}");
            udp?.Dispose();
            return;
        }

        using (udp)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                    ProcessBeacon(result.Buffer, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    AppLog.Warn($"[LocalBrokerDiscovery] UDP receive error: {ex.Message}");
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private void ProcessBeacon(byte[] data, CancellationToken ct)
    {
        try
        {
            var json = Encoding.UTF8.GetString(data);
            var beacon = JsonSerializer.Deserialize<BeaconPacket>(json, _jsonOptions);
            if (beacon?.Service != "cassia-mqtt" || string.IsNullOrWhiteSpace(beacon.Host))
                return;

            var key = $"{beacon.Host}:{beacon.Port}";
            var entry = _brokers.GetOrAdd(key,
                _ => new LocalBrokerEntry(beacon.Host!, beacon.Port, _mqtt));

            entry.UpdateLastSeen();
            _ = entry.EnsureConnectedAsync(ct);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[LocalBrokerDiscovery] Beacon parse error: {ex.Message}");
        }
    }

    // ── Cleanup loop ──────────────────────────────────────────────────────────

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(5000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            var now = DateTimeOffset.UtcNow;
            foreach (var kv in _brokers)
            {
                if ((now - kv.Value.LastSeenUtc).TotalMilliseconds > BeaconTimeoutMs)
                {
                    if (_brokers.TryRemove(kv.Key, out var removed))
                    {
                        AppLog.Info($"[LocalBrokerDiscovery] Broker {kv.Key} beacon timeout — removing.");
                        removed.Dispose();
                    }
                }
            }
        }
    }

    // ── DTOs ──────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private sealed class BeaconPacket
    {
        public string? Service   { get; set; }
        public string? Host      { get; set; }
        public int     Port      { get; set; } = 1883;
        public string? NetworkId { get; set; }
    }

    // ── Per-broker secondary connection ──────────────────────────────────────

    private sealed class LocalBrokerEntry : IDisposable
    {
        private readonly string      _host;
        private readonly int         _port;
        private readonly MqttService _mqtt;
        private IMqttClient?         _client;
        private readonly SemaphoreSlim _connectGate = new(1, 1);

        public DateTimeOffset LastSeenUtc { get; private set; } = DateTimeOffset.UtcNow;
        public void UpdateLastSeen() => LastSeenUtc = DateTimeOffset.UtcNow;

        public LocalBrokerEntry(string host, int port, MqttService mqtt)
        {
            _host  = host;
            _port  = port;
            _mqtt  = mqtt;
        }

        public async Task EnsureConnectedAsync(CancellationToken ct)
        {
            if (_client?.IsConnected == true) return;
            if (!await _connectGate.WaitAsync(0).ConfigureAwait(false)) return;

            try
            {
                if (_client?.IsConnected == true) return;

                _client?.Dispose();
                _client = new MqttClientFactory().CreateMqttClient();

                _client.DisconnectedAsync += _ =>
                {
                    AppLog.Info($"[LocalBrokerDiscovery] Disconnected from {_host}:{_port}");
                    return Task.CompletedTask;
                };

                _client.ApplicationMessageReceivedAsync += e =>
                {
                    var topic   = e.ApplicationMessage.Topic;
                    var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());
                    _ = _mqtt.FeedCommandAsync(topic, payload);
                    return Task.CompletedTask;
                };

                var opts = _mqtt.CurrentOptions;
                var clientId = $"mirror-{opts.Name}-{Environment.MachineName}";

                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(_host, _port)
                    .WithClientId(clientId)
                    .WithCredentials("local", LocalToken)
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                    .WithCleanSession(true)
                    .Build();

                await _client.ConnectAsync(options, ct).ConfigureAwait(false);
                AppLog.Info($"[LocalBrokerDiscovery] Connected to local broker {_host}:{_port}");

                // Subscribe to commands directed at this AccessApp instance
                var topicMine = $"{opts.BaseTopic}/{opts.NetworkId}/cmd/{opts.Name}/#";
                var topicAll  = $"{opts.BaseTopic}/{opts.NetworkId}/cmd/all/#";

                await _client.SubscribeAsync(new MqttTopicFilter
                {
                    Topic = topicMine,
                    QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce
                }, ct).ConfigureAwait(false);

                await _client.SubscribeAsync(new MqttTopicFilter
                {
                    Topic = topicAll,
                    QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[LocalBrokerDiscovery] Connect to {_host}:{_port} failed: {ex.Message}");
                _client?.Dispose();
                _client = null;
            }
            finally
            {
                _connectGate.Release();
            }
        }

        /// <summary>Best-effort mirror publish; never throws.</summary>
        public async Task PublishAsync(string topic, byte[] payload)
        {
            try
            {
                var client = _client;
                if (client?.IsConnected != true) return;

                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                    .Build();

                await client.PublishAsync(msg, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* best-effort */ }
        }

        public void Dispose()
        {
            try { _client?.Dispose(); } catch { }
            _connectGate.Dispose();
        }
    }
}
