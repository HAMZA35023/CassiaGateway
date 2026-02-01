using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AccessAppMqttWpf.Models;

public partial class QueueItem : ObservableObject
{
    public int QueueSortKey => (Status?.Equals("Done", StringComparison.OrdinalIgnoreCase) == true) ? 1 : 0;
    public bool IsDone => QueueSortKey == 1;

    [ObservableProperty] private string mac = "";
    [ObservableProperty] private string cassia = "";
    [ObservableProperty] private string command = "";
    [ObservableProperty] private string detectorType = "";
    [ObservableProperty] private string firmwareVersion = "";
    [ObservableProperty] private string chipUsed = "";


    [ObservableProperty] private string status = "Queued";
    [ObservableProperty] private int progress = 0;
    [ObservableProperty] private string notes = "";
    [ObservableProperty] private DateTimeOffset lastUpdateUtc = DateTimeOffset.MinValue;

    // Snapshot of RSSI values for this MAC across all Cassias.
    // Populated from discovered device data, so the queue view remains self-contained.
    public ObservableCollection<RssiEntry> RssiEntries { get; } = new();

    public string ProgressText => $"{Progress}%";
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
    partial void OnLastUpdateUtcChanged(DateTimeOffset value) => OnPropertyChanged(nameof(LastUpdateLocal));
}
