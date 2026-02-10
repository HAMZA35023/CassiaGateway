using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;

namespace AccessAppMqttWpf.Models;

/// <summary>
/// Host-side BLE scan record (based on advertisements received on this PC).
/// </summary>
public partial class HostBleScanItem : ObservableObject
{
    [ObservableProperty] private string mac = "";

    // P41/P42/P46/P47/P48/P49 (mirrors DiscoveredDevice.SensorModel when available)
    [ObservableProperty] private string sensorModel = "";

    [ObservableProperty] private int avgHostRssi = int.MinValue;
    [ObservableProperty] private DateTimeOffset lastSeenUtc = DateTimeOffset.MinValue;

    // Closest Cassia (based on MQTT discovered-device RSSIs)
    [ObservableProperty] private string closestCassia = "";

    // Assigned Cassia (mirrors DiscoveredDevice.AssignedCassia when available)
    [ObservableProperty] private string assignedCassia = "";

    // Current FW (mirrors DiscoveredDevice.CurrentFw / DisplayFw if available)
    [ObservableProperty] private string currentFw = "";

    // Reuse the same coloring semantics as the main device list
    [ObservableProperty] private bool isInQueue;
    [ObservableProperty] private bool isUpgradeSuccess;
    [ObservableProperty] private bool isUpgradeWarn;
    [ObservableProperty] private bool isUpgradeNoFwRead;
    [ObservableProperty] private bool isUpgradeFailed;

    // True while an identify request is active (tele/identify stages until disconnected/failed).
    [ObservableProperty] private bool isIdentifying;

    // True after clicking Identify until we receive the first "connected" stage (UI pulses).
    [ObservableProperty] private bool isIdentifyPending;

    // Cassia RSSIs by name (e.g. cassia-01 -> -53)
    public Dictionary<string, int> CassiaRssi { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Replace Cassia RSSI dictionary and notify WPF indexer bindings (Item[]).
    /// </summary>
    public void SetCassiaRssi(IDictionary<string, int>? values)
    {
        CassiaRssi.Clear();
        if (values != null)
        {
            foreach (var kv in values)
                CassiaRssi[kv.Key] = kv.Value;
        }

        // Indexer bindings listen to Item[]
        OnPropertyChanged("Item[]");
    }

    public string LastSeenLocal => LastSeenUtc == DateTimeOffset.MinValue
        ? ""
        : LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// Indexer for WPF binding: {Binding [cassia-01]} -> "-53".
    /// </summary>
    public int? this[string cassiaName]
        => (cassiaName != null && CassiaRssi.TryGetValue(cassiaName, out var v)) ? v : null;
}
