using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace AccessAppMqttWpf.Models;

public partial class UpgradeLogEntry : ObservableObject
{
    [ObservableProperty] private string cassia = "";
    [ObservableProperty] private string logId = "";
    [ObservableProperty] private string mac = "";
    [ObservableProperty] private string stage = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string firmware = "";

    /// <summary>
    /// Best-effort local timestamp parsed from the log line or timeLocal.
    /// Used for sorting; if unknown, defaults to DateTimeOffset.MinValue.
    /// </summary>
    [ObservableProperty] private DateTimeOffset timeLocal = DateTimeOffset.MinValue;

    [ObservableProperty] private string line = "";

    public string TimeLocalText =>
        TimeLocal == DateTimeOffset.MinValue ? "" : TimeLocal.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
