using AccessAPP.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static SettingsVersionPatcher;

namespace AccessAPP.Services
{
    public sealed class DeviceSettingsBackupService : IDeviceSettingsBackupService
    {
        private readonly IDeviceSettingsBleApi _ble;
        private readonly string _rootDir;

        // Central per-detector backup/restore profile (clean single place to modify)
        private sealed record SettingsBackupProfile(
            bool UserConfig,
            bool WiredPushButtons,
            bool DaliPushButtons,
            bool BlePushButtons);

        // Conservative default: only UserConfig. Add explicit profiles per detector below.
        // This avoids calling unsupported BLE endpoints for unknown detector families.
        private static readonly SettingsBackupProfile DefaultProfile = new(
            UserConfig: true,
            WiredPushButtons: false,
            DaliPushButtons: false,
            BlePushButtons: false);

        // NOTE: keys are UPPERCASE detector types.
        private static readonly Dictionary<string, SettingsBackupProfile> Profiles = new()
        {
            // P46/P49/P41: UserConfig only
            ["P46"] = new SettingsBackupProfile(UserConfig: true, WiredPushButtons: false, DaliPushButtons: false, BlePushButtons: false),
            ["P49"] = new SettingsBackupProfile(UserConfig: true, WiredPushButtons: false, DaliPushButtons: false, BlePushButtons: false),
            ["P41"] = new SettingsBackupProfile(UserConfig: true, WiredPushButtons: false, DaliPushButtons: false, BlePushButtons: false),

            // P42: UserConfig + Wired + BLE (NO DALI)
            ["P42"] = new SettingsBackupProfile(UserConfig: true, WiredPushButtons: true, DaliPushButtons: false, BlePushButtons: true),

            // P47/P48: DALI masters (explicit)
            ["P47"] = new SettingsBackupProfile(UserConfig: true, WiredPushButtons: true, DaliPushButtons: true, BlePushButtons: true),
            ["P48"] = new SettingsBackupProfile(UserConfig: true, WiredPushButtons: true, DaliPushButtons: true, BlePushButtons: true),
        };

        private static SettingsBackupProfile GetProfile(string? detectorType)
        {
            var key = (detectorType ?? string.Empty).Trim().ToUpperInvariant();
            if (Profiles.TryGetValue(key, out var profile))
                return profile;
            return DefaultProfile;
        }

        public DeviceSettingsBackupService(IDeviceSettingsBleApi bleApi, string? rootDir = null)
        {
            _ble = bleApi ?? throw new ArgumentNullException(nameof(bleApi));

            _rootDir = string.IsNullOrWhiteSpace(rootDir)
                ? Path.Combine(AppContext.BaseDirectory, "device-settings-backups")
                : rootDir;

            Directory.CreateDirectory(_rootDir);
        }

        private string GetBackupPath(string macAddress, string? logId)
        {
            var safeMac = (macAddress ?? "unknown").Trim().Replace(":", "").Replace("-", "").Replace(" ", "");
            var safeLog = string.IsNullOrWhiteSpace(logId) ? DateTime.Now.ToString("yyyyMMddHHmmss") : logId.Trim();
                
            //return Path.Combine(_rootDir, $"{safeMac}_{safeLog}_settings.json");
            
            return Path.Combine(_rootDir, $"{safeMac}_settings.json");
        }


