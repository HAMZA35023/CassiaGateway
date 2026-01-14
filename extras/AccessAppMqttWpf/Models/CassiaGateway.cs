using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AccessAppMqttWpf.Models;

public partial class CassiaGateway : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string networkId = "";
    [ObservableProperty] private string state = "unknown";
    [ObservableProperty] private DateTimeOffset lastSeenUtc = DateTimeOffset.MinValue;
    [ObservableProperty] private int devicesSeen = 0;
    [ObservableProperty] private int queue = 0;
    // Will be added to the status payload later (same level as "queue").
    [ObservableProperty] private int programming = 0;
    [ObservableProperty] private double totalSpeedpct = 1;

    // Client-side speed history for graphing (max 1 hour kept)
    public ObservableCollection<SpeedSample> SpeedHistory { get; } = new();

    public void AddSpeedSample(DateTimeOffset tsUtc, double speedPctPerMin)
    {
        // Append
        SpeedHistory.Add(new SpeedSample(tsUtc, speedPctPerMin));

        // Prune older than 1 hour
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        while (SpeedHistory.Count > 0 && SpeedHistory[0].TimeUtc < cutoff)
            SpeedHistory.RemoveAt(0);
    }


    // ---- Sticky assignment counts (computed client-side) ----
    [ObservableProperty] private int assignedP41;
    [ObservableProperty] private int assignedP42;
    [ObservableProperty] private int assignedP46;
    [ObservableProperty] private int assignedP47;
    [ObservableProperty] private int assignedP48;

    public int AssignedGroupA => AssignedP41 + AssignedP42 + AssignedP46; // P41+P42+P46
    public int AssignedGroupB => AssignedP47 + AssignedP48;              // P47+P48

    public string AssignedLine => $"Assigned: P41 {AssignedP41}  P42 {AssignedP42}  P46 {AssignedP46}   |   P47 {AssignedP47}  P48 {AssignedP48}";
    public string QueueLine => $"Queue {Queue} • Programming {Programming}";

    // Firmware manifest (tele/.../fw-manifest)
    [ObservableProperty] private DateTimeOffset fwManifestLastSeenUtc = DateTimeOffset.MinValue;

    // Key: P41/P42/P46/P47/P48, Value: list of versions (v02.xx)
    [ObservableProperty] private Dictionary<string, string[]> firmwareManifest = new(StringComparer.OrdinalIgnoreCase);

    public string StatusLine => $"{NetworkId} • last seen {LastSeenUtc.ToLocalTime():HH:mm:ss} • devices {DevicesSeen} • {QueueLine}";

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
    partial void OnQueueChanged(int value)
    {
        OnPropertyChanged(nameof(QueueLine));
        OnPropertyChanged(nameof(StatusLine));
    }
    partial void OnProgrammingChanged(int value)
    {
        OnPropertyChanged(nameof(QueueLine));
        OnPropertyChanged(nameof(StatusLine));
    }

    partial void OnAssignedP41Changed(int value) => OnPropertyChanged(nameof(AssignedLine));
    partial void OnAssignedP42Changed(int value) => OnPropertyChanged(nameof(AssignedLine));
    partial void OnAssignedP46Changed(int value) => OnPropertyChanged(nameof(AssignedLine));
    partial void OnAssignedP47Changed(int value) => OnPropertyChanged(nameof(AssignedLine));
    partial void OnAssignedP48Changed(int value) => OnPropertyChanged(nameof(AssignedLine));
    partial void OnFwManifestLastSeenUtcChanged(DateTimeOffset value) => OnPropertyChanged(nameof(FwManifestLastSeenLocal));
    partial void OnFirmwareManifestChanged(Dictionary<string, string[]> value) => OnPropertyChanged(nameof(HasFwManifest));
}

public sealed record SpeedSample(DateTimeOffset TimeUtc, double SpeedPctPerMin);
