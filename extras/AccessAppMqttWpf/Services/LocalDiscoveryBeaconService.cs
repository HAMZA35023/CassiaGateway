using System;
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
/// </summary>
public sealed class LocalDiscoveryBeaconService : IDisposable
{
    public const int BeaconPort = 60004;
    private const int BeaconIntervalMs = 3000;

    private CancellationTokenSource? _cts;
    private Task? _task;

    public bool IsRunning => _task != null && !_task.IsCompleted;

    public void Start(int mqttPort, string networkId)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => BroadcastLoopAsync(mqttPort, networkId, _cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(1000); } catch { }
        _cts?.Dispose();
        _cts = null;
        _task = null;
    }

    public void Dispose() => Stop();

    private static async Task BroadcastLoopAsync(int mqttPort, string networkId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var (localIp, broadcastIp) in GetLanInterfaces())
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

                    var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(beacon));
                    await socket.SendToAsync(data, SocketFlags.None,
                        new IPEndPoint(broadcastIp, BeaconPort), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch { /* interface temporarily unavailable — ignore */ }
            }

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

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var ip   = addr.Address.GetAddressBytes();
                var mask = addr.IPv4Mask?.GetAddressBytes();
                if (mask == null || mask.Length != 4) continue;

                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcast[i] = (byte)((ip[i] & mask[i]) | (~mask[i] & 0xFF));

                result.Add((addr.Address, new IPAddress(broadcast)));
            }
        }

        // Fallback: if no suitable interface found, use 255.255.255.255 with a dummy local bind
        if (result.Count == 0)
            result.Add((IPAddress.Any, IPAddress.Broadcast));

        return result;
    }
}