        private string ResolveBackupPath(string macAddress, string? backupFilePath)
        {
            // 1) If provided path exists, keep it.
            if (!string.IsNullOrWhiteSpace(backupFilePath) && File.Exists(backupFilePath))
                return backupFilePath;

            var safeMac = (macAddress ?? "unknown").Trim().Replace(":", "").Replace("-", "").Replace(" ", "");

            // 2) If a relative path was provided, try under rootDir.
            if (!string.IsNullOrWhiteSpace(backupFilePath) && !Path.IsPathRooted(backupFilePath))
            {
                try
                {
                    var candidate = Path.Combine(_rootDir, backupFilePath);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { /* ignore */ }
            }

            // 3) Default expected filename for this MAC.
            try
            {
                var candidate = Path.Combine(_rootDir, $"{safeMac}_settings.json");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { /* ignore */ }

            // 4) As last resort, search for any backup matching MAC.
            try
            {
                var matches = Directory.GetFiles(_rootDir, $"{safeMac}*_settings.json", SearchOption.TopDirectoryOnly);
                if (matches != null && matches.Length > 0)
                    return matches.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }
            catch { /* ignore */ }

            return backupFilePath ?? string.Empty;
        }

        private static int GetBackupReadAttempts()
            => Math.Max(1, RuntimeVariables.UPGRADE_SETTINGS_BACKUP_READ_ATTEMPTS);

        private static int GetBackupReadRetryDelayMs()
            => Math.Max(200, RuntimeVariables.UPGRADE_SETTINGS_BACKUP_READ_RETRY_DELAY_MS);

        private static async Task<string> ReadRequiredAsync(
            Func<Task<string>> readFunc,
            string label)
        {
            int attempts = GetBackupReadAttempts();
            int delayMs = GetBackupReadRetryDelayMs();
            string lastError = "empty response";

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    var hex = await readFunc().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(hex))
                        return hex;

                    lastError = "empty response";
                    AppLog.Warn($"[Backup] {label} empty (attempt {attempt}/{attempts}).");
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    AppLog.Warn($"[Backup] {label} exception (attempt {attempt}/{attempts}): {ex.Message}");
                }

                if (attempt < attempts)
                    await Task.Delay(delayMs).ConfigureAwait(false);
            }

            throw new Exception($"Settings backup read failed for {label} after {attempts} attempts. Last error: {lastError}");
        }

        public async Task<DeviceSettingsSnapshot> CaptureSnapshotAsync(
            string macAddress,
            string detectorType,
            string firmwareVersion)
        {
            var profile = GetProfile(detectorType);

            return new DeviceSettingsSnapshot
            {
                MacAddress = macAddress,
                CapturedAt = DateTimeOffset.Now,
                DetectorType = detectorType,
                FirmwareVersionTarget = firmwareVersion,

                UserConfigHex = profile.UserConfig
                    ? StripBleHeader(await ReadRequiredAsync(() => _ble.GetUserConfig(macAddress), "UserConfig").ConfigureAwait(false))
                    : null,

                PushButtonsHex = profile.WiredPushButtons
                    ? StripBleHeader(await ReadRequiredAsync(() => _ble.GetWiredPushButtonList(macAddress), "WiredPushButtons").ConfigureAwait(false))
                    : null,

                DaliPushButtonsHex = profile.DaliPushButtons
                    ? StripBleHeader(await ReadRequiredAsync(() => _ble.GetDaliPushButtonList(macAddress), "DaliPushButtons").ConfigureAwait(false))
                    : null,

                BlePushButtonsHex = profile.BlePushButtons
                    ? StripBleHeader(await ReadRequiredAsync(() => _ble.GetBLEPushButtonList(macAddress), "BlePushButtons").ConfigureAwait(false))
                    : null,
            };
        }

        public async Task<(string filePath, DeviceSettingsSnapshot snapshot)> BackupToFileAsync(
            string macAddress,
            string pincode, // kept in interface for your earlier calls; not used here
            string detectorType,
            string firmwareVersion,
            string? logId)
        {
            var path = GetBackupPath(macAddress, logId);
            var snapshot = await CaptureSnapshotAsync(macAddress, detectorType, firmwareVersion).ConfigureAwait(false);

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, Encoding.UTF8).ConfigureAwait(false);

