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

        if (!UpgradePipelineSupport.SupportsSettingsBackup(ctx.DetectorType))
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"Post-actor steps skipped (unsupported detector '{ctx.DetectorType}')", "Info", ctx.FirmwareVersion);
            return true;
        }

        var isDaliMaster = UpgradePipelineSupport.IsDaliMaster(ctx.DetectorType);

        if (isDaliMaster)
        {
            await Task.Delay(10000).ConfigureAwait(false);

            AppLog.Info($"Post-actor: connect+login for {ctx.MacAddress}");
            var cl = await svc.ConnectAndLoginWithRetryForPipelineAsync(
                svc.GatewayIpAddress, 80, ctx.MacAddress, ctx.Pincode, ctx.LogId, ctx.FirmwareVersion,
                maxAttempts: Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS),
                delayBetweenAttemptsMs: 6000).ConfigureAwait(false);

            if (!cl.Success)
            {
                UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"Post-actor connect+login failed: {cl.Message}", "Warn", ctx.FirmwareVersion);
                ctx.Response.Success = false;
                ctx.Response.StatusCode = 500;
                ctx.Response.Message = "Could not connect and login to detector!";
                return false;
            }

            if (RuntimeVariables.AutoSetSysFailLevelUnderUpdate)
            {
                var restoreValue = ctx.OriginalDaliSysFailLevel ?? (byte)0xFE;
                var restoreHex = $"0x{restoreValue:X2}";

                if (await svc.DaliSetDeviceSysFailLevelAsync(ctx.MacAddress, restoreValue).ConfigureAwait(false))
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"DALI SysFail Level restored to {restoreHex}", "Success", ctx.FirmwareVersion);
                else
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"DALI SysFail Level restore failed ({restoreHex})", "Warn", ctx.FirmwareVersion);
            }
        }

        if (RuntimeVariables.RebootDetectorAfterUpgrade && !ctx.ActorUpdatedBeforeFirmware)
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
        else if (RuntimeVariables.RebootDetectorAfterUpgrade && ctx.ActorUpdatedBeforeFirmware)
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Reboot skipped (actor updated before bootloader/sensor)", "Info", ctx.FirmwareVersion);
            AppLog.Info($"Skipping post-actor reboot for {ctx.MacAddress} because actor was already updated pre-firmware.");
        }

        if (isDaliMaster && RuntimeVariables.Restore102DBAfterUpgrade)
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

        await svc.ConnectService.DisconnectFromBleDevice(svc.GatewayIpAddress, ctx.MacAddress, 0, chip: ctx.ChipId).ConfigureAwait(false);
        return true;
    }
}
