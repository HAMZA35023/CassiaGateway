using AccessAPP.Models;

namespace AccessAPP.Services.UpgradePipeline
{
    internal static class UpgradeSummaryFactory
    {
        public static CassiaFirmwareUpgradeService.DeviceUpgradeSummary Create(
            string mac,
            UpgradeProgress dev,
            double seconds,
            string status,
            string? error = null)
        {
            return new CassiaFirmwareUpgradeService.DeviceUpgradeSummary
            {
                Mac = mac,
                DetectorType = dev.DetectotType ?? "",
                TargetFw = dev.FirmwareVersion ?? "",
                CurrentFw = dev.CurrentFirmwareVersion ?? "",

                ActorNeeded = dev.isActorUpgradeNeeded,
                BootloaderNeeded = dev.upgradeBootloader,

                ActorSuccess = dev.ActorSuccess,
                BootloaderSuccess = dev.BootloaderSuccess,
                SensorSuccess = dev.SensorSuccess,
                IsFullyUpgraded = dev.IsFullyUpgraded,
                ConfigRestored = dev.isConfigRestored,

                RetryTotal = dev.RetryCount,
                RetryActor = dev.RetryCountActor,
                RetryBootloader = dev.RetryCountBootloader,
                RetrySensor = dev.RetryCountSensor,

                Seconds = seconds,
                Status = status,
                Error = error
            };
        }
    }
}
