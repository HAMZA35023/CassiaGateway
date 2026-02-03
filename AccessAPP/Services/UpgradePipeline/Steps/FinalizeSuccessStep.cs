using System.Threading.Tasks;
using AccessAPP.Services.HelperClasses;
using AccessAPP.Services;

namespace AccessAPP.Services.UpgradePipeline.Steps;

internal sealed class FinalizeSuccessStep : IDeviceUpgradeStep
{
    public Task<bool> ExecuteAsync(DeviceUpgradeContext ctx)
    {
        ctx.Response.Success = true;
        ctx.Response.StatusCode = 200;
        ctx.Response.Message = "Sensor and actor upgrades completed successfully.";

        if (ctx.Dev.finalUpgradeResult != "Warn")
            ctx.Dev.finalUpgradeResult = "Success";

        if (ctx.IsInBoot && !ctx.DetectorNameLogged && DeviceStorageService.TryGetDeviceName(ctx.MacAddress, out var name))
        {
            UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Device Name", name, ctx.FirmwareVersion, name);
            ctx.DetectorNameLogged = true;
        }

        UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Device Upgrade Task Done.", "Success", ctx.FirmwareVersion);
        return Task.FromResult(true);
    }
}
