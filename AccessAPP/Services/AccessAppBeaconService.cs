using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAPP.Services;

/// <summary>
/// Broadcasts a UDP beacon on every active LAN interface so WPF AccessAppMqttWpf clients
/// on the same subnet can discover this AccessApp instance and push local MQTT broker settings.
///
/// Beacon JSON (sent to subnet broadcast : <see cref="BeaconPort"/> every 5 s):
/// <code>{ "service":"cassia-accessapp", "host":"192.168.x.x", "httpPort":60000, "name":"...", "networkId":"..." }</code>
/// </summary>
public sealed class AccessAppBeaconService : IDisposable
{
    public const int BeaconPort = 60004;
    public const int HttpPort   = 60000;
    private const int BeaconIntervalMs = 5000;

    private readonly IMqttService _mqtt;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public AccessAppBeaconService(IMqttService mqtt) => _mqtt = mqtt;

    public void Start()
    {
        if (_cts != null) return;
        _cts  = new CancellationTokenSource();
        _task = Task.Run(() => BroadcastLoopAsync(_cts.Token));
        AppLog.Info("[AccessAppBeacon] Broadcasting presence on UDP port " + BeaconPort);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(1000); } catch { }
        _cts?.Dispose();
        _cts  = null;
        _task = null;
    }

    public void Dispose() => Stop();

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        bool firstLoop = true;
        while (!ct.IsCancellationRequested)
        {
            var opts = _mqtt.CurrentOptions;
            var interfaces = GetLanInterfaces();

            if (firstLoop)
            {
                foreach (var (localIp, broadcastIp) in interfaces)
                    AppLog.Info($"[AccessAppBeacon] Interface {localIp} → broadcast {broadcastIp}:{BeaconPort}");
            }

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
                        service   = "cassia-accessapp",
                        host      = localIp.ToString(),
                        httpPort  = HttpPort,
                        name      = opts.Name,
                        networkId = opts.NetworkId
                    };

                    var json = JsonSerializer.Serialize(beacon);
                    var data = Encoding.UTF8.GetBytes(json);
                    await socket.SendToAsync(data, SocketFlags.None,
                        new IPEndPoint(broadcastIp, BeaconPort), ct).ConfigureAwait(false);

                    if (firstLoop)
                        AppLog.Info($"[AccessAppBeacon] Sent to {broadcastIp}:{BeaconPort}: {json}");
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    AppLog.Warn($"[AccessAppBeacon] Send on {localIp} failed: {ex.Message}");
                }
            }

            firstLoop = false;

            try { await Task.Delay(BeaconIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

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

        if (result.Count == 0)
            result.Add((IPAddress.Any, IPAddress.Broadcast));

        return result;
    }
}
