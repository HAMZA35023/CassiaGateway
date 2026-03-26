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
// ---- MQTT parsing ----
    // accessapp/dk-lab/tele/cassia-01/status
    // accessapp/dk-lab/tele/cassia-01/discovered
    // accessapp/dk-lab/tele/cassia-01/progress
    private static readonly Regex TopicRx =
        new(@"^accessapp/(?<net>[^/]+)/(?<kind>tele|cmd)/(?<cassia>[^/]+)/(?<leaf>[^/]+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Upgrade-log / text-line parsing
    private static readonly Regex LogLineMacRx =
        new(@"\bmac=(?<mac>([0-9A-F]{2}:){5}[0-9A-F]{2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LogLineStageRx =
        new(@"\bstage=(?<stage>.*?)\s+time=", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LogLineStatusRx =
        new(@"\bstatus=(?<status>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LogLineFwRx =
        new(@"\bfw=(?<fw>[^\s]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LogLineNameRx =
        new(@"\bname=(?<name>[^\s]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LogLineDetectorRx =
        new(@"\bdetector=(?<det>[^\s]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SensorAppFromStatusRx =
        new(@"Sensor:\s*App:\s*(?<app>[^\s|]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LogLineIdRx =
        new(@"\[logId=(?<id>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LogLineTimeRx =
        new(@"\btime=(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LogLineChipUsedRx =
        new(@"\busing(?:\s+chip)?\s+(?<c>hci\d+|all|\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string ExtractDeviceName(string? stage, string? status, string? nameFromLine)
    {
        if (!string.IsNullOrWhiteSpace(nameFromLine))
            return nameFromLine.Trim();

        var st = (stage ?? "").Trim();
        if (st.Equals("Device Name", StringComparison.OrdinalIgnoreCase)
            || st.Equals("Detector Name", StringComparison.OrdinalIgnoreCase))
            return (status ?? "").Trim();

        return "";
    }

    private static int? TryReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var num))
            return num;

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var fromText))
            return fromText;

        return null;
    }

    private void HandlePlainReplyPayload(string payload)
    {
        // We accept:
        //   "AA:BB:..: connect OK"
        //   "[info] AA:BB:..: disconnect OK"
        //   "\"AA:BB:..: notif=01-10-...\"" (quoted)
        // and we handle multiple lines in one payload.
        try
        {
            var text = payload ?? "";
            foreach (var raw in text.Split(new[] { "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                line = line.Trim().Trim('"');

                var mm = PlainReplyMacRx.Match(line);
                if (!mm.Success) continue;

                var mac = mm.Groups["mac"].Value.ToUpperInvariant();

                // Message is whatever comes after the MAC (optionally preceded by ':')
                var after = line.Substring(mm.Index + mm.Length).TrimStart();
                if (after.StartsWith(":")) after = after.Substring(1).TrimStart();
                var msg = after.Length > 0 ? after : line; // fallback

                // Always update on UI thread so subscribers can safely update ObservableCollections.
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    SetDeviceBleLinkFromPlainReply(mac, msg);
                    PlainReplyReceived?.Invoke(mac, msg);
                }));
            }
        }
        catch { /* ignore */ }
    }

    private void OnMqttMessage(string topic, string payload)
    {
        // Learn available scopes from incoming topics so the user can switch quickly.
        RegisterObservedScopeFromTopic(topic);

        var m = TopicRx.Match(topic);
        if (!m.Success)
        {
            HandlePlainReplyPayload(payload);
            return;
        }

        var net = m.Groups["net"].Value;

        // When local MQTT is active and no gateways are known yet, auto-adopt the first network seen.
        if (_localMqttActive && CassiaGateways.Count == 0
            && m.Groups["kind"].Value.Equals("tele", StringComparison.OrdinalIgnoreCase)
            && m.Groups["leaf"].Value.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (CassiaGateways.Count == 0)
                    NetworkId = net;
            });
        }

        if (!net.Equals(NetworkId, StringComparison.OrdinalIgnoreCase))
            return;

        var kind = m.Groups["kind"].Value.ToLowerInvariant();
        var cassia = m.Groups["cassia"].Value;
        var leaf = m.Groups["leaf"].Value.ToLowerInvariant();

        HandlePlainReplyPayload(payload);

        if (kind == "tele" && leaf == "status")
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? cassia : cassia;
                var version = root.TryGetProperty("version", out var verEl) ? (verEl.GetString() ?? "") : "";
                var state = root.TryGetProperty("state", out var s) ? s.GetString() ?? "unknown" : "unknown";
                var ts = root.TryGetProperty("time", out var t) && t.TryGetDateTimeOffset(out var dto) ? dto : DateTimeOffset.UtcNow;
                int queue = root.TryGetProperty("queue", out var q) ? q.GetInt32() : 0;
                int programming = root.TryGetProperty("programming", out var pr) ? pr.GetInt32() : 0;
                double totalSpeedpct = root.TryGetProperty("totalSpeedpct", out var sp) ? sp.GetDouble() : 0;
                var bleBackend = root.TryGetProperty("backend", out var bb) ? (bb.GetString() ?? "") : "";
                var cellularState = root.TryGetProperty("cellularState", out var cs) ? (cs.GetString() ?? "") : "";
                var cellularNetworkType = root.TryGetProperty("cellularNetworkType", out var cnt) ? (cnt.GetString() ?? "") : "";
                var cellularProvider = root.TryGetProperty("cellularProvider", out var cp) ? (cp.GetString() ?? "") : "";
                var cellularSignalBar = TryReadInt(root, "cellularSignalBar");
                var cellularRssiDbm = TryReadInt(root, "cellularRssiDbm");
                var cellularLteRsrpDbm = TryReadInt(root, "cellularLteRsrpDbm");
                var cellularLteRsrqDb = TryReadInt(root, "cellularLteRsrqDb");
                var cellularLteSnrDb = TryReadInt(root, "cellularLteSnrDb");
                long uptimeSeconds = 0;
                if (root.TryGetProperty("uptimeSeconds", out var upEl))
                {
                    if (upEl.ValueKind == JsonValueKind.Number) uptimeSeconds = upEl.GetInt64();
                    else if (upEl.ValueKind == JsonValueKind.String && long.TryParse(upEl.GetString(), out var uv)) uptimeSeconds = uv;
                }
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var gw = CassiaGateways.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (gw == null)
                    {
                        gw = new CassiaGateway { Name = name, NetworkId = net };
                        RestoreSpeedHistoryIfPresent(gw);
                        CassiaGateways.Add(gw);
                        SortCassiaGatewaysByName();
                        SortCassiaGatewaysByName();
                    }

                    EnsureCassiaOption(name);

                    // default for upgrade log tab
                    if (SelectedLogGateway == null)
                        SelectedLogGateway = gw;

                    if (!LogGatewayOptions.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        LogGatewayOptions.Add(name);

                    gw.State = state;
                    gw.Version = version;
                    gw.LastSeenUtc = ts;
                    gw.Queue = queue;
                    gw.Programming = programming;
                    gw.TotalSpeedpct = totalSpeedpct;
                    gw.AddSpeedSample(ts, totalSpeedpct);
                    if (!string.IsNullOrWhiteSpace(bleBackend)) gw.BleBackend = bleBackend;
                    gw.CellularState = cellularState;
                    gw.CellularNetworkType = cellularNetworkType;
                    gw.CellularProvider = cellularProvider;
                    gw.CellularSignalBar = cellularSignalBar;
                    gw.CellularRssiDbm = cellularRssiDbm;
                    gw.CellularLteRsrpDbm = cellularLteRsrpDbm;
                    gw.CellularLteRsrqDb = cellularLteRsrqDb;
                    gw.CellularLteSnrDb = cellularLteSnrDb;
                    if (uptimeSeconds > 0)
                    {
                        gw.UptimeSeconds = uptimeSeconds;
                        gw.UptimeReportedUtc = ts;
                    }


                    // When a gateway announces itself, ask it for FW manifest once per connect.
                    MaybeAutoRequestFirmwareManifestAfterStatus(gw);

                    // Also request runtime snapshot (queue / programming / parallel programmers) so the UI can reconnect mid-run.
                    MaybeAutoRequestRuntimeStateAfterStatus(gw);

                    MaybeAutoRequestDeviceListAfterStatus(gw);

                    MaybeAutoRequestUpgradeLogAfterStatus(gw);

                });
            }
            catch { }
            return;
        }

        if (kind == "tele" && leaf == "upgrade-log")
        {
            HandleUpgradeLogTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "fw-manifest")
        {
            HandleFwManifestTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "device-list")
        {
            HandleDeviceListTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "clear-device-list")
        {
            HandleClearDeviceListTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "queue-remove")
        {
            HandleQueueRemoveTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "queue-list")
        {
            HandleQueueListTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "programming-list")
        {
            HandleProgrammingListTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "parallel-programmers")
        {
            HandleParallelProgrammersTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "runtime")
        {
            HandleRuntimeVariablesTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "fw-version")
        {
            HandleFwVersionTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "pir-peak")
        {
            HandlePirPeakTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "walktest")
        {
            HandleWalktestTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "disconnect")
        {
            HandleDisconnectTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "identify")
        {
            HandleIdentifyTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "update-channel")
        {
            HandleUpdateChannelTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "reboot")
        {
            HandleRebootTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "mqtt-config")
        {
            HandleMqttConfigTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "cassia-settings")
        {
            HandleCassiaSettingsTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "detector-settings")
        {
            HandleDetectorSettingsTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "led-range")
        {
            HandleLedRangeTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "shell")
        {
            RaiseShellResponse(cassia, payload);
            return;
        }


        
if (kind == "tele" && leaf == "progress")
        {
            // { mac, progressPercent, stage, time, firmwareTarget, ... }
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                // Use local arrival time for ordering to avoid gateway clock skew issues.
                var ts = DateTimeOffset.UtcNow;

                var mac = root.TryGetProperty("mac", out var macEl) ? (macEl.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(mac))
                    return;

                var stage = root.TryGetProperty("stage", out var stEl) ? (stEl.GetString() ?? "") : "";
                var fwTarget = root.TryGetProperty("firmwareTarget", out var ftEl) ? (ftEl.GetString() ?? "") : "";
                var chipUsed = "";

                if (root.TryGetProperty("chipUsed", out var cuEl))
                {
                    if (cuEl.ValueKind == JsonValueKind.String)
                        chipUsed = (cuEl.GetString() ?? "").Trim();
                    else if (cuEl.ValueKind == JsonValueKind.Number && cuEl.TryGetInt32(out var cuNum))
                        chipUsed = cuNum.ToString();
                }

                if (string.IsNullOrWhiteSpace(chipUsed) && root.TryGetProperty("chip", out var chEl))
                {
                    if (chEl.ValueKind == JsonValueKind.String)
                        chipUsed = (chEl.GetString() ?? "").Trim();
                    else if (chEl.ValueKind == JsonValueKind.Number && chEl.TryGetInt32(out var chNum))
                        chipUsed = chNum.ToString();
                }

                if (string.IsNullOrWhiteSpace(chipUsed) && !string.IsNullOrWhiteSpace(stage))
                {
                    var cm = LogLineChipUsedRx.Match(stage);
                    if (cm.Success)
                        chipUsed = cm.Groups["c"].Value.Trim();
                }

                double pct = 0;
                if (root.TryGetProperty("progressPercent", out var pEl))
                {
                    if (pEl.ValueKind == JsonValueKind.Number) pct = pEl.GetDouble();
                    else if (pEl.ValueKind == JsonValueKind.String && double.TryParse(pEl.GetString(), out var pd)) pct = pd;
                }

                double? speedPctPerMin = null;
                if (root.TryGetProperty("speedPctPerMin", out var spEl))
                {
                    if (spEl.ValueKind == JsonValueKind.Number) speedPctPerMin = spEl.GetDouble();
                    else if (spEl.ValueKind == JsonValueKind.String && double.TryParse(spEl.GetString(), out var sd)) speedPctPerMin = sd;
                }

                lock (_progressBufLock)
                {
                    if (!_progressByMac.TryGetValue(mac, out var bp))
                    {
                        bp = new BufferedProgress { Mac = mac };
                        _progressByMac[mac] = bp;
                    }
                    else if (bp.TimeUtc != DateTimeOffset.MinValue && ts < bp.TimeUtc)
                    {
                        // Ignore stale out-of-order progress samples for this MAC.
                        return;
                    }

                    bp.Cassia = cassia;
                    bp.Stage = stage;
                    bp.QueueStatus = "";
                    bp.FirmwareTarget = fwTarget;
                    bp.HasProgressPercent = true;
                    bp.ProgressPercent = pct;
                    bp.HasSpeedPctPerMin = true;
                    bp.SpeedPctPerMin = speedPctPerMin;
                    bp.ClearSpeed = false;
                    if (!string.IsNullOrWhiteSpace(chipUsed))
                        bp.ChipUsed = chipUsed;
                    bp.TimeUtc = ts;
                }
            }
            catch { }
            return;
        }

        if (kind == "tele" && leaf == "discovered")
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                var ts = root.TryGetProperty("time", out var t) && t.TryGetDateTimeOffset(out var dto) ? dto : DateTimeOffset.UtcNow;

                if (root.TryGetProperty("devices", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var gw = CassiaGateways.FirstOrDefault(x => x.Name.Equals(cassia, StringComparison.OrdinalIgnoreCase));
                        if (gw == null)
                        {
                            gw = new CassiaGateway { Name = cassia, NetworkId = net };
                            CassiaGateways.Add(gw);
                            SortCassiaGatewaysByName();
                        }

                        EnsureCassiaOption(gw.Name);

                    EnsureCassiaOption(cassia);

                        if (!LogGatewayOptions.Any(x => x.Equals(cassia, StringComparison.OrdinalIgnoreCase)))
                            LogGatewayOptions.Add(cassia);

                        gw.LastSeenUtc = ts;
                        gw.State = "online";

                        if (!_gwSeenMacs.TryGetValue(cassia, out var seen))
                        {
                            seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            _gwSeenMacs[cassia] = seen;
                        }

                        foreach (var dev in arr.EnumerateArray())
                        {
                            var mac = dev.TryGetProperty("mac", out var macEl) ? macEl.GetString() ?? "" : "";
                            if (string.IsNullOrWhiteSpace(mac)) continue;

                            // Track unique MACs per gateway
                            seen.Add(mac);

                            var rssi = dev.TryGetProperty("rssi", out var rssiEl) && rssiEl.TryGetInt32(out var r) ? r : int.MinValue;
                            var dn = dev.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                            var pn = dev.TryGetProperty("productNumber", out var pnEl) ? pnEl.GetString() ?? "" : "";
                            var fam = dev.TryGetProperty("detectorFamily", out var famEl) ? famEl.GetString() ?? "" : "";
                            var typ = dev.TryGetProperty("detectorType", out var typEl) ? typEl.GetString() ?? "" : "";

                            
if (!_deviceByMac.TryGetValue(mac, out var existing))
{
    existing = new DiscoveredDevice { Mac = mac };
    _deviceByMac[mac] = existing;
    _devices.Add(existing);
}

                            EnsureDeviceAssignmentWiring(existing);
                            ApplyCachedStatusToDevice(existing);

                            ApplyDeviceNameWithGuards(existing, dn);
                            if (!string.IsNullOrWhiteSpace(fam)) existing.DetectorFamily = fam;
                            if (!string.IsNullOrWhiteSpace(typ)) existing.DetectorType = typ;

                            if (!string.IsNullOrWhiteSpace(pn))
                            {
                                existing.ProductNumber = pn;
                                if (_productToModel.TryGetValue(pn, out var model))
                                    existing.SensorModel = model;
                            }
                            else if (!string.IsNullOrWhiteSpace(existing.ProductNumber) && _productToModel.TryGetValue(existing.ProductNumber, out var model2))
                            {
                                existing.SensorModel = model2;
                            }

                            existing.UpdateFromCassia(cassia, rssi, ts);
                            UpdateQueueRssiForMac(mac);
                            EnsureStickyAssignment(existing);
                        }


                        // show unique count since last clear
                        gw.DevicesSeen = seen.Count;

                        // Update per-gateway assignment counts
                        RecalculateAssignmentCounts();

                        RequestDevicesRefresh();
                        OnPropertyChanged(nameof(DevicesSubtitle));
                    });
                }
            }
            catch { }
            return;
        }
    }

    private void SetDeviceBleLinkFromPlainReply(string mac, string msg)
    {
        if (string.IsNullOrWhiteSpace(mac)) return;
        var d = _devices.FirstOrDefault(x => string.Equals(x.Mac, mac, StringComparison.OrdinalIgnoreCase));
        if (d == null) return;

        // Normalize a compact status for the grid
        var lower = (msg ?? "").ToLowerInvariant();
        if (lower.StartsWith("connect"))
            d.BleLink = msg;
        else if (lower.StartsWith("disconnect"))
            d.BleLink = msg;
        else if (lower.StartsWith("write"))
            d.BleLink = msg;
        else if (lower.StartsWith("notif"))
            d.BleLink = "notif";
        else if (lower.Contains("timeout"))
            d.BleLink = "timeout";
        else
            d.BleLink = msg;
    }

}
