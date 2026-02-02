using System.Threading.Tasks;
using AccessAPP.Services.UpgradePipeline.Steps.Firmware;

namespace AccessAPP.Services.UpgradePipeline.Steps;

internal sealed class FirmwareUpgradeStep : IDeviceUpgradeStep
{
    private readonly IDeviceUpgradeStep[] _sub;

    public FirmwareUpgradeStep()
    {
        _sub = new IDeviceUpgradeStep[]
        {
            new BootloaderUpgradeStep(),
            new SensorUpgradeStep(),
        };
    }

    public async Task<bool> ExecuteAsync(DeviceUpgradeContext ctx)
    {
        foreach (var step in _sub)
        {
            var ok = await step.ExecuteAsync(ctx).ConfigureAwait(false);
            if (!ok) return false;
        }

        return true;
    }
}
