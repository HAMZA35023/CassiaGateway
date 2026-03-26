using System;
using System.Net;
using System.Threading.Tasks;
using AccessAPP.Logging;
using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;

namespace AccessAPP.Services.UpgradePipeline.Steps;

internal sealed class SettingsRestoreStep : IDeviceUpgradeStep
{
    public async Task<bool> ExecuteAsync(DeviceUpgradeContext ctx)
    {
        var svc = ctx.Svc;
        var dev = ctx.Dev;

        bool restorePending = dev.requiresConfigRestore && !dev.isConfigRestored;

        if (!ctx.AnyFirmwareStepExecuted && !restorePending)
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Settings restore skipped (no FW step executed in this attempt)", "Info", ctx.FirmwareVersion);
            return true;
        }

        // 3) Restore settings right after sensor upgrade (do NOT reboot here)
        if (!(RuntimeVariables.RestoreSettingsAfterUpgrade && UpgradePipelineSupport.SupportsSettingsBackup(ctx.DetectorType)))
            return true;

        ctx.SettingsBackupPath ??= dev.SettingsBackupPath;
        if (string.IsNullOrWhiteSpace(ctx.SettingsBackupPath))
        {
            // No local backup — ask peers on the same MQTT network before giving up.
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "No local backup — querying peers via MQTT", "Info", ctx.FirmwareVersion);
            var peerSnapshot = await svc.RequestPeerSnapshotAsync(ctx.MacAddress, ctx.LogId).ConfigureAwait(false);
            if (peerSnapshot != null)
            {
                try
                {
                    var peerPath = await svc.SettingsBackupService.SaveSnapshotAsync(peerSnapshot).ConfigureAwait(false);
                    ctx.SettingsBackupPath = peerPath;
                    dev.SettingsBackupPath = peerPath;
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"Settings backup received from peer: {peerPath}", "Success", ctx.FirmwareVersion);
                    AppLog.Info($"[PeerBackup] Saved peer backup for {ctx.MacAddress} to: {peerPath}");
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"[PeerBackup] Failed to save peer snapshot for {ctx.MacAddress}: {ex.Message}");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(ctx.SettingsBackupPath))
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Settings restore skipped (no backup file available)", "Failed", ctx.FirmwareVersion);
            AppLog.Error($" Settings restore skipped for {ctx.MacAddress} - no backup file available");

            if (dev.requiresConfigRestore)
            {
                // Keep shouldRetry=true: the backup is missing because the device started
                // in boot mode and the backup step couldn't run (or a transient failure).
                // A retry will re-attempt the backup step from Application mode and the
                // restore can then succeed.  Disabling retries here would prevent the actor
                // upgrade (a separate concern) from being retried as well.
                dev.finalUpgradeResult = "Warn";
            }
            return true;
        }

        await Task.Delay(10000).ConfigureAwait(false);

        bool alreadyConnected = false;

        if (ctx.IsInBoot)
        {
            try
            {
                // Re-check boot mode after delay to avoid stale status across retries.
                ctx.IsInBoot = svc.CheckIfDeviceInBootMode(svc.GatewayIpAddress, ctx.MacAddress);
            }
            catch (Exception ex)
            {
                UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"BootMode check exception before settings restore: {ex.Message}", "Warn", ctx.FirmwareVersion);
            }

            if (ctx.IsInBoot)
            {
                // Device is still in boot mode on the existing connection — disconnect and reconnect
                // to see if the device has rebooted into application firmware by now.
                // Checking mode on a live bootloader connection always reports boot mode even if the
                // device has already transitioned; a fresh connect picks up the new GATT table.
                UpgradeLogger.Log(ctx.LogId, ctx.MacAddress,
                    "Settings restore: device still in boot mode — disconnecting and reconnecting to check app-mode transition",
                    "Info", ctx.FirmwareVersion);
                AppLog.Info($" Settings restore: {ctx.MacAddress} still in boot mode — disconnecting and reconnecting");

                await svc.ConnectService
                    .DisconnectFromBleDevice(svc.GatewayIpAddress, ctx.MacAddress, 0, chip: ctx.ChipId)
                    .ConfigureAwait(false);

                var bootRecovery = await svc.ConnectAndLoginWithRetryForPipelineAsync(
                    svc.GatewayIpAddress, 80, ctx.MacAddress, ctx.Pincode, ctx.LogId, ctx.FirmwareVersion,
                    maxAttempts: Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS),
                    delayBetweenAttemptsMs: 6000,
                    bootModeIsRetryable: true).ConfigureAwait(false);

                if (!bootRecovery.Success)
                {
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress,
                        $"Settings restore skipped (reconnect after boot mode failed: {bootRecovery.Message})",
                        "Warn", ctx.FirmwareVersion);
                    AppLog.Warn($" Settings restore skipped for {ctx.MacAddress} — reconnect failed: {bootRecovery.Message}");
                    return true;
                }

                // Re-check mode on the fresh connection.
                try { ctx.IsInBoot = svc.CheckIfDeviceInBootMode(svc.GatewayIpAddress, ctx.MacAddress); } catch { }
                if (ctx.IsInBoot)
                {
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress,
                        "Settings restore skipped (device still in boot mode after reconnect)",
                        "Warn", ctx.FirmwareVersion);
                    AppLog.Warn($" Settings restore skipped for {ctx.MacAddress} — still in boot mode after reconnect");
                    await svc.ConnectService
                        .DisconnectFromBleDevice(svc.GatewayIpAddress, ctx.MacAddress, 0, chip: ctx.ChipId)
                        .ConfigureAwait(false);
                    return true;
                }

                UpgradeLogger.Log(ctx.LogId, ctx.MacAddress,
                    "Settings restore: app mode confirmed after reconnect — proceeding",
                    "Info", ctx.FirmwareVersion);
                AppLog.Info($" Settings restore: {ctx.MacAddress} now in app mode — proceeding (already connected)");
                alreadyConnected = true;
            }
        }

        if (!alreadyConnected)
        {
            AppLog.Info($"Starting settings restore for {ctx.MacAddress} - trying to connect and login");

            var cl = await svc.ConnectAndLoginWithRetryForPipelineAsync(
                svc.GatewayIpAddress, 80, ctx.MacAddress, ctx.Pincode, ctx.LogId, ctx.FirmwareVersion,
                maxAttempts: Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS),
                delayBetweenAttemptsMs: 6000).ConfigureAwait(false);

            if (!cl.Success)
            {
                UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"Restore connect+login failed: {cl.Message}", "Warn", ctx.FirmwareVersion);
                AppLog.Warn($" Restore connect+login failed for {ctx.MacAddress}: {cl.Message}");

                ctx.Response.Success = false;
                ctx.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                ctx.Response.Message = "Could not connect and login to detector!";

                if (dev.requiresConfigRestore)
                {
                    dev.LastFailureReason = ctx.Response.Message;
                    dev.shouldRetry = false;

                    return false;
                }

                // If config restore isn't required, we keep going.
                return true;
            }
        }

        AppLog.Info($"Starting settings restore for {ctx.MacAddress} - upload config");
        try
        {
            ServiceResponse restore = new() { Success = false, StatusCode = 500, Message = "Restore not attempted" };
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                UpgradeLogger.Log(
                    ctx.LogId,
                    ctx.MacAddress,
                    $"Settings restore attempt {attempt}/3: Starting",
                    "Info",
                    ctx.FirmwareVersion);

                restore = await svc.SettingsBackupService.RestoreFromFileAsync(
                        ctx.MacAddress,
                        ctx.Pincode,
                        ctx.DetectorType,
                        ctx.FirmwareVersion,
                        ctx.SettingsBackupPath,
                        ctx.LogId)
                    .ConfigureAwait(false);

                UpgradeLogger.Log(
                    ctx.LogId,
                    ctx.MacAddress,
                    $"Settings restore attempt {attempt}/3: {(restore.Success ? "Success" : "Fail")} - {restore.Message}",
                    restore.Success ? "Success" : "Failed",
                    ctx.FirmwareVersion);

                AppLog.Info($" Settings restore attempt {attempt}/3 for {ctx.MacAddress} - result: {restore.Success} - {restore.Message}");

                if (restore.Success)
                    break;

                await Task.Delay(4000).ConfigureAwait(false);
            }

            dev.isConfigRestored = restore.Success;

            if (!restore.Success && dev.requiresConfigRestore)
            {
                ctx.Response.Success = false;
                ctx.Response.StatusCode = restore.StatusCode;
                ctx.Response.Message = $"Settings restore failed after retries: {restore.Message}";
                dev.LastFailureReason = ctx.Response.Message;
                dev.shouldRetry = false;
                return false;
            }
        }
        catch (Exception ex)
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"Settings restore failed: {ex.Message}", "Failed", ctx.FirmwareVersion);
            AppLog.Error($" Settings restore failed for {ctx.MacAddress}: {ex}");
            dev.isConfigRestored = false;

            if (dev.requiresConfigRestore)
            {
                ctx.Response.Success = false;
                ctx.Response.StatusCode = 500;
                ctx.Response.Message = $"Settings restore exception: {ex.Message}";
                dev.LastFailureReason = ctx.Response.Message;
                dev.shouldRetry = false;
                dev.finalUpgradeResult = "Warn";
                return false;
            }
        }

        bool postActorNeedsConnection =
            UpgradePipelineSupport.IsDaliMaster(ctx.DetectorType) &&
            (RuntimeVariables.AutoSetSysFailLevelUnderUpdate || RuntimeVariables.Restore102DBAfterUpgrade);

        bool actorStepPending = ctx.UpgradeActor && !dev.ActorSuccess;
        bool keepSessionForPostActor =
            RuntimeVariables.UPGRADE_OPTIMIZE_RECONNECT_FLOW &&
            postActorNeedsConnection &&
            !actorStepPending;

        if (keepSessionForPostActor)
        {
            ctx.ReuseConnectionForPostActor = true;
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Connected", "Session kept for post-actor steps", ctx.FirmwareVersion);
            return true;
        }

        await svc.ConnectService.DisconnectFromBleDevice(svc.GatewayIpAddress, ctx.MacAddress, 0, chip: ctx.ChipId).ConfigureAwait(false);
        return true;
    }
}
