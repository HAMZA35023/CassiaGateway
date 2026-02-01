using System.Threading.Tasks;
using AccessAPP.Logging;

namespace AccessAPP.Services.UpgradePipeline.Steps.Firmware;

internal sealed class SensorUpgradeStep : IDeviceUpgradeStep
{
    public async Task<bool> ExecuteAsync(DeviceUpgradeContext ctx)
    {
        if (!ctx.UpgradeSensor || ctx.DisableUpdate)
        {
            // Consider sensor step satisfied when skipped (e.g., already at target FW).
            ctx.Dev.SensorSuccess = true;
            return true;
        }

        var svc = ctx.Svc;
        var dev = ctx.Dev;

        AppLog.Info($"Starting Sensor upgrade for {ctx.MacAddress}");
        dev.RetryCountSensor++;

        // IMPORTANT: after JumpToBootloader, Cassia often needs a longer cool-down before next Connect+Login
        await Task.Delay(8000).ConfigureAwait(false);

        ctx.Stopwatch.Restart();
        var sensorUpgradeResult = await svc.UpgradeSensorAsync(
            ctx.MacAddress,
            ctx.Pincode,
            false,
            false,
            ctx.DetectorType,
            ctx.FirmwareVersion,
            ctx.LogId).ConfigureAwait(false);
        ctx.Stopwatch.Stop();

        AppLog.Info($"Sensor upgrade completed for {ctx.MacAddress}. Time taken: {ctx.Stopwatch.Elapsed.TotalSeconds} seconds - result: {sensorUpgradeResult.Success}");

        if (!sensorUpgradeResult.Success)
        {
            ctx.Response.Success = false;
            ctx.Response.StatusCode = sensorUpgradeResult.StatusCode;
            ctx.Response.Message = $"Sensor upgrade failed: {sensorUpgradeResult.Message}";
            dev.SensorSuccess = false;
            return false;
        }

        dev.SensorSuccess = true;
        await Task.Delay(20000).ConfigureAwait(false);
        return true;
    }
}
