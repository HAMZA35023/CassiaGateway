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


    private readonly HostBleScannerService _hostBleScanner = new("10:B9:F7");
    private readonly ConcurrentDictionary<string, HostBleScannerService.HostBleUpdate> _hostBleLatest = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _hostBleUiTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<string, HostBleScanItem> _hostBleRowsByMac = new(StringComparer.OrdinalIgnoreCase);

    // Identify state per MAC (driven by tele/.../identify stages).
    // - Pending: user clicked Identify but we haven't received 'logged-in'/'login-skipped-bootmode' yet (UI pulses)
    // - Ready: we've received 'logged-in' or 'login-skipped-bootmode' for the current request
    // - Active: logged-in..holding.. until disconnected/failed
    private readonly ConcurrentDictionary<string, bool> _identifyPendingByMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _identifyConnectedByMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _identifyActiveByMac = new(StringComparer.OrdinalIgnoreCase);

    public event Action? RequestClearHostBleSelection;

    // ---- UI update cadence (throttled at MQTT client level) ----
    // Progress updates are emitted every 5 seconds, discovered every 15 seconds.
    // We show countdowns so users understand why numbers/statuses are not "live" per message.
    private readonly DispatcherTimer _uiCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _progressCountdownSec = 5;
    private int _discoveredCountdownSec = 15;


    // Suppress repeated weak-RSSI warnings during bulk/batch queue operations
    private bool _suppressWeakRssiPrompt;
    [ObservableProperty] private string progressUiCountdownText = "Progress UI update in 5s";
    [ObservableProperty] private string discoveredUiCountdownText = "Discovered UI update in 15s";


    // ---- Firmware manifest (tele/.../fw-manifest) ----
    private readonly DispatcherTimer _fwManifestValidateTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _fwManifestTimeoutTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly DispatcherTimer _notesAutosaveTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _fwManifestTimeoutArmed;
    private string _lastFwManifestMissingHash = "";

    // After each connect we wait for per-gateway status, then request its FW manifest once.
    private readonly HashSet<string> _fwManifestRequestedForGw = new(StringComparer.OrdinalIgnoreCase);
    // After each connect we request queue/programming/parallel-programmers once per gateway.
    private readonly HashSet<string> _runtimeStateRequestedForGw = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, RuntimeVariableValue>> _runtimeVarsByGw = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource<IReadOnlyDictionary<string, RuntimeVariableValue>>> _runtimeVarsPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _runtimeVarsLock = new();
    private readonly HashSet<string> _deviceListRequestedForGw = new(StringComparer.OrdinalIgnoreCase);
    private bool _deviceListRequestedAfterConnect;
    private DateTimeOffset _connectedAtUtc = DateTimeOffset.MinValue;

    // Track last subscribed NetworkId to avoid duplicate resubscribe spam.
    private string _lastSubscribedNetworkId = "";

    // Auto-adjust parallel-programmers based on queue contents
    private int _lastAutoParallelProgrammersSent = int.MinValue;


    private readonly ObservableCollection<DiscoveredDevice> _devices = new();
    private readonly Dictionary<string, DiscoveredDevice> _deviceByMac = new(StringComparer.OrdinalIgnoreCase);

    // Raw discovered devices collection (useful for code-behind context menus)
    public ObservableCollection<DiscoveredDevice> Devices => _devices;

    public ICollectionView FilteredDevices { get; private set; } = null!;

    public ObservableCollection<HostBleScanItem> HostBleDevices { get; } = new();
    public ICollectionView HostBleDevicesView { get; }

    public ObservableCollection<int> HostRssiAverageOptions { get; } = new() { 5, 10, 20, 30, 60 };

    [ObservableProperty] private int hostRssiAverageSeconds = 10;

    public ObservableCollection<int> HostBleUiUpdateOptions { get; } = new() { 2, 5, 10, 20, 30, 60 };

    [ObservableProperty] private int hostBleUiUpdateSeconds = 2;

    [ObservableProperty] private bool hostBleAutoUpdate = true;
