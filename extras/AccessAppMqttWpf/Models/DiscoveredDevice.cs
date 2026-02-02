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
    [ObservableProperty] private string chipUsed = "";
    [ObservableProperty] private DateTimeOffset? processLastUpdateUtc;

    // BLE link status from connect/disconnect plain replies
    [ObservableProperty] private string bleLink = "";

    // Parsed from upgrade-log ("Current FW Version" lines)
    [ObservableProperty] private string currentFw = "";

    // If the latest (final) upgrade-log entry for this device is Warn, we highlight the row.
    [ObservableProperty] private bool isUpgradeWarn;

    // If the latest (final) upgrade-log completion entry for this device is Failed, we highlight the row red.
    [ObservableProperty] private bool isUpgradeFailed;


    // --- Upgrade result (from upgrade-log) ---
    [ObservableProperty] private bool isUpgradeSuccess;
    [ObservableProperty] private string lastTargetFw = "";
    [ObservableProperty] private DateTimeOffset? lastUpgradeSuccessUtc;

    // Device is in queue / being processed (derived from queue/progress)
    [ObservableProperty] private bool isInQueue;

    
    // For device list: show the Sensor App version (from CurrentFw).
    // When a get-fw-version request is made, CurrentFw may temporarily be set to "requested".
    public string DisplayFw => (CurrentFw ?? "");
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


    partial void OnProcessStatusChanged(string value)
    {
        MarkInQueueFromProcessStatus(value);
        UpdateRowFlagsFromProcess();
    }
    partial void OnIsInQueueChanged(bool value) => UpdateRowFlagsFromProcess();
    partial void OnProcessProgressChanged(int value)
    {
        MarkInQueueFromProgress(value);
        UpdateRowFlagsFromProcess();
    }
    partial void OnProcessCassiaChanged(string value) => UpdateRowFlagsFromProcess();

        
    private void MarkInQueueFromProcessStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return;

        // IMPORTANT: We only ever set IsInQueue=true here.
        // Clearing IsInQueue should be done by authoritative queue state in the ViewModel.
        var s = status.Trim();

        if (s.Contains("queued", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("queue", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("program", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("upgrad", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsInQueue) IsInQueue = true;
        }
    }

    private void MarkInQueueFromProgress(int progress)
    {
        // Some flows update progress without a stable status string.
        if (progress is > 0 and < 100)
        {
            if (!IsInQueue) IsInQueue = true;
        }
    }


private void UpdateRowFlagsFromProcess()
    {
        // NOTE: IsInQueue is set explicitly by the VM (from queue/progress snapshots),
        // so we must not "guess" it here. We only derive Success/Fail/Warn from ProcessStatus,
        // and queued state always overrides previous completion coloring.
        if (IsInQueue)
        {
            IsUpgradeSuccess = false;
            IsUpgradeFailed = false;
            IsUpgradeWarn = false;
            return;
        }

        var sLower = ((ProcessStatus ?? "").Trim()).ToLowerInvariant();

        var success =
            sLower.Contains("success") ||
            sLower.Contains("completed") ||
            sLower.Contains("complete") ||
            sLower.Contains("done") ||
            sLower.StartsWith("ok");

        var failed =
            sLower.Contains("fail") ||
            sLower.Contains("error") ||
            sLower.Contains("timeout") ||
            sLower.Contains("aborted");

        var warn = !failed && !success && sLower.Contains("warn");

        // Set explicitly (do not keep stale values from a previous run)
        IsUpgradeFailed = failed;
        IsUpgradeSuccess = !failed && success;
        IsUpgradeWarn = !failed && !success && warn;
    }

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
