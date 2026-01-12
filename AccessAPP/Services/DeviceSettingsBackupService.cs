using AccessAPP.Models;
using System;
using System.IO;
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

        public async Task<(string filePath, DeviceSettingsSnapshot snapshot)> BackupToFileAsync(
            string macAddress,
            string pincode, // kept in interface for your earlier calls; not used here
            string detectorType,
            string firmwareVersion,
            string? logId)
        {
            var path = GetBackupPath(macAddress, logId);

            var snapshot = new DeviceSettingsSnapshot
            {
                MacAddress = macAddress,
                CapturedAt = DateTimeOffset.Now,
                DetectorType = detectorType,
                FirmwareVersionTarget = firmwareVersion,

                UserConfigHex = StripBleHeader(
                    await _ble.GetUserConfig(macAddress).ConfigureAwait(false)
                ),

                PushButtonsHex = StripBleHeader(
                    await _ble.GetWiredPushButtonList(macAddress).ConfigureAwait(false)
                ),

                DaliPushButtonsHex = StripBleHeader(
                    await _ble.GetDaliPushButtonList(macAddress).ConfigureAwait(false)
                ),

                BlePushButtonsHex = StripBleHeader(
                    await _ble.GetBLEPushButtonList(macAddress).ConfigureAwait(false)
                ),
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
            Console.WriteLine($"[Restore] START");
            Console.WriteLine($"[Restore] MAC={macAddress}, Detector={detectorType}, FW={firmwareVersion}, LogId={logId}");
            Console.WriteLine($"[Restore] BackupFile={backupFilePath}");

            if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
            {
                Console.WriteLine($"[Restore][ERROR] Backup file not found");
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
                Console.WriteLine($"[Restore] Backup file loaded ({json.Length} chars)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Restore][ERROR] Failed to read backup file: {ex}");
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
                Console.WriteLine($"[Restore][ERROR] JSON deserialization failed: {ex}");
                return new ServiceResponse
                {
                    Success = false,
                    StatusCode = 400,
                    Message = "Settings backup file could not be parsed."
                };
            }

            if (snap == null)
            {
                Console.WriteLine($"[Restore][ERROR] Snapshot is null after deserialize");
                return new ServiceResponse
                {
                    Success = false,
                    StatusCode = 400,
                    Message = "Settings backup file could not be parsed."
                };
            }

            Console.WriteLine($"[Restore] Snapshot loaded:");
            Console.WriteLine($"  UserConfigHex      = {Trunc(snap.UserConfigHex)}");
            Console.WriteLine($"  WiredPushButtons   = {Trunc(snap.PushButtonsHex)}");
            Console.WriteLine($"  DaliPushButtons    = {Trunc(snap.DaliPushButtonsHex)}");
            Console.WriteLine($"  BlePushButtons     = {Trunc(snap.BlePushButtonsHex)}");

            // ---- Rules loading ----
            SettingsVersionPatcher.RulesRoot rulesRoot;

            try
            {
                rulesRoot = FirmwareRulesLoader.LoadFromRootFolder();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Restore][WARN] Rules load failed: {ex}");
                rulesRoot = new SettingsVersionPatcher.RulesRoot(); // safe fallback
            }

            SettingsVersionPatcher.ApplyRestoreVersionRules(
                snap,
                firmwareVersion,
                rulesRoot
            );

            Console.WriteLine($"[Restore] Snapshot AFTER patching:");
            Console.WriteLine($"  UserConfigHex      = {Trunc(snap.UserConfigHex)}");
            Console.WriteLine($"  WiredPushButtons   = {Trunc(snap.PushButtonsHex)}");
            Console.WriteLine($"  DaliPushButtons    = {Trunc(snap.DaliPushButtonsHex)}");
            Console.WriteLine($"  BlePushButtons     = {Trunc(snap.BlePushButtonsHex)}");

            bool ok = true;

            // ---- BLE restores ----
            if (!string.IsNullOrWhiteSpace(snap.UserConfigHex))
            {
                Console.WriteLine($"[Restore] Writing UserConfig...");
                bool r = await _ble.SetUserConfig(macAddress, snap.UserConfigHex).ConfigureAwait(false);
                Console.WriteLine($"[Restore] UserConfig result={r}");
                ok &= r;
            }

            if (!string.IsNullOrWhiteSpace(snap.PushButtonsHex))
            {
                Console.WriteLine($"[Restore] Writing Wired PushButtons...");
                bool r = await _ble.SetWiredPushButtonList(macAddress, snap.PushButtonsHex).ConfigureAwait(false);
                Console.WriteLine($"[Restore] Wired PushButtons result={r}");
                ok &= r;
            }

            if (!string.IsNullOrWhiteSpace(snap.DaliPushButtonsHex))
            {
                Console.WriteLine($"[Restore] Writing DALI PushButtons...");
                bool r = await _ble.SetDaliPushButtonList(macAddress, snap.DaliPushButtonsHex).ConfigureAwait(false);
                Console.WriteLine($"[Restore] DALI PushButtons result={r}");
                ok &= r;
            }

            if (!string.IsNullOrWhiteSpace(snap.BlePushButtonsHex))
            {
                Console.WriteLine($"[Restore] Writing BLE PushButtons...");
                bool r = await _ble.SetBLEPushButtonList(macAddress, snap.BlePushButtonsHex).ConfigureAwait(false);
                Console.WriteLine($"[Restore] BLE PushButtons result={r}");
                ok &= r;
            }

            Console.WriteLine($"[Restore] END ok={ok}");

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