// Host BLE model filter
    public ObservableCollection<string> HostBleModelOptions { get; } = new(new[] { "All" });
    [ObservableProperty] private string hostBleModelFilter = "All";

    // Host BLE extra filters
    [ObservableProperty] private bool hostBleHideCompleted;
    [ObservableProperty] private bool hostBleHideInQueue;
    [ObservableProperty] private bool hostBleAutoRemoveStaleDevices = false;
    [ObservableProperty] private string hostBleSearchText = "";

    [ObservableProperty] private string hostBleUiStatusText = "";

    private void InitHostBle()
    {
        // --- Host BLE scanner (PC side) ---
        // We keep the BLE scanner "hot" (updates every second), but throttle UI refreshes to once per 10 seconds
        // to avoid grid churn while the user is interacting.
        _hostBleScanner.WindowSeconds = HostRssiAverageSeconds;
        _hostBleScanner.Updated += u =>
        {
            // Just buffer latest values; UI is updated on a timer.
            _hostBleLatest[u.Mac] = u;
        };

        _hostBleUiTimer.Tick += (_, _) => FlushHostBleToUi();
        UpdateHostBleUiStatus();
        if (HostBleAutoUpdate) _hostBleUiTimer.Start();

        _hostBleScanner.Start();
    }

    partial void OnHostRssiAverageSecondsChanged(int value)
    {
        // Update scanner window immediately.
        try { _hostBleScanner.WindowSeconds = value; } catch { }
    }

    partial void OnHostBleAutoUpdateChanged(bool value)
    {
        try
        {
            _hostBleUiTimer.Stop();
            if (value)
            {
                _hostBleUiTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, HostBleUiUpdateSeconds));
                _hostBleUiTimer.Start();
            }
            UpdateHostBleUiStatus();
        }
        catch { }
    }

    partial void OnConnectionStatusChanged(string value) => AppendStatusBar("MQTT", value);
    partial void OnHostBleUiStatusTextChanged(string value) => AppendStatusBar("Host BLE", value);
    partial void OnLedRangeStatusTextChanged(string value) => AppendStatusBar("LED range", value);

    private void RunOnUi(Action action)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null || disp.CheckAccess())
        {
            action();
            return;
        }

        disp.Invoke(action);
    }

    private void AppendStatusBar(string source, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        StatusBarText = $"{DateTime.Now:HH:mm:ss} {source}: {message}";
    }

    private void OnGatewayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CassiaGateway.TotalSpeedpct)
            || e.PropertyName == nameof(CassiaGateway.State))
        {
            UpdateTotalSpeedStatus();
        }
    }

    private void UpdateTotalSpeedStatus()
    {
        var all = CassiaGateways.Where(g => g != null).ToList();
        var online = all.Where(g => string.Equals(g.State, "online", StringComparison.OrdinalIgnoreCase)).ToList();
        var list = online.Count > 0 ? online : all;

        if (list.Count == 0)
        {
            TotalSpeedStatusText = "Speed: -- %/min";
            return;
        }

        var total = list.Sum(g => g.TotalSpeedpct);
        var optimal = 60.0 * list.Count;
        var pctOfOptimal = optimal > 0 ? (total / optimal) * 100.0 : 0.0;
        TotalSpeedStatusText = $"Speed total: {total:0.#}%/min";
    }

