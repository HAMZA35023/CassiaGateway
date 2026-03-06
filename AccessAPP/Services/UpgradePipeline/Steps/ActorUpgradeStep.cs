using System.Threading.Tasks;
using AccessAPP.Logging;
using AccessAPP.Services.HelperClasses;

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
        ctx.AnyFirmwareStepExecuted = true;
        ctx.ActorFirmwareStepExecuted = true;

        ctx.Stopwatch.Restart();
        var actorUpgradeResult = await svc.UpgradeActorAsync(ctx.MacAddress, ctx.Pincode, true, ctx.DetectorType, ctx.FirmwareVersion, ctx.LogId)
            .ConfigureAwait(false);
        ctx.Stopwatch.Stop();

        AppLog.Info($"Actor upgrade completed for {ctx.MacAddress}. Time taken: {ctx.Stopwatch.Elapsed.TotalSeconds} seconds - result: {actorUpgradeResult.Success}");
        dev.ActorSuccess = actorUpgradeResult.Success;

        if (!actorUpgradeResult.Success)
        {
            if ((actorUpgradeResult.ProgrammingReturnCode ?? 0) == (int)ReturnCodes.CYRET_ERR_COMM_LENGTH)
                dev.ActorCommErrCount++;
            ctx.Response.Success = false;
            ctx.Response.StatusCode = actorUpgradeResult.StatusCode;
            ctx.Response.Message = $"Actor upgrade failed again after sensor application completed: {actorUpgradeResult.Message}";
            return false;
        }

        await Task.Delay(1000).ConfigureAwait(false);
        return true;
    }
}
