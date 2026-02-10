using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AccessAppMqttWpf.Models;

public partial class CassiaGateway : ObservableObject
{
    [ObservableProperty] private string name = "";
    // Version string received from tele/.../status (e.g. "2.5.1" or "v2.5.1")
    [ObservableProperty] private string version = "";
    [ObservableProperty] private string networkId = "";
    [ObservableProperty] private string state = "unknown";
    [ObservableProperty] private DateTimeOffset lastSeenUtc = DateTimeOffset.MinValue;
    [ObservableProperty] private int devicesSeen = 0;
    [ObservableProperty] private int queue = 0;
    // Will be added to the status payload later (same level as "queue").
    [ObservableProperty] private int programming = 0;
    // Runtime-only limit received from tele/.../parallel-programmers
    [ObservableProperty] private int parallelProgrammers = 0;
    // Editable value in UI (what the user wants to set). Kept separate from ParallelProgrammers.
    [ObservableProperty] private int parallelProgrammersDesired = 0;
    [ObservableProperty] private double totalSpeedpct = 1;
    [ObservableProperty] private long uptimeSeconds;
    [ObservableProperty] private DateTimeOffset uptimeReportedUtc = DateTimeOffset.MinValue;
    [ObservableProperty] private string lastSeenAgoText = "";
    [ObservableProperty] private string uptimeText = "";
    [ObservableProperty] private string cellularState = "";
    [ObservableProperty] private string cellularNetworkType = "";
    [ObservableProperty] private int? cellularSignalBar;
    [ObservableProperty] private int? cellularRssiDbm;
    [ObservableProperty] private int? cellularLteRsrpDbm;
    [ObservableProperty] private int? cellularLteRsrqDb;
    [ObservableProperty] private int? cellularLteSnrDb;
    [ObservableProperty] private string cellularProvider = "";

    // Client-side speed history for graphing (max 12 hours kept)
    public ObservableCollection<SpeedSample> SpeedHistory { get; } = new();

    public void AddSpeedSample(DateTimeOffset tsUtc, double speedPctPerMin)
    {
        // Append
        SpeedHistory.Add(new SpeedSample(tsUtc, speedPctPerMin));

        // Prune older than 12 hours
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(12);
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

    public string NameWithVersion => string.IsNullOrWhiteSpace(Version)
        ? Name
        : $"{Name}  •  {Version}";

    // Firmware manifest (tele/.../fw-manifest)
    [ObservableProperty] private DateTimeOffset fwManifestLastSeenUtc = DateTimeOffset.MinValue;

    // Key: P41/P42/P46/P47/P48, Value: list of versions (v02.xx)
    [ObservableProperty] private Dictionary<string, string[]> firmwareManifest = new(StringComparer.OrdinalIgnoreCase);

    public string StatusLine => $"{NetworkId} • last seen {LastSeenUtc.ToLocalTime():HH:mm:ss} • devices {DevicesSeen} • {QueueLine}";
    public string CellularSummary
    {
        get
        {
            var state = (CellularState ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(state) && !state.Equals("connected", StringComparison.OrdinalIgnoreCase))
                return state;

            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(CellularNetworkType)) bits.Add(CellularNetworkType.Trim());
            if (CellularSignalBar.HasValue) bits.Add($"{CellularSignalBar.Value}/5");
            if (CellularLteRsrpDbm.HasValue) bits.Add($"{CellularLteRsrpDbm.Value} dBm");
            else if (CellularRssiDbm.HasValue) bits.Add($"{CellularRssiDbm.Value} dBm");

            if (bits.Count > 0)
                return string.Join(" | ", bits);

            if (!string.IsNullOrWhiteSpace(CellularProvider))
                return CellularProvider.Trim();

            return "";
        }
    }

    public string LastSeenLocal => LastSeenUtc == DateTimeOffset.MinValue ? "" : LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string FwManifestLastSeenLocal => FwManifestLastSeenUtc == DateTimeOffset.MinValue ? "" : FwManifestLastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string StateLower => (State ?? "").ToLowerInvariant();

