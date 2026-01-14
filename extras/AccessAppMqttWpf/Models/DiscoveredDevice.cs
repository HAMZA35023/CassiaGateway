using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AccessAppMqttWpf.Models;

public partial class DiscoveredDevice : ObservableObject
{
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private string mac = "";
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string productNumber = "";
    [ObservableProperty] private string detectorFamily = "";
    [ObservableProperty] private string detectorType = "";

    // P41/P42/P46/P47/P48 (P49 shown as P46)
[ObservableProperty] private string sensorModel = "";

// --- Process / queue status (mirrored from QueueItems + progress tele) ---
[ObservableProperty] private string processStatus = "";
[ObservableProperty] private int processProgress = 0; // 0..100
[ObservableProperty] private string processCassia = "";
[ObservableProperty] private string processFirmware = "";
    [ObservableProperty] private DateTimeOffset? processLastUpdateUtc;

    // BLE link status from connect/disconnect plain replies
    [ObservableProperty] private string bleLink = "";

    // Parsed from upgrade-log ("Current FW Version" lines)
    [ObservableProperty] private string currentFw = "";


    // --- Upgrade result (from upgrade-log) ---
    [ObservableProperty] private bool isUpgradeSuccess;
    [ObservableProperty] private string lastTargetFw = "";
    [ObservableProperty] private DateTimeOffset? lastUpgradeSuccessUtc;

    // Device is in queue / being processed (derived from queue/progress)
    [ObservableProperty] private bool isInQueue;

    
    // For device list: when upgrade completed successfully, show the FW from log entries (LastTargetFw, like v02.xx),
    // otherwise show the current FW (from "Current FW Version" lines).
    public string DisplayFw =>
        IsUpgradeSuccess && !string.IsNullOrWhiteSpace(LastTargetFw) && LastTargetFw.Trim().StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? LastTargetFw.Trim()
            : (CurrentFw ?? "");
public string LastUpgradeSuccessLocal => LastUpgradeSuccessUtc.HasValue
        ? LastUpgradeSuccessUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "";


    [ObservableProperty] private int bestRssi = int.MinValue;
    [ObservableProperty] private string bestCassia = "";

    // ----- Assignment (sticky) -----
    // This is the Cassia we will actually use for commands (connect / update / write-read).
    // IMPORTANT: we only auto-set it once (when first discovered) unless the user manually reassigns.
    [ObservableProperty] private string assignedCassia = "";
    [ObservableProperty] private DateTimeOffset lastSeenUtc = DateTimeOffset.MinValue;

    public Dictionary<string, int> CassiaRssi { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string ProcessLastUpdateLocal => ProcessLastUpdateUtc.HasValue ? ProcessLastUpdateUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "";

    public string RssiAll => CassiaRssi.Count == 0 ? "" : string.Join("  ", CassiaRssi.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));

    public string LastSeenLocal => LastSeenUtc == DateTimeOffset.MinValue ? "" : LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    partial void OnProductNumberChanged(string value) => OnPropertyChanged(nameof(SensorModel));

    
    partial void OnCurrentFwChanged(string value) => OnPropertyChanged(nameof(DisplayFw));
    partial void OnLastTargetFwChanged(string value) => OnPropertyChanged(nameof(DisplayFw));
    partial void OnIsUpgradeSuccessChanged(bool value) => OnPropertyChanged(nameof(DisplayFw));
public void UpdateFromCassia(string cassia, int rssi, DateTimeOffset tsUtc)
    {
        CassiaRssi[cassia] = rssi;
        LastSeenUtc = tsUtc;

        var best = CassiaRssi.OrderByDescending(kv => kv.Value).FirstOrDefault();
        BestCassia = best.Key ?? cassia;
        OnPropertyChanged(nameof(RssiAll));
        BestRssi = (best.Key == null) ? rssi : best.Value;
        OnPropertyChanged(nameof(RssiAll));
    }
}
