using System;
using System.Threading.Tasks;
using AccessAPP.Logging;
using AccessAPP.Services.HelperClasses;

namespace AccessAPP.Services.UpgradePipeline.Steps;

internal sealed class PostActorStep : IDeviceUpgradeStep
{
    public async Task<bool> ExecuteAsync(DeviceUpgradeContext ctx)
    {
        var svc = ctx.Svc;
        var dev = ctx.Dev;
        var requestedCommissioningScan =
            dev.RunDaliAddressAllToZone1AfterUpdate ||
            dev.RunDali102TotalNewScanAfterUpdate ||
            dev.RunDali103TotalNewScanAfterUpdate;

        if (!ctx.AnyFirmwareStepExecuted && !requestedCommissioningScan)
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Post-actor skipped (no FW step executed in this attempt)", "Info", ctx.FirmwareVersion);
            return true;
        }

        if (!UpgradePipelineSupport.SupportsSettingsBackup(ctx.DetectorType))
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"Post-actor steps skipped (unsupported detector '{ctx.DetectorType}')", "Info", ctx.FirmwareVersion);
            return true;
        }

        var isDaliMaster = UpgradePipelineSupport.IsDaliMaster(ctx.DetectorType);

        if (isDaliMaster)
        {
            if (!ctx.AnyFirmwareStepExecuted && requestedCommissioningScan)
            {
                UpgradeLogger.Log(
                    ctx.LogId,
                    ctx.MacAddress,
                    "Post-actor: running requested DALI commissioning scans without FW flashing",
                    "Info",
                    ctx.FirmwareVersion);
            }

            bool loggedIn = false;
            bool reusedSession =
                RuntimeVariables.UPGRADE_OPTIMIZE_RECONNECT_FLOW &&
                ctx.ReuseConnectionForPostActor;

            if (!reusedSession)
            {
                await Task.Delay(10000).ConfigureAwait(false);
            }
            else
            {
                UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Connected", "Skipped (reusing session from settings restore)", ctx.FirmwareVersion);
                loggedIn = await svc.EnsureLoginOnConnectedSessionUnlessBootModeAsync(
                    ctx.MacAddress,
                    ctx.Pincode,
                    ctx.LogId,
                    ctx.FirmwareVersion,
                    stageName: "LoggedIn (post-actor reused session)",
                    maxAttempts: 1).ConfigureAwait(false);

                if (!loggedIn)
                {
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Connected", "Reused session unavailable; reconnecting", ctx.FirmwareVersion);
                    ctx.ReuseConnectionForPostActor = false;
                }
            }

            if (!loggedIn)
            {
                AppLog.Info($"Post-actor: connect+login for {ctx.MacAddress}");
                var cl = await svc.ConnectAndLoginWithRetryForPipelineAsync(
                    svc.GatewayIpAddress, 80, ctx.MacAddress, ctx.Pincode, ctx.LogId, ctx.FirmwareVersion,
                    maxAttempts: Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS),
                    delayBetweenAttemptsMs: 6000,
                    bootModeIsRetryable: true).ConfigureAwait(false);

                if (!cl.Success)
                {
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"Post-actor connect+login failed: {cl.Message}", "Warn", ctx.FirmwareVersion);
                    ctx.Response.Success = false;
                    ctx.Response.StatusCode = 500;
                    ctx.Response.Message = "Could not connect and login to detector!";
                    return false;
                }
            }

            if (ctx.AnyFirmwareStepExecuted && RuntimeVariables.AutoSetSysFailLevelUnderUpdate)
            {
                var restoreValue = ctx.OriginalDaliSysFailLevel ?? (byte)0xFE;
                var restoreHex = $"0x{restoreValue:X2}";

                if (await svc.DaliSetDeviceSysFailLevelAsync(ctx.MacAddress, restoreValue).ConfigureAwait(false))
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"DALI SysFail Level restored to {restoreHex}", "Success", ctx.FirmwareVersion);
                else
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"DALI SysFail Level restore failed ({restoreHex})", "Warn", ctx.FirmwareVersion);
            }
        }

        bool actorWasUpdatedThisRun = ctx.AnyFirmwareStepExecuted && ctx.ActorFirmwareStepExecuted && dev.ActorSuccess;

        if (ctx.AnyFirmwareStepExecuted && RuntimeVariables.RebootDetectorAfterUpgrade && actorWasUpdatedThisRun && !ctx.ActorUpdatedBeforeFirmware)
        {
            AppLog.Info($"Rebooting device {ctx.MacAddress} after actor update");
            await svc.RebootDeviceAsync(ctx.MacAddress).ConfigureAwait(false);
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Device rebooted after actor update", "Success", ctx.FirmwareVersion);
            await Task.Delay(10000).ConfigureAwait(false);

            if (isDaliMaster)
            {
                await svc.ConnectAndLoginWithRetryForPipelineAsync(
                    svc.GatewayIpAddress, svc.GatewayPort, ctx.MacAddress, ctx.Pincode, ctx.LogId, ctx.FirmwareVersion,
                    maxAttempts: Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS),
                    delayBetweenAttemptsMs: 2000).ConfigureAwait(false);
            }
        }
        else if (ctx.AnyFirmwareStepExecuted && RuntimeVariables.RebootDetectorAfterUpgrade && ctx.ActorUpdatedBeforeFirmware)
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Reboot skipped (actor updated before bootloader/sensor)", "Info", ctx.FirmwareVersion);
            AppLog.Info($"Skipping post-actor reboot for {ctx.MacAddress} because actor was already updated pre-firmware.");
        }
        else if (ctx.AnyFirmwareStepExecuted && RuntimeVariables.RebootDetectorAfterUpgrade && !actorWasUpdatedThisRun)
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Reboot skipped (actor not updated in this attempt)", "Info", ctx.FirmwareVersion);
        }

        if (ctx.AnyFirmwareStepExecuted && isDaliMaster && RuntimeVariables.Restore102DBAfterUpgrade)
        {
            bool resp = false;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                resp = await svc.DaliRestore102Database(ctx.MacAddress).ConfigureAwait(false);
                AppLog.Debug($"Dali Restore 102 Database attempt {attempt}/3 response: {resp} for {ctx.MacAddress}");
                UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"Dali Restore 102 Database attempt {attempt}/3 response: {resp}", resp ? "Success" : "Failed", ctx.FirmwareVersion);
                if (resp) break;
                await Task.Delay(3000).ConfigureAwait(false);
            }

            dev.restore102Success = resp;
            if (!resp && dev.requires102Restore)
            {
                ctx.Response.Success = false;
                ctx.Response.StatusCode = 500;
                ctx.Response.Message = "DALI Restore 102 Database failed after retries";
                dev.LastFailureReason = ctx.Response.Message;
                dev.shouldRetry = false;
                dev.finalUpgradeResult = "Failed";
                return false;
            }
        }

        if (isDaliMaster && dev.RunDaliAddressAllToZone1AfterUpdate)
        {
            var addressAll = await svc.DaliRunTotalNewCommissioningScanAsync(ctx.MacAddress, 0x00).ConfigureAwait(false);
            dev.DaliAddressAllToZone1Success = addressAll.Success;
            UpgradeLogger.Log(
                ctx.LogId,
                ctx.MacAddress,
                $"DALI address-all to zone 1: {addressAll.Message}",
                addressAll.Success ? "Success" : "Failed",
                ctx.FirmwareVersion);

            if (addressAll.DevicesFound.HasValue)
            {
                UpgradeLogger.Log(
                    ctx.LogId,
                    ctx.MacAddress,
                    $"DALI address-all to zone 1 devices found: {addressAll.DevicesFound.Value}",
                    "Info",
                    ctx.FirmwareVersion);
            }

            var dbCount102 = await svc.DaliGetDeviceDatabaseCount102Async(ctx.MacAddress).ConfigureAwait(false);
            if (dbCount102.Success && dbCount102.Count.HasValue)
            {
                UpgradeLogger.Log(
                    ctx.LogId,
                    ctx.MacAddress,
                    $"DALI 102 database total devices: {dbCount102.Count.Value}",
                    "Info",
                    ctx.FirmwareVersion);
            }
            else
            {
                UpgradeLogger.Log(
                    ctx.LogId,
                    ctx.MacAddress,
                    $"DALI 102 database count read failed: {dbCount102.Message}",
                    "Warn",
                    ctx.FirmwareVersion);
            }

            if (!addressAll.Success)
            {
                ctx.Response.Success = false;
                ctx.Response.StatusCode = 500;
                ctx.Response.Message = $"DALI address-all to zone 1 failed: {addressAll.Message}";
                dev.LastFailureReason = ctx.Response.Message;
                dev.shouldRetry = false;
                dev.finalUpgradeResult = "Failed";
                return false;
            }
        }

        if (isDaliMaster && dev.RunDali102TotalNewScanAfterUpdate)
        {
            var scan102 = await svc.DaliRunTotalNewCommissioningScanAsync(ctx.MacAddress, 0x01).ConfigureAwait(false);
            dev.Dali102TotalNewScanSuccess = scan102.Success;
            UpgradeLogger.Log(
                ctx.LogId,
                ctx.MacAddress,
                $"DALI 102 total-new scan: {scan102.Message}",
                scan102.Success ? "Success" : "Failed",
                ctx.FirmwareVersion);

            if (scan102.DevicesFound.HasValue)
            {
                UpgradeLogger.Log(
                    ctx.LogId,
                    ctx.MacAddress,
                    $"DALI 102 total-new scan devices found: {scan102.DevicesFound.Value}",
                    "Info",
                    ctx.FirmwareVersion);
            }

            var dbCount102 = await svc.DaliGetDeviceDatabaseCount102Async(ctx.MacAddress).ConfigureAwait(false);
            if (dbCount102.Success && dbCount102.Count.HasValue)
            {
                UpgradeLogger.Log(
                    ctx.LogId,
                    ctx.MacAddress,
                    $"DALI 102 database total devices: {dbCount102.Count.Value}",
                    "Info",
                    ctx.FirmwareVersion);
            }
            else
            {
                UpgradeLogger.Log(
                    ctx.LogId,
                    ctx.MacAddress,
                    $"DALI 102 database count read failed: {dbCount102.Message}",
                    "Warn",
                    ctx.FirmwareVersion);
            }

            if (!scan102.Success)
            {
                ctx.Response.Success = false;
                ctx.Response.StatusCode = 500;
                ctx.Response.Message = $"DALI 102 total-new scan failed: {scan102.Message}";
                dev.LastFailureReason = ctx.Response.Message;
                dev.shouldRetry = false;
                dev.finalUpgradeResult = "Failed";
                return false;
            }
        }

        if (isDaliMaster && dev.RunDali103TotalNewScanAfterUpdate)
        {
            var scan103 = await svc.DaliRunTotalNewCommissioningScanAsync(ctx.MacAddress, 0x03).ConfigureAwait(false);
            dev.Dali103TotalNewScanSuccess = scan103.Success;
            UpgradeLogger.Log(
                ctx.LogId,
                ctx.MacAddress,
                $"DALI 103 total-new scan: {scan103.Message}",
                scan103.Success ? "Success" : "Failed",
                ctx.FirmwareVersion);

            if (scan103.DevicesFound.HasValue)
            {
                UpgradeLogger.Log(
                    ctx.LogId,
                    ctx.MacAddress,
                    $"DALI 103 total-new scan devices found: {scan103.DevicesFound.Value}",
                    "Info",
                    ctx.FirmwareVersion);
            }

            var dbCount103 = await svc.DaliGetDeviceDatabaseCount103Async(ctx.MacAddress).ConfigureAwait(false);
            if (dbCount103.Success && dbCount103.Count.HasValue)
            {
                UpgradeLogger.Log(
                    ctx.LogId,
                    ctx.MacAddress,
                    $"DALI 103 database total devices: {dbCount103.Count.Value}",
                    "Info",
                    ctx.FirmwareVersion);
            }
            else
            {
                UpgradeLogger.Log(
                    ctx.LogId,
                    ctx.MacAddress,
                    $"DALI 103 database count read failed: {dbCount103.Message}",
                    "Warn",
                    ctx.FirmwareVersion);
            }

            if (!scan103.Success)
            {
                ctx.Response.Success = false;
                ctx.Response.StatusCode = 500;
                ctx.Response.Message = $"DALI 103 total-new scan failed: {scan103.Message}";
                dev.LastFailureReason = ctx.Response.Message;
                dev.shouldRetry = false;
                dev.finalUpgradeResult = "Failed";
                return false;
            }
        }

        // Keep the connection open so the post-upgrade FW verification read can reuse it
        // without a reconnect round-trip. The outer scope (ProcessSingleDeviceUpgradeAsync)
        // owns the final disconnect after the FW read completes.
        ctx.KeepConnectionOpen = true;
        ctx.Dev.ConnectionLeftOpenForFwRead = true;
        return true;
    }
}
