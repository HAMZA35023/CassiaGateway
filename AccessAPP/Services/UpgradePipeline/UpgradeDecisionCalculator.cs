using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;

namespace AccessAPP.Services.UpgradePipeline
{
    internal readonly record struct UpgradeDecisions(
        bool IsDaliMaster,
        bool UpgradeBootloader,
        bool UpgradeSensor,
        bool ActorUpgradeNeeded,
        bool RequiresConfigRestore,
        bool Requires102Restore
    );

    internal static class UpgradeDecisionCalculator
    {
        public static UpgradeDecisions Compute(UpgradeProgress dev)
        {
            var upgradeBootloader = FirmwareResolver.ShouldUpgradeBootloader(
                dev.DetectotType,
                dev.FirmwareVersion,
                dev.CurrentFirmwareVersion
            );

            // Skip sensor upgrade when current App FW matches target, unless forced.
            var upgradeSensor = dev.ForceUpdate || !FirmwareResolver.IsSameAppVersion(dev.CurrentFirmwareVersion, dev.FirmwareVersion);

            var isDaliMaster = dev.DetectotType == "P48" || dev.DetectotType == "P47";

            // Skip actor upgrade when current Actor App FW matches target, unless forced.
            var actorNeeded = isDaliMaster && (dev.ForceUpdate || !FirmwareResolver.IsSameActorAppVersion(dev.CurrentFirmwareVersion, dev.FirmwareVersion));

            var requiresConfigRestore = RuntimeVariables.RestoreSettingsAfterUpgrade &&
                                       (dev.DetectotType == "P48" || dev.DetectotType == "P47" || dev.DetectotType == "P46" ||
                                        dev.DetectotType == "P49" || dev.DetectotType == "P41" || dev.DetectotType == "P42");

            var requires102Restore = RuntimeVariables.Restore102DBAfterUpgrade &&
                                     (dev.DetectotType == "P48" || dev.DetectotType == "P47");

            return new UpgradeDecisions(
                IsDaliMaster: isDaliMaster,
                UpgradeBootloader: upgradeBootloader,
                UpgradeSensor: upgradeSensor,
                ActorUpgradeNeeded: actorNeeded,
                RequiresConfigRestore: requiresConfigRestore,
                Requires102Restore: requires102Restore
            );
        }
    }
}