partial void OnHostBleUiUpdateSecondsChanged(int value)
    {
        try
        {
            var v = Math.Max(1, value);
            _hostBleUiTimer.Interval = TimeSpan.FromSeconds(v);
            ResetHostBleUiTimer();
        }
        catch { }
    }

    partial void OnHostBleModelFilterChanged(string value)
    {
        try { HostBleDevicesView.Refresh(); } catch { }
    }

    partial void OnHostBleHideCompletedChanged(bool value)
    {
        try { HostBleDevicesView.Refresh(); } catch { }
    }

    partial void OnHostBleHideInQueueChanged(bool value)
    {
        try { HostBleDevicesView.Refresh(); } catch { }
    }

    partial void OnHostBleSearchTextChanged(string value)
    {
        try { HostBleDevicesView.Refresh(); } catch { }
    }

    partial void OnHostBleAutoRemoveStaleDevicesChanged(bool value)
    {
        if (_isInitializing) return;

        try
        {
            var s = _store.Load();
            _store.Save(BuildSettingsSnapshot(s));
        }
        catch
        {
            // best effort
        }

        try { HostBleRefreshUi(); } catch { }
    }

    [RelayCommand]
    private void HostBleRefreshUi()
    {
        // Manual refresh button.
        FlushHostBleToUi();
        ResetHostBleUiTimer();
    }

    private void ScheduleHostBleUiRefresh(TimeSpan delay)
    {
        try
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(delay).ConfigureAwait(false);
                try
                {
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        try { HostBleRefreshUi(); } catch { }
                    });
                }
                catch { }
            });
        }
        catch { }
    }

    public void ResetHostBleUiTimer()
    {
        // "Freeze" grid churn while the user clicks/reads: next refresh will be N seconds after the last click.
        try
        {
            _hostBleUiTimer.Stop();
            _hostBleUiTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, HostBleUiUpdateSeconds));

            if (HostBleAutoUpdate)
                _hostBleUiTimer.Start();

            UpdateHostBleUiStatus();
        }
        catch { }
    }

    private void UpdateHostBleUiStatus()
    {
        try
        {
            HostBleUiStatusText = HostBleAutoUpdate
                ? ("Auto-update: " + HostBleUiUpdateSeconds + "s")
                : "Auto-update: off";
        }
        catch { }
    }


    private void FlushHostBleToUi()
    {
        // Preserve selection by MAC
        var selectedMac = SelectedHostBleDevice?.Mac;

        var items = _hostBleLatest.Values
            .OrderByDescending(x => x.AvgRssi)
            .ThenBy(x => x.Mac, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Update rows in-place so selection doesn't drop and buttons work without a "first click".
        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in items)
        {
            alive.Add(u.Mac);

            if (!_hostBleRowsByMac.TryGetValue(u.Mac, out var row))
            {
                row = new HostBleScanItem { Mac = u.Mac };
                _hostBleRowsByMac[u.Mac] = row;
                HostBleDevices.Add(row);
            }

            row.AvgHostRssi = u.AvgRssi;
            row.LastSeenUtc = u.LastSeenUtc;

            // Identify button state
            row.IsIdentifyPending = _identifyPendingByMac.TryGetValue(u.Mac, out var ip) && ip;
            row.IsIdentifying = _identifyActiveByMac.TryGetValue(u.Mac, out var ia) && ia;

            // Merge Cassia RSSIs + closest + current FW from discovered device list (if present)
            var d = FindDiscoveredDevice(u.Mac);
            if (d != null)
            {
                row.SetCassiaRssi(d.CassiaRssi);
                row.ClosestCassia = d.BestCassia ?? "";
                row.SensorModel = (d.SensorModel ?? "").Trim();

                // Only update Host-BLE CurrentFw if it came from Get FW
                if (_cachedStatusByMac.TryGetValue(u.Mac, out var cs) && cs.CurrentFwFromGetFw)
                    row.CurrentFw = cs.CurrentFw ?? "";
                // else: keep existing row.CurrentFw (do NOT overwrite from logs/identify/scan)

                // Coloring semantics (same as device list)
                row.IsInQueue = d.IsInQueue;
                row.IsUpgradeSuccess = d.IsUpgradeSuccess;
                row.IsUpgradeWarn = d.IsUpgradeWarn;
                row.IsUpgradeNoFwRead = d.IsUpgradeNoFwRead;
                row.IsUpgradeFailed = d.IsUpgradeFailed;
            }
            else
            {
                row.SetCassiaRssi(null);
                row.ClosestCassia = "";
                row.SensorModel = row.SensorModel; // keep previous if any
                // keep CurrentFw (so it doesn't flicker empty if host sees adv before MQTT sees device)

                // Default flags when unknown
                row.IsInQueue = false;
                row.IsUpgradeSuccess = false;
                row.IsUpgradeWarn = false;
                row.IsUpgradeNoFwRead = false;
                row.IsUpgradeFailed = false;
            }
        }

        if (HostBleAutoRemoveStaleDevices)
        {
            // Optional: remove rows not present in latest set (keeps grid tight)
            for (int i = HostBleDevices.Count - 1; i >= 0; i--)
            {
                var r = HostBleDevices[i];
                if (r == null) continue;
                if (!alive.Contains(r.Mac))
                {
                    HostBleDevices.RemoveAt(i);
                    _hostBleRowsByMac.Remove(r.Mac);
                }
            }
        }

        // Restore selection
        if (!string.IsNullOrWhiteSpace(selectedMac))
        {
            var sel = HostBleDevices.FirstOrDefault(x => string.Equals(x.Mac, selectedMac, StringComparison.OrdinalIgnoreCase));
            if (sel != null)
                SelectedHostBleDevice = sel;
        }

        try { HostBleDevicesView.Refresh(); } catch { }

        // Refresh model filter options (All + distinct models + Unknown)
        try
        {
            var models = HostBleDevices
                .Where(x => x != null)
                .Select(x => (x.SensorModel ?? "").Trim())
                .Select(m => string.IsNullOrWhiteSpace(m) ? "Unknown" : m)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var wanted = new List<string> { "All" };
            wanted.AddRange(models);

            // keep current selection if possible
            var current = (HostBleModelFilter ?? "All").Trim();

            HostBleModelOptions.Clear();
            foreach (var m in wanted) HostBleModelOptions.Add(m);

            if (HostBleModelOptions.Any(x => x.Equals(current, StringComparison.OrdinalIgnoreCase)))
                HostBleModelFilter = HostBleModelOptions.First(x => x.Equals(current, StringComparison.OrdinalIgnoreCase));
            else
                HostBleModelFilter = "All";
        }
        catch { }
    }


    private async Task IdentifyHost(HostBleScanItem? item)
    {
        item ??= SelectedHostBleDevice;
        if (item == null || string.IsNullOrWhiteSpace(item.Mac))
            return;

        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        var mac = item.Mac.Trim();
        var target = string.IsNullOrWhiteSpace(item.ClosestCassia) ? "all" : item.ClosestCassia.Trim();

        // New identify command (2026-01): supports pincode/seconds/maxConnectAttempts/requestId and reports stages on tele/.../identify.
        var requestId = Guid.NewGuid().ToString("N");
        var topic = BuildCmdTopic(target, "identify");
        var payload = new
        {
            sensors = new[] { mac },
            pincode = DefaultPincode,
            seconds = 15,
            maxConnectAttempts = 1,
            requestId
        };

        try
        {
            // UI: pulse until we receive 'logged-in' (or 'login-skipped-bootmode'), then turn green until disconnected/failed.
            _identifyPendingByMac[mac] = true;
            _identifyConnectedByMac[mac] = false;
            _identifyActiveByMac[mac] = false;

            item.IsIdentifyPending = true;
            item.IsIdentifying = false;

            await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token);

            await AutoAdjustParallelProgrammersAsync().ConfigureAwait(false);
            ConnectionStatus = $"Identify ({requestId}) sent to {target} for {mac}";
        }
        catch (Exception ex)
        {
            _identifyPendingByMac[mac] = false;
            _identifyConnectedByMac[mac] = false;
            _identifyActiveByMac[mac] = false;

            item.IsIdentifyPending = false;
            item.IsIdentifying = false;
            ConnectionStatus = "Identify failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task StartLedRangeVisualization()
    {
        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        var target = string.IsNullOrWhiteSpace(LedRangeCassia) ? "all" : LedRangeCassia.Trim();
        var requestId = Guid.NewGuid().ToString("N");
        var selectedModel = (SelectedLedRangeModel ?? "All").Trim();
        var models = string.Equals(selectedModel, "All", StringComparison.OrdinalIgnoreCase)
            ? Array.Empty<string>()
            : new[] { selectedModel };

        LedRangeConnectedDevices.Clear();
        LedRangeFailedDevices.Clear();
        LedRangeRequestedTotal = 0;
        LedRangeTriedCount = 0;
        LedRangeConnectedCount = 0;
        LedRangeFailedCount = 0;
        LedRangeProgressPercent = 0;
        LedRangeProgressText = "0 / 0 tried";
        LedRangeStatusText = $"Starting on {target}...";

        await SendLedRangeVisualizeRequestAsync(target, requestId, models, null).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task DisconnectLedRangeSession()
    {
        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        var target = string.IsNullOrWhiteSpace(LedRangeCassia) ? "all" : LedRangeCassia.Trim();
        var requestId = Guid.NewGuid().ToString("N");
        var payload = new { requestId, forceAll = false };

        try
        {
            await _mqtt.PublishJsonAsync(BuildCmdTopic(target, "led-range-disconnect"), payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            ConnectionStatus = $"LED range disconnect requested on {target} ({requestId})";
            LedRangeStatusText = $"Disconnect requested on {target} ({requestId})";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"LED range disconnect failed: {ex.Message}";
            LedRangeStatusText = $"Disconnect request failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ForceDisconnectAllLedRangeSession()
    {
        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        var target = string.IsNullOrWhiteSpace(LedRangeCassia) ? "all" : LedRangeCassia.Trim();
        var requestId = Guid.NewGuid().ToString("N");
        var payload = new { requestId, forceAll = true };

        try
        {
            await _mqtt.PublishJsonAsync(BuildCmdTopic(target, "led-range-disconnect"), payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            ConnectionStatus = $"LED range force-disconnect requested on {target} ({requestId})";
            LedRangeStatusText = $"Force disconnect requested on {target} ({requestId})";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"LED range force-disconnect failed: {ex.Message}";
            LedRangeStatusText = $"Force disconnect request failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RetryFailedLedRangeDevices()
    {
        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        var failedMacs = LedRangeFailedDevices
            .Select(x => (x.Mac ?? "").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (failedMacs.Count == 0)
        {
            LedRangeStatusText = "No failed devices to retry.";
            return;
        }

        var target = string.IsNullOrWhiteSpace(LedRangeCassia) ? "all" : LedRangeCassia.Trim();
        var requestId = Guid.NewGuid().ToString("N");
        var selectedModel = (SelectedLedRangeModel ?? "All").Trim();
        var models = string.Equals(selectedModel, "All", StringComparison.OrdinalIgnoreCase)
            ? Array.Empty<string>()
            : new[] { selectedModel };

        LedRangeTriedCount = 0;
        LedRangeConnectedCount = 0;
        LedRangeFailedCount = 0;
        LedRangeProgressPercent = 0;
        LedRangeProgressText = $"0 / {failedMacs.Count} tried";
        LedRangeRequestedTotal = failedMacs.Count;
        LedRangeStatusText = $"Retrying {failedMacs.Count} failed devices...";

        await SendLedRangeVisualizeRequestAsync(target, requestId, models, failedMacs).ConfigureAwait(false);
    }

    private async Task SendLedRangeVisualizeRequestAsync(string target, string requestId, IReadOnlyList<string> models, IReadOnlyList<string>? sensors)
    {
        var payload = new
        {
            requestId,
            pincode = string.IsNullOrWhiteSpace(LedRangePincode) ? null : LedRangePincode.Trim(),
            minRssi = LedRangeMinRssi,
            models,
            sensors,
            maxConnectAttempts = Math.Max(3, LedRangeMaxConnectAttempts),
            useBothChips = true
        };

        try
        {
            await _mqtt.PublishJsonAsync(BuildCmdTopic(target, "led-range-visualize"), payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            ConnectionStatus = $"LED range visualize requested on {target} ({requestId})";
            LedRangeStatusText = $"Requested on {target} ({requestId})";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"LED range request failed: {ex.Message}";
            LedRangeStatusText = $"Failed to request: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task AddToQueueHost(HostBleScanItem? item)
    {
        // Backwards-compat command name. UI uses UpdateHost.
        await UpdateHost(item);
    }

    [RelayCommand]
    private async Task UpdateHost(HostBleScanItem? item)
    {
        item ??= SelectedHostBleDevice;
        if (item == null || string.IsNullOrWhiteSpace(item.Mac))
            return;

        var mac = item.Mac.Trim();
        // UX: clear selection immediately when pressing Update (even if user clicked the button on an unselected row).
        try { RequestClearHostBleSelection?.Invoke(); } catch { }

        // Reuse discovered device object if present; otherwise create a minimal one.
        var dev = FindDiscoveredDevice(mac);
        if (dev == null)
        {
            dev = new DiscoveredDevice
            {
                Mac = mac,
                Name = "BLE (host scan)",
            };
            _devices.Add(dev);
            _deviceByMac[mac] = dev;
        }

        // IMPORTANT: Host BLE scan rows keep per-Cassia RSSIs in HostBleScanItem.CassiaRssi.
        // The balancing algorithm uses DiscoveredDevice.CassiaRssi, so we must sync them here.
        // Otherwise the algorithm only sees the "closest" Cassia and cannot choose a free alternative.
        if (item.CassiaRssi != null && item.CassiaRssi.Count > 0)
        {
            dev.CassiaRssi.Clear();
            foreach (var kv in item.CassiaRssi)
            {
                var k = (kv.Key ?? "").Trim();
                if (k.Length == 0) continue;
                dev.CassiaRssi[k] = kv.Value;
            }

            // Refresh best Cassia/RSSI fields.
            var best = dev.CassiaRssi.OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(best.Key))
            {
                dev.BestCassia = best.Key.Trim();
                dev.BestRssi = best.Value;
            }
        }

        // Suggested Cassia: use the SAME algorithm as Add-to-queue/Reassign.
        // - If best RSSI < -65 => closest Cassia.
        // - If best RSSI >= -65 => balance using Cassia reported Queue/Programming, but still prefer closest when similar.
        if (!TryChooseCassiaForUpdate(dev, plannedLoad: null, out var suggested, out var suggestedReason))
        {
            suggested = (item.ClosestCassia ?? "").Trim();
            if (suggested.Length == 0) suggested = (dev.BestCassia ?? "").Trim();
            if (suggested.Length == 0) suggested = (dev.AssignedCassia ?? "").Trim();
            suggestedReason = "closest fallback";
        }

        var current = (dev.AssignedCassia ?? "").Trim();
        if (current.Length == 0) current = (dev.BestCassia ?? "").Trim();

        int GetRssiNormalized(string c)
        {
            c = (c ?? "").Trim();
            if (c.Length == 0) return int.MinValue;
            foreach (var kv in dev.CassiaRssi)
            {
                var k = (kv.Key ?? "").Trim();
                if (string.Equals(k, c, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
            return int.MinValue;
        }

        var closestCassia = (dev.BestCassia ?? suggested).Trim();
        var closestRssi = GetRssiNormalized(closestCassia);
        var suggestedRssi = GetRssiNormalized(suggested);

        // If weak (< -70) and user hasn't suppressed warnings, warn before queueing (single-device case).
        if (!_suppressWeakRssiPrompt && suggestedRssi != int.MinValue && suggestedRssi < RssiWarnQueueThreshold)
        {
            var res = MessageBox.Show(
                $"Warning: weak RSSI (< {RssiWarnQueueThreshold} dBm) for {mac}.\n\n" +
                $"Suggested: {suggested} @ {suggestedRssi} dBm\n\nQueue anyway?",
                "Weak RSSI",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes)
                return;
        }

        var rows = new ObservableCollection<AssignmentChangeRow>
        {
            new AssignmentChangeRow
            {
                Mac = mac,
                ClosestCassia = closestCassia,
                ClosestRssi = closestRssi == int.MinValue ? 0 : closestRssi,
                CurrentAssigned = current,
                SuggestedAssigned = suggested.Length == 0 ? current : suggested,
                SuggestedRssi = suggestedRssi == int.MinValue ? 0 : suggestedRssi,
                Reason = suggestedReason
            }
        };

        var loadRows = BuildLoadSummaryForPlannedAdds(rows);

        var dlg = ShowAssignmentPlanDialog(
            title: "Update device",
            subtitle: $"{mac} - choose Cassia assignment before queueing",
            rows: rows,
            loadRows: loadRows,
            footer: "Apply = set suggested Cassia and queue update - Keep current = queue without changing assignment",
            notes: $"Suggestion uses the same balancing rules as Add-to-queue/Reassign.\n" +
                   $"If best RSSI < {RssiAllowBalancingThreshold}: closest Cassia. Otherwise: balance using Queue/Programming/Workers (free worker slots reduce effective load), while still preferring closest when similar.",
            showKeepButton: true);

        if (dlg == AssignmentPlanDialogResult.Cancel)
            return;

        if (dlg == AssignmentPlanDialogResult.Apply)
        {
            var chosen = (rows.FirstOrDefault()?.SuggestedAssigned ?? "").Trim();
            if (chosen.Length > 0)
            {
                dev.BestCassia = chosen;
                dev.AssignedCassia = chosen;
            }
        }

        // Queue + request FW on the chosen/assigned Cassia
        await QueueDeviceAndRequestAsync(dev);
        // UX: refresh Host BLE list shortly after Update so queued/assigned status & FW request results show up.
        ScheduleHostBleUiRefresh(TimeSpan.FromSeconds(2));

    }

    [RelayCommand]
    private async Task GetFirmwareHost(HostBleScanItem? item)
    {
        item ??= SelectedHostBleDevice;
        if (item == null || string.IsNullOrWhiteSpace(item.Mac))
            return;

        var mac = item.Mac.Trim();
        var dev = FindDiscoveredDevice(mac);
        if (dev == null)
        {
            dev = new DiscoveredDevice
            {
                Mac = mac,
                Name = "BLE (host scan)",
            };
            _devices.Add(dev);
            _deviceByMac[mac] = dev;
        }

        // Ensure we use the closest Cassia (if known)
        if (!string.IsNullOrWhiteSpace(item.ClosestCassia))
        {
            dev.BestCassia = item.ClosestCassia.Trim();
            dev.AssignedCassia = item.ClosestCassia.Trim();
        }

        await SendGetFwVersionAsync(new[] { dev });
    }
    

}
