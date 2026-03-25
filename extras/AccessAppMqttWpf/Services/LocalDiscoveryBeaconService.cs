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
/// Broadcasts a UDP beacon on every active LAN interface so AccessApp instances that CAN
/// receive subnet broadcasts discover the local MQTT broker without an HTTP scan.
///
/// Beacon payload every 3 s:
/// <code>{ "service":"cassia-mqtt", "host":"192.168.x.x", "port":1883, "networkId":"..." }</code>
///
/// Additionally sends unicast beacons every 3 s to session-known gateway IPs (added via
/// <see cref="AddGatewayIp"/>). This keeps the UDP path alive for devices that received the
/// initial HTTP config push but also listen for the beacon to refresh their broker address.
///
/// Note: Discovery and configuration of Cassia gateways is handled entirely by
/// <see cref="AccessAppDiscoveryService"/> via TCP/HTTP on port 60000. This service is a
/// complementary UDP path only.
/// </summary>
public sealed class LocalDiscoveryBeaconService : IDisposable
{
    public const int BeaconPort = 60004;
    private const int BeaconIntervalMs = 3000;

    private readonly ConcurrentDictionary<string, bool> _unicastTargets =
        new(StringComparer.OrdinalIgnoreCase);

    private int    _mqttPort;
    private string _networkId = "";

    private CancellationTokenSource? _cts;
    private Task? _beaconTask;

    public bool IsRunning => _beaconTask != null && !_beaconTask.IsCompleted;

    public void Start(int mqttPort, string networkId)
    {
        Stop();
        _mqttPort  = mqttPort;
        _networkId = networkId;
        _unicastTargets.Clear();

        _cts        = new CancellationTokenSource();
        _beaconTask = Task.Run(() => BeaconLoopAsync(_cts.Token));
        AppLog.Info($"[LocalDiscoveryBeacon] Started — MQTT port {mqttPort}, networkId={networkId}");
    }

    /// <summary>
    /// Registers a session-level unicast target (gateway found via TCP scan). The beacon loop
    /// will unicast to this IP every cycle in addition to the subnet broadcast. Not persisted.
    /// </summary>
    public void AddGatewayIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        ip = ip.Trim();
        if (_unicastTargets.TryAdd(ip, true))
            AppLog.Info($"[LocalDiscoveryBeacon] Unicast target registered: {ip}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _beaconTask?.Wait(1000); } catch { }
        _cts?.Dispose();
        _cts        = null;
        _beaconTask = null;
        AppLog.Info("[LocalDiscoveryBeacon] Stopped.");
    }

    public void Dispose() => Stop();

    private async Task BeaconLoopAsync(CancellationToken ct)
    {
        bool firstLoop = true;
        while (!ct.IsCancellationRequested)
        {
            var interfaces = GetLanInterfaces();

            if (firstLoop)
                foreach (var (localIp, broadcastIp) in interfaces)
                    AppLog.Info($"[LocalDiscoveryBeacon] Interface {localIp} → broadcast {broadcastIp}:{BeaconPort}");

            // ── Subnet broadcast ──────────────────────────────────────────────
            foreach (var (localIp, broadcastIp) in interfaces)
            {
                try
                {
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.EnableBroadcast = true;
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    socket.Bind(new IPEndPoint(localIp, 0));

                    var json = MakeBeaconJson(localIp.ToString());
                    var data = Encoding.UTF8.GetBytes(json);
                    await socket.SendToAsync(data, SocketFlags.None,
                        new IPEndPoint(broadcastIp, BeaconPort), ct).ConfigureAwait(false);

                    if (firstLoop)
                        AppLog.Info($"[LocalDiscoveryBeacon] Broadcast to {broadcastIp}:{BeaconPort}: {json}");
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { AppLog.Warn($"[LocalDiscoveryBeacon] Broadcast on {localIp} failed: {ex.Message}"); }
            }

            // ── Unicast to session-known gateway IPs ──────────────────────────
            foreach (var target in _unicastTargets.Keys)
            {
                try
                {
                    var outboundIp = GetOutboundIpFor(target);
                    if (string.IsNullOrEmpty(outboundIp)) continue;

                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.Bind(new IPEndPoint(IPAddress.Parse(outboundIp), 0));

                    var json = MakeBeaconJson(outboundIp);
                    var data = Encoding.UTF8.GetBytes(json);
                    await socket.SendToAsync(data, SocketFlags.None,
                        new IPEndPoint(IPAddress.Parse(target), BeaconPort), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { AppLog.Warn($"[LocalDiscoveryBeacon] Unicast to {target} failed: {ex.Message}"); }
            }

            firstLoop = false;

            try { await Task.Delay(BeaconIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private string MakeBeaconJson(string hostIp) =>
        JsonSerializer.Serialize(new { service = "cassia-mqtt", host = hostIp, port = _mqttPort, networkId = _networkId });

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
                var mask = MaskFromPrefix(addr.IPv4Mask, addr.PrefixLength);
                if (mask == null) continue;

                var ip        = addr.Address.GetAddressBytes();
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

    private static bool IsVirtualAdapter(NetworkInterface ni)
    {
        var desc = ni.Description ?? "";
        return desc.Contains("Virtual",     StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Hyper-V",     StringComparison.OrdinalIgnoreCase)
            || desc.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("WireGuard",   StringComparison.OrdinalIgnoreCase)
            || desc.Contains("vEthernet",   StringComparison.OrdinalIgnoreCase);
    }

    private static byte[]? MaskFromPrefix(IPAddress? ipv4Mask, int prefixLength)
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
