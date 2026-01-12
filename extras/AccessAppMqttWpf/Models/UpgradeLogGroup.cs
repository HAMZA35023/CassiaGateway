using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace AccessAppMqttWpf.Models;

public partial class UpgradeLogGroup : ObservableObject
{
    [ObservableProperty] private string cassia = "";
    [ObservableProperty] private string logId = "";
    [ObservableProperty] private string mac = "";

    public ObservableCollection<UpgradeLogEntry> Entries { get; } = new();

    // Deduplicate across payload shapes (raw line, JSON object, saved-log replay)
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public DateTimeOffset LastTimeLocal =>
        Entries.Count == 0 ? DateTimeOffset.MinValue : Entries.Max(e => e.TimeLocal);

    public string LastTimeLocalText =>
        LastTimeLocal == DateTimeOffset.MinValue ? "" : LastTimeLocal.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public string LatestStage =>
        Entries.OrderByDescending(e => e.TimeLocal).FirstOrDefault()?.Stage ?? "";

    public string LatestStatus =>
        Entries.OrderByDescending(e => e.TimeLocal).FirstOrDefault()?.Status ?? "";

    public string LatestFirmware =>
        Entries.OrderByDescending(e => e.TimeLocal).FirstOrDefault()?.Firmware ?? "";

    public string LatestMac =>
        Entries.OrderByDescending(e => e.TimeLocal).FirstOrDefault()?.Mac ?? Mac ?? "";

    // Friendly header preview (no raw log text)
    public string LatestSummary
    {
        get
        {
            var e = Entries.OrderByDescending(x => x.TimeLocal).FirstOrDefault();
            if (e is null) return "";
            var t = e.TimeLocal == DateTimeOffset.MinValue ? "" : e.TimeLocal.ToLocalTime().ToString("HH:mm:ss");
            var fw = string.IsNullOrWhiteSpace(e.Firmware) ? "" : e.Firmware.Trim();
            return $"{t} • {e.Stage} • {fw} • {e.Status}".Trim(' ', '•');
        }
    }

    // logId format: 10B9F711083B_20260112161955 (macPart_timestamp)
    public string LogIdMacPart
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LogId)) return "";
            var idx = LogId.IndexOf('_');
            return idx > 0 ? LogId[..idx] : LogId;
        }
    }

    public DateTimeOffset StartedAtLocal
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LogId)) return DateTimeOffset.MinValue;
            var idx = LogId.IndexOf('_');
            if (idx < 0 || idx + 1 >= LogId.Length) return DateTimeOffset.MinValue;

            var ts = LogId[(idx + 1)..].Trim();
            if (DateTime.TryParseExact(ts, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var dt))
                return new DateTimeOffset(dt);

            return DateTimeOffset.MinValue;
        }
    }

    public string StartedAtLocalText =>
        StartedAtLocal == DateTimeOffset.MinValue ? "" : StartedAtLocal.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public void AddEntry(UpgradeLogEntry e)
    {
        if (e is null) return;

        // Use normalized fingerprint to dedup
        var fp =
            $"{e.TimeLocal:yyyyMMddHHmmss}|{(e.Stage ?? "").Trim()}|{(e.Status ?? "").Trim()}|{(e.Firmware ?? "").Trim()}|{(e.Mac ?? "").Trim()}";

        if (!_seen.Add(fp))
            return;

        Entries.Insert(0, e); // newest on top

        // Keep group-level MAC filled (helps searching/headers)
        if (string.IsNullOrWhiteSpace(Mac) && !string.IsNullOrWhiteSpace(e.Mac))
            Mac = e.Mac;

        NotifyHeaderChanged();
    }

    public void NotifyHeaderChanged()
    {
        OnPropertyChanged(nameof(LastTimeLocal));
        OnPropertyChanged(nameof(LastTimeLocalText));
        OnPropertyChanged(nameof(LatestStage));
        OnPropertyChanged(nameof(LatestStatus));
        OnPropertyChanged(nameof(LatestFirmware));
        OnPropertyChanged(nameof(LatestMac));
        OnPropertyChanged(nameof(LatestSummary));
        OnPropertyChanged(nameof(LogIdMacPart));
        OnPropertyChanged(nameof(StartedAtLocal));
        OnPropertyChanged(nameof(StartedAtLocalText));
    }
}
