using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAppMqttWpf.Services;

/// <summary>
/// Broadcasts a UDP beacon on the LAN so AccessApp instances (and Cassia gateways via AccessApp)
/// can discover and connect to the local MQTT broker.
///
/// Beacon JSON (sent to 255.255.255.255:<see cref="BeaconPort"/> every 3 s):
/// <code>{ "service":"cassia-mqtt", "host":"192.168.x.x", "port":1883, "networkId":"..." }</code>
/// </summary>
public sealed class LocalDiscoveryBeaconService : IDisposable
{
    public const int BeaconPort = 18884;
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
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.EnableBroadcast = true;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        var broadcastEp = new IPEndPoint(IPAddress.Broadcast, BeaconPort);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var host = GetLocalIpAddress();
                var beacon = new
                {
                    service = "cassia-mqtt",
                    host,
                    port = mqttPort,
                    networkId
                };

                var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(beacon));
                await socket.SendToAsync(data, SocketFlags.None, broadcastEp, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { /* network temporarily unavailable — ignore */ }

            try { await Task.Delay(BeaconIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Returns the first non-loopback LAN IPv4 address, or 127.0.0.1 as fallback.</summary>
    private static string GetLocalIpAddress()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    return addr.Address.ToString();
            }
        }
        return "127.0.0.1";
    }
}
