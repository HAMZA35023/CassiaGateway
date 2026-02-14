using System;
using System.IO;
using System.Threading.Tasks;
using AccessAPP.Logging;
using AccessAPP.Services.HelperClasses;

namespace AccessAPP.Services.UpgradePipeline.Steps;

internal sealed class SettingsBackupStep : IDeviceUpgradeStep
{
    public async Task<bool> ExecuteAsync(DeviceUpgradeContext ctx)
    {
        var svc = ctx.Svc;
        var dev = ctx.Dev;

        // 1) Settings backup (best-effort but CRITICAL gating before FW update)
        if (!(RuntimeVariables.RestoreSettingsAfterUpgrade && !ctx.IsInBoot))
            return true;

        if (!UpgradePipelineSupport.SupportsSettingsBackup(ctx.DetectorType))
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Settings backup skipped (unsupported detector)", "Info", ctx.FirmwareVersion);
            return true;
        }

        // Only take backup when we will actually update firmware, and only once per device
        if (!ctx.UpgradeActor && !ctx.UpgradeBootloader && !ctx.UpgradeSensor)
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Settings backup skipped (no FW steps in this attempt)", "Info", ctx.FirmwareVersion);
            AppLog.Info($"Skipping settings backup for {ctx.MacAddress} - no FW steps in this attempt");
            return true;
        }

        // If backup already exists, reuse it and DO NOT block upgrade on connect/login here.
        if (!string.IsNullOrWhiteSpace(ctx.SettingsBackupPath) && File.Exists(ctx.SettingsBackupPath))
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"Settings backup already exists: {ctx.SettingsBackupPath}", "Info", ctx.FirmwareVersion);
            AppLog.Info($"Settings backup already exists for {ctx.MacAddress}: {ctx.SettingsBackupPath}");
            return true;
        }

        AppLog.Info($"Starting settings backup for {ctx.MacAddress}");
        try
        {
            bool reuseSession =
                RuntimeVariables.UPGRADE_OPTIMIZE_RECONNECT_FLOW &&
                dev.PrecheckSessionAlive &&
                !ctx.IsInBoot;

            bool loggedInViaReuse = false;
            if (reuseSession)
            {
                AppLog.Info($"Reusing precheck session for settings backup: {ctx.MacAddress}");
                loggedInViaReuse = await svc.EnsureLoginOnConnectedSessionUnlessBootModeAsync(
                    ctx.MacAddress,
                    ctx.Pincode,
                    ctx.LogId,
                    ctx.FirmwareVersion,
                    stageName: "LoggedIn (settings backup)",
                    maxAttempts: 2).ConfigureAwait(false);

                if (loggedInViaReuse)
                {
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Connected", "Reusing precheck session for settings backup", ctx.FirmwareVersion);
                }
            }

            if (!loggedInViaReuse)
            {
                // IMPORTANT: increased retries + delays, because logs show 417 after boot transitions.
                var cl = await svc.ConnectAndLoginWithRetryForPipelineAsync(
                    svc.GatewayIpAddress, 80, ctx.MacAddress, ctx.Pincode, ctx.LogId, ctx.FirmwareVersion,
                    maxAttempts: Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS),
                    delayBetweenAttemptsMs: 5000).ConfigureAwait(false);

                if (!cl.Success)
                {
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"[1] Connect+login failed: {cl.Message}", "Warn", ctx.FirmwareVersion);
                    AppLog.Warn($" [1] Connect+login failed for {ctx.MacAddress}: {cl.Message}");

                    // CRITICAL: Do NOT start firmware update if backup cannot be taken.
                    ctx.Response.Success = false;
                    ctx.Response.StatusCode = cl.StatusCode;
                    ctx.Response.Message = $"Settings backup blocked upgrade: {cl.Message}";
                    dev.LastFailureReason = ctx.Response.Message;
                    dev.shouldRetry = false;
                    return false;
                }
            }

            if (RuntimeVariables.AutoSetSysFailLevelUnderUpdate && (ctx.DetectorType == "P48" || ctx.DetectorType == "P47"))
            {
                var original = await svc.DaliGetDeviceSysFailLevelAsync(ctx.MacAddress).ConfigureAwait(false);
                ctx.OriginalDaliSysFailLevel = original;

                if (original.HasValue)
                {
                    var oldHex = $"0x{original.Value:X2}";
                    AppLog.Info($"DALI SysFail Level original value for {ctx.MacAddress}: {oldHex}");
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"DALI SysFail Level original: {oldHex}", "Info", ctx.FirmwareVersion);
                }
                else
                {
                    AppLog.Warn($"Could not read original DALI SysFail Level for {ctx.MacAddress}");
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "DALI SysFail Level original read failed", "Warn", ctx.FirmwareVersion);
                }

                if (await svc.DaliSetDeviceSysFailLevelAsync(ctx.MacAddress, 0xFF).ConfigureAwait(false))
                {
                    AppLog.Info($"DALI SysFail Level set to 0xFF for {ctx.MacAddress}");
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "DALI SysFail Level set to 0xFF", "Success", ctx.FirmwareVersion);
                }
                else
                {
                    AppLog.Warn($"Failed to set DALI SysFail Level for {ctx.MacAddress}");
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "DALI SysFail Level set failed", "Warn", ctx.FirmwareVersion);
                }
            }

            int backupRounds = Math.Max(1, RuntimeVariables.UPGRADE_SETTINGS_BACKUP_RETRY_ROUNDS);
            int backupRetryDelayMs = Math.Max(500, RuntimeVariables.UPGRADE_SETTINGS_BACKUP_RETRY_DELAY_MS);
            Exception? lastBackupEx = null;
            bool backupOk = false;

            for (int attempt = 1; attempt <= backupRounds; attempt++)
            {
                try
                {
                    var backup = await svc.SettingsBackupService
                        .BackupToFileAsync(ctx.MacAddress, ctx.Pincode, ctx.DetectorType, ctx.FirmwareVersion, ctx.LogId)
                        .ConfigureAwait(false);

                    ctx.SettingsBackupPath = backup.filePath;
                    dev.SettingsBackupPath = ctx.SettingsBackupPath;

                    if (string.IsNullOrWhiteSpace(ctx.SettingsBackupPath) || !File.Exists(ctx.SettingsBackupPath))
                        throw new Exception($"Settings backup failed (file missing): {ctx.SettingsBackupPath}");

                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Settings backup saved", "Success", ctx.FirmwareVersion);
                    AppLog.Info($" Settings backup saved for {ctx.MacAddress} to: {ctx.SettingsBackupPath}");
                    backupOk = true;
                    break;
                }
                catch (Exception ex)
                {
                    lastBackupEx = ex;
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"Settings backup attempt {attempt}/{backupRounds} failed: {ex.Message}", "Warn", ctx.FirmwareVersion);
                    AppLog.Warn($" Settings backup attempt {attempt}/{backupRounds} failed for {ctx.MacAddress}: {ex.Message}");

                    if (attempt < backupRounds)
                    {
                        // Best-effort reconnect before retry
                        try
                        {
                            await svc.ConnectService
                                .DisconnectFromBleDevice(svc.GatewayIpAddress, ctx.MacAddress, 0, chip: svc.GetChipForMac(ctx.MacAddress))
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            // ignore
                        }

                        var cl = await svc.ConnectAndLoginWithRetryForPipelineAsync(
                            svc.GatewayIpAddress, 80, ctx.MacAddress, ctx.Pincode, ctx.LogId, ctx.FirmwareVersion,
                            maxAttempts: Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS),
                            delayBetweenAttemptsMs: 5000).ConfigureAwait(false);

                        if (!cl.Success)
                        {
                            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"Settings backup reconnect failed: {cl.Message}", "Warn", ctx.FirmwareVersion);
                            AppLog.Warn($" Settings backup reconnect failed for {ctx.MacAddress}: {cl.Message}");
                        }

                        await Task.Delay(backupRetryDelayMs).ConfigureAwait(false);
                    }
                }
            }

            if (!backupOk)
            {
                ctx.Response.Success = false;
                ctx.Response.StatusCode = 500;
                ctx.Response.Message = $"Settings backup failed: {lastBackupEx?.Message}";
                UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, ctx.Response.Message, "Failed", ctx.FirmwareVersion);
                dev.LastFailureReason = ctx.Response.Message;
                dev.shouldRetry = false;
                return false;
            }
        }
        catch (Exception ex)
        {
            ctx.Response.Success = false;
            ctx.Response.StatusCode = 500;
            ctx.Response.Message = $"Settings backup failed: {ex.Message}";
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, ctx.Response.Message, "Failed", ctx.FirmwareVersion);
            AppLog.Error($" Settings backup failed for {ctx.MacAddress}: {ex}");
            dev.LastFailureReason = ctx.Response.Message;
            dev.shouldRetry = false;
            return false;
        }

        return true;
    }
}
