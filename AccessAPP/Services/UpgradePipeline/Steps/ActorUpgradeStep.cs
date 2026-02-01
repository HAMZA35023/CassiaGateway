using System.Threading.Tasks;
using AccessAPP.Logging;

namespace AccessAPP.Services.UpgradePipeline.Steps;

internal sealed class ActorUpgradeStep : IDeviceUpgradeStep
{
    public async Task<bool> ExecuteAsync(DeviceUpgradeContext ctx)
    {
        var svc = ctx.Svc;
        var dev = ctx.Dev;

        if (dev.ActorSuccess == true || !dev.isActorUpgradeNeeded)
            return true;

        if (!(ctx.UpgradeActor && !ctx.DisableUpdate))
            return true;

        AppLog.Info($"Starting actor upgrade for {ctx.MacAddress}");
        dev.RetryCountActor++;

        ctx.Stopwatch.Restart();
        var actorUpgradeResult = await svc.UpgradeActorAsync(ctx.MacAddress, ctx.Pincode, true, ctx.DetectorType, ctx.FirmwareVersion, ctx.LogId)
            .ConfigureAwait(false);
        ctx.Stopwatch.Stop();

        AppLog.Warn($"Retry Actor upgrade after sensor application completed for {ctx.MacAddress}. Time taken: {ctx.Stopwatch.Elapsed.TotalSeconds} seconds - result: {actorUpgradeResult.Success}");
        dev.ActorSuccess = actorUpgradeResult.Success;

        if (!actorUpgradeResult.Success)
        {
            ctx.Response.Success = false;
            ctx.Response.StatusCode = actorUpgradeResult.StatusCode;
            ctx.Response.Message = $"Actor upgrade failed again after sensor application completed: {actorUpgradeResult.Message}";
            return false;
        }

        await Task.Delay(1000).ConfigureAwait(false);
        return true;
    }
}
