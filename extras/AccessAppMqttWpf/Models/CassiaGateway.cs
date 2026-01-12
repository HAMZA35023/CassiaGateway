using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;

namespace AccessAppMqttWpf.Models;

public partial class CassiaGateway : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string networkId = "";
    [ObservableProperty] private string state = "unknown";
    [ObservableProperty] private DateTimeOffset lastSeenUtc = DateTimeOffset.MinValue;
    [ObservableProperty] private int devicesSeen = 0;
    [ObservableProperty] private int queue = 0;

    // Firmware manifest (tele/.../fw-manifest)
    [ObservableProperty] private DateTimeOffset fwManifestLastSeenUtc = DateTimeOffset.MinValue;

    // Key: P41/P42/P46/P47/P48, Value: list of versions (v02.xx)
    [ObservableProperty] private Dictionary<string, string[]> firmwareManifest = new(StringComparer.OrdinalIgnoreCase);

    public string StatusLine => $"{NetworkId} • last seen {LastSeenUtc.ToLocalTime():HH:mm:ss} • devices {DevicesSeen}";

    public string LastSeenLocal => LastSeenUtc == DateTimeOffset.MinValue ? "" : LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string FwManifestLastSeenLocal => FwManifestLastSeenUtc == DateTimeOffset.MinValue ? "" : FwManifestLastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string StateLower => (State ?? "").ToLowerInvariant();

    public System.Windows.Media.Brush StatusBrush =>
        StateLower == "online" ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("GoodBrush") :
        StateLower == "offline" ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BadBrush") :
        (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("WarnBrush");

    public bool HasFwManifest => FirmwareManifest.Count > 0;

    partial void OnStateChanged(string value) => OnPropertyChanged(nameof(StatusLine));
    partial void OnLastSeenUtcChanged(DateTimeOffset value)
    {
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(LastSeenLocal));
    }
    partial void OnDevicesSeenChanged(int value) => OnPropertyChanged(nameof(StatusLine));
    partial void OnFwManifestLastSeenUtcChanged(DateTimeOffset value) => OnPropertyChanged(nameof(FwManifestLastSeenLocal));
    partial void OnFirmwareManifestChanged(Dictionary<string, string[]> value) => OnPropertyChanged(nameof(HasFwManifest));
}
