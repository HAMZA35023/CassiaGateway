namespace AccessAPP.Models
{
    public class UpgradeProgress
    {
        public string MacAddress { get; set; }
        public string Pincode { get; set; }

        public string DetectotType { get; set; }
        public string FirmwareVersion { get; set; }
        public string CurrentFirmwareVersion { get; set; }
        public string? SettingsBackupPath { get; set; }

        // If true, forces re-programming even when current FW matches target.
        public bool ForceUpdate { get; set; } = false;
        public bool BootloaderSuccess { get; set; } = false;
        public bool SensorSuccess { get; set; } = false;
        public bool ActorSuccess { get; set; } = false;
        public int RetryCount { get; set; } = 0;
        public int RetryCountActor { get; set; } = 0;
        public int RetryCountBootloader { get; set; } = 0;
        public int RetryCountSensor { get; set; } = 0;
        public string LastFailureReason { get; set; } = string.Empty;
        public bool isActorUpgradeNeeded = true;

        public bool upgradeBootloader = true;
        public bool upgradeSensor = true;
        public bool isConfigRestored = false;
        public bool requiresConfigRestore = false;
        public bool requires102Restore = false;
        public bool restore102Success = false;
        public bool shouldRetry = true;
        public bool PrecheckSessionAlive { get; set; } = false;
        public bool PrecheckBootMode { get; set; } = false;

        public string finalUpgradeResult = "Failed";

        public bool IsFullyUpgraded
            => (!isActorUpgradeNeeded || ActorSuccess)
               && (!upgradeBootloader || BootloaderSuccess)
               && (SensorSuccess)
               && (!requiresConfigRestore || isConfigRestored)
               && (!requires102Restore || restore102Success);
    }
    public class UpgradeResponse
    {
        public string MacAddress { get; set; }
        public bool Success { get; set; }
        public int StatusCode { get; set; }
        public string Message { get; set; }
        public int RetryCount { get; set; } // How many retries were used (0 = first attempt)
    }

}
