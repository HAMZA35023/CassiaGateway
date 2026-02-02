using System;

namespace AccessAPP.Services
{
    public partial class CassiaFirmwareUpgradeService
    {
        // Extracted from CassiaFirmwareUpgradeService.cs to keep the main file smaller.
        internal sealed class DeviceUpgradeSummary
        {
            public string Mac { get; init; } = "";
            public string DetectorType { get; init; } = "";
            public string TargetFw { get; init; } = "";
            public string CurrentFw { get; init; } = "";

            public bool ActorNeeded { get; init; }
            public bool BootloaderNeeded { get; init; }

            public bool ActorSuccess { get; init; }
            public bool BootloaderSuccess { get; init; }
            public bool SensorSuccess { get; init; }
            public bool IsFullyUpgraded { get; init; }
            public bool ConfigRestored { get; init; }

            public int RetryTotal { get; init; }
            public int RetryActor { get; init; }
            public int RetryBootloader { get; init; }
            public int RetrySensor { get; init; }

            public double Seconds { get; init; }
            public string Status { get; init; } = "OK"; // OK / SKIPPED / ERROR
            public string? Error { get; init; }
        }
    }
}
