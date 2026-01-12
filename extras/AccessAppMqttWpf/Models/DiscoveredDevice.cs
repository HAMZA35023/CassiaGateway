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

    // Parsed from upgrade-log ("Current FW Version" lines)
    [ObservableProperty] private string currentFw = "";



    [ObservableProperty] private int bestRssi = int.MinValue;
    [ObservableProperty] private string bestCassia = "";
    [ObservableProperty] private DateTimeOffset lastSeenUtc = DateTimeOffset.MinValue;

    public Dictionary<string, int> CassiaRssi { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string ProcessLastUpdateLocal => ProcessLastUpdateUtc.HasValue ? ProcessLastUpdateUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "";

    public string RssiAll => CassiaRssi.Count == 0 ? "" : string.Join("  ", CassiaRssi.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));

    public string LastSeenLocal => LastSeenUtc == DateTimeOffset.MinValue ? "" : LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    partial void OnProductNumberChanged(string value) => OnPropertyChanged(nameof(SensorModel));

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
