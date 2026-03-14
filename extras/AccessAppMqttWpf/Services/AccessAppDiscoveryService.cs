using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAppMqttWpf.Services;

/// <summary>
/// Listens on UDP port <see cref="BeaconPort"/> for "cassia-accessapp" beacons broadcast by
/// AccessApp instances on the LAN.  When a new instance is discovered, pushes the local MQTT
/// broker settings to it via HTTP POST to its <c>/api/local-mqtt</c> endpoint so it can connect
/// without relying on UDP beacon discovery in the other direction.
/// </summary>
public sealed class AccessAppDiscoveryService : IDisposable
{
    public const int BeaconPort = 60004;
    private const int PushIntervalMs    = 30_000; // re-push config every 30 s
    private const int BeaconTimeoutMs   = 20_000; // remove after 4× beacon-interval silence

    private readonly ConcurrentDictionary<string, DiscoveredEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Task? _cleanupTask;

    private int    _mqttPort;
    private string _networkId = "";

    public bool IsRunning => _listenTask != null && !_listenTask.IsCompleted;

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public void Start(int mqttPort, string networkId)
    {
        Stop();
        _mqttPort  = mqttPort;
        _networkId = networkId;

        _cts         = new CancellationTokenSource();
        _listenTask  = Task.Run(() => ListenLoopAsync(_cts.Token));
        _cleanupTask = Task.Run(() => CleanupLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listenTask?.Wait(1000); }  catch { }
        try { _cleanupTask?.Wait(1000); } catch { }
        _cts?.Dispose();
        _cts         = null;
        _listenTask  = null;
        _cleanupTask = null;
        _entries.Clear();
    }

    public void Dispose() => Stop();

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
            System.Diagnostics.Debug.WriteLine($"[AccessAppDiscovery] Cannot bind UDP port {BeaconPort}: {ex.Message}");
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
                catch { }
            }
        }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    private void ProcessBeacon(byte[] data, CancellationToken ct)
    {
        try
        {
            var json   = Encoding.UTF8.GetString(data);
            var beacon = JsonSerializer.Deserialize<AccessAppBeacon>(json, _jsonOpts);
            if (beacon?.Service != "cassia-accessapp" || string.IsNullOrWhiteSpace(beacon.Host))
                return;

            var key   = $"{beacon.Host}:{beacon.HttpPort}";
            var entry = _entries.GetOrAdd(key, _ => new DiscoveredEntry(beacon.Host!, beacon.HttpPort));

            var wasStale = (DateTimeOffset.UtcNow - entry.LastSeenUtc).TotalMilliseconds > PushIntervalMs;
            entry.UpdateLastSeen();

            // Push config on first discovery or after push interval
            if (wasStale || !entry.ConfigPushed)
                _ = PushConfigAsync(entry, ct);
        }
        catch { }
    }

    // ── Config push ───────────────────────────────────────────────────────────

    private async Task PushConfigAsync(DiscoveredEntry entry, CancellationToken ct)
    {
        if (!await entry.PushGate.WaitAsync(0).ConfigureAwait(false))
            return; // another push is already in progress

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = $"http://{entry.Host}:{entry.HttpPort}/api/local-mqtt";

            // The AccessApp controller reads the caller's IP from the TCP connection, so we
            // only need to send the token and port — no need to figure out our own IP.
            var payload = new
            {
                token    = LocalMqttServerService.LocalToken,
                mqttPort = _mqttPort
            };

            var body = new System.Net.Http.StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            var response = await http.PostAsync(url, body, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                entry.ConfigPushed = true;
                System.Diagnostics.Debug.WriteLine(
                    $"[AccessAppDiscovery] Pushed MQTT config to {entry.Host}:{entry.HttpPort} (port {_mqttPort})");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AccessAppDiscovery] Push to {entry.Host}:{entry.HttpPort} returned {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AccessAppDiscovery] Push to {entry.Host}:{entry.HttpPort} failed: {ex.Message}");
        }
        finally
        {
            entry.PushGate.Release();
        }
    }

    // ── Cleanup loop ──────────────────────────────────────────────────────────

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(10_000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            var now = DateTimeOffset.UtcNow;
            foreach (var kv in _entries)
            {
                if ((now - kv.Value.LastSeenUtc).TotalMilliseconds > BeaconTimeoutMs)
                    if (_entries.TryRemove(kv.Key, out var removed))
                        removed.PushGate.Dispose();
            }
        }
    }

    // ── DTOs / helpers ────────────────────────────────────────────────────────

    private sealed class AccessAppBeacon
    {
        public string? Service   { get; set; }
        public string? Host      { get; set; }
        public int     HttpPort  { get; set; } = 60000;
        public string? Name      { get; set; }
        public string? NetworkId { get; set; }
    }

    private sealed class DiscoveredEntry
    {
        public string           Host       { get; }
        public int              HttpPort   { get; }
        public DateTimeOffset   LastSeenUtc { get; private set; } = DateTimeOffset.UtcNow;
        public bool             ConfigPushed { get; set; }
        public SemaphoreSlim    PushGate   { get; } = new(1, 1);

        public DiscoveredEntry(string host, int httpPort) { Host = host; HttpPort = httpPort; }
        public void UpdateLastSeen() => LastSeenUtc = DateTimeOffset.UtcNow;
    }
}