    public System.Windows.Media.Brush StatusBrush =>
        StateLower == "online" ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("GoodBrush") :
        StateLower == "offline" ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BadBrush") :
        (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("WarnBrush");

    // Left color stripe in the Cassia tile
    // red = offline
    // green = programming
    // yellow = has queue (but not programming)
    // blue = idle (online, no queue, no programming)
    public System.Windows.Media.Brush StripeBrush =>
        StateLower == "offline" ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BadBrush") :
        Programming > 0 ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("GoodBrush") :
        Queue > 0 ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("WarnBrush") :
        (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentBrush");

    public bool HasFwManifest => FirmwareManifest.Count > 0;

    partial void OnStateChanged(string value)
    {
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(StripeBrush));
    }
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(NameWithVersion));
    partial void OnVersionChanged(string value) => OnPropertyChanged(nameof(NameWithVersion));
    partial void OnLastSeenUtcChanged(DateTimeOffset value)
    {
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(LastSeenLocal));
        UpdateDerivedTimes(DateTimeOffset.UtcNow);
    }
    partial void OnDevicesSeenChanged(int value) => OnPropertyChanged(nameof(StatusLine));
    partial void OnQueueChanged(int value)
    {
        OnPropertyChanged(nameof(QueueLine));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(StripeBrush));
    }
    partial void OnProgrammingChanged(int value)
    {
        OnPropertyChanged(nameof(QueueLine));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(StripeBrush));
    }
    partial void OnCellularStateChanged(string value) => OnPropertyChanged(nameof(CellularSummary));
    partial void OnCellularNetworkTypeChanged(string value) => OnPropertyChanged(nameof(CellularSummary));
    partial void OnCellularSignalBarChanged(int? value) => OnPropertyChanged(nameof(CellularSummary));
    partial void OnCellularRssiDbmChanged(int? value) => OnPropertyChanged(nameof(CellularSummary));
    partial void OnCellularLteRsrpDbmChanged(int? value) => OnPropertyChanged(nameof(CellularSummary));
    partial void OnCellularLteRsrqDbChanged(int? value) => OnPropertyChanged(nameof(CellularSummary));
    partial void OnCellularLteSnrDbChanged(int? value) => OnPropertyChanged(nameof(CellularSummary));
    partial void OnCellularProviderChanged(string value) => OnPropertyChanged(nameof(CellularSummary));

    partial void OnParallelProgrammersChanged(int value)
    {
        // Keep UI editable value in sync when we receive an update.
        if (ParallelProgrammersDesired <= 0)
            ParallelProgrammersDesired = value;
    }
    partial void OnParallelProgrammersDesiredChanged(int value) { }

    partial void OnAssignedP41Changed(int value) => OnPropertyChanged(nameof(AssignedLine));
    partial void OnAssignedP42Changed(int value) => OnPropertyChanged(nameof(AssignedLine));
    partial void OnAssignedP46Changed(int value) => OnPropertyChanged(nameof(AssignedLine));
    partial void OnAssignedP47Changed(int value) => OnPropertyChanged(nameof(AssignedLine));
    partial void OnAssignedP48Changed(int value) => OnPropertyChanged(nameof(AssignedLine));
    partial void OnFwManifestLastSeenUtcChanged(DateTimeOffset value) => OnPropertyChanged(nameof(FwManifestLastSeenLocal));
    partial void OnFirmwareManifestChanged(Dictionary<string, string[]> value) => OnPropertyChanged(nameof(HasFwManifest));

    partial void OnUptimeSecondsChanged(long value) => UpdateDerivedTimes(DateTimeOffset.UtcNow);
    partial void OnUptimeReportedUtcChanged(DateTimeOffset value) => UpdateDerivedTimes(DateTimeOffset.UtcNow);

    public void UpdateDerivedTimes(DateTimeOffset nowUtc)
    {
        if (LastSeenUtc == DateTimeOffset.MinValue)
        {
            LastSeenAgoText = "";
        }
        else
        {
            var delta = nowUtc - LastSeenUtc;
            if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
            var totalMinutes = (int)Math.Floor(delta.TotalMinutes);
            var seconds = delta.Seconds;
            LastSeenAgoText = $"{totalMinutes:00}:{seconds:00}";
        }

        if (UptimeSeconds <= 0 || UptimeReportedUtc == DateTimeOffset.MinValue)
        {
            UptimeText = "";
        }
        else
        {
            var extra = nowUtc - UptimeReportedUtc;
            if (extra < TimeSpan.Zero) extra = TimeSpan.Zero;
            var totalSeconds = UptimeSeconds + (long)extra.TotalSeconds;
            if (totalSeconds < 0) totalSeconds = 0;
            var ts = TimeSpan.FromSeconds(totalSeconds);
            var days = (int)ts.TotalDays;
            UptimeText = $"{days}d, {ts.Hours:00}:{ts.Minutes:00}";
        }
    }
}

public sealed record SpeedSample(DateTimeOffset TimeUtc, double SpeedPctPerMin);
