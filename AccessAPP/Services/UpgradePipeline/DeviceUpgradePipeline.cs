using AccessAPP.Services.UpgradePipeline.Steps;
using System.Threading.Tasks;

namespace AccessAPP.Services.UpgradePipeline;

internal sealed class DeviceUpgradePipeline
{
    private readonly IDeviceUpgradeStep[] _steps;

    public DeviceUpgradePipeline()
    {
        // Order is intentionally the same as the previous monolithic UpgradeDeviceAsync.
        _steps = new IDeviceUpgradeStep[]
        {
            new ProbeAndBootModeStep(),
            new SettingsBackupStep(),
            new ActorPreUpgradeStep(),
            new FirmwareUpgradeStep(),
            new SettingsRestoreStep(),
            new ActorUpgradeStep(),
            new PostActorStep(),
            new FinalizeSuccessStep(),
        };
    }

    public async Task ExecuteAsync(DeviceUpgradeContext ctx)
    {
        foreach (var step in _steps)
        {
            var ok = await step.ExecuteAsync(ctx).ConfigureAwait(false);
            if (!ok) return;
        }
    }
}
