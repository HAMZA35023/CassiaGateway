using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AccessAppMqttWpf.Models;

public partial class QueueItem : ObservableObject
{
    public int QueueSortKey => IsTerminalStatus(Status) ? 1 : 0;
    public bool IsDone => QueueSortKey == 1;


private static bool IsTerminalStatus(string? status)
{
    if (string.IsNullOrWhiteSpace(status)) return false;
    var s = status.Trim();

    // Backends have used: Done / Success / Completed / Failed / Error / Warn
    if (s.Equals("done", StringComparison.OrdinalIgnoreCase)) return true;
    if (s.Equals("success", StringComparison.OrdinalIgnoreCase)) return true;
    if (s.Equals("warn", StringComparison.OrdinalIgnoreCase) || s.Equals("warning", StringComparison.OrdinalIgnoreCase)) return true;
    if (s.Equals("failed", StringComparison.OrdinalIgnoreCase) || s.Equals("fail", StringComparison.OrdinalIgnoreCase)) return true;
    if (s.Equals("error", StringComparison.OrdinalIgnoreCase)) return true;
    if (s.Contains("complete", StringComparison.OrdinalIgnoreCase)) return true;
    if (s.Contains("fail", StringComparison.OrdinalIgnoreCase) || s.Contains("error", StringComparison.OrdinalIgnoreCase)) return true;

    return false;
}

    [ObservableProperty] private string mac = "";
    [ObservableProperty] private string cassia = "";
    [ObservableProperty] private string command = "";
    [ObservableProperty] private string detectorType = "";
    [ObservableProperty] private string firmwareVersion = "";
    [ObservableProperty] private string chipUsed = "";


    [ObservableProperty] private string status = "Queued";
    [ObservableProperty] private int progress = 0;
    [ObservableProperty] private double? speedPctPerMin;
    [ObservableProperty] private string notes = "";
    [ObservableProperty] private DateTimeOffset lastUpdateUtc = DateTimeOffset.MinValue;

    // Snapshot of RSSI values for this MAC across all Cassias.
    // Populated from discovered device data, so the queue view remains self-contained.
    public ObservableCollection<RssiEntry> RssiEntries { get; } = new();

    public string ProgressText => $"{Progress}%";
    public string SpeedText => SpeedPctPerMin.HasValue ? $"{SpeedPctPerMin.Value:0.##} %/min" : "";
    public string LastUpdateLocal => lastUpdateUtc == DateTimeOffset.MinValue ? "" : lastUpdateUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public void UpdateRssiEntries(Dictionary<string, int> cassiaRssi, string queuedCassia)
    {
        queuedCassia = (queuedCassia ?? "").Trim();

        RssiEntries.Clear();
        if (cassiaRssi == null || cassiaRssi.Count == 0)
            return;

        foreach (var kv in cassiaRssi
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            RssiEntries.Add(new RssiEntry
            {
                Cassia = kv.Key,
                Rssi = kv.Value,
                IsQueued = !string.IsNullOrWhiteSpace(queuedCassia) && kv.Key.Equals(queuedCassia, StringComparison.OrdinalIgnoreCase)
            });
        }
    }

    partial void OnProgressChanged(int value) => OnPropertyChanged(nameof(ProgressText));
    partial void OnSpeedPctPerMinChanged(double? value) => OnPropertyChanged(nameof(SpeedText));
    partial void OnLastUpdateUtcChanged(DateTimeOffset value) => OnPropertyChanged(nameof(LastUpdateLocal));
}
