using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAppMqttWpf.Services;

/// <summary>
/// Scans the local /24 subnets for devices listening on AccessApp's HTTP port (60000).
/// Used to populate the known gateway IP list when auto-discovery via UDP broadcast is
/// blocked (e.g. Cassia LXC container NAT).
/// </summary>
public static class GatewayScannerService
{
    public const int AccessAppHttpPort = 60000;
    private const int ConnectTimeoutMs = 300;
    private const int MaxParallel = 64;

    /// <summary>
    /// Scans all local /24 subnets for hosts with AccessApp's HTTP port open.
    /// </summary>
    /// <param name="progress">Reports (hostsChecked, hostsTotal) progress.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of IP address strings that responded on port 60000.</returns>
    public static async Task<List<string>> ScanAsync(
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        var candidates = BuildCandidateList();
        var found      = new ConcurrentBag<string>();
        int done       = 0;
        int total      = candidates.Count;

        using var sem = new SemaphoreSlim(MaxParallel);

        var tasks = new List<Task>(total);
        foreach (var ip in candidates)
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var tcp = new TcpClient();
                    var connect = tcp.ConnectAsync(ip, AccessAppHttpPort, ct).AsTask();
                    if (await Task.WhenAny(connect, Task.Delay(ConnectTimeoutMs, ct)).ConfigureAwait(false) == connect
                        && connect.IsCompletedSuccessfully)
                    {
                        found.Add(ip);
                    }
                }
                catch { }
                finally
                {
                    sem.Release();
                    progress?.Report((Interlocked.Increment(ref done), total));
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new List<string>(found);
    }

    /// <summary>
    /// Returns all host IPs in every local /24 subnet, excluding the machine's own IPs.
    /// </summary>
    private static List<string> BuildCandidateList()
    {
        var ownIps     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                ownIps.Add(addr.Address.ToString());

                var ip  = addr.Address.GetAddressBytes();
                // Only scan /24 (or larger prefix, treat as /24)
                for (int host = 1; host <= 254; host++)
                {
                    var candidate = new IPAddress(new byte[] { ip[0], ip[1], ip[2], (byte)host });
                    candidates.Add(candidate.ToString());
                }
            }
        }

        // Deduplicate and remove own IPs
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var c in candidates)
            if (!ownIps.Contains(c) && seen.Add(c))
                result.Add(c);

        return result;
    }
}
