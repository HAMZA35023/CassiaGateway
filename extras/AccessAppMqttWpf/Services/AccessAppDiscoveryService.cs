using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAppMqttWpf.Services;

/// <summary>
/// Discovers AccessApp instances on the local network via TCP/HTTP on port 60000 and pushes
/// the local MQTT broker configuration to them.
///
/// Discovery flow:
///   1. <see cref="StartFastScan"/> fires parallel HTTP GET probes to all hosts in the local
///      subnet(s).  Any host whose <c>GET /api/local-mqtt</c> response contains
///      <c>"cassia-accessapp"</c> is immediately sent a config push via POST.
///      The whole scan completes in ~300 ms regardless of subnet size.
///
///   2. A periodic re-push loop sends the config to all known hosts every 30 s.
///      This keeps AccessApp's in-memory <c>LOCAL_MQTT_HOST</c> alive and doubles as
///      a health check.  Three consecutive failures fire <see cref="GatewayLost"/>.
///
///   3. <see cref="StartSlowScan"/> (called when a gateway goes offline) probes subnet
///      candidates one per cycle (3 s) for up to 2 minutes as a reconnect fallback.
/// </summary>
public sealed class AccessAppDiscoveryService : IDisposable
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const int HttpPort           = 60000;
    private const int ScanTimeoutMs      = 400;    // per-host timeout during fast/slow scan probes
    private const int PushTimeoutMs      = 5_000;  // timeout for periodic re-pushes
    private const int RePushIntervalMs   = 30_000;
    private const int MaxPushFails       = 3;
    private const int SlowScanIntervalMs = 3_000;
    private const int SlowScanMaxMs      = 120_000;

    // ── Events / state ────────────────────────────────────────────────────────

    /// <summary>Fired on the thread-pool when a new AccessApp is discovered and configured.</summary>
    public event Action<string>? GatewayFound;

    /// <summary>Fired on the thread-pool after <see cref="MaxPushFails"/> consecutive re-push
    /// failures, indicating the gateway is likely offline.</summary>
    public event Action<string>? GatewayLost;

    /// <summary>Number of AccessApp instances currently considered alive.</summary>
    public int DiscoveredCount => _entries.Count;

    // ── Internals ─────────────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<string, GatewayEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    // Separate clients so scan probes use a short timeout without affecting re-push reliability.
    private readonly HttpClient _scanHttp = new() { Timeout = TimeSpan.FromMilliseconds(ScanTimeoutMs) };
    private readonly HttpClient _pushHttp = new() { Timeout = TimeSpan.FromMilliseconds(PushTimeoutMs) };

    private int    _mqttPort;
    private string _networkId           = "";
    private bool   _useSharedNetworkId;
    private bool   _sendMqttHost        = true;

    // Slow scan
    private Queue<string>? _slowQueue;
    private DateTimeOffset  _slowDeadline;
    private readonly object _slowLock = new();

    private CancellationTokenSource? _cts;
    private Task? _rePushTask;
    private Task? _slowScanTask;
    private Task? _fastScanTask;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start(int mqttPort, string networkId, LocalMqttServerService? mqttServer = null, bool useSharedNetworkId = false, bool sendMqttHost = true)
    {
        Stop();
        _mqttPort           = mqttPort;
        _networkId          = networkId;
        _useSharedNetworkId = useSharedNetworkId;
        _sendMqttHost       = sendMqttHost;
        _entries.Clear();
        lock (_slowLock) { _slowQueue = null; _slowDeadline = DateTimeOffset.MinValue; }

        _cts         = new CancellationTokenSource();
        _rePushTask  = Task.Run(() => RePushLoopAsync(_cts.Token));

        AppLog.Info($"[AccessAppDiscovery] Started — MQTT port {mqttPort}, networkId={networkId}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _fastScanTask?.Wait(500);  } catch { }
        try { _slowScanTask?.Wait(500);  } catch { }
        try { _rePushTask?.Wait(1000);   } catch { }
        _cts?.Dispose();
        _cts = null;
        _fastScanTask = _slowScanTask = _rePushTask = null;
        _entries.Clear();
        AppLog.Info("[AccessAppDiscovery] Stopped.");
    }

    public void Dispose()
    {
        Stop();
        _scanHttp.Dispose();
        _pushHttp.Dispose();
    }

    // ── Public scan API ───────────────────────────────────────────────────────

    /// <summary>
    /// Probes all host IPs in local subnet(s) in parallel (TCP port 60000, ~300 ms total).
    /// Each confirmed AccessApp immediately receives a config push.
    /// <paramref name="onComplete"/> is invoked after all probes finish.
    /// </summary>
    public void StartFastScan(Action? onComplete = null)
    {
        if (_cts == null) return;
        var ct = _cts.Token;

        var candidates = BuildSubnetCandidates();
        AppLog.Info($"[AccessAppDiscovery] Fast scan — {candidates.Count} candidates ({ScanTimeoutMs} ms timeout, ≤200 concurrent).");

        _fastScanTask = Task.Run(async () =>
        {
            // Parallel.ForEachAsync streams work without pre-allocating all tasks, so
            // a /16 scan (65k hosts) won't flood the thread pool or the GC.
            await Parallel.ForEachAsync(candidates,
                new ParallelOptions { MaxDegreeOfParallelism = 200, CancellationToken = ct },
                async (ip, innerCt) => await TryDiscoverAndPushAsync(ip, innerCt).ConfigureAwait(false))
                .ConfigureAwait(false);

            AppLog.Info($"[AccessAppDiscovery] Fast scan complete — {_entries.Count} gateway(s) found.");
            onComplete?.Invoke();
        }, ct);
    }

    /// <summary>
    /// Probes the local machine's own LAN IPs every <paramref name="intervalMs"/> ms until
    /// at least one AccessApp responds or <paramref name="maxWaitMs"/> elapses.
    /// Call this immediately after launching a local AccessApp process so WPF finds it
    /// as soon as it finishes starting up — without waiting for a full subnet rescan.
    /// </summary>
    public void StartLocalAccessAppProbe(int intervalMs = 2000, int maxWaitMs = 30_000)
    {
        if (_cts == null) return;
        var ct = _cts.Token;

        Task.Run(async () =>
        {
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(maxWaitMs);
            var localIps = GetOwnLanIps();
            AppLog.Info($"[AccessAppDiscovery] Local probe started — watching {string.Join(", ", localIps)} every {intervalMs} ms.");

            while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
            {
                if (_entries.Count > 0) break; // already found something

                foreach (var ip in localIps)
                    await TryDiscoverAndPushAsync(ip, ct).ConfigureAwait(false);

                if (_entries.Count > 0) break;

                try { await Task.Delay(intervalMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            AppLog.Info(_entries.Count > 0
                ? $"[AccessAppDiscovery] Local probe complete — found {_entries.Count} gateway(s)."
                : "[AccessAppDiscovery] Local probe timed out — no local AccessApp found.");
        }, ct);
    }

    private static List<string> GetOwnLanIps()
    {
        var result = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            if (IsVirtualAdapter(ni)) continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    result.Add(addr.Address.ToString());
        }
        return result;
    }

    /// <summary>
    /// Starts a slow sequential scan of all subnet candidates (one probe every
    /// <see cref="SlowScanIntervalMs"/> ms) for up to <see cref="SlowScanMaxMs"/> ms.
    /// Call when a gateway goes offline to find it again if it restarted.
    /// Replaces any previously running slow scan.
    /// </summary>
    public void StartSlowScan()
    {
        if (_cts == null) return;
        var ct = _cts.Token;

        var candidates = BuildSubnetCandidates();
        lock (_slowLock)
        {
            _slowQueue    = new Queue<string>(candidates);
            _slowDeadline = DateTimeOffset.UtcNow.AddMilliseconds(SlowScanMaxMs);
        }

        // Cancel previous slow scan task if running and restart.
        _slowScanTask = Task.Run(() => SlowScanLoopAsync(ct), ct);
        AppLog.Info($"[AccessAppDiscovery] Slow scan started — {candidates.Count} candidates over max {SlowScanMaxMs / 1000} s.");
    }

    // ── Probe + push ──────────────────────────────────────────────────────────

    /// <summary>
    /// Probes <paramref name="ip"/> by posting the MQTT config directly.
    /// A 200 response means it's an AccessApp and it is already configured — one round-trip
    /// handles both discovery and setup with no AccessApp changes required.
    /// Any other outcome (404, timeout, refused) is silently ignored.
    /// </summary>
    private async Task TryDiscoverAndPushAsync(string ip, CancellationToken ct)
    {
        if (_entries.ContainsKey(ip)) return; // already known this session

        // Use a temporary entry to attempt the push. Only register on success so
        // the entries dictionary reflects confirmed, reachable AccessApp instances.
        var candidate = new GatewayEntry(ip);
        var success   = await PushConfigAsync(candidate, _scanHttp, ct).ConfigureAwait(false);
        if (!success) return;

        // POST returned 200 — this is AccessApp and is now configured.
        if (_entries.TryAdd(ip, candidate))
        {
            AppLog.Info($"[AccessAppDiscovery] Discovered AccessApp at {ip}.");
            GatewayFound?.Invoke(ip);
        }
    }

    /// <summary>
    /// Pushes the local MQTT broker config to <paramref name="entry"/>.
    /// Returns true on HTTP 200, false on any error or non-success status.
    /// Pass <see cref="_scanHttp"/> for discovery probes and <see cref="_pushHttp"/> for
    /// periodic re-pushes so timeouts are appropriate for each use.
    /// </summary>
    private async Task<bool> PushConfigAsync(GatewayEntry entry, HttpClient http, CancellationToken ct)
    {
        if (!await entry.PushGate.WaitAsync(0).ConfigureAwait(false))
            return false; // concurrent push already in progress

        try
        {
            string? mqttHost = null;
            if (_sendMqttHost)
            {
                mqttHost = GetOutboundIpFor(entry.Host);
                if (string.IsNullOrWhiteSpace(mqttHost))
                {
                    AppLog.Warn($"[AccessAppDiscovery] Cannot probe outbound IP for {entry.Host} — skipping push.");
                    return false;
                }
            }

            var payload = new
            {
                token     = LocalMqttServerService.LocalToken,
                mqttPort  = _mqttPort,
                mqttHost,                           // null when sendMqttHost=false → AccessApp uses RemoteIpAddress
                networkId = _useSharedNetworkId ? _networkId : (string?)null
            };

            var body = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await http.PostAsync(
                $"http://{entry.Host}:{HttpPort}/api/local-mqtt", body, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                entry.LastPushedUtc        = DateTimeOffset.UtcNow;
                entry.ConsecutivePushFails = 0;
                var suffix = _useSharedNetworkId ? $", networkId={_networkId}" : "";
                AppLog.Info($"[AccessAppDiscovery] Config pushed to {entry.Host} (mqttHost={mqttHost}, port={_mqttPort}{suffix}) — OK");
                return true;
            }

            AppLog.Warn($"[AccessAppDiscovery] Push to {entry.Host} returned HTTP {(int)response.StatusCode}");
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLog.Warn($"[AccessAppDiscovery] Push to {entry.Host} timed out.");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[AccessAppDiscovery] Push to {entry.Host} failed: {ex.Message}");
            return false;
        }
        finally
        {
            entry.PushGate.Release();
        }
    }

    // ── Background loops ──────────────────────────────────────────────────────

    /// <summary>
    /// Re-pushes the MQTT config to every known gateway every <see cref="RePushIntervalMs"/> ms.
    /// Consecutive failures increment a counter; after <see cref="MaxPushFails"/> the entry is
    /// removed and <see cref="GatewayLost"/> is fired.
    /// </summary>
    private async Task RePushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(RePushIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            foreach (var kv in _entries)
            {
                var entry   = kv.Value;
                var success = await PushConfigAsync(entry, _pushHttp, ct).ConfigureAwait(false);

                if (!success)
                {
                    entry.ConsecutivePushFails++;
                    AppLog.Warn($"[AccessAppDiscovery] Re-push fail #{entry.ConsecutivePushFails} for {entry.Host}");

                    if (entry.ConsecutivePushFails >= MaxPushFails &&
                        _entries.TryRemove(entry.Host, out _))
                    {
                        AppLog.Info($"[AccessAppDiscovery] Gateway lost (push failed {MaxPushFails}x): {entry.Host}");
                        entry.PushGate.Dispose();
                        GatewayLost?.Invoke(entry.Host);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Sequential slow scan: probes one candidate every <see cref="SlowScanIntervalMs"/> ms
    /// until the deadline or the queue is empty.
    /// </summary>
    private async Task SlowScanLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? ip = null;
            lock (_slowLock)
            {
                if (_slowQueue == null || DateTimeOffset.UtcNow >= _slowDeadline)
                {
                    if (_slowQueue != null)
                    {
                        AppLog.Info("[AccessAppDiscovery] Slow scan deadline reached.");
                        _slowQueue = null;
                    }
                    return;
                }

                // Skip already-known IPs.
                while (_slowQueue.Count > 0 && _entries.ContainsKey(_slowQueue.Peek()))
                    _slowQueue.Dequeue();

                _slowQueue.TryDequeue(out ip);

                if (_slowQueue.Count == 0)
                {
                    AppLog.Info("[AccessAppDiscovery] Slow scan exhausted all candidates.");
                    _slowQueue = null;
                }
            }

            if (ip != null)
                await TryDiscoverAndPushAsync(ip, ct).ConfigureAwait(false);

            try { await Task.Delay(SlowScanIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── Subnet enumeration ────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates host IPs to scan, covering the /16 supernet of each active LAN interface.
    /// This ensures cross-subnet devices (e.g. gateway at 192.168.40.1 when WPF is on
    /// 192.168.0.x/24) are discovered without requiring the user to configure IPs manually.
    /// Subnets narrower than /8 are safe; /8 and wider are skipped (too many hosts).
    /// </summary>
    private List<string> BuildSubnetCandidates()
    {
        var ownIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    ownIps.Add(addr.Address.ToString());
        }

        var seen       = new HashSet<uint>();   // deduplicate when multiple interfaces share the same /16
        var candidates = new List<string>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            if (IsVirtualAdapter(ni)) continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (addr.PrefixLength < 8) continue;  // skip /0–/7 (too many hosts)

                var ipBytes = addr.Address.GetAddressBytes();
                uint ipUint = ToUInt32BE(ipBytes);

                // Clamp to /16 so we cover the whole 192.168.x.x block even when the
                // interface is configured with a narrower mask (e.g. /24).
                const int ScanPrefix = 16;
                const uint ScanMask  = 0xFFFF_0000u;  // /16
                uint net       = ipUint & ScanMask;
                uint broadcast = net | ~ScanMask;

                for (uint h = net + 1; h < broadcast; h++)
                {
                    if (!seen.Add(h)) continue;
                    var ip = new IPAddress(FromUInt32BE(h)).ToString();
                    if (!ownIps.Contains(ip) && !_entries.ContainsKey(ip))
                        candidates.Add(ip);
                }
            }
        }

        return candidates;
    }

    private static uint   ToUInt32BE(byte[] b) =>
        ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    private static byte[] FromUInt32BE(uint v) =>
        new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static bool IsVirtualAdapter(NetworkInterface ni)
    {
        var desc = ni.Description ?? "";
        return desc.Contains("Virtual",     StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Hyper-V",     StringComparison.OrdinalIgnoreCase)
            || desc.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("WireGuard",   StringComparison.OrdinalIgnoreCase)
            || desc.Contains("vEthernet",   StringComparison.OrdinalIgnoreCase);
    }

    private static string GetOutboundIpFor(string targetIp)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(targetIp, 1);
            return ((IPEndPoint)probe.LocalEndPoint!).Address.ToString();
        }
        catch { return ""; }
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    private sealed class GatewayEntry
    {
        public string          Host                 { get; }
        public DateTimeOffset  LastPushedUtc        { get; set; } = DateTimeOffset.MinValue;
        public int             ConsecutivePushFails { get; set; }
        public SemaphoreSlim   PushGate             { get; } = new(1, 1);

        public GatewayEntry(string host) => Host = host;
    }
}
