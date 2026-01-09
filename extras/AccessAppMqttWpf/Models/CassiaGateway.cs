using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace AccessAppMqttWpf.Models;

public partial class CassiaGateway : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string networkId = "";
    [ObservableProperty] private string state = "unknown";
    [ObservableProperty] private DateTimeOffset lastSeenUtc = DateTimeOffset.MinValue;
    [ObservableProperty] private int devicesSeen = 0;
    [ObservableProperty] private int queue = 0;

    public string StatusLine => $"{NetworkId} • last seen {LastSeenUtc.ToLocalTime():HH:mm:ss} • devices {DevicesSeen}";
    public string StateLower => (State ?? "").ToLowerInvariant();

    public System.Windows.Media.Brush StatusBrush =>
        StateLower == "online" ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("GoodBrush") :
        StateLower == "offline" ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BadBrush") :
        (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("WarnBrush");

    partial void OnStateChanged(string value) => OnPropertyChanged(nameof(StatusLine));
    partial void OnLastSeenUtcChanged(DateTimeOffset value) => OnPropertyChanged(nameof(StatusLine));
    partial void OnDevicesSeenChanged(int value) => OnPropertyChanged(nameof(StatusLine));
}
