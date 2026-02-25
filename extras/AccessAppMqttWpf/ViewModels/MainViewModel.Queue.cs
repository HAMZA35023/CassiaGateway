using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.VisualBasic;
using AccessAppMqttWpf;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel : ObservableObject
{


    public ObservableCollection<QueueItem> QueueItems { get; } = new();
    public ICollectionView QueueView { get; private set; } = null!;

    [ObservableProperty] private QueueItem? selectedQueueItem;
    [ObservableProperty] private string? selectedQueueMac;
    [ObservableProperty] private bool enableDoubleClickQueue;

    // Sticky per-device assignment.
    // - We auto-assign ONCE when a device first appears.
    // - We NEVER change assignment when RSSI changes, unless user presses "Reassign".
    private const int AssignmentRssiSlack = 20; // if another cassia is within 8-10 RSSI, it can take the device for balancing

    // ---- RSSI balancing thresholds (requested to be variables at top of the class) ----
    // Note: RSSI values are negative; e.g. -60 is stronger than -80.
    private const int RssiAllowBalancingThreshold = -65;   // >= -65: allow balancing among eligible Cassias
    private const int RssiWarnQueueThreshold = -70;        // < -70: show warning before queueing (still allowed)

    // Weights for balancing: lower score wins. Score = (load * weight) - (rssi * 1). Since RSSI is negative, stronger (less negative) lowers score.
    private const int AssignmentLoadWeight = 10;            // how much 1 queued/programming item counts vs 1 dB RSSI
    private const int RssiForceClosestThreshold = -999; // unused (kept for compatibility)     // <= -75: always use the closest Cassia (best RSSI)

    // Balancing goal: finish fastest by keeping roughly the same amount of work per Cassia.
    // We count "assigned detectors" as part of the load, not only queue/programming, because
    // your workflow tends to keep using the assigned Cassia for that device.
    private const int AssignedDetectorsWeight = 1; // 1 = treat one assigned detector as one unit of load

    private readonly HashSet<string> _deviceAssignmentWired = new(StringComparer.OrdinalIgnoreCase);

    private void WireDeviceAssignmentHooks(DiscoveredDevice dev)
    {
        if (dev == null) return;

        // If the device is first seen and has no assignment yet, seed it from BestCassia when available.
        void EnsureSeed()
        {
            if (!string.IsNullOrWhiteSpace(dev.AssignedCassia)) return;
            if (string.IsNullOrWhiteSpace(dev.BestCassia)) return;
            dev.AssignedCassia = dev.BestCassia;
        }

        EnsureSeed();

        dev.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiscoveredDevice.BestCassia))
                EnsureSeed();
        };
    }

    private void InitQueueView()
    {
        QueueView = CollectionViewSource.GetDefaultView(QueueItems);
        QueueView.SortDescriptions.Clear();
        // Put Done items at the bottom, then newest updates on top
        QueueView.SortDescriptions.Add(new SortDescription(nameof(QueueItem.QueueSortKey), ListSortDirection.Ascending));
        QueueView.SortDescriptions.Add(new SortDescription(nameof(QueueItem.LastUpdateUtc), ListSortDirection.Descending));

    }

    [RelayCommand]
    private void ClearQueue() => QueueItems.Clear();

    // IMPORTANT: Keep method names QueueSingle/QueueSelected so your XAML/code-behind bindings keep working.
    // These are async, so toolkit generates QueueSingleCommand/QueueSelectedCommand as IAsyncRelayCommand.
    [RelayCommand]
    private async Task QueueSingle()
    {
        if (SelectedDevice != null)
            await QueueDeviceAndRequestAsync(SelectedDevice);
    }

    [RelayCommand]
    private async Task QueueSelected()
    {
        var selected = _devices.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0 && SelectedDevice != null)
            selected.Add(SelectedDevice);

        if (selected.Count == 0) return;

        // If any selected device has very weak RSSI (< -70), warn once (device is still queueable).
        var weak = selected
            .Where(d => d != null && d.CassiaRssi != null && d.CassiaRssi.Count > 0)
            .Select(d => new
            {
                Dev = d,
                Best = d.CassiaRssi.Where(kv => !string.IsNullOrWhiteSpace(kv.Key)).OrderByDescending(kv => kv.Value).FirstOrDefault()
            })
            .Select(x => new { x.Dev, BestCassia = (x.Best.Key ?? "").Trim(), BestRssi = x.Best.Value })
            .Where(x => x.BestRssi < RssiWarnQueueThreshold)
            .ToList();

        if (weak.Count > 0)
        {
            var lines = weak
                .OrderBy(x => x.BestRssi)
                .Take(20)
                .Select(x => $"{x.Dev.Mac}  best={x.BestCassia}:{x.BestRssi} dBm")
                .ToList();

            var more = weak.Count > 20 ? $"\n... and {weak.Count - 20} more" : "";

            var res = MessageBox.Show(
                "Warning: Some devices have weak RSSI (< -70 dBm).\n\n" +
                string.Join("\n", lines) + more +
                "\n\nQueue anyway?",
                "Weak RSSI",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes)
                return;
        }

        // Build a preview of what will be queued and where (batch-aware load balancing).
        var plan = ComputeBatchAssignmentPlan(selected);

        // Show planned changes in a dedicated dialog (instead of MessageBox).
        var dialogRows = BuildAssignmentRowsFromDevices(selected, plan);
        var loadRows = BuildLoadSummaryForPlannedAdds(dialogRows);

        var dlgResult = ShowAssignmentPlanDialog(
            title: "Add to queue",
            subtitle: "Review suggested Cassia assignment (RSSI + load balancing) before queueing",
            rows: dialogRows,
            loadRows: loadRows,
            footer: "Apply = use suggested assignment - Keep current = use current assignment - Cancel = abort",
            notes: $"Rules: If best RSSI < {RssiAllowBalancingThreshold} we always pick the closest Cassia. Otherwise we balance using (assigned*{AssignedDetectorsWeight} + queue + programming), preferring ONLINE gateways and using RSSI as tie-break. If best RSSI < {RssiWarnQueueThreshold}, you get a warning.",
            showKeepButton: true);

        if (dlgResult == AssignmentPlanDialogResult.Cancel) return;

        if (dlgResult == AssignmentPlanDialogResult.Apply)
        {
            ApplySuggestedAssignmentsToDevices(selected, dialogRows);
        }

        _suppressWeakRssiPrompt = true;
        try
        {
            foreach (var d in selected)
            {
                await QueueDeviceAndRequestAsync(d);
                d.IsSelected = false;
            }
        }
        finally
        {
            _suppressWeakRssiPrompt = false;
        }
    }

    [RelayCommand]
    private async Task RebalanceQueuedItems()
    {
        // Only rebalance items that are still pending in the queue (not actively programming/done).
        var queued = QueueItems
            .Where(q => q != null
                        && !string.IsNullOrWhiteSpace(q.Mac)
                        && string.Equals((q.Status ?? "").Trim(), "Queued", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (queued.Count == 0)
            return;

        // Map queue items to discovered devices (for RSSI/closest Cassia).
        var devices = queued
            .Select(q => FindDiscoveredDevice((q.Mac ?? "").Trim()))
            .Where(d => d != null)
            .Cast<DiscoveredDevice>()
            .ToList();

        if (devices.Count == 0)
            return;

        var plan = ComputeBatchAssignmentPlan(devices);
        var rows = BuildAssignmentRowsFromQueue(queued, plan);
        var loadRows = BuildLoadSummaryForMoves(rows);

        var dlgResult = ShowAssignmentPlanDialog(
            title: "Rebalance queued items",
            subtitle: "Suggested moves based on RSSI + workload balancing",
            rows: rows,
            loadRows: loadRows,
            footer: "Apply = move queue items - Cancel = do nothing",
            notes: $"Rules: If best RSSI < {RssiAllowBalancingThreshold} we always pick the closest Cassia. Otherwise we balance using (assigned*{AssignedDetectorsWeight} + queue + programming), preferring ONLINE gateways and using RSSI as tie-break. If best RSSI < {RssiWarnQueueThreshold}, you get a warning.",
            showKeepButton: false);

        if (dlgResult != AssignmentPlanDialogResult.Apply)
            return;

        // Apply changes sequentially (MQTT best-effort) so we can show reasons before the action.
        foreach (var r in rows.Where(r => r.IsChange))
        {
            var qi = queued.FirstOrDefault(q => (q.Mac ?? "").Trim().Equals((r.Mac ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (qi == null) continue;

            // Skip if it changed since plan was built.
            if (!string.Equals((qi.Cassia ?? "").Trim(), (r.CurrentAssigned ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            await MoveQueueItemToCassiaAsync(qi, r.SuggestedAssigned).ConfigureAwait(false);

            // Also update sticky assignment so future actions keep the device balanced on the same Cassia.
            var dev = FindDiscoveredDevice((r.Mac ?? "").Trim());
            if (dev != null && !string.IsNullOrWhiteSpace(r.SuggestedAssigned))
                dev.AssignedCassia = r.SuggestedAssigned.Trim();
        }

        RecalculateAssignmentCounts();
        RequestDevicesRefresh();
    }

    private void ApplySuggestedAssignmentsToDevices(IReadOnlyList<DiscoveredDevice> selected, ObservableCollection<AssignmentChangeRow> rows)
    {
        foreach (var r in rows.Where(r => r.IsChange))
        {
            var d = selected.FirstOrDefault(x => (x.Mac ?? "").Trim().Equals((r.Mac ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (d == null) continue;
            if (IsDeviceInWork(d)) continue;
            if (!string.IsNullOrWhiteSpace(r.SuggestedAssigned))
                d.AssignedCassia = r.SuggestedAssigned;
        }
        RecalculateAssignmentCounts();
        RequestDevicesRefresh();
    }

    private ObservableCollection<AssignmentChangeRow> BuildAssignmentRowsFromDevices(
        IReadOnlyList<DiscoveredDevice> devices,
        IReadOnlyList<AssignmentPlanItem> plan)
    {
        var rows = new ObservableCollection<AssignmentChangeRow>();
        var byMac = plan.ToDictionary(p => (p.Mac ?? "").Trim(), p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var d in devices.Where(d => d != null).OrderBy(d => d.Mac, StringComparer.OrdinalIgnoreCase))
        {
            var mac = (d.Mac ?? "").Trim();
            if (mac.Length == 0) continue;

            byMac.TryGetValue(mac, out var p);
            var suggested = (p?.Cassia ?? "").Trim();
            var current = (d.AssignedCassia ?? d.BestCassia ?? "").Trim();
            var closest = (d.BestCassia ?? "").Trim();

            rows.Add(new AssignmentChangeRow
            {
                Mac = mac,
                ClosestCassia = closest,
                ClosestRssi = d.BestRssi == int.MinValue ? 0 : d.BestRssi,
                CurrentAssigned = current,
                SuggestedAssigned = suggested.Length == 0 ? current : suggested,
                SuggestedRssi = (suggested.Length > 0 && d.CassiaRssi.TryGetValue(suggested, out var rr)) ? rr : (d.BestRssi == int.MinValue ? 0 : d.BestRssi),
                Reason = p?.Reason ?? ""
            });
        }

        return rows;
    }

    private ObservableCollection<AssignmentChangeRow> BuildAssignmentRowsFromQueue(
        IReadOnlyList<QueueItem> queued,
        IReadOnlyList<AssignmentPlanItem> plan)
    {
        var rows = new ObservableCollection<AssignmentChangeRow>();
        var byMac = plan.ToDictionary(p => (p.Mac ?? "").Trim(), p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var qi in queued.Where(q => q != null).OrderBy(q => q.Mac, StringComparer.OrdinalIgnoreCase))
        {
            var mac = (qi.Mac ?? "").Trim();
            if (mac.Length == 0) continue;

            var dev = FindDiscoveredDevice(mac);
            byMac.TryGetValue(mac, out var p);

            var suggested = (p?.Cassia ?? "").Trim();
            var current = (qi.Cassia ?? "").Trim();
            var closest = (dev?.BestCassia ?? "").Trim();
            var closestRssi = dev?.BestRssi ?? 0;

            rows.Add(new AssignmentChangeRow
            {
                Mac = mac,
                ClosestCassia = closest,
                ClosestRssi = closestRssi == int.MinValue ? 0 : closestRssi,
                CurrentAssigned = current,
                SuggestedAssigned = suggested.Length == 0 ? current : suggested,
                SuggestedRssi = (dev != null && suggested.Length > 0 && dev.CassiaRssi.TryGetValue(suggested, out var rr)) ? rr : (closestRssi == int.MinValue ? 0 : closestRssi),
                Reason = p?.Reason ?? (dev == null ? "device not in list" : "")
            });
        }

        return rows;
    }

    private ObservableCollection<CassiaLoadSummaryRow> BuildLoadSummaryForPlannedAdds(ObservableCollection<AssignmentChangeRow> rows)
    {
        // Summary is QUEUE + PROGRAMMING (these come from MQTT status).
        var before = CassiaGateways
            .Where(g => g != null && !string.IsNullOrWhiteSpace(g.Name))
            .ToDictionary(g => g.Name.Trim(), g => Math.Max(0, g.Queue) + Math.Max(0, g.Programming), StringComparer.OrdinalIgnoreCase);

        // After = before + planned queue adds per suggested Cassia
        var adds = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.SuggestedAssigned))
            .GroupBy(r => r.SuggestedAssigned.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var after = new Dictionary<string, int>(before, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in adds)
        {
            after[kv.Key] = (after.TryGetValue(kv.Key, out var v) ? v : 0) + kv.Value;
        }

        return BuildLoadRows(before, after);
    }

    private ObservableCollection<CassiaLoadSummaryRow> BuildLoadSummaryForMoves(ObservableCollection<AssignmentChangeRow> rows)
    {
        // Summary is QUEUE + PROGRAMMING (these come from MQTT status).
        var before = CassiaGateways
            .Where(g => g != null && !string.IsNullOrWhiteSpace(g.Name))
            .ToDictionary(g => g.Name.Trim(), g => Math.Max(0, g.Queue) + Math.Max(0, g.Programming), StringComparer.OrdinalIgnoreCase);

        // After = before + deltas from moves (queue moves only).
        var after = new Dictionary<string, int>(before, StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows.Where(r => r.IsChange))
        {
            var from = (r.CurrentAssigned ?? "").Trim();
            var to = (r.SuggestedAssigned ?? "").Trim();
            if (from.Length > 0)
                after[from] = (after.TryGetValue(from, out var v) ? v : 0) - 1;
            if (to.Length > 0)
                after[to] = (after.TryGetValue(to, out var v) ? v : 0) + 1;
        }
        return BuildLoadRows(before, after);
    }

    private ObservableCollection<CassiaLoadSummaryRow> BuildLoadRows(Dictionary<string, int> before, Dictionary<string, int> after)
    {
        var rows = new ObservableCollection<CassiaLoadSummaryRow>();
        var keys = before.Keys.Concat(after.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

        foreach (var k in keys)
        {
            var b = before.TryGetValue(k, out var bv) ? bv : 0;
            var a = after.TryGetValue(k, out var av) ? av : 0;

            var gw = CassiaGateways.FirstOrDefault(g => g != null && (g.Name ?? "").Trim().Equals(k, StringComparison.OrdinalIgnoreCase));
            rows.Add(new CassiaLoadSummaryRow
            {
                Cassia = k,
                BeforeLoad = b,
                AfterLoad = a,
                Delta = a - b,
                BeforeQueue = gw?.Queue ?? 0,
                BeforeProgramming = gw?.Programming ?? 0,
            });
        }
        return rows;
    }

    private AssignmentPlanDialogResult ShowAssignmentPlanDialog(
        string title,
        string subtitle,
        ObservableCollection<AssignmentChangeRow> rows,
        ObservableCollection<CassiaLoadSummaryRow> loadRows,
        string footer,
        string notes,
        bool showKeepButton)
    {
        var result = AssignmentPlanDialogResult.Cancel;

        Application.Current.Dispatcher.Invoke(() =>
        {
            AssignmentPlanWindow? win = null;
            var vm = new AssignmentPlanWindowViewModel(
                title: title,
                subtitle: subtitle,
                rows: rows,
                loadRows: loadRows,
                footer: footer,
                notes: notes,
                showKeepButton: showKeepButton,
                close: r =>
                {
                    result = r;
                    try { win?.Close(); } catch { }
                });

            win = new AssignmentPlanWindow(vm)
            {
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            try { win.ShowDialog(); } catch { }
        });

        return result;
    }

    // ---------------------------------------------------------------------
    // Device context actions (Connect / Disconnect / Write-Read)
    // These are used by the Devices grid right-click menu.
    // ---------------------------------------------------------------------

    [RelayCommand]
    private async Task ConnectDevice(DiscoveredDevice? device)
        => await ConnectDeviceAsync(device);

    [RelayCommand]
    private async Task DisconnectDevice(DiscoveredDevice? device)
        => await DisconnectDeviceAsync(device);

    [RelayCommand]
    private async Task GetFwForDevice(DiscoveredDevice? device)
        => await GetFwForDeviceAsync(device);

    [RelayCommand]
    private async Task GetFwSelected()
        => await GetFwForSelectedAsync();

    internal async Task ConnectDeviceAsync(DiscoveredDevice? device)
    {
        if (device == null) return;
        await SendConnectOrDisconnectAsync(device, action: "connect");
    }

    internal async Task DisconnectDeviceAsync(DiscoveredDevice? device)
    {
        if (device == null) return;
        await SendDisconnectAsync(new[] { device });
    }

    internal async Task DisconnectDevicesAsync(IEnumerable<DiscoveredDevice> devices)
        => await SendDisconnectAsync(devices);

    internal async Task GetFwForDeviceAsync(DiscoveredDevice? device)
    {
        if (device == null) return;
        await SendGetFwVersionAsync(new[] { device });
    }

    internal async Task GetFwForSelectedAsync()
    {
        var selected = _devices.Where(d => d != null && d.IsSelected).ToList();
        if (selected.Count == 0) return;

        // If any selected device has very weak RSSI (< -70), warn once (device is still queueable).
        var weak = selected
            .Where(d => d != null && d.CassiaRssi != null && d.CassiaRssi.Count > 0)
            .Select(d => new
            {
                Dev = d,
                Best = d.CassiaRssi.Where(kv => !string.IsNullOrWhiteSpace(kv.Key)).OrderByDescending(kv => kv.Value).FirstOrDefault()
            })
            .Select(x => new { x.Dev, BestCassia = (x.Best.Key ?? "").Trim(), BestRssi = x.Best.Value })
            .Where(x => x.BestRssi < RssiWarnQueueThreshold)
            .ToList();

        if (weak.Count > 0)
        {
            var lines = weak
                .OrderBy(x => x.BestRssi)
                .Take(20)
                .Select(x => $"{x.Dev.Mac}  best={x.BestCassia}:{x.BestRssi} dBm")
                .ToList();

            var more = weak.Count > 20 ? $"\n... and {weak.Count - 20} more" : "";

            var res = MessageBox.Show(
                "Warning: Some devices have weak RSSI (< -70 dBm).\n\n" +
                string.Join("\n", lines) + more +
                "\n\nQueue anyway?",
                "Weak RSSI",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes)
                return;
        }

        // In Production Update mode, force scan-under-programming off before each query-selected action.
        await ApplyProductionRuntimeForSelectedQueryAsync(selected).ConfigureAwait(false);

        await SendGetFwVersionAsync(selected);
    }

    private static Dictionary<string, object?> BuildProductionUpdateRuntimePayload()
        => new()
        {
            ["RebootDetectorAfterUpgrade"] = false,
            ["Restore102DBAfterUpgrade"] = false,
            ["RestoreSettingsAfterUpgrade"] = false,
            ["AutoSetSysFailLevelUnderUpdate"] = false,
            ["BLE_SCAN_UNDER_PROGRAMMING"] = false
        };

    private static Dictionary<string, object?> BuildProductionUpdateResetPayload()
        => new()
        {
            ["RebootDetectorAfterUpgrade"] = true,
            ["Restore102DBAfterUpgrade"] = true,
            ["RestoreSettingsAfterUpgrade"] = true,
            ["AutoSetSysFailLevelUnderUpdate"] = true
        };

    private async Task ApplyProductionRuntimeForSelectedQueryAsync(IEnumerable<DiscoveredDevice> devices)
    {
        if (!ProductionUpdateEnabled)
            return;

        var cassias = (devices ?? Array.Empty<DiscoveredDevice>())
            .Where(d => d != null)
            .Select(ResolveCassiaForCommand)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (cassias.Length == 0)
            return;

        var runtimePayload = BuildProductionUpdateRuntimePayload();
        foreach (var cassia in cassias)
            await SetRuntimeForCassiaAsync(cassia, runtimePayload).ConfigureAwait(false);
    }

    [RelayCommand]
    private void OpenWriteRead(DiscoveredDevice? device)
    {
        if (device == null) return;
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var win = new WriteReadWindow(this, device);
                win.Owner = Application.Current.MainWindow;
                win.Show();
                win.Activate();
            });
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Open Write-Read failed: " + ex.Message;
        }
    }

    private string BuildCmdTopic(string cassia, string command)
    {
        var tpl = string.IsNullOrWhiteSpace(CommandTopicTemplate)
            ? "accessapp/{networkId}/cmd/{cassia}/{command}"
            : CommandTopicTemplate;

        return tpl
            .Replace("{networkId}", NetworkId ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{cassia}", cassia ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{command}", command ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendConnectOrDisconnectAsync(DiscoveredDevice device, string action)
    {
        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        var mac = (device.Mac ?? "").Trim();
        if (string.IsNullOrWhiteSpace(mac)) return;

        var cassia = (device.AssignedCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassia))
            cassia = (device.BestCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassia))
        {
            ConnectionStatus = "No Cassia selected for device";
            return;
        }

        // Connect still uses cmd/<cassia>/connect.
        // Disconnect is now a dedicated cmd/<cassia>/disconnect endpoint.
        var isDisconnect = action.Equals("disconnect", StringComparison.OrdinalIgnoreCase);
        var topic = BuildCmdTopic(cassia, isDisconnect ? "disconnect" : "connect");

        object payload = isDisconnect
            ? new { sensors = new[] { mac } }
            : new { sensors = new[] { mac } };

        device.BleLink = action.Equals("disconnect", StringComparison.OrdinalIgnoreCase) ? "disconnecting…" : "connecting…";
        await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
    }

    private const string DefaultPincode = "1234";

    private async Task SendGetFwVersionAsync(IEnumerable<DiscoveredDevice> devices)
    {
        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        var list = devices?.Where(d => d != null && !string.IsNullOrWhiteSpace(d.Mac)).Distinct().ToList() ?? new();
        if (list.Count == 0) return;

        // Group by target Cassia because the topic contains the cassia name.
        var groups = list
            .Select(d => new
            {
                Dev = d,
                Cassia = ResolveCassiaForCommand(d)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Cassia))
            .GroupBy(x => x.Cassia, StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            var cassia = g.Key;
            var macs = g.Select(x => (x.Dev.Mac ?? "").Trim()).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (macs.Length == 0) continue;

            // UI: show that a FW query was requested.
            foreach (var x in g)
            {
                var mac = (x.Dev.Mac ?? "").Trim();
                var cs = GetOrCreateCache(mac);
                cs.CurrentFw = "requested";
                cs.CurrentFwFromGetFw = true;   // ✅ mark as Get FW sourced
                x.Dev.CurrentFw = "requested";
            }

            var topic = BuildCmdTopic(cassia, "get-fw-version");
            var payload = new { sensors = macs, pincode = DefaultPincode };

            try
            {
                await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ConnectionStatus = $"get-fw-version publish failed: {ex.Message}";
            }
        }
    }

    private async Task SendDisconnectAsync(IEnumerable<DiscoveredDevice> devices)
    {
        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        var list = devices?.Where(d => d != null && !string.IsNullOrWhiteSpace(d.Mac)).Distinct().ToList() ?? new();
        if (list.Count == 0) return;

        var groups = list
            .Select(d => new { Dev = d, Cassia = ResolveCassiaForCommand(d) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Cassia))
            .GroupBy(x => x.Cassia, StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            var cassia = g.Key;
            var macs = g.Select(x => (x.Dev.Mac ?? "").Trim()).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (macs.Length == 0) continue;

            foreach (var x in g)
                x.Dev.BleLink = "disconnecting…";

            var topic = BuildCmdTopic("all", "disconnect");
            var payload = new { sensors = macs };

            try
            {
                await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ConnectionStatus = $"disconnect publish failed: {ex.Message}";
            }
        }
    }

    private string ResolveCassiaForCommand(DiscoveredDevice d)
    {
        var cassia = (d.AssignedCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassia))
            cassia = (d.BestCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassia))
            cassia = CassiaGateways.FirstOrDefault(g => string.Equals(g.State, "online", StringComparison.OrdinalIgnoreCase))?.Name
                     ?? CassiaGateways.FirstOrDefault()?.Name
                     ?? "";
        return cassia;
    }

    internal async Task SendWriteReadAsync(
        DiscoveredDevice device,
        string hex,
        int handle = 19,
        bool noResponse = true,
        bool expectReply = false,
        int? timeoutSeconds = null)
    {
        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        var mac = (device.Mac ?? "").Trim();
        var cassia = (device.AssignedCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassia))
            cassia = (device.BestCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(mac) || string.IsNullOrWhiteSpace(cassia))
            return;

        var topic = BuildCmdTopic(cassia, "write-read");

        hex = NormalizeHexInput(hex);

        // Minimal payload defaults: handle=19, noResponse=true, expectReply=false
        object payload = timeoutSeconds.HasValue
            ? new { sensors = new[] { mac }, handle, hex, noResponse, expectReply, timeoutSeconds = timeoutSeconds.Value }
            : new { sensors = new[] { mac }, handle, hex, noResponse, expectReply };

        await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
    }

    private static readonly Regex NonHexRx = new("[^0-9A-Fa-f]", RegexOptions.Compiled);

    private static string NormalizeHexInput(string? hex)
    {
        var s = (hex ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(2);

        // Allow common formats: "0110", "01 10", "01-10", etc.
        s = NonHexRx.Replace(s, "");
        return s.ToUpperInvariant();
    }

    // ---------------------------------------------------------------------
    // Sticky assignment (balanced between cassias)
    // ---------------------------------------------------------------------

    private void EnsureCassiaOption(string? name)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!CassiaNameOptions.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)))
            CassiaNameOptions.Add(name);
    }

    private void SortCassiaGatewaysByName()
    {
        if (CassiaGateways.Count <= 1) return;

        var ordered = CassiaGateways
            .Where(g => g != null)
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Reorder in-place (preserves bindings)
        for (int targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
        {
            var item = ordered[targetIndex];
            var currentIndex = CassiaGateways.IndexOf(item);
            if (currentIndex >= 0 && currentIndex != targetIndex)
                CassiaGateways.Move(currentIndex, targetIndex);
        }
    }

    private void EnsureDeviceAssignmentWiring(DiscoveredDevice d)
    {
        if (d == null) return;
        var mac = (d.Mac ?? "").Trim();
        if (string.IsNullOrWhiteSpace(mac)) return;
        if (_deviceAssignmentWired.Contains(mac)) return;
        _deviceAssignmentWired.Add(mac);

        d.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiscoveredDevice.AssignedCassia)
                || e.PropertyName == nameof(DiscoveredDevice.SensorModel))
            {
                // User changed assignment from the dropdown, or model updated.
                RecalculateAssignmentCounts();
            }
        };
    }

    private static int GetGroupForModel(string? model)
    {
        model = (model ?? "").Trim().ToUpperInvariant();
        return model switch
        {
            "P41" or "P42" or "P46" => 1,
            "P47" or "P48" => 2,
            _ => 0
        };
    }

    /// <summary>
    /// Ensures the device has a sticky AssignedCassia.
    /// We only auto-assign once (when AssignedCassia is empty).
    /// </summary>
    
    // ---------------- Assignment helpers ----------------
    private int GetGatewayLoad(string cassia)
    {
        if (string.IsNullOrWhiteSpace(cassia)) return 0;
        var gw = CassiaGateways.FirstOrDefault(g => g.Name.Equals(cassia, StringComparison.OrdinalIgnoreCase));
        if (gw == null) return 0;
        // Workload must reflect what Cassia reports (queue + programming). Assigned counts are NOT used here.
        return Math.Max(0, gw.Queue) + Math.Max(0, gw.Programming);
    }

    private bool IsDeviceInWork(DiscoveredDevice d)
    {
        if (d == null) return false;

        // If the device has an active queue entry (not done), consider it "in work".
        var mac = (d.Mac ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(mac))
        {
            if (QueueItems.Any(q => q != null &&
                                   mac.Equals((q.Mac ?? "").Trim(), StringComparison.OrdinalIgnoreCase) &&
                                   !q.IsDone &&
                                   (q.Progress < 100 || (DateTimeOffset.UtcNow - q.LastUpdateUtc) <= TimeSpan.FromMinutes(1))))
                return true;
        }

        // Also treat "process/progress tele" as work if progress is < 100.
        if (d.ProcessProgress > 0 && d.ProcessProgress < 100)
            return true;

        return false;
    }

    private bool IsDoneForBalancing(DiscoveredDevice d)
    {
        if (d == null) return false;

        // User rule: if % is 100 for over 1 minute, assume done and exclude from balancing counts.
        if (d.ProcessProgress >= 100 && d.ProcessLastUpdateUtc.HasValue)
        {
            if (DateTimeOffset.UtcNow - d.ProcessLastUpdateUtc.Value > TimeSpan.FromMinutes(1))
                return true;
        }

        // If we only have queue info, apply same heuristic.
        var mac = (d.Mac ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(mac))
        {
            var q = QueueItems.FirstOrDefault(x =>
                x != null && mac.Equals((x.Mac ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

            if (q != null && q.Progress >= 100 && (DateTimeOffset.UtcNow - q.LastUpdateUtc) > TimeSpan.FromMinutes(1))
                return true;

            if (q != null && q.IsDone)
                return true;
        }

        return false;
    }


    private void EnsureStickyAssignment(DiscoveredDevice d)
{
    if (d == null) return;
    if (!string.IsNullOrWhiteSpace(d.AssignedCassia)) return; // already assigned (sticky)

    // Do not auto-assign devices that are already being worked on (queued/programming).
    if (IsDeviceInWork(d)) return;

    if (!TryChooseCassiaForUpdate(d, plannedLoad: null, out var chosen, out _))
        return;

    d.AssignedCassia = chosen;
}


    private (string cassia, string reason) SuggestCassiaForDevice(DiscoveredDevice d)
{
    if (d == null) return ("", "no device");
    if (d.CassiaRssi.Count == 0) return ("", "no RSSI");

    if (!TryChooseCassiaForUpdate(d, plannedLoad: null, out var cassia, out var reason))
        return ("", reason);

    return (cassia, reason);
}

    /// <summary>
    /// Chooses the best Cassia for updating a device, respecting RSSI threshold and load balancing.
    /// Rules:
    ///  - Only Cassias with RSSI >= RssiAllowBalancingThreshold are eligible (e.g. -65).
    ///  - Prefer ONLINE gateways when possible.
    ///  - Primary sort: lowest effective load (assigned detectors + queue + programming + optional planned load).
    ///  - Tie-break: higher RSSI, then name.
    /// Returns false if no eligible Cassia meets the RSSI threshold.
    /// </summary>
    private bool TryChooseCassiaForUpdate(
    DiscoveredDevice d,
    Dictionary<string, int>? plannedLoad,
    out string cassia,
    out string reason)
{
    cassia = "";
    reason = "";

    if (d == null)
    {
        reason = "no device";
        return false;
    }

    if (d.CassiaRssi.Count == 0)
    {
        reason = "no RSSI";
        return false;
    }

    // Determine the closest Cassia (best RSSI).
    var best = d.CassiaRssi
        .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
        .OrderByDescending(kv => kv.Value)
        .FirstOrDefault();

    var bestCassia = (best.Key ?? "").Trim();
    var bestRssi = best.Value;

    if (string.IsNullOrWhiteSpace(bestCassia))
    {
        reason = "no RSSI";
        return false;
    }

    // Rule: if the strongest RSSI is weaker than the balancing threshold, ALWAYS use the closest Cassia.
    // This guarantees a device is always queueable and avoids "legal but weak" balancing moves.
    if (bestRssi < RssiAllowBalancingThreshold)
    {
        cassia = bestCassia;
        reason = $"rssi {bestRssi} (< {RssiAllowBalancingThreshold}): weak link, chose closest={cassia}";
        return true;
    }

    // For strong links (>= threshold): allow balancing, but still prefer the closest when choices are otherwise equal.
    // Eligible: RSSI >= threshold AND within slack of the closest Cassia (so we don't pick a much worse radio just for load).
    var candidates = d.CassiaRssi
        .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
        .Where(kv => kv.Value >= RssiAllowBalancingThreshold)        .Select(kv => kv.Key.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (candidates.Count == 0)
    {
        // Should not happen because bestRssi >= threshold, but keep it safe.
        cassia = bestCassia;
        reason = $"rssi {bestRssi} (>= {RssiAllowBalancingThreshold}): closest fallback={cassia}";
        return true;
    }

    int EffectiveLoad(string c)
    {
        var baseLoad = GetGatewayLoad(c); // uses latest Cassia-reported queue/programming + assigned group counts
        var extra = (plannedLoad != null && plannedLoad.TryGetValue(c, out var v)) ? v : 0;
        return baseLoad + extra;
    }

    bool IsOnline(string c)
    {
        var gw = CassiaGateways.FirstOrDefault(g => g != null && string.Equals(g.Name, c, StringComparison.OrdinalIgnoreCase));
        return gw != null && string.Equals(gw.State, "online", StringComparison.OrdinalIgnoreCase);
    }

    var anyOnline = candidates.Any(IsOnline);
    var pool = anyOnline ? candidates.Where(IsOnline).ToList() : candidates;

    int GetRssi(string c)
    {
        foreach (var kv in d.CassiaRssi)
        {
            var k = (kv.Key ?? "").Trim();
            if (string.Equals(k, c, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return int.MinValue;
    }


    // Strong-link rule (best RSSI >= threshold):
    //   1) Always prefer the Cassia with the LOWEST *current* workload (queue + programming) (plus planned batch load)
    //   2) If tied, prefer the closest Cassia (highest RSSI)
    // This matches the expected field behavior: if multiple Cassias are "good enough" radio-wise, we spread work first.
    cassia = pool
        .OrderBy(c => EffectiveLoad(c))
        .ThenByDescending(c => GetRssi(c))
        .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault() ?? bestCassia;

    var chosenRssi = GetRssi(cassia);
    var extraTxt = plannedLoad != null && plannedLoad.TryGetValue(cassia, out var extra) ? $"+{extra}" : "";
    reason = $"rssi {chosenRssi} (>= {RssiAllowBalancingThreshold}): balance(load-first), chose={cassia}, load={EffectiveLoad(cassia)} (base={GetGatewayLoad(cassia)}{extraTxt})";
    return true;
}



    private sealed record AssignmentPlanItem(string Mac, string Cassia, string Reason);


    /// <summary>
    /// Computes a batch-aware assignment plan for the given devices.
    /// Rules:
    ///  - If best RSSI is >= -65: allow balancing among eligible Cassias (load + rssi tie-break).
    ///  - If best RSSI is weaker than -65: load-balance among eligible Cassias.
    ///  - If best RSSI is <= -75: always pick the closest Cassia.
    /// Eligible = within AssignmentRssiSlack dB of best.
    /// Load = (assigned detectors * AssignedDetectorsWeight) + Cassia status (queue+programming) + already planned assignments in this batch.
    /// </summary>
    /// <summary>
    /// Computes a batch-aware assignment plan for the given devices.
    /// Rules:
    ///  - Only Cassias with RSSI >= RssiAllowBalancingThreshold are eligible (e.g. -65).
    ///  - We load-balance across all eligible Cassias using reported workload:
    ///      load = (assigned detectors * AssignedDetectorsWeight) + queue + programming + already planned assigns in this batch.
    ///  - Prefer ONLINE gateways when possible.
    ///  - If no Cassia meets the RSSI threshold for a device, the plan keeps the current assignment (no suggested change).
    /// </summary>
    private List<AssignmentPlanItem> ComputeBatchAssignmentPlan(IReadOnlyList<DiscoveredDevice> devices)
{
    var result = new List<AssignmentPlanItem>();
    if (devices == null || devices.Count == 0) return result;

    // Planned incremental load per Cassia for this batch.
    var planned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    void AddPlanned(string cassia)
    {
        cassia = (cassia ?? "").Trim();
        if (cassia.Length == 0) return;
        planned[cassia] = planned.TryGetValue(cassia, out var v) ? v + 1 : 1;
    }

    // Deterministic order: assign strongest devices first (more options) so later items can still balance well.
    foreach (var d in devices
                 .Where(x => x != null)
                 .OrderByDescending(x => x.CassiaRssi.Count == 0 ? int.MinValue : x.CassiaRssi.Max(kv => kv.Value))
                 .ThenBy(x => x.Mac, StringComparer.OrdinalIgnoreCase))
    {
        var mac = (d.Mac ?? "").Trim();
        if (mac.Length == 0) continue;

        // If we have no RSSI, keep current assignment (or best known) as "no suggestion".
        if (d.CassiaRssi.Count == 0)
        {
            var keep = (d.AssignedCassia ?? d.BestCassia ?? "").Trim();
            result.Add(new AssignmentPlanItem(mac, keep, "no RSSI"));
            AddPlanned(keep);
            continue;
        }

        if (TryChooseCassiaForUpdate(d, planned, out var chosen, out var reason))
        {
            result.Add(new AssignmentPlanItem(mac, chosen, reason));
            AddPlanned(chosen);
        }
        else
        {
            // No eligible Cassia (RSSI < threshold). Keep current assignment so we don't propose illegal moves.
            var keep = (d.AssignedCassia ?? "").Trim();
            result.Add(new AssignmentPlanItem(mac, keep, reason));
            AddPlanned(keep);
        }
    }

    return result;
}

    private Dictionary<string, int> GetCurrentGroupCounts(int group)
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var dev in _devices)
        {
            if (IsDoneForBalancing(dev)) continue;
            var cassia = (dev.AssignedCassia ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cassia)) continue;
            if (GetGroupForModel(dev.SensorModel) != group) continue;
            dict[cassia] = dict.TryGetValue(cassia, out var v) ? v + 1 : 1;
        }
        return dict;
    }

    private Dictionary<string, int> GetCurrentModelCounts(string model)
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        model = (model ?? "").Trim().ToUpperInvariant();
        foreach (var dev in _devices)
        {
            if (IsDoneForBalancing(dev)) continue;
            var cassia = (dev.AssignedCassia ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cassia)) continue;
            if (!string.Equals((dev.SensorModel ?? "").Trim(), model, StringComparison.OrdinalIgnoreCase)) continue;
            dict[cassia] = dict.TryGetValue(cassia, out var v) ? v + 1 : 1;
        }
        return dict;
    }

    private void RecalculateAssignmentCounts()
    {
        // Reset
        foreach (var gw in CassiaGateways)
        {
            gw.AssignedP41 = 0;
            gw.AssignedP42 = 0;
            gw.AssignedP46 = 0;
            gw.AssignedP47 = 0;
            gw.AssignedP48 = 0;
        }

        foreach (var dev in _devices)
        {
            if (IsDoneForBalancing(dev)) continue;
            var cassia = (dev.AssignedCassia ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cassia)) continue;

            var gw = CassiaGateways.FirstOrDefault(g => g.Name.Equals(cassia, StringComparison.OrdinalIgnoreCase));
            if (gw == null) continue;

            var model = (dev.SensorModel ?? "").Trim().ToUpperInvariant();
            switch (model)
            {
                case "P41": gw.AssignedP41++; break;
                case "P42": gw.AssignedP42++; break;
                case "P46": gw.AssignedP46++; break;
                case "P47": gw.AssignedP47++; break;
                case "P48": gw.AssignedP48++; break;
            }
        }
    }

    [RelayCommand]
    private void ReassignDevices()
    {
        // If some devices are checked, only reassign those. Otherwise reassign all.
        var checkedDevices = _devices.Where(d => d != null && d.IsSelected).ToList();
        var targets = checkedDevices.Count > 0 ? checkedDevices : _devices.ToList();

        // Clear assignment for targets (except devices already queued/programming).
        foreach (var dev in targets)
        {
            if (dev == null) continue;
            if (IsDeviceInWork(dev)) continue;
            dev.AssignedCassia = "";
        }

        foreach (var dev in targets.OrderBy(d => d.SensorModel).ThenBy(d => d.Mac, StringComparer.OrdinalIgnoreCase))
            EnsureStickyAssignment(dev);

        RecalculateAssignmentCounts();
        RequestDevicesRefresh();
    }

    private bool TryGetPreferredCassiaFromLatestFailure(string mac, out string cassia, out DateTimeOffset whenLocal)
    {
        cassia = "";
        whenLocal = DateTimeOffset.MinValue;
        mac = (mac ?? "").Trim();
        if (mac.Length == 0) return false;

        // Find the latest log group we have for this MAC.
        UpgradeLogGroup? latest = null;
        foreach (var g in UpgradeLogGroups)
        {
            if (g == null) continue;
            var gMac = (g.Mac ?? "").Trim();
            if (gMac.Length == 0) gMac = (g.LatestMac ?? "").Trim();
            if (gMac.Length == 0) continue;
            if (!gMac.Equals(mac, StringComparison.OrdinalIgnoreCase)) continue;

            if (latest == null || g.LastTimeLocal > latest.LastTimeLocal)
                latest = g;
        }

        if (latest == null) return false;
        if (!latest.ContainsCompletionFailed) return false;

        var c = (latest.Cassia ?? "").Trim();
        if (c.Length == 0) return false;

        cassia = c;
        whenLocal = latest.LastTimeLocal;
        return true;
    }

    private static string NormalizeDetectorModel(string? value)
    {
        var s = (value ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(s)) return "";

        // Accept full short descriptions like M42MR / P42-LR and collapse to core model P42.
        var m = Regex.Match(s, @"^([PM]\d{2})", RegexOptions.IgnoreCase);
        if (m.Success)
            s = m.Groups[1].Value.ToUpperInvariant();

        if (s.StartsWith("M", StringComparison.Ordinal))
            s = "P" + s.Substring(1);
        if (s == "P49")
            s = "P46";

        return s;
    }

    private string ResolveDetectorTypeForMac(string mac, string fallback)
    {
        mac = (mac ?? "").Trim();
        var model = NormalizeDetectorModel(fallback);
        if (!string.IsNullOrWhiteSpace(model)) return model;

        var dev = _devices.FirstOrDefault(d => d != null && string.Equals((d.Mac ?? "").Trim(), mac, StringComparison.OrdinalIgnoreCase));
        model = NormalizeDetectorModel(dev?.SensorModel);
        if (!string.IsNullOrWhiteSpace(model)) return model;

        if (!string.IsNullOrWhiteSpace(dev?.ProductNumber) && _productToModel.TryGetValue(dev.ProductNumber, out var m2))
            model = NormalizeDetectorModel(m2);

        return string.IsNullOrWhiteSpace(model) ? "" : model;
    }

    /// <summary>
    /// Queue + publish start-update immediately.
    /// Status becomes "Requested update" and we wait for tele/progress to mark it really queued.
    /// </summary>
    private static bool IsDaliMasterModel(string? model)
    {
        model = NormalizeDetectorModel(model);
        return model == "P47" || model == "P48";
    }

    private async Task AutoAdjustParallelProgrammersAsync()
    {
        if (!AutoSetWorkersByModelEnabled)
            return;

        try
        {
            // Rule:
            // - If ALL active (non-done) queue items are DALI masters (P47/P48) => 4 workers
            // - Otherwise => 2 workers
            var active = QueueItems.Where(q => q != null && !q.IsDone).ToList();
            var known = active.Where(q => !string.IsNullOrWhiteSpace(q.DetectorType)).ToList();

            var desired = 2;
            if (known.Count > 0 && known.All(q => IsDaliMasterModel(q.DetectorType)))
                desired = 4;

            if (desired == _lastAutoParallelProgrammersSent && _lastAutoParallelProgrammersSent != int.MinValue)
                return;

            _lastAutoParallelProgrammersSent = desired;

            foreach (var gw in CassiaGateways.ToList())
            {
                if (gw == null || string.IsNullOrWhiteSpace(gw.Name)) continue;
                if (!gw.State.Equals("online", StringComparison.OrdinalIgnoreCase)) continue;

                gw.ParallelProgrammersDesired = desired;
                await SetParallelProgrammersAsync(gw.Name, desired).ConfigureAwait(false);
            }
        }
        catch
        {
            // best-effort; do not block queueing UX
        }
    }

    internal async Task QueueDeviceAndRequestForceAsync(DiscoveredDevice? device)
    {
        if (device == null) return;
        await QueueDeviceAndRequestAsync(device, forceUpdateOverride: true);
    }

    private async Task QueueDeviceAndRequestAsync(
        DiscoveredDevice d,
        bool? forceUpdateOverride = null,
        DetectorSettingsPatchModel? detectorSettings = null)
    {
        if (d == null || string.IsNullOrWhiteSpace(d.Mac))
            return;

        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        // Determine model
        var model = NormalizeDetectorModel(d.SensorModel);
        if (string.IsNullOrWhiteSpace(model))
        {
            // try derive from product number if present
            if (!string.IsNullOrWhiteSpace(d.ProductNumber) && _productToModel.TryGetValue(d.ProductNumber, out var m2))
                model = NormalizeDetectorModel(m2);
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            ConnectionStatus = "Cannot queue: detector model (P4x) is unknown for " + d.Mac;
            try { MessageBox.Show($"Cannot queue {d.Mac} because detector model (P4x) could not be resolved.\n\nMake sure the device has a SensorModel/ProductNumber, or refresh discovery.", "Unknown model", MessageBoxButton.OK, MessageBoxImage.Warning); } catch { }
            return;
        }

        // Determine firmware from dropdown selection
        var fw = GetFirmwareForModel(model);


        // Guard: firmware must look like a version (v02.xx). If not, don't accidentally send a model string.
        if (!string.IsNullOrWhiteSpace(fw) && !fw.Trim().StartsWith("v", StringComparison.OrdinalIgnoreCase))
            fw = "";

        // If the latest upgrade attempt for this MAC failed, prefer using the SAME Cassia again
        // (it likely holds the settings backup). We only deviate if the device is not within reach
        // and the user explicitly chooses to use the currently suggested Cassia instead.
        var preferredFailureCassia = "";
        DateTimeOffset preferredFailureWhen = DateTimeOffset.MinValue;
        var hasPreferredFailureCassia = TryGetPreferredCassiaFromLatestFailure(d.Mac, out preferredFailureCassia, out preferredFailureWhen);

        
    // Determine Cassia for update:
    //  - If strongest RSSI is < RssiAllowBalancingThreshold: ALWAYS use the closest Cassia (highest RSSI).
    //  - If strongest RSSI is >= RssiAllowBalancingThreshold: allow load-balancing (queue+programming+assigned), but still prefer the closest on ties.
    //  - Device should ALWAYS be queueable. If strongest RSSI is < RssiWarnQueueThreshold: show a warning before queueing.
    var cassia = (d.AssignedCassia ?? "").Trim();

    string bestCassia = "";
    int bestRssi = int.MinValue;

    if (d.CassiaRssi.Count > 0)
{
    var best = d.CassiaRssi
        .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
        .OrderByDescending(kv => kv.Value)
        .FirstOrDefault();

    bestCassia = (best.Key ?? "").Trim();
    bestRssi = best.Value;

    if (!_suppressWeakRssiPrompt && bestRssi < RssiWarnQueueThreshold)
    {
        var res = MessageBox.Show(
            $"Warning: Weak RSSI for {d.Mac} (best={bestCassia}:{bestRssi} dBm).\n\n" +
            $"The device is below {RssiWarnQueueThreshold} dBm, which can cause failures.\n\n" +
            "Do you still want to queue it?",
            "Weak RSSI",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (res != MessageBoxResult.Yes)
        {
            ConnectionStatus = $"Not queued: {d.Mac} (weak RSSI)";
            return;
        }
    }

    // Under the balancing threshold -> always closest Cassia.
    if (!string.IsNullOrWhiteSpace(bestCassia) && bestRssi < RssiAllowBalancingThreshold)
    {
        cassia = bestCassia;
    }
}

    // If still no cassia chosen, try to keep sticky (only if it has strong RSSI), else balance.
    if (string.IsNullOrWhiteSpace(cassia))
{
    // Validate sticky assignment against threshold when we have RSSI readings.
    var sticky = (d.AssignedCassia ?? "").Trim();
    if (!string.IsNullOrWhiteSpace(sticky) && d.CassiaRssi.Count > 0)
    {
        if (d.CassiaRssi.TryGetValue(sticky, out var stickyRssi) && stickyRssi >= RssiAllowBalancingThreshold)
            cassia = sticky;
    }

    if (string.IsNullOrWhiteSpace(cassia))
    {
        if (d.CassiaRssi.Count > 0)
        {
            // Strong RSSI case: balance among eligible Cassias.
            if (!TryChooseCassiaForUpdate(d, plannedLoad: null, out cassia, out _))
            {
                // Fallback: closest Cassia (if any)
                cassia = bestCassia;
            }
        }
        else
        {
            // Fallback: no RSSI at all -> pick first online Cassia to avoid blocking.
            cassia = CassiaGateways.FirstOrDefault(g => string.Equals(g.State, "online", StringComparison.OrdinalIgnoreCase))?.Name
                     ?? CassiaGateways.FirstOrDefault()?.Name
                     ?? "";
        }
    }
}

        // Apply preferred failure Cassia (sticky) if relevant.
        if (hasPreferredFailureCassia)
        {
            var pref = (preferredFailureCassia ?? "").Trim();
            if (pref.Length > 0 && !string.Equals(pref, cassia, StringComparison.OrdinalIgnoreCase))
            {
                // Consider "within reach" if we have a reading and it's not extremely weak.
                var prefHasRssi = d.CassiaRssi.TryGetValue(pref, out var prefRssi);
                var withinReach = prefHasRssi && prefRssi >= RssiWarnQueueThreshold;

                if (withinReach)
                {
                    cassia = pref;
                }
                else
                {
                    var prefRssiTxt = prefHasRssi ? $"{prefRssi} dBm" : "(no RSSI)";
                    var suggested = cassia;

                    var res = MessageBox.Show(
                        $"This device previously FAILED on {pref} ({preferredFailureWhen.ToLocalTime():yyyy-MM-dd HH:mm:ss}).\n" +
                        $"We should ideally use the same Cassia again because it likely has the settings backup.\n\n" +
                        $"But RSSI to {pref} is {prefRssiTxt}, so it may be out of reach.\n\n" +
                        $"Use {pref} anyway?\n\n" +
                        $"Yes = use {pref} (sticky)\n" +
                        $"No = use suggested {suggested}",
                        "Reuse Cassia for failed device",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (res == MessageBoxResult.Yes)
                        cassia = pref;
                }
            }
        }

    if (string.IsNullOrWhiteSpace(cassia))
{
    ConnectionStatus = "No Cassia gateway known yet (cannot send start-update)";
    return;
}

    // Create/update queue item
        var qi = QueueItems.FirstOrDefault(q => q.Mac.Equals(d.Mac, StringComparison.OrdinalIgnoreCase));
        var wasAlreadyInQueue = (qi != null);
        if (qi == null)
        {
            qi = new QueueItem
            {
                Mac = d.Mac,
                Command = DefaultCommand
            };
            QueueItems.Add(qi);
        }

        qi.Cassia = cassia;
        qi.DetectorType = model;          // payload DetectorType
        qi.FirmwareVersion = fw;          // payload FirmwareVersion
        qi.Command = DefaultCommand;      // start-update
        qi.Status = "Requested update";
        qi.Progress = 0;
        qi.Notes = "";
        qi.LastUpdateUtc = DateTimeOffset.UtcNow;

        UpdateQueueRssiForMac(d.Mac);

        // Mirror into discovered list immediately
        MirrorQueueToDevice(qi);

        RequestQueueRefresh();

        // Publish request
        var topic = CommandTopicTemplate
            .Replace("{networkId}", NetworkId)
            .Replace("{cassia}", cassia)
            .Replace("{command}", DefaultCommand);

        if (ProductionUpdateEnabled)
        {
            var runtimePayload = BuildProductionUpdateRuntimePayload();
            await SetRuntimeForCassiaAsync(cassia, runtimePayload).ConfigureAwait(false);
        }

        var forceUpdate = forceUpdateOverride ?? ForceUpdateEnabled;
        var normalizedDetectorSettings = detectorSettings?.CloneNormalized();
        if (normalizedDetectorSettings != null && !normalizedDetectorSettings.HasAnyValue)
            normalizedDetectorSettings = null;
        if (normalizedDetectorSettings == null)
        {
            if (!TryResolveModelProfilePatch(model, out normalizedDetectorSettings, out var profileError)
                && !string.IsNullOrWhiteSpace(profileError))
            {
                qi.Status = "Error";
                qi.Notes = profileError;
                qi.LastUpdateUtc = DateTimeOffset.UtcNow;
                MirrorQueueToDevice(qi);
                RequestQueueRefresh();
                ConnectionStatus = profileError;
                return;
            }
        }

        var payload = new[]
        {
            new
            {
                DetectorType = model,
                FirmwareVersion = fw,
                MacAddress = d.Mac,
                Pincode = "",
                forceUpdate,
                DetectorSettings = normalizedDetectorSettings
            }
        };

        // Before queueing: send disconnect to /all to ensure no gateway is stuck on this device.
        // Only do this if the MAC wasn't already present in our queue list (avoid spamming disconnect).
        if (!wasAlreadyInQueue)
        {
            try
            {
                await _mqtt.PublishJsonAsync(BuildCmdTopic("all", "disconnect"),
                    new { sensors = new[] { d.Mac } },
                    retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            }
            catch { /* best-effort */ }
        }

        AppendQueuedMacToNotes(d.Mac);


        try
        {
            await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token);

            // Keep "Requested update" until we see tele/progress for that MAC.
            qi.LastUpdateUtc = DateTimeOffset.UtcNow;
            MirrorQueueToDevice(qi);
            RequestQueueRefresh();

            // IMPORTANT: ask the Cassia for its queue/programming snapshot shortly after queuing.
            // This is the authoritative "accepted" confirmation (tele/queue-list & tele/programming-list).
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(600, _appCts.Token).ConfigureAwait(false);
                    await RequestQueueListAsync(cassia).ConfigureAwait(false);
                    await RequestProgrammingListAsync(cassia).ConfigureAwait(false);
                }
                catch { }
            });

            // Auto-tune parallel programmers after queueing.
            await AutoAdjustParallelProgrammersAsync().ConfigureAwait(false);

        }
        catch (Exception ex)
        {
            qi.Status = "Error";
            qi.Notes = "Publish failed: " + ex.Message;
            qi.LastUpdateUtc = DateTimeOffset.UtcNow;
            MirrorQueueToDevice(qi);
            RequestQueueRefresh();
        }
    }

    [RelayCommand]
    private async Task StartQueueAsync()
    {
        // Optional “re-send” for items that are still not picked up.
        if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
        if (QueueItems.Count == 0) return;

        foreach (var item in QueueItems)
        {
            if (item.Status.Equals("Done", StringComparison.OrdinalIgnoreCase)) continue;

            var dev = _devices.FirstOrDefault(d => d.Mac.Equals(item.Mac, StringComparison.OrdinalIgnoreCase));
            if (dev == null) continue;

            await QueueDeviceAndRequestAsync(dev);
        }
    }


    private void AppendQueuedMacToNotes(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
            return;

        // Always append at the end, on its own line, with timestamp.
        // Keep whatever the user has written above intact.
        var t = NotesText ?? string.Empty;

        if (t.Length > 0 && !t.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            t += Environment.NewLine;

        t += $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} -> {mac.Trim()}{Environment.NewLine}";
        NotesText = t;
    }

    private DiscoveredDevice EnsureDeviceExistsForProgress(string mac)
    {
        // IMPORTANT: Do NOT create new "discovered devices" from progress/logs.
        // Only the scan/discovered feed may add devices to the device list.
        var dev = _devices.FirstOrDefault(d => d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
        if (dev != null) return dev;

        return new DiscoveredDevice { Mac = mac };
    }

    private void MirrorQueueToDevice(QueueItem qi)
    {
        if (qi == null || string.IsNullOrWhiteSpace(qi.Mac)) return;

        // Always update cache
        var cs = GetOrCreateCache(qi.Mac);
        cs.ProcessStatus = qi.Status ?? "";
        cs.ProcessProgress = qi.Progress;
        cs.ProcessCassia = qi.Cassia ?? "";
        cs.ProcessFirmware = qi.FirmwareVersion ?? "";
        if (!string.IsNullOrWhiteSpace(qi.ChipUsed))
            cs.ChipUsed = qi.ChipUsed;
        else if (!string.IsNullOrWhiteSpace(cs.ChipUsed))
            qi.ChipUsed = cs.ChipUsed;
        cs.LastUpdateUtc = qi.LastUpdateUtc;

        // Mark queue state for row coloring (ignore items that have been 100% for > 1 minute)
        var doneExpired = qi.Progress >= 100 && (DateTimeOffset.UtcNow - qi.LastUpdateUtc) > TimeSpan.FromMinutes(1);
        cs.IsInQueue = !qi.IsDone && !doneExpired;

        var dev = _devices.FirstOrDefault(d => d.Mac.Equals(qi.Mac, StringComparison.OrdinalIgnoreCase));
        if (dev == null) return;

        dev.ProcessStatus = cs.ProcessStatus;
        dev.ChipUsed = cs.ChipUsed;
        dev.ProcessProgress = cs.ProcessProgress;
        dev.ProcessCassia = cs.ProcessCassia;
        // When a device is queued/programming, force AssignedCassia to the gateway currently handling it
        if (!string.IsNullOrWhiteSpace(dev.ProcessCassia) && dev.IsInQueue)
            dev.AssignedCassia = dev.ProcessCassia;
        dev.ProcessFirmware = cs.ProcessFirmware;
        dev.ProcessLastUpdateUtc = cs.LastUpdateUtc;

        dev.IsInQueue = cs.IsInQueue;

        // When a device is queued/programming, it is no longer "successful" from a previous run.
        // Clear result flags so row coloring always prefers queue state.
        if (dev.IsInQueue)
        {
            cs.IsUpgradeSuccess = false;
            cs.IsUpgradeFailed = false;
            cs.IsUpgradeWarn = false;
            cs.IsUpgradeNoFwRead = false;
            cs.LastUpgradeSuccessUtc = null;
            cs.LastTargetFw = "";

            dev.IsUpgradeSuccess = false;
            dev.IsUpgradeFailed = false;
            dev.IsUpgradeWarn = false;
            dev.IsUpgradeNoFwRead = false;
        }
    }

    
    

}
