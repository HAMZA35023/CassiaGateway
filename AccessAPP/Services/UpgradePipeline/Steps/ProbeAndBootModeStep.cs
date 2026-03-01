using System;
using System.Net;
using System.Threading.Tasks;
using AccessAPP.Logging;
using AccessAPP.Services.HelperClasses;
using AccessAPP.Services;

namespace AccessAPP.Services.UpgradePipeline.Steps;

internal sealed class ProbeAndBootModeStep : IDeviceUpgradeStep
{
    public async Task<bool> ExecuteAsync(DeviceUpgradeContext ctx)
    {
        var svc = ctx.Svc;
        var dev = ctx.Dev;

        UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Process Start Device Async", "Success", ctx.FirmwareVersion);

        bool noFwActionsRequired =
            !ctx.UpgradeActor &&
            !ctx.UpgradeBootloader &&
            !ctx.UpgradeSensor &&
            !dev.requiresConfigRestore &&
            !dev.requires102Restore;

        if (noFwActionsRequired)
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Connected (probe)", "Skipped (no FW actions required in this attempt)", ctx.FirmwareVersion);
            if (!ctx.DetectorNameLogged && DeviceStorageService.TryGetDeviceName(ctx.MacAddress, out var detectorName))
            {
                UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Device Name", detectorName, ctx.FirmwareVersion, detectorName);
                ctx.DetectorNameLogged = true;
            }

            return true;
        }

        // 0) Determine boot/application mode early (best-effort + robust connect)
        AppLog.Info($"Getting current FW Verison if possible {ctx.MacAddress}");

        bool reusedPrecheckSession =
            RuntimeVariables.UPGRADE_OPTIMIZE_RECONNECT_FLOW &&
            dev.PrecheckSessionAlive &&
            dev.RetryCount == 0;
        if (!reusedPrecheckSession)
        {
            var connProbe = await svc.ConnectOnlyWithRetryAsync_Internal(
                maxAttempts: Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS),
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
        }
        else
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Connected (probe)", "Skipped (reusing live precheck session)", ctx.FirmwareVersion);
        }

        ctx.ChipId = svc.GetChipForMac(ctx.MacAddress);
        bool linuxNativeBackend = svc.ConnectService is AccessAPP.Services.LinuxBle.LinuxBleConnectionService;
        string chipOrAdapter = linuxNativeBackend
            ? AccessAPP.Services.LinuxBle.BlueZHelpers.GetDeviceAdapter(ctx.MacAddress)
            : $"Chip {ctx.ChipId}";
        UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"Using {chipOrAdapter}", "info");
        AppLog.Info($"Using {chipOrAdapter} for {ctx.MacAddress}");

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

            var loggedIn = await svc.EnsureLoginOnConnectedSessionUnlessBootModeAsync(
                ctx.MacAddress,
                ctx.Pincode,
                ctx.LogId,
                ctx.FirmwareVersion,
                stageName: "LoggedIn (probe)",
                maxAttempts: 3).ConfigureAwait(false);

            if (!loggedIn)
            {
                if (reusedPrecheckSession)
                {
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "LoggedIn (probe)", "Failed on reused session; reconnecting", ctx.FirmwareVersion);
                    AppLog.Warn($"Login failed on reused precheck session for {ctx.MacAddress}. Reconnecting...");
                }
                else
                {
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "LoggedIn (probe)", "Failed; reconnecting", ctx.FirmwareVersion);
                    AppLog.Warn($"Login failed for {ctx.MacAddress}. Reconnecting...");
                }

                var connProbe = await svc.ConnectOnlyWithRetryAsync_Internal(
                    maxAttempts: Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS),
                    delayMs: 2000,
                    stageName: "Connected (probe retry)",
                    logSuccess: true,
                    macAddress: ctx.MacAddress,
                    firmwareVersion: ctx.FirmwareVersion,
                    logId: ctx.LogId
                ).ConfigureAwait(false);

                if (!connProbe.ok)
                {
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Connected (probe retry)", "Failed", ctx.FirmwareVersion);
                    ctx.Response.Success = false;
                    ctx.Response.StatusCode = (int)(connProbe.code == 0 ? HttpStatusCode.ServiceUnavailable : connProbe.code);
                    ctx.Response.Message = "Failed to connect to device.";
                    dev.LastFailureReason = ctx.Response.Message;
                    dev.RetryCount++;
                    dev.shouldRetry = false;
                    return false;
                }

                loggedIn = await svc.EnsureLoginOnConnectedSessionUnlessBootModeAsync(
                    ctx.MacAddress,
                    ctx.Pincode,
                    ctx.LogId,
                    ctx.FirmwareVersion,
                    stageName: "LoggedIn (probe retry)",
                    maxAttempts: 3).ConfigureAwait(false);

                if (!loggedIn)
                {
                    // Final fallback: do a full Connect+Login retry sequence before failing the probe.
                    UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "LoggedIn (probe)", "Fallback to Connect+Login retry", ctx.FirmwareVersion);
                    AppLog.Warn($"Login failed after probe reconnect for {ctx.MacAddress}. Trying full Connect+Login fallback.");

                    var cl = await svc.ConnectAndLoginWithRetryForPipelineAsync(
                        svc.GatewayIpAddress,
                        svc.GatewayPort,
                        ctx.MacAddress,
                        ctx.Pincode,
                        ctx.LogId,
                        ctx.FirmwareVersion,
                        maxAttempts: Math.Min(3, Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS)),
                        delayBetweenAttemptsMs: 4000).ConfigureAwait(false);

                    if (cl.Success)
                    {
                        loggedIn = true;
                    }
                    else
                    {
                        bool bootDetected = false;
                        try
                        {
                            bootDetected = svc.CheckIfDeviceInBootMode(svc.GatewayIpAddress, ctx.MacAddress);
                        }
                        catch (Exception ex)
                        {
                            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, $"BootMode check exception after login fallback: {ex.Message}", "Warn", ctx.FirmwareVersion);
                        }

                        if (bootDetected || cl.StatusCode == (int)HttpStatusCode.Conflict)
                        {
                            ctx.IsInBoot = true;
                            loggedIn = true;
                            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "LoggedIn (probe)", "Skipped (bootloader mode after login fallback)", ctx.FirmwareVersion);
                        }
                    }
                }

                if (!loggedIn)
                {
                    ctx.Response.Success = false;
                    ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    ctx.Response.Message = "Failed to login to device after connect.";
                    dev.LastFailureReason = ctx.Response.Message;
                    dev.RetryCount++;
                    dev.shouldRetry = false;
                    return false;
                }
            }
        }

        if (!ctx.IsInBoot && !ctx.DetectorNameLogged && DeviceStorageService.TryGetDeviceName(ctx.MacAddress, out var name))
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Device Name", name, ctx.FirmwareVersion, name);
            ctx.DetectorNameLogged = true;
        }

        // Signal to downstream steps that a live BLE session is available for reuse.
        ctx.PipelineSessionAlive = true;
        return true;
    }
}
