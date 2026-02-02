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

        public async Task<(string filePath, DeviceSettingsSnapshot snapshot)> BackupToFileAsync(
            string macAddress,
            string pincode, // kept in interface for your earlier calls; not used here
            string detectorType,
            string firmwareVersion,
            string? logId)
        {
            var path = GetBackupPath(macAddress, logId);

            var profile = GetProfile(detectorType);

            var snapshot = new DeviceSettingsSnapshot
            {
                MacAddress = macAddress,
                CapturedAt = DateTimeOffset.Now,
                DetectorType = detectorType,
                FirmwareVersionTarget = firmwareVersion,

                UserConfigHex = profile.UserConfig
                    ? StripBleHeader(await _ble.GetUserConfig(macAddress).ConfigureAwait(false))
                    : null,

                PushButtonsHex = profile.WiredPushButtons
                    ? StripBleHeader(await _ble.GetWiredPushButtonList(macAddress).ConfigureAwait(false))
                    : null,

                DaliPushButtonsHex = profile.DaliPushButtons
                    ? StripBleHeader(await _ble.GetDaliPushButtonList(macAddress).ConfigureAwait(false))
                    : null,

                BlePushButtonsHex = profile.BlePushButtons
                    ? StripBleHeader(await _ble.GetBLEPushButtonList(macAddress).ConfigureAwait(false))
                    : null,
            };

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, Encoding.UTF8).ConfigureAwait(false);

            return (path, snapshot);
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
bool r = await _ble.SetUserConfig(macAddress, snap.UserConfigHex).ConfigureAwait(false);
                AppLog.Info($"[Restore] UserConfig result={r}");
ok &= r;
            }

            if (profile.WiredPushButtons && !string.IsNullOrWhiteSpace(snap.PushButtonsHex))
            {
                AppLog.Info($"[Restore] Writing Wired PushButtons...");
bool r = await _ble.SetWiredPushButtonList(macAddress, snap.PushButtonsHex).ConfigureAwait(false);
                AppLog.Info($"[Restore] Wired PushButtons result={r}");
ok &= r;
            }

            if (profile.DaliPushButtons && !string.IsNullOrWhiteSpace(snap.DaliPushButtonsHex))
            {
                AppLog.Info($"[Restore] Writing DALI PushButtons...");
bool r = await _ble.SetDaliPushButtonList(macAddress, snap.DaliPushButtonsHex).ConfigureAwait(false);
                AppLog.Info($"[Restore] DALI PushButtons result={r}");
ok &= r;
            }

            if (profile.BlePushButtons && !string.IsNullOrWhiteSpace(snap.BlePushButtonsHex))
            {
                AppLog.Info($"[Restore] Writing BLE PushButtons...");
bool r = await _ble.SetBLEPushButtonList(macAddress, snap.BlePushButtonsHex).ConfigureAwait(false);
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
