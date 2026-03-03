using System.Diagnostics;
using AccessAPP.Models;

namespace AccessAPP.Services.UpgradePipeline;

internal sealed class DeviceUpgradeContext
{
    public CassiaFirmwareUpgradeService Svc { get; }
    public UpgradeProgress Dev { get; }

    public string MacAddress { get; }
    public string Pincode { get; }
    public string DetectorType { get; }
    public string FirmwareVersion { get; }
    public string LogId { get; }

    public bool UpgradeActor { get; }
    public bool UpgradeBootloader { get; }
    public bool UpgradeSensor { get; }

    public ServiceResponse Response { get; } = new();

    // Mutable state shared across steps
    public bool DisableUpdate { get; set; } = false;
    public string? SettingsBackupPath { get; set; }
    public int ChipId { get; set; }
    public bool IsInBoot { get; set; }
    public bool DetectorNameLogged { get; set; }
    public byte? OriginalDaliSysFailLevel { get; set; }
    public bool ActorUpdatedBeforeFirmware { get; set; }
    public bool ActorConnectionReuseConsumed { get; set; }
    public bool ReuseConnectionForPostActor { get; set; }
    public bool AnyFirmwareStepExecuted { get; set; }
    public bool ActorFirmwareStepExecuted { get; set; }

    /// <summary>
    /// Set by ProbeAndBootModeStep after it establishes a live BLE session,
    /// so subsequent steps (e.g. SettingsBackupStep) can reuse it instead of reconnecting.
    /// </summary>
    public bool PipelineSessionAlive { get; set; } = false;

    /// <summary>
    /// Set by PostActorStep to signal that the BLE connection should be kept open
    /// for the post-upgrade FW verification read instead of disconnecting here.
    /// The outer scope (ProcessSingleDeviceUpgradeAsync) is responsible for the final disconnect.
    /// </summary>
    public bool KeepConnectionOpen { get; set; } = false;

    public Stopwatch Stopwatch { get; } = new();

    public DeviceUpgradeContext(
        CassiaFirmwareUpgradeService svc,
        UpgradeProgress dev,
        string macAddress,
        string pincode,
        string detectorType,
        string firmwareVersion,
        bool upgradeActor,
        bool upgradeBootloader,
        bool upgradeSensor,
        string? logId)
    {
        Svc = svc;
        Dev = dev;
        MacAddress = macAddress;
        Pincode = pincode;
        DetectorType = detectorType;
        FirmwareVersion = firmwareVersion;
        UpgradeActor = upgradeActor;
        UpgradeBootloader = upgradeBootloader;
        UpgradeSensor = upgradeSensor;
        LogId = logId ?? "";

        SettingsBackupPath = dev.SettingsBackupPath;
    }
}
