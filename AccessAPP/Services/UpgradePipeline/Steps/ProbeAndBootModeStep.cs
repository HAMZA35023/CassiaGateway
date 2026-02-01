using System;
using System.Net;
using System.Threading.Tasks;
using AccessAPP.Logging;
using AccessAPP.Services.HelperClasses;

namespace AccessAPP.Services.UpgradePipeline.Steps;

internal sealed class ProbeAndBootModeStep : IDeviceUpgradeStep
{
    public async Task<bool> ExecuteAsync(DeviceUpgradeContext ctx)
    {
        var svc = ctx.Svc;
        var dev = ctx.Dev;

        UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Process Start Device Async", "Success", ctx.FirmwareVersion);

        // 0) Determine boot/application mode early (best-effort + robust connect)
        AppLog.Info($"Getting current FW Verison if possible {ctx.MacAddress}");

        var connProbe = await svc.ConnectOnlyWithRetryAsync_Internal(
            maxAttempts: 5,
            delayMs: 2000,
            stageName: "Connected (probe)",
            logSuccess: false,
            macAddress: ctx.MacAddress,
            firmwareVersion: ctx.FirmwareVersion,
            logId: ctx.LogId
        ).ConfigureAwait(false);

        if (!connProbe.ok)
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Connected", "Failed", ctx.FirmwareVersion);
            ctx.Response.Success = false;
            ctx.Response.StatusCode = (int)(connProbe.code == 0 ? HttpStatusCode.ServiceUnavailable : connProbe.code);
            ctx.Response.Message = "Failed to connect to device.";
            dev.LastFailureReason = ctx.Response.Message;
            dev.RetryCount++;
            dev.shouldRetry = false;
            return false;
        }

        ctx.ChipId = svc.GetChipForMac(ctx.MacAddress);
        UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"Using Chip {ctx.ChipId}", "info");
        AppLog.Info($"Using ChipID {ctx.ChipId} for {ctx.MacAddress}");

        // NOTE: if CheckIfDeviceInBootMode relies on Cassia state, this is now safer.
        ctx.IsInBoot = false;
        try
        {
            ctx.IsInBoot = svc.CheckIfDeviceInBootMode(svc.GatewayIpAddress, ctx.MacAddress);
        }
        catch (Exception ex)
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"BootMode check exception: {ex.Message}", "Warn", ctx.FirmwareVersion);
        }

        if (ctx.IsInBoot)
        {
            AppLog.Info($"Device is in boot mode, skipping FW version check: {ctx.MacAddress}");
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Device in boot mode, skipping FW version check", "Info", ctx.FirmwareVersion);
        }
        else
        {
            AppLog.Info($"Device is in application mode, checking FW version: {ctx.MacAddress}");
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Device in application mode, checking FW version", "Info", ctx.FirmwareVersion);
        }

        return true;
    }
}
