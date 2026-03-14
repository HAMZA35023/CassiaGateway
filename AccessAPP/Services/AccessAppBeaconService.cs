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
///
/// For Cassia gateways running AccessApp in an LXC container the container's network
/// (10.10.10.x) is invisible to the LAN.  This service therefore also queries the Cassia
/// web API to discover the host's WAN IP (wired.iface.ip / wired.iface.bcast) and sends
/// an additional directed broadcast to the WAN subnet with host=&lt;WAN IP&gt;, so that WPF
/// clients on the same LAN can receive the beacon and push MQTT config to the correct IP.
/// </summary>
public sealed class AccessAppBeaconService : IDisposable
{
    public const int BeaconPort = 60004;
    public const int HttpPort   = 60000;
    private const int BeaconIntervalMs    = 5000;
    private const int WanDiscoveryRetryMs = 15_000; // retry WAN API if first attempt fails

    private readonly IMqttService _mqtt;
    private readonly CassiaWebSettingsService _cassiaWeb;

    private CancellationTokenSource? _cts;
    private Task? _task;
    private Task? _wanDiscoveryTask;

    // Set once WAN discovery succeeds; read from the broadcast loop.
    private volatile IPAddress? _wanIp;
    private volatile IPAddress? _wanBroadcast;

    public AccessAppBeaconService(IMqttService mqtt, CassiaWebSettingsService cassiaWeb)
    {
        _mqtt      = mqtt;
        _cassiaWeb = cassiaWeb;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts  = new CancellationTokenSource();
        _task = Task.Run(() => BroadcastLoopAsync(_cts.Token));
        _wanDiscoveryTask = Task.Run(() => WanDiscoveryLoopAsync(_cts.Token));
        AppLog.Info("[ AccessAppBeacon] Broadcasting presence on UDP port " + BeaconPort);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(1000); }             catch { }
        try { _wanDiscoveryTask?.Wait(1000); } catch { }
        _cts?.Dispose();
        _cts              = null;
        _task             = null;
        _wanDiscoveryTask = null;
    }

    public void Dispose() => Stop();

    // ── WAN discovery ─────────────────────────────────────────────────────────

    /// <summary>
    /// Queries the Cassia web API on startup to learn the host's WAN IP and subnet broadcast.
    /// Retries every <see cref="WanDiscoveryRetryMs"/> until successful or cancelled.
    /// </summary>
    private async Task WanDiscoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _cassiaWeb.TryGetWanInfoAsync(ct).ConfigureAwait(false);
                if (result.HasValue)
                {
                    _wanIp        = result.Value.WanIp;
                    _wanBroadcast = result.Value.WanBroadcast;
                    AppLog.Info($"[AccessAppBeacon] WAN info discovered via Cassia API: " +
                                $"wanIp={_wanIp}, wanBroadcast={_wanBroadcast}");
                    return; // success — no need to keep retrying
                }

                AppLog.Info("[AccessAppBeacon] Cassia web API not available — WAN broadcast disabled (will retry).");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                AppLog.Warn($"[AccessAppBeacon] WAN discovery failed: {ex.Message} — will retry in {WanDiscoveryRetryMs / 1000}s");
            }

            try { await Task.Delay(WanDiscoveryRetryMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ── Broadcast loop ────────────────────────────────────────────────────────

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        bool firstLoop = true;
        while (!ct.IsCancellationRequested)
        {
            var opts = _mqtt.CurrentOptions;
            var interfaces = GetLanInterfaces();

            if (firstLoop)
            {
                foreach (var entry in interfaces)
                    AppLog.Info($"[AccessAppBeacon] Interface bind={entry.BindIp} → broadcast={entry.BroadcastIp}:{BeaconPort} (advertised host={entry.AdvertisedIp})");
            }

            foreach (var entry in interfaces)
            {
                try
                {
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.EnableBroadcast = true;
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    socket.Bind(new IPEndPoint(entry.BindIp, 0));

                    var beacon = new
                    {
                        service   = "cassia-accessapp",
                        host      = entry.AdvertisedIp.ToString(),  // WAN IP for NAT entries, local IP otherwise
                        httpPort  = HttpPort,
                        name      = opts.Name,
                        networkId = opts.NetworkId
                    };

                    var json = JsonSerializer.Serialize(beacon);
                    var data = Encoding.UTF8.GetBytes(json);
                    await socket.SendToAsync(data, SocketFlags.None,
                        new IPEndPoint(entry.BroadcastIp, BeaconPort), ct).ConfigureAwait(false);

                    if (firstLoop)
                        AppLog.Info($"[AccessAppBeacon] Sent to {entry.BroadcastIp}:{BeaconPort}: {json}");
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    AppLog.Warn($"[AccessAppBeacon] Send on bind={entry.BindIp} → {entry.BroadcastIp} failed: {ex.Message}");
                }
            }

            firstLoop = false;

            try { await Task.Delay(BeaconIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── Interface enumeration ─────────────────────────────────────────────────

    private readonly record struct InterfaceEntry(
        IPAddress BindIp,       // local IP to bind the UDP socket to
        IPAddress BroadcastIp,  // UDP broadcast destination
        IPAddress AdvertisedIp  // IP written into the beacon 'host' field
    );

    private List<InterfaceEntry> GetLanInterfaces()
    {
        var result = new List<InterfaceEntry>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var ip   = addr.Address.GetAddressBytes();
                // IPv4Mask is often null on Linux ARM — derive from PrefixLength instead
                var mask = MaskFromPrefix(addr.IPv4Mask, addr.PrefixLength);
                if (mask == null) continue;

                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcast[i] = (byte)((ip[i] & mask[i]) | (~mask[i] & 0xFF));

                // Normal LAN entry: bind to local IP, advertise local IP
                result.Add(new InterfaceEntry(addr.Address, new IPAddress(broadcast), addr.Address));
            }
        }

        // Add WAN entry if Cassia API already returned the WAN info.
        // The socket binds to any interface (IPAddress.Any) and sends to the WAN subnet
        // broadcast.  The beacon's 'host' is set to the Cassia's WAN IP so that WPF
        // can push config back to the correct publicly-routable address.
        var wanIp   = _wanIp;
        var wanBcast = _wanBroadcast;
        if (wanIp != null && wanBcast != null)
            result.Add(new InterfaceEntry(IPAddress.Any, wanBcast, wanIp));

        // Fallback: if nothing found at all, use 255.255.255.255 global broadcast
        if (result.Count == 0)
            result.Add(new InterfaceEntry(IPAddress.Any, IPAddress.Broadcast, IPAddress.Any));

        return result;
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
}