            return (path, snapshot);
        }

        public async Task<ServiceResponse> ApplyOverridesAsync(
            string macAddress,
            string detectorType,
            string firmwareVersion,
            DetectorSettingsPatch overrides,
            bool writeOnlyChanged = true)
        {
            if (overrides == null || !overrides.HasAnyValue())
            {
                return new ServiceResponse
                {
                    Success = false,
                    StatusCode = 400,
                    Message = "No detector settings override values were provided."
                };
            }

            var normalized = overrides.CloneNormalized();
            var current = await CaptureSnapshotAsync(macAddress, detectorType, firmwareVersion).ConfigureAwait(false);
            var profile = GetProfile(detectorType);

            var target = new DeviceSettingsSnapshot
            {
                MacAddress = macAddress,
                CapturedAt = DateTimeOffset.Now,
                DetectorType = detectorType,
                FirmwareVersionTarget = firmwareVersion,
                UserConfigHex = current.UserConfigHex,
                PushButtonsHex = current.PushButtonsHex,
                DaliPushButtonsHex = current.DaliPushButtonsHex,
                BlePushButtonsHex = current.BlePushButtonsHex
            };

            bool requestedUser = !string.IsNullOrWhiteSpace(normalized.UserConfigHex)
                || !string.IsNullOrWhiteSpace(normalized.UserConfigMaskHex);
            bool requestedWired = !string.IsNullOrWhiteSpace(normalized.PushButtonsHex)
                || !string.IsNullOrWhiteSpace(normalized.PushButtonsMaskHex);
            bool requestedDali = !string.IsNullOrWhiteSpace(normalized.DaliPushButtonsHex)
                || !string.IsNullOrWhiteSpace(normalized.DaliPushButtonsMaskHex);
            bool requestedBle = !string.IsNullOrWhiteSpace(normalized.BlePushButtonsHex)
                || !string.IsNullOrWhiteSpace(normalized.BlePushButtonsMaskHex);

            if (requestedUser)
            {
                if (!TryBuildMergedSectionHex(
                    currentHex: current.UserConfigHex,
                    requestedHex: normalized.UserConfigHex,
                    requestedMaskHex: normalized.UserConfigMaskHex,
                    mergedHex: out var merged,
                    error: out var err))
                {
                    return new ServiceResponse
                    {
                        Success = false,
                        StatusCode = 400,
                        Message = $"Invalid userConfig patch: {err}"
                    };
                }
                target.UserConfigHex = merged;
            }

            if (requestedWired)
            {
                if (!TryBuildMergedSectionHex(
                    currentHex: current.PushButtonsHex,
                    requestedHex: normalized.PushButtonsHex,
                    requestedMaskHex: normalized.PushButtonsMaskHex,
                    mergedHex: out var merged,
                    error: out var err))
                {
                    return new ServiceResponse
                    {
                        Success = false,
                        StatusCode = 400,
                        Message = $"Invalid pushButtons patch: {err}"
                    };
                }
                target.PushButtonsHex = merged;
            }

            if (requestedDali)
            {
                if (!TryBuildMergedSectionHex(
                    currentHex: current.DaliPushButtonsHex,
                    requestedHex: normalized.DaliPushButtonsHex,
                    requestedMaskHex: normalized.DaliPushButtonsMaskHex,
                    mergedHex: out var merged,
                    error: out var err))
                {
                    return new ServiceResponse
                    {
                        Success = false,
                        StatusCode = 400,
                        Message = $"Invalid daliPushButtons patch: {err}"
                    };
                }
                target.DaliPushButtonsHex = merged;
            }

            if (requestedBle)
            {
                if (!TryBuildMergedSectionHex(
                    currentHex: current.BlePushButtonsHex,
                    requestedHex: normalized.BlePushButtonsHex,
                    requestedMaskHex: normalized.BlePushButtonsMaskHex,
                    mergedHex: out var merged,
                    error: out var err))
                {
                    return new ServiceResponse
                    {
                        Success = false,
                        StatusCode = 400,
                        Message = $"Invalid blePushButtons patch: {err}"
                    };
                }
                target.BlePushButtonsHex = merged;
            }

            // Keep payload versions aligned with target FW when rules exist.
            try
            {
                var rulesRoot = FirmwareRulesLoader.LoadFromRootFolder();
                SettingsVersionPatcher.ApplyRestoreVersionRules(target, firmwareVersion, rulesRoot);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[SettingsPatch] Rules load/apply failed (continuing): {ex.Message}");
            }

            bool ok = true;
            int writes = 0;

            bool ShouldWrite(string? before, string? after)
                => !writeOnlyChanged || !HexEquals(before, after);

            if (requestedUser)
            {
                if (!profile.UserConfig)
                {
                    ok = false;
                    AppLog.Warn($"[SettingsPatch] UserConfig override requested but detector '{detectorType}' profile does not support it.");
                }
                else if (!string.IsNullOrWhiteSpace(target.UserConfigHex) && ShouldWrite(current.UserConfigHex, target.UserConfigHex))
                {
                    var r = await WriteWithRetryAsync(() => _ble.SetUserConfig(macAddress, target.UserConfigHex), "UserConfig").ConfigureAwait(false);
                    ok &= r;
                    if (r) writes++;
                }
            }

            if (requestedWired)
            {
                if (!profile.WiredPushButtons)
                {
                    ok = false;
                    AppLog.Warn($"[SettingsPatch] WiredPushButtons override requested but detector '{detectorType}' profile does not support it.");
                }
                else if (!string.IsNullOrWhiteSpace(target.PushButtonsHex) && ShouldWrite(current.PushButtonsHex, target.PushButtonsHex))
                {
                    var r = await WriteWithRetryAsync(() => _ble.SetWiredPushButtonList(macAddress, target.PushButtonsHex), "Wired PushButtons").ConfigureAwait(false);
                    ok &= r;
                    if (r) writes++;
                }
            }

            if (requestedDali)
            {
                if (!profile.DaliPushButtons)
                {
                    ok = false;
                    AppLog.Warn($"[SettingsPatch] DaliPushButtons override requested but detector '{detectorType}' profile does not support it.");
                }
                else if (!string.IsNullOrWhiteSpace(target.DaliPushButtonsHex) && ShouldWrite(current.DaliPushButtonsHex, target.DaliPushButtonsHex))
                {
                    var r = await WriteWithRetryAsync(() => _ble.SetDaliPushButtonList(macAddress, target.DaliPushButtonsHex), "DALI PushButtons").ConfigureAwait(false);
                    ok &= r;
                    if (r) writes++;
                }
            }

            if (requestedBle)
            {
                if (!profile.BlePushButtons)
                {
                    ok = false;
                    AppLog.Warn($"[SettingsPatch] BlePushButtons override requested but detector '{detectorType}' profile does not support it.");
                }
                else if (!string.IsNullOrWhiteSpace(target.BlePushButtonsHex) && ShouldWrite(current.BlePushButtonsHex, target.BlePushButtonsHex))
                {
                    var r = await WriteWithRetryAsync(() => _ble.SetBLEPushButtonList(macAddress, target.BlePushButtonsHex), "BLE PushButtons").ConfigureAwait(false);
                    ok &= r;
                    if (r) writes++;
                }
            }

            return new ServiceResponse
            {
                Success = ok,
                StatusCode = ok ? 200 : 500,
                Message = ok
                    ? (writes > 0 ? $"Detector settings applied ({writes} section(s) written)." : "Detector settings already matched requested values; nothing written.")
                    : "Detector settings apply failed for one or more sections."
            };
        }

        public async Task<ServiceResponse> RestoreFromFileAsync(
            string macAddress,
            string pincode,
            string detectorType,
            string firmwareVersion,
            string backupFilePath,
            string? logId)
        {
            AppLog.Info($"[Restore] START");
AppLog.Info($"[Restore] MAC={macAddress}, Detector={detectorType}, FW={firmwareVersion}, LogId={logId}");
AppLog.Info($"[Restore] BackupFile={backupFilePath}");
// Resolve backup path robustly (absolute/relative, fallback by MAC)
            backupFilePath = ResolveBackupPath(macAddress, backupFilePath);

            if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
            {
                AppLog.Error($"[Restore] Backup file not found");
return new ServiceResponse
                {
                    Success = false,
                    StatusCode = 404,
                    Message = $"Settings backup file not found: {backupFilePath}"
                };
            }

            string json;
            try
            {
                json = await File.ReadAllTextAsync(backupFilePath, Encoding.UTF8).ConfigureAwait(false);
                AppLog.Debug($"[Restore] Backup file loaded ({json.Length} chars)");
}
            catch (Exception ex)
            {
                AppLog.Error($"[Restore] Failed to read backup file: {ex}");
return new ServiceResponse
                {
                    Success = false,
                    StatusCode = 500,
                    Message = "Failed to read settings backup file."
                };
            }

            DeviceSettingsSnapshot? snap;
            try
            {
                snap = JsonSerializer.Deserialize<DeviceSettingsSnapshot>(json);
            }
            catch (Exception ex)
            {
                AppLog.Error($"[Restore] JSON deserialization failed: {ex}");
return new ServiceResponse
                {
                    Success = false,
                    StatusCode = 400,
                    Message = "Settings backup file could not be parsed."
                };
            }

            if (snap == null)
            {
                AppLog.Error($"[Restore] Snapshot is null after deserialize");
return new ServiceResponse
                {
                    Success = false,
                    StatusCode = 400,
                    Message = "Settings backup file could not be parsed."
                };
            }

            AppLog.Info($"[Restore] Snapshot loaded:");
AppLog.Info($"  UserConfigHex      = {Trunc(snap.UserConfigHex)}");
AppLog.Info($"  WiredPushButtons   = {Trunc(snap.PushButtonsHex)}");
AppLog.Info($"  DaliPushButtons    = {Trunc(snap.DaliPushButtonsHex)}");
AppLog.Info($"  BlePushButtons     = {Trunc(snap.BlePushButtonsHex)}");
// ---- Rules loading ----
            SettingsVersionPatcher.RulesRoot rulesRoot;

            try
            {
                rulesRoot = FirmwareRulesLoader.LoadFromRootFolder();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[Restore] Rules load failed: {ex}");
rulesRoot = new SettingsVersionPatcher.RulesRoot(); // safe fallback
            }

            SettingsVersionPatcher.ApplyRestoreVersionRules(
                snap,
                firmwareVersion,
                rulesRoot
            );

            // Apply per-detector restore profile (even if backup file contains fields)
            var profile = GetProfile(detectorType);

            AppLog.Info($"[Restore] Snapshot AFTER patching:");
AppLog.Info($"  UserConfigHex      = {Trunc(snap.UserConfigHex)}");
AppLog.Info($"  WiredPushButtons   = {Trunc(snap.PushButtonsHex)}");
AppLog.Info($"  DaliPushButtons    = {Trunc(snap.DaliPushButtonsHex)}");
AppLog.Info($"  BlePushButtons     = {Trunc(snap.BlePushButtonsHex)}");
bool ok = true;

            // ---- BLE restores ----
            if (profile.UserConfig && !string.IsNullOrWhiteSpace(snap.UserConfigHex))
            {
                AppLog.Info($"[Restore] Writing UserConfig...");
                bool r = await WriteWithRetryAsync(
                        () => _ble.SetUserConfig(macAddress, snap.UserConfigHex),
                        "UserConfig")
                    .ConfigureAwait(false);
                AppLog.Info($"[Restore] UserConfig result={r}");
                ok &= r;
            }

            if (profile.WiredPushButtons && !string.IsNullOrWhiteSpace(snap.PushButtonsHex))
            {
                AppLog.Info($"[Restore] Writing Wired PushButtons...");
                bool r = await WriteWithRetryAsync(
                        () => _ble.SetWiredPushButtonList(macAddress, snap.PushButtonsHex),
                        "Wired PushButtons")
                    .ConfigureAwait(false);
                AppLog.Info($"[Restore] Wired PushButtons result={r}");
                ok &= r;
            }

            if (profile.DaliPushButtons && !string.IsNullOrWhiteSpace(snap.DaliPushButtonsHex))
            {
                AppLog.Info($"[Restore] Writing DALI PushButtons...");
                bool r = await WriteWithRetryAsync(
                        () => _ble.SetDaliPushButtonList(macAddress, snap.DaliPushButtonsHex),
                        "DALI PushButtons")
                    .ConfigureAwait(false);
                AppLog.Info($"[Restore] DALI PushButtons result={r}");
                ok &= r;
            }

            if (profile.BlePushButtons && !string.IsNullOrWhiteSpace(snap.BlePushButtonsHex))
            {
                AppLog.Info($"[Restore] Writing BLE PushButtons...");
                bool r = await WriteWithRetryAsync(
                        () => _ble.SetBLEPushButtonList(macAddress, snap.BlePushButtonsHex),
                        "BLE PushButtons")
                    .ConfigureAwait(false);
                AppLog.Info($"[Restore] BLE PushButtons result={r}");
                ok &= r;
            }

            AppLog.Info($"[Restore] END ok={ok}");
return new ServiceResponse
            {
                Success = ok,
                StatusCode = ok ? 200 : 500,
                Message = ok
                    ? "Settings restored successfully."
                    : "Settings restore failed for one or more sections."
            };
        }

        private static string Trunc(string? hex, int max = 24)
        {
            if (string.IsNullOrWhiteSpace(hex)) return "<null>";
            return hex.Length <= max ? hex : hex.Substring(0, max) + "...";
        }

        private static async Task<bool> WriteWithRetryAsync(
            Func<Task<bool>> action,
            string label,
            int maxAttempts = 3,
            int delayMs = 2000)
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var ok = await action().ConfigureAwait(false);
                    if (ok) return true;

                    AppLog.Warn($"[Restore] {label} failed (attempt {attempt}/{maxAttempts}).");
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"[Restore] {label} exception (attempt {attempt}/{maxAttempts}): {ex.Message}");
                }

                if (attempt < maxAttempts)
                    await Task.Delay(delayMs).ConfigureAwait(false);
            }

            return false;
        }

        private static bool HexEquals(string? a, string? b)
            => string.Equals(NormalizeHexForCompare(a), NormalizeHexForCompare(b), StringComparison.Ordinal);

        private static string NormalizeHexForCompare(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        }

        private static bool TryBuildMergedSectionHex(
            string? currentHex,
            string? requestedHex,
            string? requestedMaskHex,
            out string mergedHex,
            out string error)
        {
            mergedHex = string.Empty;
            error = string.Empty;

            var normalizedCurrent = NormalizeHexForCompare(currentHex);
            var normalizedRequested = NormalizeHexForCompare(requestedHex);
            var normalizedMask = NormalizeHexForCompare(requestedMaskHex);

            if (string.IsNullOrWhiteSpace(normalizedRequested))
            {
                error = "No value hex was supplied for the section.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(normalizedMask) && normalizedMask.Length != normalizedRequested.Length)
            {
                error = $"Mask length ({normalizedMask.Length / 2} bytes) must match value length ({normalizedRequested.Length / 2} bytes).";
                return false;
            }

            if (string.IsNullOrWhiteSpace(normalizedMask))
            {
                mergedHex = normalizedRequested;
                return true;
            }

            var currentBytes = HexToBytes(normalizedCurrent);
            var valueBytes = HexToBytes(normalizedRequested);
            var maskBytes = HexToBytes(normalizedMask);
            var targetLen = Math.Max(currentBytes.Length, Math.Max(valueBytes.Length, maskBytes.Length));
            if (targetLen <= 0)
            {
                mergedHex = string.Empty;
                return true;
            }

            if (currentBytes.Length == 0)
            {
                error = "Current section is empty. Masked updates require a baseline section value.";
                return false;
            }

            Array.Resize(ref currentBytes, targetLen);
            Array.Resize(ref valueBytes, targetLen);
            Array.Resize(ref maskBytes, targetLen);

            var merged = new byte[targetLen];
            for (var i = 0; i < targetLen; i++)
            {
                var m = maskBytes[i];
                merged[i] = (byte)((currentBytes[i] & ~m) | (valueBytes[i] & m));
            }

            mergedHex = Convert.ToHexString(merged);
            return true;
        }

        private static byte[] HexToBytes(string? hex)
        {
            var clean = NormalizeHexForCompare(hex);
            if (string.IsNullOrWhiteSpace(clean))
                return Array.Empty<byte>();
            if ((clean.Length & 1) != 0)
                clean = "0" + clean;
            return Convert.FromHexString(clean);
        }


        private static string StripBleHeader(string? hex, int headerBytes = 0)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return string.Empty;

            int charsToRemove = headerBytes * 2;

            if (hex.Length <= charsToRemove)
                return string.Empty;

            return hex.Substring(charsToRemove);
        }
    }
}
