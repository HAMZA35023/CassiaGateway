using System.Threading.Tasks;
using AccessAPP.Services.HelperClasses;

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

        UpgradeLogger.Log(ctx.LogId, ctx.MacAddress, "Device Upgrade Task Done.", "Success", ctx.FirmwareVersion);
        return Task.FromResult(true);
    }
}
