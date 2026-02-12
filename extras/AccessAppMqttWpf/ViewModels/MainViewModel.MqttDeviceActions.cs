using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.VisualBasic;
using AccessAppMqttWpf;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private void HandleFwVersionTele(string cassia, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
                return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var r in resultsEl.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    var mac = r.TryGetProperty("mac", out var m) ? (m.GetString() ?? "") : "";
                    var ver = r.TryGetProperty("version", out var v) ? (v.GetString() ?? "") : "";
                    mac = (mac ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(mac)) continue;

                    // Extract the Sensor App version when the backend returns a full combined string.
                    var app = "";
                    var mm = SensorAppFromStatusRx.Match(ver ?? "");
                    if (mm.Success) app = mm.Groups["app"].Value;
                    if (string.IsNullOrWhiteSpace(app))
                        app = (ver ?? "").Trim();

                    var cs = GetOrCreateCache(mac);
                    cs.CurrentFw = app;
                    cs.CurrentFwFromGetFw = true;   // ✅ mark as Get FW sourced


                    var dev = FindDiscoveredDevice(mac);
                    if (dev != null)
                        dev.CurrentFw = app;
                }
            });
        }
        catch { }
    }

    private void HandleDisconnectTele(string cassia, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
                return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var r in resultsEl.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    var mac = r.TryGetProperty("mac", out var m) ? (m.GetString() ?? "") : "";
                    mac = (mac ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(mac)) continue;

                    var ok = true;
                    if (r.TryGetProperty("success", out var s))
                    {
                        if (s.ValueKind == JsonValueKind.False) ok = false;
                        else if (s.ValueKind == JsonValueKind.True) ok = true;
                    }

                    var dev = FindDiscoveredDevice(mac);
                    if (dev != null)
                        dev.BleLink = ok ? "disconnected" : "disconnect failed";
                }
            });
        }
        catch { }
    }

    private void HandleIdentifyTele(string cassia, string payload)
    {
        // Telemetry format:
        // {
        //   name, networkId, requestId,
        //   data: { stage, mac, time, errorStep?, error? }
        // }
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
                return;

            var stage = dataEl.TryGetProperty("stage", out var st) ? (st.GetString() ?? "") : "";
            var mac = dataEl.TryGetProperty("mac", out var m) ? (m.GetString() ?? "") : "";
            mac = (mac ?? "").Trim();
            if (string.IsNullOrWhiteSpace(mac)) return;

            var isFinished =
            stage.Equals("disconnected", StringComparison.OrdinalIgnoreCase) ||
            stage.Equals("failed", StringComparison.OrdinalIgnoreCase);

            // We only show the button as "active/green" after the gateway reports a successful login:
            // - logged-in
            // - login-skipped-bootmode
            var isLoggedIn =
            stage.Equals("logged-in", StringComparison.OrdinalIgnoreCase) ||
            stage.Equals("login-skipped-bootmode", StringComparison.OrdinalIgnoreCase);

            if (isLoggedIn)
            {
                _identifyConnectedByMac[mac] = true;   // "ready" marker for this request
                _identifyPendingByMac[mac] = false;    // stop pulsing
                _identifyActiveByMac[mac] = true;      // turn green
            }
            else if (isFinished)
            {
                _identifyPendingByMac[mac] = false;
                _identifyConnectedByMac[mac] = false;
                _identifyActiveByMac[mac] = false;
            }
            else
            {
                // Still working: keep pulsing until we reach logged-in / login-skipped-bootmode
                var readySeen = _identifyConnectedByMac.TryGetValue(mac, out var cs) && cs;
                _identifyActiveByMac[mac] = readySeen;

                if (!readySeen)
                    _identifyPendingByMac[mac] = true;
            }

            // Update Host BLE row immediately (button pulses while pending, turns green while active)
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_hostBleRowsByMac.TryGetValue(mac, out var row) && row != null)
                {
                    row.IsIdentifyPending = _identifyPendingByMac.TryGetValue(mac, out var ip) && ip;
                    row.IsIdentifying = _identifyActiveByMac.TryGetValue(mac, out var ia) && ia;
                }

                // Optional: show a brief status line
                if (!string.IsNullOrWhiteSpace(stage))
                {
                    var requestId = root.TryGetProperty("requestId", out var rid) ? (rid.GetString() ?? "") : "";
                    if (stage.Equals("failed", StringComparison.OrdinalIgnoreCase))
                    {
                        var step = dataEl.TryGetProperty("errorStep", out var es) ? (es.GetString() ?? "") : "";
                        var err = dataEl.TryGetProperty("error", out var ee) ? (ee.GetString() ?? "") : "";
                        ConnectionStatus = $"Identify failed {mac} {(!string.IsNullOrWhiteSpace(step) ? ("(" + step + ") ") : "")} {err}".Trim();
                    }
                    else
                    {
                        ConnectionStatus = $"Identify {mac}: {stage}{(string.IsNullOrWhiteSpace(requestId) ? "" : (" (" + requestId + ")"))}";
                    }
                }
            });
        }
        catch { }
    }

    private void HandleUpdateChannelTele(string cassia, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            var channel = root.TryGetProperty("channel", out var ch) ? (ch.GetString() ?? "") : "";
            var message = root.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";

            Application.Current.Dispatcher.Invoke(() =>
            {
                if (ok)
                    ConnectionStatus = $"[{cassia}] update channel set to '{channel}'.";
                else
                    ConnectionStatus = $"[{cassia}] set-update-channel failed: {message}";
            });
        }
        catch
        {
            // ignore malformed telemetry
        }
    }

    private void HandleMqttConfigTele(string cassia, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            var message = root.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
            var host = root.TryGetProperty("host", out var h) ? (h.GetString() ?? "") : "";
            var port = root.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
            var useTls = root.TryGetProperty("useTls", out var t) && t.ValueKind == JsonValueKind.True;

            Application.Current.Dispatcher.Invoke(() =>
            {
                if (ok)
                {
                    var summary = string.IsNullOrWhiteSpace(host) || port <= 0
                        ? "mqtt.json saved (restart required)."
                        : $"mqtt.json saved: {host}:{port} tls={useTls} (restart required).";
                    ConnectionStatus = $"[{cassia}] {summary}";
                }
                else
                {
                    ConnectionStatus = $"[{cassia}] mqtt config failed: {message}";
                }
            });
        }
        catch
        {
            // ignore malformed telemetry
        }
    }

    private void HandleLedRangeTele(string cassia, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
                return;

            var stage = dataEl.TryGetProperty("stage", out var st) ? (st.GetString() ?? "") : "";
            var mac = dataEl.TryGetProperty("mac", out var m) ? (m.GetString() ?? "") : "";
            var model = dataEl.TryGetProperty("model", out var mo) ? (mo.GetString() ?? "") : "";
            var color = dataEl.TryGetProperty("color", out var c) ? (c.GetString() ?? "") : "";
            var error = dataEl.TryGetProperty("error", out var e) ? (e.GetString() ?? "") : "";
            var rssi = dataEl.TryGetProperty("rssi", out var rs) && rs.ValueKind == JsonValueKind.Number ? rs.GetInt32() : 0;
            var chip = dataEl.TryGetProperty("chip", out var ch) && ch.ValueKind == JsonValueKind.Number ? ch.GetInt32() : 0;
            var requestId = root.TryGetProperty("requestId", out var rid) ? (rid.GetString() ?? "") : "";

            Application.Current.Dispatcher.Invoke(() =>
            {
                if (stage.Equals("connected", StringComparison.OrdinalIgnoreCase))
                {
                    UpsertLedRangeRow(LedRangeConnectedDevices, mac, model, rssi, chip, color, "connected", "");
                    RemoveLedRangeRow(LedRangeFailedDevices, mac);
                    LedRangeStatusText = $"{cassia}: connected {mac} ({color}, RSSI {rssi})";
                }
                else if (stage.Equals("failed", StringComparison.OrdinalIgnoreCase))
                {
                    UpsertLedRangeRow(LedRangeFailedDevices, mac, model, rssi, chip, color, "failed", error);
                    RemoveLedRangeRow(LedRangeConnectedDevices, mac);
                    LedRangeStatusText = $"{cassia}: failed {mac} ({error})";
                }
                else if (stage.Equals("disconnected", StringComparison.OrdinalIgnoreCase))
                {
                    RemoveLedRangeRow(LedRangeConnectedDevices, mac);
                    LedRangeStatusText = $"{cassia}: disconnected {mac}";
                }
                else if (stage.Equals("disconnect-failed", StringComparison.OrdinalIgnoreCase))
                {
                    UpsertLedRangeRow(LedRangeFailedDevices, mac, model, rssi, chip, color, "disconnect-failed", error);
                    LedRangeStatusText = $"{cassia}: disconnect failed {mac} ({error})";
                }
                else if (stage.Equals("started", StringComparison.OrdinalIgnoreCase))
                {
                    var requested = dataEl.TryGetProperty("requested", out var rq) && rq.ValueKind == JsonValueKind.Number ? rq.GetInt32() : 0;
                    var minRssi = dataEl.TryGetProperty("minRssi", out var mr) && mr.ValueKind == JsonValueKind.Number ? mr.GetInt32() : LedRangeMinRssi;
                    LedRangeRequestedTotal = requested;
                    LedRangeTriedCount = 0;
                    LedRangeConnectedCount = 0;
                    LedRangeFailedCount = 0;
                    LedRangeProgressPercent = requested > 0 ? 0 : 100;
                    LedRangeProgressText = $"0 / {requested} tried";
                    LedRangeStatusText = $"{cassia}: started, requested {requested}, min RSSI {minRssi}";
                }
                else if (stage.Equals("completed", StringComparison.OrdinalIgnoreCase))
                {
                    var requested = dataEl.TryGetProperty("requested", out var req) && req.ValueKind == JsonValueKind.Number ? req.GetInt32() : LedRangeRequestedTotal;
                    var tried = dataEl.TryGetProperty("tried", out var tr) && tr.ValueKind == JsonValueKind.Number ? tr.GetInt32() : LedRangeTriedCount;
                    var connected = dataEl.TryGetProperty("connected", out var con) && con.ValueKind == JsonValueKind.Number ? con.GetInt32() : 0;
                    var failed = dataEl.TryGetProperty("failed", out var fa) && fa.ValueKind == JsonValueKind.Number ? fa.GetInt32() : 0;
                    LedRangeRequestedTotal = requested;
                    LedRangeTriedCount = tried;
                    LedRangeConnectedCount = connected;
                    LedRangeFailedCount = failed;
                    LedRangeProgressPercent = requested > 0 ? Math.Clamp((100.0 * tried) / requested, 0, 100) : 100;
                    LedRangeProgressText = $"{tried} / {requested} tried";
                    LedRangeStatusText = $"{cassia}: completed. Connected {connected}, failed {failed}";
                }
                else if (stage.Equals("canceled", StringComparison.OrdinalIgnoreCase))
                {
                    var requested = dataEl.TryGetProperty("requested", out var req) && req.ValueKind == JsonValueKind.Number ? req.GetInt32() : LedRangeRequestedTotal;
                    var tried = dataEl.TryGetProperty("tried", out var tr) && tr.ValueKind == JsonValueKind.Number ? tr.GetInt32() : LedRangeTriedCount;
                    var connected = dataEl.TryGetProperty("connected", out var con) && con.ValueKind == JsonValueKind.Number ? con.GetInt32() : LedRangeConnectedCount;
                    var failed = dataEl.TryGetProperty("failed", out var fa) && fa.ValueKind == JsonValueKind.Number ? fa.GetInt32() : LedRangeFailedCount;
                    LedRangeRequestedTotal = requested;
                    LedRangeTriedCount = tried;
                    LedRangeConnectedCount = connected;
                    LedRangeFailedCount = failed;
                    LedRangeProgressPercent = requested > 0 ? Math.Clamp((100.0 * tried) / requested, 0, 100) : 100;
                    LedRangeProgressText = $"{tried} / {requested} tried";
                    LedRangeStatusText = $"{cassia}: canceled. Tried {tried}/{requested}.";
                }
                else if (stage.Equals("disconnect-completed", StringComparison.OrdinalIgnoreCase))
                {
                    var forceAll = dataEl.TryGetProperty("forceAll", out var faAll) && faAll.ValueKind == JsonValueKind.True;
                    var disconnected = dataEl.TryGetProperty("disconnected", out var dis) && dis.ValueKind == JsonValueKind.Number ? dis.GetInt32() : 0;
                    var failed = dataEl.TryGetProperty("failed", out var fa) && fa.ValueKind == JsonValueKind.Number ? fa.GetInt32() : 0;
                    LedRangeStatusText = forceAll
                    ? $"{cassia}: force disconnect completed. Disconnected {disconnected}, failed {failed}"
                    : $"{cassia}: disconnect completed. Disconnected {disconnected}, failed {failed}";
                }

                var requestedFromStage = dataEl.TryGetProperty("requested", out var reqStage) && reqStage.ValueKind == JsonValueKind.Number ? reqStage.GetInt32() : LedRangeRequestedTotal;
                var triedFromStage = dataEl.TryGetProperty("tried", out var trStage) && trStage.ValueKind == JsonValueKind.Number ? trStage.GetInt32() : LedRangeTriedCount;
                var connectedFromStage = dataEl.TryGetProperty("connected", out var conStage) && conStage.ValueKind == JsonValueKind.Number ? conStage.GetInt32() : LedRangeConnectedCount;
                var failedFromStage = dataEl.TryGetProperty("failed", out var faStage) && faStage.ValueKind == JsonValueKind.Number ? faStage.GetInt32() : LedRangeFailedCount;
                LedRangeRequestedTotal = requestedFromStage;
                LedRangeTriedCount = triedFromStage;
                LedRangeConnectedCount = connectedFromStage;
                LedRangeFailedCount = failedFromStage;
                LedRangeProgressPercent = requestedFromStage > 0 ? Math.Clamp((100.0 * triedFromStage) / requestedFromStage, 0, 100) : 100;
                LedRangeProgressText = $"{triedFromStage} / {requestedFromStage} tried";

                if (!string.IsNullOrWhiteSpace(requestId))
                    ConnectionStatus = $"LED range {stage} ({requestId})";
            });
        }
        catch { }
    }

    private static void UpsertLedRangeRow(ObservableCollection<LedRangeDeviceRow> rows, string mac, string model, int rssi, int chip, string color, string status, string error)
    {
        if (string.IsNullOrWhiteSpace(mac)) return;
        var row = rows.FirstOrDefault(x => x.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
        if (row == null)
        {
            row = new LedRangeDeviceRow { Mac = mac };
            rows.Add(row);
        }

        row.Model = model ?? "";
        row.Rssi = rssi;
        row.Chip = chip;
        row.Color = color ?? "";
        row.Status = status ?? "";
        row.Error = error ?? "";
    }

    private static void RemoveLedRangeRow(ObservableCollection<LedRangeDeviceRow> rows, string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return;
        var row = rows.FirstOrDefault(x => x.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
        if (row != null)
            rows.Remove(row);
    }


    private void ShowFwManifestTimeoutIfAny()
    {
        // If we have at least one manifest, don't show a timeout warning
        var haveAny = CassiaGateways.Any(g => g.HasFwManifest);
        if (haveAny) return;

        MessageBox.Show(
        "No firmware manifest received yet.\n\n" +
        "Expected one or more retained/tele messages on:\n" +
        $"  accessapp/{NetworkId}/tele/<cassia>/fw-manifest\n\n" +
        "Make sure the Cassia gateways are online and publishing manifests.",
        "FW manifest",
        MessageBoxButton.OK,
        MessageBoxImage.Warning);
    }

    private void ValidateFwManifestsAndUpdateOptions()
    {
        var union = GetUnionManifest();
        if (union.Count == 0) return;

        UpdateFirmwareOptionsFromUnion(union);

        // Check per gateway for missing versions (relative to union)
        var missingLines = new List<string>();

        foreach (var gw in CassiaGateways.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!gw.HasFwManifest) continue;

            foreach (var kv in union)
            {
                var product = kv.Key;
                var expected = kv.Value;

                if (!gw.FirmwareManifest.TryGetValue(product, out var gotArr) || gotArr == null)
                {
                    missingLines.Add($"{gw.Name}: missing {product}: {string.Join(", ", expected)}");
                    continue;
                }

                var got = new HashSet<string>(gotArr, StringComparer.OrdinalIgnoreCase);
                var miss = expected.Where(v => !got.Contains(v)).ToList();
                if (miss.Count > 0)
                    missingLines.Add($"{gw.Name}: missing {product}: {string.Join(", ", miss)}");
            }
        }

        if (missingLines.Count == 0) return;

        var hash = string.Join("|", missingLines);
        if (hash.Equals(_lastFwManifestMissingHash, StringComparison.Ordinal))
            return;

        _lastFwManifestMissingHash = hash;

        MessageBox.Show(
        "Some Cassia gateways do not contain all firmwares (compared to the union of received manifests):\n\n" +
        string.Join("\n", missingLines),
        "FW manifest mismatch",
        MessageBoxButton.OK,
        MessageBoxImage.Error);
    }

    private Dictionary<string, List<string>> GetUnionManifest()
    {
        var union = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var gw in CassiaGateways)
        {
            if (!gw.HasFwManifest) continue;

            foreach (var kv in gw.FirmwareManifest)
            {
                if (!union.TryGetValue(kv.Key, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    union[kv.Key] = set;
                }

                foreach (var v in kv.Value ?? Array.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(v))
                        set.Add(v.Trim());
            }
        }

        // Convert to sorted lists
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in union)
            result[kv.Key] = kv.Value.OrderBy(ParseFwVersionSafe).ThenBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

        return result;
    }

    private static Version ParseFwVersionSafe(string s)
    {
        // expects v02.36 etc
        if (string.IsNullOrWhiteSpace(s)) return new Version(0, 0);
        var m = Regex.Match(s.Trim(), @"^v?(\d+)\.(\d+)$", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var maj) && int.TryParse(m.Groups[2].Value, out var min))
            return new Version(maj, min);

        return new Version(0, 0);
    }

    private void UpdateFirmwareOptionsFromUnion(Dictionary<string, List<string>> union)
    {
        void apply(ObservableCollection<string> target, string key)
        {
            if (!union.TryGetValue(key, out var list) || list.Count == 0) return;

            target.Clear();
            foreach (var v in list)
                target.Add(v);
        }

        apply(FirmwareOptionsP48, "P48");
        apply(FirmwareOptionsP47, "P47");
        apply(FirmwareOptionsP46, "P46");
        apply(FirmwareOptionsP41, "P41");
        apply(FirmwareOptionsP42, "P42");

        // Preserve user selection if it still exists; otherwise fall back to latest.
        SelectedFirmwareP48 = PreserveFirmwareSelection(FirmwareOptionsP48, SelectedFirmwareP48);
        SelectedFirmwareP47 = PreserveFirmwareSelection(FirmwareOptionsP47, SelectedFirmwareP47);
        SelectedFirmwareP46 = PreserveFirmwareSelection(FirmwareOptionsP46, SelectedFirmwareP46);
        SelectedFirmwareP41 = PreserveFirmwareSelection(FirmwareOptionsP41, SelectedFirmwareP41);
        SelectedFirmwareP42 = PreserveFirmwareSelection(FirmwareOptionsP42, SelectedFirmwareP42);
    }


    private void FlushBufferedProgressOnUi()
    {
        List<BufferedProgress> batch;
        lock (_progressBufLock)
        {
            if (_progressByMac.Count == 0) return;
            batch = _progressByMac.Values.ToList();
            _progressByMac.Clear();
        }

        var anyQueueChanged = false;

        foreach (var p in batch)
        {
            var pctRounded = (int)Math.Round(p.ProgressPercent, 0);

            // Protect terminal completion state from being overwritten by late/duplicate progress=100 "Programming" updates.
            var cs = GetOrCreateCache(p.Mac);

            if (cs.IsUpgradeSuccess && cs.LastUpgradeSuccessUtc.HasValue)
            {
                // Older than completion -> ignore
                if (p.TimeUtc <= cs.LastUpgradeSuccessUtc.Value)
                    continue;

                // New run starts -> clear completion
                if (LooksLikeNewRunStage(p.Stage, pctRounded))
                {
                    cs.IsUpgradeSuccess = false;
                    cs.LastUpgradeSuccessUtc = null;
                    cs.LastTargetFw = "";
                }
                else if (pctRounded >= 100 && IsNonTerminalStage(p.Stage)
                && string.Equals(cs.ProcessStatus?.Trim(), "Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase))
                {
                    // Late progress=100 "Programming"/etc after completion -> ignore
                    continue;
                }
            }

            // Keep FW field as target firmware (not model)
            if (LooksLikeFirmwareVersion(p.FirmwareTarget))
                cs.ProcessFirmware = p.FirmwareTarget;

            if (!string.IsNullOrWhiteSpace(p.Cassia))
                cs.ProcessCassia = p.Cassia;

            if (!string.IsNullOrWhiteSpace(p.Stage))
                cs.ProcessStatus = p.Stage;

            cs.ProcessProgress = pctRounded;
            cs.LastUpdateUtc = p.TimeUtc;

            // Update discovered device if present (apply cached so timestamp rules are respected)
            if (_deviceByMac.TryGetValue(p.Mac, out var dev))
                ApplyCachedStatusToDevice(dev);

            // Update queue item (keyed by mac)
            var qi = QueueItems.FirstOrDefault(x => x.Mac.Equals(p.Mac, StringComparison.OrdinalIgnoreCase));
            if (qi == null)
            {
                qi = new QueueItem { Mac = p.Mac };
                QueueItems.Add(qi);
                anyQueueChanged = true;
            }

            // Only apply if newer than the current queue row
            if (qi.LastUpdateUtc != default && p.TimeUtc < qi.LastUpdateUtc)
                continue;

            qi.Cassia = cs.ProcessCassia ?? "";
            qi.FirmwareVersion = LooksLikeFirmwareVersion(cs.ProcessFirmware) ? cs.ProcessFirmware : qi.FirmwareVersion;

            // If we already know the device completed successfully, keep the queue row "Done".
            if (cs.IsUpgradeSuccess && string.Equals(cs.ProcessStatus?.Trim(), "Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase))
            {
                qi.Status = "Done";
                qi.Progress = 100;
            }
            else
            {
                qi.Status = cs.ProcessStatus ?? "";
                qi.Progress = pctRounded;
            }

            qi.LastUpdateUtc = p.TimeUtc;
        }

        if (anyQueueChanged)
            RequestQueueRefresh();
        else
            RequestQueueRefresh();
    }




    [RelayCommand]
    private void RefreshUiNow()
    {
        try
        {
            // Re-apply cached status to all known devices (cheap)
            foreach (var d in _devices)
                ApplyCachedStatusToDevice(d);

            FilteredDevices.Refresh();
            QueueView.Refresh();
            MarkLatestUpgradeLogMapDirty();
            RequestUpgradeLogViewRefresh();
        }
        catch { }
    }

    [RelayCommand]
    private void RemoveRssiMinus127Devices()
    {
        try
        {
            var toRemove = _devices.Where(d => d.BestRssi <= -127).ToList();
            foreach (var d in toRemove)
            {
                _devices.Remove(d);
                _deviceByMac.Remove(d.Mac);
            }
            FilteredDevices.Refresh();
        }
        catch { }
    }

    [RelayCommand]
    private void RemoveCompletedFromQueue()
    {
        try
        {
            var done = QueueItems.Where(q =>
            q.IsDone
            || string.Equals(q.Status?.Trim(), "Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(q.Status?.Trim(), "Success", StringComparison.OrdinalIgnoreCase))
            .ToList();

            foreach (var q in done)
                QueueItems.Remove(q);

            RequestQueueRefresh();
        }
        catch { }
    }

    [RelayCommand]
    private void ExportUpgradeLogToExcel()
    {
        try
        {
            // Export from CURRENT VIEW (what the operator sees)
            var groups = UpgradeLogGroupsView.Cast<object>()
            .OfType<UpgradeLogGroup>()
            .OrderByDescending(g => g.LastTimeLocal)
            .ToList();

            if (groups.Count == 0)
            {
                MessageBox.Show("No upgrade log entries to export (current view is empty).", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = $"upgrade-log_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
            };

            if (dlg.ShowDialog() != true)
                return;

            using var wb = new ClosedXML.Excel.XLWorkbook();

            // ---------------- Summary ----------------
            var ws1 = wb.Worksheets.Add("Summary");
            ws1.Cell(1, 1).Value = "Cassia";
            ws1.Cell(1, 2).Value = "MAC";
            ws1.Cell(1, 3).Value = "Name";
            ws1.Cell(1, 4).Value = "LogId";
            ws1.Cell(1, 5).Value = "Started time";
            ws1.Cell(1, 6).Value = "Last time";
            ws1.Cell(1, 7).Value = "Old FW";
            ws1.Cell(1, 8).Value = "FW (target)";
            ws1.Cell(1, 9).Value = "Latest stage";
            ws1.Cell(1, 10).Value = "Latest status";
            ws1.Cell(1, 11).Value = "Has newer entry";
            ws1.Cell(1, 12).Value = "Summary";

            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                var r = i + 2;
                ws1.Cell(r, 1).Value = g.Cassia;
                ws1.Cell(r, 2).Value = g.Mac;
                ws1.Cell(r, 3).Value = g.LatestDeviceName;
                ws1.Cell(r, 4).Value = g.LogId;
                ws1.Cell(r, 5).Value = g.StartedAtLocalText;
                ws1.Cell(r, 6).Value = g.LastTimeLocalText;
                ws1.Cell(r, 7).Value = g.OldFirmwareText;
                ws1.Cell(r, 8).Value = g.TargetFirmware;
                ws1.Cell(r, 9).Value = g.LatestStage;
                ws1.Cell(r, 10).Value = g.LatestStatus;
                ws1.Cell(r, 11).Value = g.HasNewerForMac ? "Yes" : "No";
                ws1.Cell(r, 12).Value = g.LatestSummary;
            }

            ws1.Columns().AdjustToContents();
            ws1.Column(12).Width = 80;

            // ---------------- Details ----------------
            var ws2 = wb.Worksheets.Add("Details");
            ws2.Cell(1, 1).Value = "Cassia";
            ws2.Cell(1, 2).Value = "MAC";
            ws2.Cell(1, 3).Value = "Name";
            ws2.Cell(1, 4).Value = "LogId";
            ws2.Cell(1, 5).Value = "Time";
            ws2.Cell(1, 6).Value = "Stage";
            ws2.Cell(1, 7).Value = "Status";
            ws2.Cell(1, 8).Value = "Display status";
            ws2.Cell(1, 9).Value = "Firmware";
            ws2.Cell(1, 10).Value = "Line";

            int row = 2;
            foreach (var g in groups)
            {
                foreach (var e in g.Entries.OrderBy(x => x.TimeLocal))
                {
                    ws2.Cell(row, 1).Value = e.Cassia;
                    ws2.Cell(row, 2).Value = e.Mac;
                    ws2.Cell(row, 3).Value = e.DeviceName;
                    ws2.Cell(row, 4).Value = e.LogId;
                    ws2.Cell(row, 5).Value = e.TimeLocalText;
                    ws2.Cell(row, 6).Value = e.Stage;
                    ws2.Cell(row, 7).Value = e.Status;
                    ws2.Cell(row, 8).Value = e.DisplayStatus;
                    ws2.Cell(row, 9).Value = e.Firmware;
                    ws2.Cell(row, 10).Value = e.Line;
                    row++;
                }
            }

            ws2.Columns(1, 9).AdjustToContents();
            ws2.Column(10).Width = 120;

            wb.SaveAs(dlg.FileName);

            MessageBox.Show("Export completed.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Export failed: " + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

}


