using System.Threading.Tasks;

namespace AccessAPP.Services.UpgradePipeline;

internal interface IDeviceUpgradeStep
{
    Task<bool> ExecuteAsync(DeviceUpgradeContext ctx);
}
