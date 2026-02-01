using AccessAPP.Models;

namespace AccessAPP.Services.UpgradePipeline
{
    internal static class UpgradeRetryPolicy
    {
        public static bool CanRetryNow(UpgradeProgress dev, int maxRetriesPerComponent)
        {
            // IMPORTANT: RetryCountActor/Sensor/Bootloader are incremented inside UpgradeDeviceAsync
            // before each attempt. We only check here.
            bool actorOk =
                !dev.isActorUpgradeNeeded || dev.ActorSuccess || dev.RetryCountActor < 2 * maxRetriesPerComponent;

            bool bootOk =
                !dev.upgradeBootloader || dev.BootloaderSuccess || dev.RetryCountBootloader < maxRetriesPerComponent;

            bool sensorOk =
                dev.SensorSuccess || dev.RetryCountSensor < maxRetriesPerComponent;

            return actorOk && bootOk && sensorOk && dev.shouldRetry && dev.RetryCount <= 10;
        }
    }
}
