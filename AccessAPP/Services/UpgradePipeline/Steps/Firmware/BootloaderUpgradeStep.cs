using System.Threading.Tasks;
using AccessAPP.Logging;
using AccessAPP.Services.HelperClasses;

namespace AccessAPP.Services.UpgradePipeline.Steps.Firmware;

internal sealed class BootloaderUpgradeStep : IDeviceUpgradeStep
{
    public async Task<bool> ExecuteAsync(DeviceUpgradeContext ctx)
    {
        if (!ctx.UpgradeBootloader || ctx.DisableUpdate)
            return true;

        var svc = ctx.Svc;
        var dev = ctx.Dev;

        dev.RetryCountBootloader++;
        ctx.AnyFirmwareStepExecuted = true;
        AppLog.Info($"Starting bootloader upgrade for {ctx.MacAddress}");

        // cooldown before bootloader step often helps after actor step
        await Task.Delay(5000).ConfigureAwait(false);

        ctx.Stopwatch.Restart();
        bool reuseExistingConnection =
            RuntimeVariables.UPGRADE_OPTIMIZE_RECONNECT_FLOW &&
            ctx.ActorUpdatedBeforeFirmware &&
            !ctx.ActorConnectionReuseConsumed;

        var bootloaderUpgradeResult = await svc.UpgradeSensorAsync(
            ctx.MacAddress,
            ctx.Pincode,
            false,
            true,
            ctx.DetectorType,
            ctx.FirmwareVersion,
            ctx.LogId,
            reuseExistingConnection: reuseExistingConnection).ConfigureAwait(false);

        if (reuseExistingConnection)
            ctx.ActorConnectionReuseConsumed = true;
        ctx.Stopwatch.Stop();

        AppLog.Info($"Bootloader upgrade completed for {ctx.MacAddress}. Time taken: {ctx.Stopwatch.Elapsed.TotalSeconds} seconds - result: {bootloaderUpgradeResult.Success}");

        if (!bootloaderUpgradeResult.Success)
        {
            ctx.Response.Success = false;
            ctx.Response.StatusCode = bootloaderUpgradeResult.StatusCode;
            ctx.Response.Message = $"bootloader upgrade failed: {bootloaderUpgradeResult.Message}";
            dev.BootloaderSuccess = false;
            return false;
        }

        dev.BootloaderSuccess = true;
        await Task.Delay(20000).ConfigureAwait(false);
        return true;
    }
}
