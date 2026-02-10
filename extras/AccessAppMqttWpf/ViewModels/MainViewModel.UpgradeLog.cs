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


    // ---- Upgrade log viewer (tele/.../upgrade-log) ----
    // Raw lines (kept for debugging / copy-paste)
    public ObservableCollection<string> UpgradeLogLines { get; } = new();

    // Grouped view by logId
    public ObservableCollection<UpgradeLogGroup> UpgradeLogGroups { get; } = new();
    public ICollectionView UpgradeLogGroupsView { get; private set; } = null!;

    [ObservableProperty] private string upgradeLogText = "";
    [ObservableProperty] private string upgradeLogStatus = "Idle";
    [ObservableProperty] private int upgradeLogTotalLines;
    [ObservableProperty] private int upgradeLogReceivedLines;
    [ObservableProperty] private int upgradeLogUniqueSuccessCount;
    [ObservableProperty] private int upgradeLogUniqueFailedCount;
    [ObservableProperty] private CassiaGateway? selectedLogGateway;


    public ObservableCollection<string> LogGatewayOptions { get; } = new();
    [ObservableProperty] private string selectedLogGatewayName = "All";

    // Search (matches MAC/logId/line; works across all groups)
    [ObservableProperty] private string upgradeLogSearchText = "";


    // Upgrade log view filters
    public ObservableCollection<string> UpgradeLogShowOptions { get; } = new()
    {
        "All",
        "Only failed",
        "Hide success",
        "Only success"
    };

    [ObservableProperty] private string selectedUpgradeLogShowOption = "All";
    [ObservableProperty] private bool upgradeLogLatestOnlyPerMac = false;

    private bool _pendingUpgradeLogViewRefresh;
    private readonly Dictionary<string, UpgradeLogGroup> _upgradeLogGroupByKey = new(StringComparer.OrdinalIgnoreCase);



    public string UpgradeLogSummary =>
        UpgradeLogTotalLines > 0
            ? $"{UpgradeLogStatus} - {UpgradeLogReceivedLines}/{UpgradeLogTotalLines} lines"
            : UpgradeLogStatus;
    public string UpgradeLogUniqueSummary => $"Unique sensors (latest per MAC): Success {UpgradeLogUniqueSuccessCount}, Failed {UpgradeLogUniqueFailedCount}";

    partial void OnUpgradeLogStatusChanged(string value)
    {
        OnPropertyChanged(nameof(UpgradeLogSummary));
        OnPropertyChanged(nameof(UpgradeLogUniqueSummary));
        AppendStatusBar("Upgrade log", value);
    }
    partial void OnUpgradeLogTotalLinesChanged(int value) => OnPropertyChanged(nameof(UpgradeLogSummary));
    partial void OnUpgradeLogReceivedLinesChanged(int value) => OnPropertyChanged(nameof(UpgradeLogSummary));
    partial void OnUpgradeLogUniqueSuccessCountChanged(int value) => OnPropertyChanged(nameof(UpgradeLogUniqueSummary));
    partial void OnUpgradeLogUniqueFailedCountChanged(int value) => OnPropertyChanged(nameof(UpgradeLogUniqueSummary));

    private readonly System.Text.StringBuilder _upgradeLogSb = new();

    private readonly HashSet<string> _requestedUpgradeLogCassias = new(StringComparer.OrdinalIgnoreCase);

    private bool _pendingUpgradeLogTextRefresh;

    // Latest-per-MAC filtering support for UpgradeLogGroupsView.
    // Rebuilt on-demand when filters change or new entries arrive.
    private readonly object _latestUpgradeLogMapLock = new();
    private readonly Dictionary<string, string> _latestUpgradeLogIdByMac = new(StringComparer.OrdinalIgnoreCase);
    private bool _latestUpgradeLogMapDirty = true;


    private void InitUpgradeLog()
    {
        UpgradeLogGroupsView = CollectionViewSource.GetDefaultView(UpgradeLogGroups);
        UpgradeLogGroupsView.SortDescriptions.Clear();
        UpgradeLogGroupsView.SortDescriptions.Add(new SortDescription(nameof(UpgradeLogGroup.LastTimeLocal), ListSortDirection.Descending));
        UpgradeLogGroupsView.Filter = obj =>
        {
            if (obj is not UpgradeLogGroup g) return false;

            if (!UpgradeLogGroupPassesFilters(g))
                return false;

            // Latest-per-MAC filter (after other filters)
            if (UpgradeLogLatestOnlyPerMac)
            {
                EnsureLatestUpgradeLogMap();
                if (_latestUpgradeLogIdByMac.TryGetValue(g.Mac ?? "", out var latestId))
                    return string.Equals(latestId, g.LogId, StringComparison.OrdinalIgnoreCase);
                return false;
            }

            return true;
        };

        LogGatewayOptions.Clear();
        LogGatewayOptions.Add("All");

    }

    partial void OnSelectedLogGatewayNameChanged(string value)
    {
        MarkLatestUpgradeLogMapDirty();
        RequestUpgradeLogViewRefresh();
    }

    
    partial void OnSelectedUpgradeLogShowOptionChanged(string value)
    {
        MarkLatestUpgradeLogMapDirty();
        RequestUpgradeLogViewRefresh();
    }

    partial void OnUpgradeLogLatestOnlyPerMacChanged(bool value)
    {
        MarkLatestUpgradeLogMapDirty();
        RequestUpgradeLogViewRefresh();
    }

    partial void OnUpgradeLogSearchTextChanged(string value)
    {
        MarkLatestUpgradeLogMapDirty();
        RequestUpgradeLogViewRefresh();
    }

    private void MarkLatestUpgradeLogMapDirty()
    {
        _latestUpgradeLogMapDirty = true;
    }

    private void EnsureLatestUpgradeLogMap()
    {
        if (!_latestUpgradeLogMapDirty)
            return;

        lock (_latestUpgradeLogMapLock)
        {
            if (!_latestUpgradeLogMapDirty)
                return;

            _latestUpgradeLogIdByMac.Clear();

            foreach (var g in UpgradeLogGroups)
            {
                if (g == null) continue;
                if (!UpgradeLogGroupPassesFilters(g))
                    continue;

                var mac = (g.Mac ?? "").Trim();
                if (string.IsNullOrWhiteSpace(mac))
                    continue;

                if (_latestUpgradeLogIdByMac.TryGetValue(mac, out var existingLogId))
                {
                    var existing = UpgradeLogGroups.FirstOrDefault(x => string.Equals(x.LogId, existingLogId, StringComparison.OrdinalIgnoreCase));
                    if (existing != null && existing.LastTimeLocal >= g.LastTimeLocal)
                        continue;
                }

                _latestUpgradeLogIdByMac[mac] = g.LogId;
            }

            // Mark groups that have a newer group for the same MAC (yellow "newer entry" badge)
            foreach (var g in UpgradeLogGroups)
            {
                if (g == null) continue;
                var mac = (g.Mac ?? "").Trim();
                if (string.IsNullOrWhiteSpace(mac))
                {
                    g.HasNewerForMac = false;
                    continue;
                }

                if (_latestUpgradeLogIdByMac.TryGetValue(mac, out var latestId))
                    g.HasNewerForMac = !string.Equals(latestId, g.LogId, StringComparison.OrdinalIgnoreCase);
                else
                    g.HasNewerForMac = false;

                g.NotifyHeaderChanged();
            }

            _latestUpgradeLogMapDirty = false;
        }
    }

    private bool UpgradeLogGroupPassesFilters(UpgradeLogGroup g)
    {
        if (g == null) return false;

        // Gateway filter
        var gw = (SelectedLogGatewayName ?? "All").Trim();
        if (!string.IsNullOrWhiteSpace(gw) && !string.Equals(gw, "All", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals((g.Cassia ?? "").Trim(), gw, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Text search filter
        var s = (UpgradeLogSearchText ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(s))
        {
            if (!ContainsIgnoreCase(g.LogId, s)
                && !ContainsIgnoreCase(g.Mac, s)
                && !ContainsIgnoreCase(g.Cassia, s)
                && !ContainsIgnoreCase(g.LatestDeviceName, s)
                && !ContainsIgnoreCase(g.LatestFirmware, s)
                && !ContainsIgnoreCase(g.LatestStage, s)
                && !ContainsIgnoreCase(g.LatestStatus, s)
                && !ContainsIgnoreCase(g.LatestSummary, s))
                return false;
        }

        // Show option filter
        var option = (SelectedUpgradeLogShowOption ?? "All").Trim();
        var status = (g.LatestStatus ?? "").Trim();
        var stage = (g.LatestStage ?? "").Trim();
        var suppressibleWarn = IsSuppressibleSameFirmwareWarn(stage, status, g.LatestSummary);

        var isSuccess = suppressibleWarn
                        || string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)
                        || (string.Equals(stage, "Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase));

        // Treat anything that looks like an error/fail as failure
        var statusLower = status.ToLowerInvariant();
        var stageLower = stage.ToLowerInvariant();
        var isFailure = statusLower.Contains("fail") || statusLower.Contains("error") || statusLower.Contains("timeout") || statusLower.Contains("aborted")
                        || statusLower.Contains("nofwread")
                        || stageLower.Contains("fail") || stageLower.Contains("error") || stageLower.Contains("timeout") || stageLower.Contains("aborted");

        if (string.Equals(option, "Only success", StringComparison.OrdinalIgnoreCase))
            return isSuccess;

        if (string.Equals(option, "Hide success", StringComparison.OrdinalIgnoreCase))
            return !isSuccess;

        if (string.Equals(option, "Only failed", StringComparison.OrdinalIgnoreCase))
            return isFailure || (!isSuccess && !string.IsNullOrWhiteSpace(status) && !string.Equals(status, "Info", StringComparison.OrdinalIgnoreCase));

        return true;
    }

    private static bool ContainsIgnoreCase(string? haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(haystack)) return false;
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void RecomputeUniqueUpgradeOutcomeCounts()
    {
        var latestByMac = new Dictionary<string, UpgradeLogGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in UpgradeLogGroups)
        {
            if (g == null) continue;
            var mac = (g.Mac ?? "").Trim();
            if (string.IsNullOrWhiteSpace(mac)) continue;

            if (!latestByMac.TryGetValue(mac, out var existing) || g.LastTimeLocal > existing.LastTimeLocal)
                latestByMac[mac] = g;
        }

        var success = 0;
        var failed = 0;

        foreach (var g in latestByMac.Values)
        {
            var status = (g.LatestStatus ?? "").Trim();
            var stage = (g.LatestStage ?? "").Trim();
            var suppressibleWarn = IsSuppressibleSameFirmwareWarn(stage, status, g.LatestSummary);

            var isSuccess = suppressibleWarn
                            || status.Equals("Success", StringComparison.OrdinalIgnoreCase)
                            || (stage.Equals("Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase)
                                && status.Equals("Success", StringComparison.OrdinalIgnoreCase));

            var s = status.ToLowerInvariant();
            var st = stage.ToLowerInvariant();
            var isFailed = s.Contains("fail") || s.Contains("error") || s.Contains("timeout") || s.Contains("aborted") || s.Contains("nofwread")
                           || st.Contains("fail") || st.Contains("error") || st.Contains("timeout") || st.Contains("aborted");

            if (isSuccess) success++;
            else if (isFailed) failed++;
        }

        UpgradeLogUniqueSuccessCount = success;
        UpgradeLogUniqueFailedCount = failed;
    }


}
