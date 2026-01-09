using CommunityToolkit.Mvvm.ComponentModel;
using System;

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

    [ObservableProperty] private string status = "Queued";
    [ObservableProperty] private int progress = 0;
    [ObservableProperty] private string notes = "";
    [ObservableProperty] private DateTimeOffset lastUpdateUtc = DateTimeOffset.MinValue;

    public string ProgressText => $"{Progress}%";
    public string LastUpdateLocal => lastUpdateUtc == DateTimeOffset.MinValue ? "" : lastUpdateUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    partial void OnProgressChanged(int value) => OnPropertyChanged(nameof(ProgressText));
    partial void OnLastUpdateUtcChanged(DateTimeOffset value) => OnPropertyChanged(nameof(LastUpdateLocal));
}
