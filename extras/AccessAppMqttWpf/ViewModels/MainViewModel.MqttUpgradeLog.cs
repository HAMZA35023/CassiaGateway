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
    private void HandleUpgradeLogTele(string cassia, string payload)
    {
        _ = HandleUpgradeLogTeleAsync(cassia, payload);
    }

    private async Task HandleUpgradeLogTeleAsync(string cassia, string payload)
    {
        // Example messages seen in mqtt.log:
        //  {"type":"saved-log-begin","totalLines":2340,"timeLocal":"2026-01-12 15:48:28"}
        //  {"type":"saved-log-chunk","seq":0,"lines":["..."]}
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
            type = type.Trim().ToLowerInvariant();

            List<string>? compressedLines = null;
            int compressedTotalLines = 0;
            string compressedTimeLocal = "";

            if (type == "saved-log-gzip" &&
            TryGetSavedLogLinesFromCompressedPayload(root, out var linesFromCompressed, out var totalLinesFromCompressed, out var timeLocalFromCompressed))
            {
                compressedLines = linesFromCompressed;
                compressedTotalLines = totalLinesFromCompressed;
                compressedTimeLocal = timeLocalFromCompressed;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // Default the gateway picker (handy when you only have one cassia)
                SelectedLogGateway ??= CassiaGateways.FirstOrDefault();
            });

            if (type == "saved-log-gzip" && compressedLines != null)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (UpgradeLogLines.Count == 0)
                    {
                        UpgradeLogLines.Clear();
                        _upgradeLogSb.Clear();
                    }
                    else
                    {
                        var sep = $"----- {cassia} saved-log-gzip -----";
                        UpgradeLogLines.Add(sep);
                        _upgradeLogSb.AppendLine(sep);
                    }

                    UpgradeLogTotalLines = compressedTotalLines > 0 ? compressedTotalLines : compressedLines.Count;
                    UpgradeLogReceivedLines = 0;
                    UpgradeLogStatus = string.IsNullOrWhiteSpace(compressedTimeLocal)
                    ? $"Receiving full log from {cassia}..."
                    : $"Receiving full log from {cassia}... (saved {compressedTimeLocal})";
                    UpgradeLogText = "";
                });

                await AppendUpgradeLogLinesInBatchesAsync(cassia, compressedLines);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    UpgradeLogStatus = UpgradeLogTotalLines > 0
                    ? $"Done ({UpgradeLogReceivedLines}/{UpgradeLogTotalLines} lines)"
                    : $"Done ({UpgradeLogReceivedLines} lines)";
                    RequestUpgradeLogTextRefresh();
                });
                return;
            }

            if (type == "saved-log-begin")
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // When requesting from multiple gateways, each gateway will send a begin.
                    // Only clear if this is the first begin in the current view.
                    if (UpgradeLogLines.Count == 0)
                    {
                        UpgradeLogLines.Clear();
                        _upgradeLogSb.Clear();
                    }
                    else
                    {
                        var sep = $"----- {cassia} saved-log-begin -----";
                        UpgradeLogLines.Add(sep);
                        _upgradeLogSb.AppendLine(sep);
                    }

                    UpgradeLogTotalLines = root.TryGetProperty("totalLines", out var tl) && tl.TryGetInt32(out var total) ? total : 0;
                    UpgradeLogReceivedLines = 0;
                    var timeLocal = root.TryGetProperty("timeLocal", out var tlc) ? (tlc.GetString() ?? "") : "";
                    UpgradeLogStatus = string.IsNullOrWhiteSpace(timeLocal)
                    ? $"Receiving log from {cassia}�"
                    : $"Receiving log from {cassia}� (saved {timeLocal})";

                    UpgradeLogText = "";
                });
                return;
            }

            if (type == "saved-log-chunk")
            {
                if (root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
                {
                    var lines = new List<string>();
                    foreach (var le in linesEl.EnumerateArray())
                    {
                        var line = le.GetString() ?? "";
                        if (!string.IsNullOrEmpty(line)) lines.Add(line);
                    }

                    await AppendUpgradeLogLinesInBatchesAsync(cassia, lines);

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        RequestUpgradeLogTextRefresh();
                        UpgradeLogStatus = UpgradeLogTotalLines > 0
                        ? $"Receiving� {UpgradeLogReceivedLines}/{UpgradeLogTotalLines} lines"
                        : $"Receiving� {UpgradeLogReceivedLines} lines";
                    });
                }
                return;
            }

            // Some deployments publish a single JSON log entry (no "type"):
            // {
                //   "logId":"...", "mac":"..", "stage":"...", "status":"...", "fw":"...", "timeLocal":"...", "line":"[...]"
                // }
                if (string.IsNullOrWhiteSpace(type) && root.ValueKind == JsonValueKind.Object)
                {
                    if (TryAddUpgradeLogEntryFromJson(cassia, root, out var line2))
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (!string.IsNullOrWhiteSpace(line2))
                            {
                                UpgradeLogLines.Add(line2);
                                _upgradeLogSb.AppendLine(line2);
                                UpgradeLogReceivedLines++;
                                ApplyStatusFromUpgradeLogLine(cassia, line2);
                                ApplyLiveProcessStatusFromUpgradeLogLine(cassia, line2);
                                RequestUpgradeLogTextRefresh();
                            }
                            UpgradeLogStatus = "upgrade-log";
                        });
                        return;
                    }
                }

                if (type == "saved-log-end")
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        UpgradeLogStatus = UpgradeLogTotalLines > 0
                        ? $"Done ({UpgradeLogReceivedLines}/{UpgradeLogTotalLines} lines)"
                        : $"Done ({UpgradeLogReceivedLines} lines)";
                        RequestUpgradeLogTextRefresh();
                    });
                    return;
                }

                // fallback
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    UpgradeLogStatus = string.IsNullOrWhiteSpace(type) ? "upgrade-log" : type;
                });
            }
            catch
            {
                // ignore malformed chunks
            }
        }

        private async Task AppendUpgradeLogLinesInBatchesAsync(string cassia, IEnumerable<string> lines)
        {
            const int batchSize = 200;
            var batch = new List<string>(batchSize);

            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                batch.Add(line);

                if (batch.Count >= batchSize)
                {
                    var chunk = batch.ToArray();
                    batch.Clear();
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var l in chunk)
                        {
                            UpgradeLogLines.Add(l);
                            _upgradeLogSb.AppendLine(l);
                            UpgradeLogReceivedLines++;

                            AddUpgradeLogEntryFromLine(cassia, l);
                            ApplyStatusFromUpgradeLogLine(cassia, l);
                        }
                    });
                    await Task.Delay(1);
                }
            }

            if (batch.Count > 0)
            {
                var chunk = batch.ToArray();
                batch.Clear();
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var l in chunk)
                    {
                        UpgradeLogLines.Add(l);
                        _upgradeLogSb.AppendLine(l);
                        UpgradeLogReceivedLines++;

                        AddUpgradeLogEntryFromLine(cassia, l);
                        ApplyStatusFromUpgradeLogLine(cassia, l);
                    }
                });
            }
        }

        private static bool TryGetSavedLogLinesFromCompressedPayload(JsonElement root, out List<string> lines, out int totalLines, out string timeLocal)
        {
            lines = new List<string>();
            totalLines = 0;
            timeLocal = "";

            try
            {
                var encoded = root.TryGetProperty("data", out var dataEl) ? (dataEl.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(encoded))
                return false;

                totalLines = root.TryGetProperty("totalLines", out var tl) && tl.TryGetInt32(out var total) ? total : 0;
                timeLocal = root.TryGetProperty("timeLocal", out var tlc) ? (tlc.GetString() ?? "") : "";

                var compressedBytes = Convert.FromBase64String(encoded);
                using var input = new MemoryStream(compressedBytes);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                var text = Encoding.UTF8.GetString(output.ToArray());

                if (string.IsNullOrEmpty(text))
                return true;

                lines = text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void AddUpgradeLogEntryFromLine(string cassia, string line)
        {
            try
            {
                var idm = LogLineIdRx.Match(line);
                if (!idm.Success) return;
                var logId = idm.Groups["id"].Value.Trim();
                if (string.IsNullOrWhiteSpace(logId)) return;

                var macm = LogLineMacRx.Match(line);
                var mac = macm.Success ? macm.Groups["mac"].Value.Trim() : "";

                var stagem = LogLineStageRx.Match(line);
                var stage = stagem.Success ? stagem.Groups["stage"].Value.Trim() : "";

                var statusm = LogLineStatusRx.Match(line);
                var status = statusm.Success ? statusm.Groups["status"].Value.Trim() : "";

                if (!string.IsNullOrWhiteSpace(status) && status.Trim().Equals("success", StringComparison.OrdinalIgnoreCase))
                status = "Success";

                var fwm = LogLineFwRx.Match(line);
                var fw = fwm.Success ? fwm.Groups["fw"].Value.Trim() : "";

                var nameFromLine = "";
                var nm = LogLineNameRx.Match(line);
                if (nm.Success) nameFromLine = nm.Groups["name"].Value.Trim();

                if (string.IsNullOrWhiteSpace(nameFromLine))
                {
                    var detm = LogLineDetectorRx.Match(line);
                    nameFromLine = detm.Success ? detm.Groups["det"].Value.Trim() : "";
                }

                var timem = LogLineTimeRx.Match(line);
                var timeLocal = ParseLocalTime(timem.Success ? timem.Groups["time"].Value : null);

                var entry = new UpgradeLogEntry
                {
                    Cassia = cassia,
                    LogId = logId,
                    Mac = mac,
                    Stage = stage,
                    Status = status,
                    Firmware = fw,
                    DeviceName = ExtractDeviceName(stage, status, nameFromLine),
                    TimeLocal = timeLocal,
                    Line = line
                };

                AddUpgradeLogEntry(entry);
            }
            catch
            {
                // ignore
            }
        }

        private bool TryAddUpgradeLogEntryFromJson(string cassia, JsonElement root, out string line)
        {
            line = "";
            try
            {
                var logId = root.TryGetProperty("logId", out var idEl) ? (idEl.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(logId)) return false;

                var mac = root.TryGetProperty("mac", out var macEl) ? (macEl.GetString() ?? "") : "";
                var stage = root.TryGetProperty("stage", out var stEl) ? (stEl.GetString() ?? "") : "";
                var status = root.TryGetProperty("status", out var sEl) ? (sEl.GetString() ?? "") : "";
                var name = root.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "") : "";
                var detector = root.TryGetProperty("detector", out var detEl) ? (detEl.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(name)) name = detector;
                var fw = root.TryGetProperty("fw", out var fwEl) ? (fwEl.GetString() ?? "") : "";
                var timeStr = root.TryGetProperty("timeLocal", out var tlEl) ? (tlEl.GetString() ?? "") : "";
                line = root.TryGetProperty("line", out var lEl) ? (lEl.GetString() ?? "") : "";

                if (string.IsNullOrWhiteSpace(line))
                {
                    // Fallback recreate a readable line
                    var namePart = string.IsNullOrWhiteSpace(name) ? "" : $" name={name}";
                    line = $"[logId={logId}] stage={stage} time={timeStr} mac={mac}{namePart} fw={fw} status={status}";
                }
                else
                {
                    // Keep line parser-compatible even when gateway sends a compact/non-keyed "line".
                    if (!string.IsNullOrWhiteSpace(logId) && !LogLineIdRx.IsMatch(line))
                        line = $"[logId={logId}] {line}";
                    if (!string.IsNullOrWhiteSpace(mac) && !LogLineMacRx.IsMatch(line))
                        line = $"{line} mac={mac}";
                    if (!string.IsNullOrWhiteSpace(stage) && !LogLineStageRx.IsMatch(line))
                        line = $"{line} stage={stage} time={timeStr}";
                    if (!string.IsNullOrWhiteSpace(status) && !LogLineStatusRx.IsMatch(line))
                        line = $"{line} status={status}";
                }

                var entry = new UpgradeLogEntry
                {
                    Cassia = cassia,
                    LogId = logId.Trim(),
                    Mac = mac.Trim(),
                    Stage = stage.Trim(),
                    Status = status.Trim(),
                    Firmware = fw.Trim(),
                    DeviceName = ExtractDeviceName(stage, status, name),
                    TimeLocal = ParseLocalTime(timeStr),
                    Line = line
                };

                AddUpgradeLogEntry(entry);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void AddUpgradeLogEntry(UpgradeLogEntry entry)
        {
            if (entry == null) return;

            var disp = Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
            {
                disp.Invoke(() => AddUpgradeLogEntry(entry));
                return;
            }

            var entryCassia = (entry.Cassia ?? "").Trim();
            var entryLogId = (entry.LogId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(entryLogId))
            return;

            var key = $"{entryCassia}||{entryLogId}";
            if (!_upgradeLogGroupByKey.TryGetValue(key, out var g))
            {
                g = UpgradeLogGroups.FirstOrDefault(x =>
                x != null
                && string.Equals(x.LogId, entryLogId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Cassia, entryCassia, StringComparison.OrdinalIgnoreCase));

                if (g == null)
                {
                    g = new UpgradeLogGroup
                    {
                        Cassia = entryCassia,
                        LogId = entryLogId,
                        Mac = entry.Mac ?? ""
                    };
                    UpgradeLogGroups.Add(g);
                }

                _upgradeLogGroupByKey[key] = g;
            }

            if (string.IsNullOrWhiteSpace(g.Mac) && !string.IsNullOrWhiteSpace(entry.Mac))
            g.Mac = entry.Mac;

            g.AddEntry(entry);
            MarkLatestUpgradeLogMapDirty();
            RequestUpgradeLogViewRefresh();
        }

        /// <summary>
        /// Recomputes per-MAC upgrade success based on the *latest* UpgradeLogGroup for that MAC.
        /// This prevents an older successful run from keeping the device green when a newer run exists.
        /// </summary>
        private void RefreshUpgradeSuccessFromLatestGroups()
        {
            // Determine latest group per MAC across ALL groups (do not depend on UI filters).
            var latestByMac = new Dictionary<string, UpgradeLogGroup>(StringComparer.OrdinalIgnoreCase);
            var groupsSnapshot = UpgradeLogGroups.Where(g => g != null).ToList();
            foreach (var g in groupsSnapshot)
            {
                var mac = (g.Mac ?? "").Trim();
                if (string.IsNullOrWhiteSpace(mac)) continue;

                if (!latestByMac.TryGetValue(mac, out var existing) || g.LastTimeLocal > existing.LastTimeLocal)
                latestByMac[mac] = g;
            }

            foreach (var kvp in latestByMac)
            {
                var mac = kvp.Key;
                var g = kvp.Value;
                var entrySnapshot = g.Entries.Where(e => e != null).ToList();
                // IMPORTANT:
                // The per-device result MUST be taken from the "Device Upgrade Completed." line (Warn/Success/Failed).
                // Do NOT rely on the last informational line.
                var completion = entrySnapshot
                .Where(e => !string.IsNullOrWhiteSpace(e.Stage)
                && e.Stage.Trim().Equals("Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.TimeLocal)
                .FirstOrDefault();

                var completionStatus = (completion?.Status ?? "").Trim();
                var isSuccess = completionStatus.Equals("Success", StringComparison.OrdinalIgnoreCase);
                var isWarn = IsWarnStatus(completionStatus);
                var isNoFwRead = IsNoFwReadStatus(completionStatus);
                if (IsSuppressibleSameFirmwareWarn(completion?.Stage, completionStatus, completion?.Line)
                    || IsSuppressibleSameFirmwareWarn(g))
                {
                    isWarn = false;
                    isSuccess = true;
                }
                var isFailed = completionStatus.Equals("Failed", StringComparison.OrdinalIgnoreCase)
                || completionStatus.StartsWith("Fail", StringComparison.OrdinalIgnoreCase);
                if (isNoFwRead)
                    isFailed = false;

                var cs = GetOrCreateCache(mac);
                cs.IsUpgradeSuccess = isSuccess;
                cs.IsUpgradeWarn = isWarn;
                cs.IsUpgradeNoFwRead = isNoFwRead;
                cs.IsUpgradeFailed = isFailed;
                // Use the group's completion timestamp if present.
                if (isSuccess)
                {
                    var t = entrySnapshot
                    .Where(e => !string.IsNullOrWhiteSpace(e.Stage)
                    && e.Stage.Trim().Equals("Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(e.Status)
                    && e.Status.Trim().Equals("Success", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(e => e.TimeLocal)
                    .FirstOrDefault()?.TimeLocal ?? DateTimeOffset.MinValue;

                    if (t != DateTimeOffset.MinValue)
                    cs.LastUpgradeSuccessUtc = t.ToUniversalTime();

                    if (!string.IsNullOrWhiteSpace(g.LatestFirmware))
                    cs.LastTargetFw = g.LatestFirmware;
                }

                var dev = FindDiscoveredDevice(mac);
                if (dev != null)
                {
                    dev.IsUpgradeSuccess = isSuccess;
                    dev.IsUpgradeWarn = isWarn;
                    dev.IsUpgradeNoFwRead = isNoFwRead;
                    dev.IsUpgradeFailed = isFailed;
                    dev.LastUpgradeSuccessUtc = cs.LastUpgradeSuccessUtc;
                    dev.LastTargetFw = cs.LastTargetFw;
                }
            }

            // For MACs that are present in cache/devices but have no groups at all, ensure we don't
            // leave a stale green state.
            foreach (var dev in _devices)
            {
                if (dev == null || string.IsNullOrWhiteSpace(dev.Mac)) continue;
                if (latestByMac.ContainsKey(dev.Mac)) continue;
                dev.IsUpgradeSuccess = false;
                dev.IsUpgradeWarn = false;
                dev.IsUpgradeFailed = false;
                dev.IsUpgradeNoFwRead = false;
            }
        }

        private static DateTimeOffset ParseLocalTime(string? timeStr)
        {
            if (string.IsNullOrWhiteSpace(timeStr)) return DateTimeOffset.MinValue;

            // Formats we see: "2026-01-12 16:23:45" (no tz)
            if (DateTime.TryParse(timeStr, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var dt))
            {
                if (dt.Kind == DateTimeKind.Unspecified)
                dt = DateTime.SpecifyKind(dt, DateTimeKind.Local);
                return new DateTimeOffset(dt);
            }
            return DateTimeOffset.MinValue;
        }

        private static bool IsWarnStatus(string? status)
            => !string.IsNullOrWhiteSpace(status) &&
               (status.Trim().Equals("Warn", StringComparison.OrdinalIgnoreCase) ||
                status.Trim().Equals("Warning", StringComparison.OrdinalIgnoreCase));

        private static bool IsNoFwReadStatus(string? status)
            => !string.IsNullOrWhiteSpace(status) &&
               status.Trim().Equals("NoFwRead", StringComparison.OrdinalIgnoreCase);

        private bool IsSuppressibleSameFirmwareWarn(string? stage, string? status, string? details)
        {
            if (!SuppressSameFirmwareWarnings) return false;
            if (!string.Equals((stage ?? "").Trim(), "Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase)) return false;
            if (!IsWarnStatus(status)) return false;

            return ContainsSameFirmwareHint(details);
        }

        private static bool ContainsSameFirmwareHint(string? details)
        {
            var text = (details ?? "").ToLowerInvariant();
            var hasSameAndFw = text.Contains("same") && (text.Contains("firmware") || text.Contains("fw"));
            return hasSameAndFw
                || text.Contains("same firmware")
                || text.Contains("same-fw")
                || text.Contains("same fw")
                || text.Contains("firmware was same")
                || text.Contains("already latest")
                || text.Contains("already up to date")
                || text.Contains("already up-to-date")
                || text.Contains("already on latest")
                || text.Contains("already current");
        }

        private static bool ContainsNoFwStepHint(string? details)
        {
            var text = (details ?? "").ToLowerInvariant();
            return text.Contains("no fw steps")
                || text.Contains("already matches target")
                || text.Contains("fw already matches target")
                || text.Contains("upgrade skipped");
        }

        private bool IsSuppressibleSameFirmwareWarn(UpgradeLogGroup? group)
        {
            if (group == null) return false;
            if (!SuppressSameFirmwareWarnings) return false;
            if (!IsWarnStatus(group.LatestStatus)) return false;

            var completionWarn = group.Entries
                .Where(e => e != null
                            && !string.IsNullOrWhiteSpace(e.Stage)
                            && e.Stage.Trim().Equals("Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase)
                            && IsWarnStatus(e.Status))
                .OrderByDescending(e => e.TimeLocal)
                .FirstOrDefault();

            if (completionWarn == null)
                return false;

            if (ContainsSameFirmwareHint(completionWarn.Line))
                return true;

            var entries = group.Entries.Where(e => e != null).ToList();

            var noFwRunByStage = entries.Any(e =>
                ContainsNoFwStepHint(e!.Stage) || ContainsNoFwStepHint(e.Line));

            var actorSkipped = entries.Any(e =>
                (e!.Stage ?? "").Contains("actor upgrade skipped", StringComparison.OrdinalIgnoreCase)
                && ContainsNoFwStepHint(e.Stage));

            var sensorSkipped = entries.Any(e =>
                (e!.Stage ?? "").Contains("sensor upgrade skipped", StringComparison.OrdinalIgnoreCase)
                && ContainsNoFwStepHint(e.Stage));

            if (noFwRunByStage || (actorSkipped && sensorSkipped))
                return true;

            if (ContainsSameFirmwareHint(group.LatestSummary))
                return true;

            return entries.Any(e => ContainsSameFirmwareHint(e!.Line));
        }

        private static bool LooksLikeFirmwareVersion(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();

            // Ignore detector model strings like "P46", "P47", etc.
            if (Regex.IsMatch(s, @"^P\d{2}$", RegexOptions.IgnoreCase))
            return false;

            // Accept typical target versions like "v02.35" or "02.35"
            return Regex.IsMatch(s, @"^v?\d{2}\.\d{2}$", RegexOptions.IgnoreCase);
        }

        private static bool LooksLikeNewRunStage(string? stage, int progressPercent)
        {
            // Heuristic: stages that indicate a fresh run starting (even if progress is still low).
            if (string.IsNullOrWhiteSpace(stage))
            return progressPercent <= 5;

            var s = stage.Trim();
            // Linux-native/cassia chip-selection marker at run start (e.g. "Using hci0", "Using chip 1").
            if (LogLineChipUsedRx.IsMatch(s)) return true;

            if (progressPercent <= 5)
            {
                if (s.Contains("Process Start", StringComparison.OrdinalIgnoreCase)) return true;
                if (s.Contains("Connect+Login", StringComparison.OrdinalIgnoreCase)) return true;
                if (s.Contains("Current FW Version", StringComparison.OrdinalIgnoreCase)) return true;
                if (s.Contains("Requested update", StringComparison.OrdinalIgnoreCase)) return true;
            }

            // Some runs can jump directly to a start stage.
            if (s.Contains("Process Start", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsNonTerminalStage(string? stage)
        {
            if (string.IsNullOrWhiteSpace(stage))
            return true;

            var s = stage.Trim();
            if (s.Equals("Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase))
            return false;

            // Treat common terminal words as terminal.
            if (s.Contains("completed", StringComparison.OrdinalIgnoreCase)) return false;
            if (s.Contains("success", StringComparison.OrdinalIgnoreCase)) return false;
            if (s.Contains("failed", StringComparison.OrdinalIgnoreCase)) return false;
            if (s.Contains("error", StringComparison.OrdinalIgnoreCase)) return false;
            if (s.Contains("aborted", StringComparison.OrdinalIgnoreCase)) return false;
            if (s.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }

        private string ResolveMacFromLogLine(string cassia, string line)
        {
            var macMatch = LogLineMacRx.Match(line);
            if (macMatch.Success)
            {
                var direct = macMatch.Groups["mac"].Value?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(direct))
                    return direct;
            }

            var idMatch = LogLineIdRx.Match(line);
            if (!idMatch.Success)
                return "";

            var logId = idMatch.Groups["id"].Value?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(logId))
                return "";

            var cassiaName = (cassia ?? "").Trim();
            var key = $"{cassiaName}||{logId}";
            if (_upgradeLogGroupByKey.TryGetValue(key, out var groupedByKey))
            {
                var groupedMac = (groupedByKey.Mac ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(groupedMac))
                    return groupedMac;
            }

            var grouped = UpgradeLogGroups.FirstOrDefault(g =>
                g != null
                && string.Equals((g.LogId ?? "").Trim(), logId, StringComparison.OrdinalIgnoreCase)
                && string.Equals((g.Cassia ?? "").Trim(), cassiaName, StringComparison.OrdinalIgnoreCase));

            return (grouped?.Mac ?? "").Trim();
        }

        private void ApplyChipUsedHint(string mac, string chipUsed)
        {
            if (string.IsNullOrWhiteSpace(mac) || string.IsNullOrWhiteSpace(chipUsed))
                return;

            var chip = chipUsed.Trim();
            var cs = GetOrCreateCache(mac);
            cs.ChipUsed = chip;

            var dev = FindDiscoveredDevice(mac);
            if (dev != null)
                dev.ChipUsed = chip;

            var qi = QueueItems.FirstOrDefault(x => x.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
            if (qi != null)
                qi.ChipUsed = chip;
        }


        private void ApplyStatusFromUpgradeLogLine(string cassia, string line)
        {
            try
            {
                var mac = ResolveMacFromLogLine(cassia, line);
                if (string.IsNullOrWhiteSpace(mac)) return;

                var stage = "";
                var sm = LogLineStageRx.Match(line);
                if (sm.Success) stage = sm.Groups["stage"].Value.Trim();

                var status = "";
                var stm = LogLineStatusRx.Match(line);
                if (stm.Success) status = stm.Groups["status"].Value.Trim();

                // IMPORTANT: for live UI ordering we must NOT rely on gateway clocks.
                // Use local arrival time for all "is this newer" comparisons.
                var arrivalUtc = DateTimeOffset.UtcNow;

                // Timestamp embedded in log line (kept for display/debug, but NOT used for ordering)
                DateTimeOffset embeddedUtc = arrivalUtc;
                var tm = LogLineTimeRx.Match(line);
                if (tm.Success)
                {
                    if (DateTime.TryParseExact(
                    tm.Groups["time"].Value.Trim(),
                    "yyyy-MM-dd HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal,
                    out var dtLocal))
                    {
                        embeddedUtc = new DateTimeOffset(DateTime.SpecifyKind(dtLocal, DateTimeKind.Local)).ToUniversalTime();
                    }
                }

                // Logs must NOT drive queue/progress UI.
                // We only harvest:
                //  - Current FW info (from "Sensor: App: ..." text embedded in status)
                //  - Final completion outcome (Success/Warn/Failed) + target FW (fw=v02.xx)
                var cs = GetOrCreateCache(mac);

                // 1) Completion outcome
                var isCompletion = stage.Trim().Equals("Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase);
                if (isCompletion)
                {
                    var outcome = (status ?? "").Trim();
                    var isSuccess = outcome.Equals("Success", StringComparison.OrdinalIgnoreCase);
                    var isWarn = IsWarnStatus(outcome);
                    var isNoFwRead = IsNoFwReadStatus(outcome);
                    if (IsSuppressibleSameFirmwareWarn(stage, outcome, line))
                    {
                        isWarn = false;
                        isSuccess = true;
                    }
                    var isFailed = outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                    outcome.Equals("Error", StringComparison.OrdinalIgnoreCase);
                    if (isNoFwRead) isFailed = false;

                    // Only accept if newer than the last completion we stored for this MAC.
                    // Note: gateway clocks can be skewed, but within a single gateway this is still useful.
                    // The live status text ordering uses arrival time elsewhere.
                    var last = cs.LastUpgradeSuccessUtc ?? DateTimeOffset.MinValue;
                    if (embeddedUtc >= last)
                    {
                        cs.LastUpgradeSuccessUtc = embeddedUtc;

                        cs.IsUpgradeSuccess = isSuccess;
                        cs.IsUpgradeWarn = isWarn;
                        cs.IsUpgradeFailed = isFailed;
                        cs.IsUpgradeNoFwRead = isNoFwRead;

                        // Completion implies no longer in queue unless queue snapshot says otherwise later.
                        if (isSuccess || isWarn || isFailed || isNoFwRead)
                        cs.IsInQueue = false;

                        var fwm = LogLineFwRx.Match(line);
                        if (fwm.Success)
                        cs.LastTargetFw = fwm.Groups["fw"].Value.Trim();
                    }
                }

                // 2) Current FW (Sensor: App: ...)
                if (!string.IsNullOrWhiteSpace(status))
                {
                    var appm = SensorAppFromStatusRx.Match(status);
                    if (appm.Success)
                    {
                        var app = appm.Groups["app"].Value;
                        if (!string.IsNullOrWhiteSpace(app))
                        cs.CurrentFw = app;
                    }
                }

                // Apply to discovered device if it exists (without touching ProcessStatus fields)
                var dev = FindDiscoveredDevice(mac);
                if (dev != null)
                {
                    dev.LastUpgradeSuccessUtc = cs.LastUpgradeSuccessUtc;
                    dev.LastTargetFw = cs.LastTargetFw ?? "";
                    dev.CurrentFw = cs.CurrentFw ?? "";

                    // Apply completion outcome immediately so the row turns green/yellow/red without waiting
                    // for any grouping/debounced UI refresh.
                    dev.IsUpgradeSuccess = cs.IsUpgradeSuccess;
                    dev.IsUpgradeWarn = cs.IsUpgradeWarn;
                    dev.IsUpgradeNoFwRead = cs.IsUpgradeNoFwRead;
                    dev.IsUpgradeFailed = cs.IsUpgradeFailed;

                    if (cs.IsUpgradeSuccess || cs.IsUpgradeWarn || cs.IsUpgradeFailed || cs.IsUpgradeNoFwRead)
                    dev.IsInQueue = false;
                }
            }
            catch
            {
                // ignore malformed lines
            }
        }


        private void ApplyLiveProcessStatusFromUpgradeLogLine(string cassia, string line)
        {
            try
            {
                var mac = ResolveMacFromLogLine(cassia, line);
                if (string.IsNullOrWhiteSpace(mac)) return;

                var stage = "";
                var sm = LogLineStageRx.Match(line);
                if (sm.Success) stage = sm.Groups["stage"].Value.Trim();

                var status = "";
                var stm = LogLineStatusRx.Match(line);
                if (stm.Success) status = stm.Groups["status"].Value.Trim();

                // IMPORTANT: for "latest" ordering we use ARRIVAL time, not gateway timeLocal.
                var arrivalUtc = DateTimeOffset.UtcNow;

                // chip from stage text (optional)
                var chipUsed = "";
                var chipSource = !string.IsNullOrWhiteSpace(stage) ? stage : status;
                if (string.IsNullOrWhiteSpace(chipSource))
                    chipSource = line;
                if (!string.IsNullOrWhiteSpace(chipSource))
                {
                    var cm = LogLineChipUsedRx.Match(chipSource);
                    if (cm.Success) chipUsed = cm.Groups["c"].Value.Trim();
                }

                // Apply chip as soon as we see it so it cannot be lost by later
                // progress ordering/de-dup heuristics.
                if (!string.IsNullOrWhiteSpace(chipUsed))
                    ApplyChipUsedHint(mac, chipUsed);

                // prefer stage; fallback to status
                var text = !string.IsNullOrWhiteSpace(stage) ? stage : status;
                if (string.IsNullOrWhiteSpace(text)) return;

                // fw=v02.xx (optional)
                var fw = "";
                var fwm = LogLineFwRx.Match(line);
                if (fwm.Success) fw = fwm.Groups["fw"].Value.Trim();

                var isCompletion = stage.Equals("Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase);

                var isSuccess = isCompletion && status.Equals("Success", StringComparison.OrdinalIgnoreCase);
                var isWarn = isCompletion && IsWarnStatus(status);
                var isNoFwRead = isCompletion && IsNoFwReadStatus(status);
                if (IsSuppressibleSameFirmwareWarn(stage, status, line))
                {
                    isWarn = false;
                    isSuccess = isCompletion;
                }
                var isFailed = isCompletion && (status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
                status.StartsWith("Fail", StringComparison.OrdinalIgnoreCase));
                if (isNoFwRead) isFailed = false;

                // queue text: show Done/Warn/Failed if completion, else normal stage/status
                var queueText = isCompletion
                ? (isSuccess ? "Done" : (isNoFwRead ? "No FW" : (isWarn ? "Warn" : (isFailed ? "Failed" : text))))
                : text;

                lock (_progressBufLock)
                {
                    var isNew = false;
                    if (!_progressByMac.TryGetValue(mac, out var bp))
                    {
                        bp = new BufferedProgress { Mac = mac, HasProgressPercent = false, HasSpeedPctPerMin = false };
                        _progressByMac[mac] = bp;
                        isNew = true;
                    }
                    else if (bp.TimeUtc != DateTimeOffset.MinValue && arrivalUtc < bp.TimeUtc)
                    {
                        return;
                    }

                    bp.Cassia = cassia;
                    bp.Stage = text.Trim();
                    bp.QueueStatus = queueText.Trim();
                    bp.ChipUsed = chipUsed;

                    if (LooksLikeFirmwareVersion(fw))
                        bp.FirmwareTarget = fw;

                    if (isCompletion)
                    {
                        bp.HasProgressPercent = true;
                        bp.ProgressPercent = 100;
                        bp.HasSpeedPctPerMin = true;
                        bp.SpeedPctPerMin = null;
                        bp.ClearSpeed = true;
                    }
                    else
                    {
                        if (isNew)
                        {
                            bp.HasProgressPercent = false;
                            bp.HasSpeedPctPerMin = false;
                        }
                        bp.ClearSpeed = false;
                    }

                    bp.TimeUtc = arrivalUtc;
                }
            }
            catch
            {
                // ignore malformed lines
            }
            ScheduleProgressFlushOnUi();
        }

        private void RequestUpgradeLogTextRefresh()
        {
            if (_pendingUpgradeLogTextRefresh) return;
            _pendingUpgradeLogTextRefresh = true;

            Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(150);
                _pendingUpgradeLogTextRefresh = false;
                UpgradeLogText = _upgradeLogSb.ToString();
            });
        }

        private void RequestUpgradeLogViewRefresh()
        {
            if (_pendingUpgradeLogViewRefresh) return;
            _pendingUpgradeLogViewRefresh = true;

            var disp = Application.Current?.Dispatcher;
            if (disp == null)
            {
                _pendingUpgradeLogViewRefresh = false;
                return;
            }

            disp.InvokeAsync(async () =>
            {
                await Task.Delay(250);
                _pendingUpgradeLogViewRefresh = false;
                RefreshUpgradeSuccessFromLatestGroups();
                RecomputeUniqueUpgradeOutcomeCounts();
                UpgradeLogGroupsView.Refresh();
            });
        }

        [RelayCommand]
        private async Task RequestUpgradeLogAsync()
        {
            if (!IsConnected)
            {
                ConnectionStatus = "Not connected";
                return;
            }

            // Clear current view (user-initiated)
            Application.Current.Dispatcher.Invoke(() =>
            {
                UpgradeLogLines.Clear();
                UpgradeLogGroups.Clear();
                _upgradeLogGroupByKey.Clear();
                UpgradeLogText = "";
                _upgradeLogSb.Clear();
                UpgradeLogReceivedLines = 0;
                UpgradeLogTotalLines = 0;
                UpgradeLogUniqueSuccessCount = 0;
                UpgradeLogUniqueFailedCount = 0;
                UpgradeLogStatus = "Requesting saved logs from all gateways…";
            });

            var gateways = CassiaGateways
            .Where(g => g != null && !string.IsNullOrWhiteSpace(g.Name))
            .Select(g => g.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            if (gateways.Count == 0)
            {
                ConnectionStatus = "No Cassia gateways known yet";
                return;
            }

            try
            {
                foreach (var cassia in gateways)
                {
                    _requestedUpgradeLogCassias.Add(cassia);
                    await RequestUpgradeLogForCassiaAsync(cassia).ConfigureAwait(false);
                }

                UpgradeLogStatus = $"Requested saved logs from {gateways.Count} gateway(s)";
            }
            catch (Exception ex)
            {
                UpgradeLogStatus = "Request failed: " + ex.Message;
            }
        }


        /// <summary>
        /// Internal helper used by the auto-request logic (per gateway). Not a command.
        /// </summary>
        private async Task RequestUpgradeLogForCassiaAsync(string cassia)
        {
            if (!IsConnected) return;
            if (string.IsNullOrWhiteSpace(cassia)) return;

            var topic = CommandTopicTemplate
            .Replace("{networkId}", NetworkId)
            .Replace("{cassia}", cassia)
            .Replace("{command}", "send-upgrade-log");

            try
            {
                var req = JsonSerializer.Serialize(new
                {
                    compressed = true
                });
                await _mqtt.PublishAsync(topic, req, retain: false).ConfigureAwait(false);
            }
            catch
            {
                // best effort; UI command path shows errors, auto path stays quiet
            }
        }


        [RelayCommand]
        private async Task ClearUpgradeLogOnCassiaAsync()
        {
            if (!IsConnected)
            {
                UpgradeLogStatus = "Not connected";
                return;
            }

            // If "All" is selected, send clear command to each Cassia sequentially.
            var selected = (SelectedLogGatewayName ?? "").Trim();

            List<string> targets;
            if (string.IsNullOrWhiteSpace(selected) || string.Equals(selected, "All", StringComparison.OrdinalIgnoreCase))
            {
                targets = CassiaGateways
                .Select(g => g.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            }
            else
            {
                targets = new List<string> { selected };
            }

            if (targets.Count == 0)
            {
                UpgradeLogStatus = "No Cassia gateway known yet";
                return;
            }

            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var cassia = targets[i];

                    var topic = CommandTopicTemplate
                    .Replace("{networkId}", NetworkId)
                    .Replace("{cassia}", cassia)
                    .Replace("{command}", "clear-upgrade-log");

                    await _mqtt.PublishAsync(topic, "{}", retain: false).ConfigureAwait(false);

                    UpgradeLogStatus = targets.Count == 1
                    ? $"Requested clear-upgrade-log on {cassia}"
                    : $"Requested clear-upgrade-log on {cassia} ({i + 1}/{targets.Count})";

                    await Task.Delay(120).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                UpgradeLogStatus = "Clear request failed: " + ex.Message;
            }
        }

        [RelayCommand]
        private void ClearUpgradeLog()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                UpgradeLogLines.Clear();
                UpgradeLogGroups.Clear();
                _upgradeLogGroupByKey.Clear();
                UpgradeLogGroupsView?.Refresh();
                _upgradeLogSb.Clear();
                UpgradeLogText = "";
                UpgradeLogSearchText = "";
                UpgradeLogReceivedLines = 0;
                UpgradeLogTotalLines = 0;
                UpgradeLogUniqueSuccessCount = 0;
                UpgradeLogUniqueFailedCount = 0;
                UpgradeLogStatus = "Idle";
            });
        }

        private bool _pendingDevicesRefresh;
        private bool _pendingQueueRefresh;


        private void MaybeAutoRequestUpgradeLogAfterStatus(CassiaGateway gw)
        {
            if (!IsConnected) return;
            if (!string.Equals(gw.StateLower, "online", StringComparison.OrdinalIgnoreCase)) return;

            // Only auto-request once per connection per gateway.
            // If we already tried requesting "all" on connect, we do not spam per-gateway requests.
            if (_requestedUpgradeLogCassias.Contains("all")) return;
            if (_requestedUpgradeLogCassias.Contains(gw.Name)) return;

            _requestedUpgradeLogCassias.Add(gw.Name);
            _ = RequestUpgradeLogForCassiaAsync(gw.Name);
        }
    }


