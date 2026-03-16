using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAppMqttWpf.Services;

/// <summary>
/// Broadcasts a UDP beacon on every active LAN interface so AccessApp instances on the same
/// subnet can discover and connect to the local MQTT broker.
///
/// For each interface a directed broadcast is sent to its subnet broadcast address
/// (e.g. 192.168.1.255) every 3 s, with that interface's IP in the payload:
/// <code>{ "service":"cassia-mqtt", "host":"192.168.x.x", "port":1883, "networkId":"..." }</code>
///
/// In addition, unicast beacons are sent directly to any IPs in <see cref="AddGatewayIp"/>.
/// This covers Cassia gateways where AccessApp runs in an LXC container and cannot receive
/// subnet broadcasts from the WPF side.
/// </summary>
public sealed class LocalDiscoveryBeaconService : IDisposable
{
    public const int BeaconPort = 60004;
    private const int BeaconIntervalMs = 3000;

    // Thread-safe set of unicast target IPs (e.g. Cassia WAN IPs)
    private readonly ConcurrentDictionary<string, bool> _unicastTargets =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _task;

    public bool IsRunning => _task != null && !_task.IsCompleted;

    public void Start(int mqttPort, string networkId, IEnumerable<string>? gatewayIps = null)
    {
        Stop();
        _unicastTargets.Clear();
        if (gatewayIps != null)
            foreach (var ip in gatewayIps)
                if (!string.IsNullOrWhiteSpace(ip))
                    _unicastTargets[ip.Trim()] = true;

        _cts  = new CancellationTokenSource();
        _task = Task.Run(() => BroadcastLoopAsync(mqttPort, networkId, _cts.Token));
        AppLog.Info($"[LocalDiscoveryBeacon] Started — broadcasting MQTT port {mqttPort}, networkId={networkId} on UDP {BeaconPort}");
        if (_unicastTargets.Count > 0)
            AppLog.Info($"[LocalDiscoveryBeacon] Unicast targets: {string.Join(", ", _unicastTargets.Keys)}");
    }

    /// <summary>
    /// Adds a unicast target IP to the running beacon without restarting.
    /// Idempotent — adding the same IP twice has no effect.
    /// </summary>
    public void AddGatewayIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        ip = ip.Trim();
        if (_unicastTargets.TryAdd(ip, true))
            AppLog.Info($"[LocalDiscoveryBeacon] Added unicast gateway target: {ip}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(1000); } catch { }
        _cts?.Dispose();
        _cts  = null;
        _task = null;
        AppLog.Info("[LocalDiscoveryBeacon] Stopped.");
    }

    public void Dispose() => Stop();

    private async Task BroadcastLoopAsync(int mqttPort, string networkId, CancellationToken ct)
    {
        bool firstLoop = true;
        while (!ct.IsCancellationRequested)
        {
            var interfaces = GetLanInterfaces();

            if (firstLoop)
            {
                foreach (var (localIp, broadcastIp) in interfaces)
                    AppLog.Info($"[LocalDiscoveryBeacon] Interface {localIp} → broadcast {broadcastIp}:{BeaconPort}");
            }

            // ── LAN subnet broadcast ───────────────────────────────────────────
            foreach (var (localIp, broadcastIp) in interfaces)
            {
                try
                {
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.EnableBroadcast = true;
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    socket.Bind(new IPEndPoint(localIp, 0));

                    var beacon = new
                    {
                        service = "cassia-mqtt",
                        host    = localIp.ToString(),
                        port    = mqttPort,
                        networkId
                    };

                    var json = JsonSerializer.Serialize(beacon);
                    var data = Encoding.UTF8.GetBytes(json);
                    await socket.SendToAsync(data, SocketFlags.None,
                        new IPEndPoint(broadcastIp, BeaconPort), ct).ConfigureAwait(false);

                    if (firstLoop)
                        AppLog.Info($"[LocalDiscoveryBeacon] Broadcast to {broadcastIp}:{BeaconPort}: {json}");
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    AppLog.Warn($"[LocalDiscoveryBeacon] Broadcast on {localIp} failed: {ex.Message}");
                }
            }

            // ── Unicast to known gateway IPs ───────────────────────────────────
            foreach (var target in _unicastTargets.Keys)
            {
                try
                {
                    var outboundIp = GetOutboundIpFor(target);
                    if (string.IsNullOrEmpty(outboundIp)) continue;

                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.Bind(new IPEndPoint(IPAddress.Parse(outboundIp), 0));

                    var beacon = new
                    {
                        service = "cassia-mqtt",
                        host    = outboundIp,   // WPF's real LAN IP toward this target
                        port    = mqttPort,
                        networkId
                    };

                    var json = JsonSerializer.Serialize(beacon);
                    var data = Encoding.UTF8.GetBytes(json);
                    await socket.SendToAsync(data, SocketFlags.None,
                        new IPEndPoint(IPAddress.Parse(target), BeaconPort), ct).ConfigureAwait(false);

                    if (firstLoop)
                        AppLog.Info($"[LocalDiscoveryBeacon] Unicast to {target}:{BeaconPort}: {json}");
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    AppLog.Warn($"[LocalDiscoveryBeacon] Unicast to {target} failed: {ex.Message}");
                }
            }

            firstLoop = false;

            try { await Task.Delay(BeaconIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Returns all active non-loopback/non-tunnel IPv4 interfaces with their directed
    /// broadcast address (ip &amp; mask | ~mask).
    /// </summary>
    private static List<(IPAddress localIp, IPAddress broadcastIp)> GetLanInterfaces()
    {
        var result = new List<(IPAddress, IPAddress)>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            if (IsVirtualAdapter(ni)) continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var ip   = addr.Address.GetAddressBytes();
                var mask = MaskFromPrefix(addr.IPv4Mask, addr.PrefixLength);
                if (mask == null) continue;

                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcast[i] = (byte)((ip[i] & mask[i]) | (~mask[i] & 0xFF));

                result.Add((addr.Address, new IPAddress(broadcast)));
            }
        }

        if (result.Count == 0)
            result.Add((IPAddress.Any, IPAddress.Broadcast));

        return result;
    }

    private static bool IsVirtualAdapter(System.Net.NetworkInformation.NetworkInterface ni)
    {
        var desc = ni.Description ?? "";
        return desc.Contains("Virtual",    StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Hyper-V",    StringComparison.OrdinalIgnoreCase)
            || desc.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("WireGuard",  StringComparison.OrdinalIgnoreCase)
            || desc.Contains("vEthernet",  StringComparison.OrdinalIgnoreCase);
    }

    private static byte[]? MaskFromPrefix(System.Net.IPAddress? ipv4Mask, int prefixLength)
    {
        var bytes = ipv4Mask?.GetAddressBytes();
        if (bytes != null && bytes.Length == 4) return bytes;

        if (prefixLength < 0 || prefixLength > 32) return null;
        var mask = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            int bits = Math.Min(8, Math.Max(0, prefixLength - i * 8));
            mask[i] = bits == 0 ? (byte)0 : (byte)(0xFF << (8 - bits));
        }
        return mask;
    }

    /// <summary>
    /// Uses the OS routing table to determine which local IP is used to reach
    /// <paramref name="targetIp"/>.  The UDP socket is never actually sent.
    /// </summary>
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
}
