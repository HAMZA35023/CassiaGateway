namespace AccessAppMqttWpf.Models;

public sealed class RuntimeSettings
{
    public int WriteSleepMs { get; set; } = 0;
    public bool UseBothCassiaChips { get; set; } = true;
    public int DefaultCassiaChip { get; set; } = 1;
    public int CassiaMaxInflightPerChip { get; set; } = 1;
    public bool BleScanUnderProgramming { get; set; } = true;
    public bool RebootDetectorAfterUpgrade { get; set; } = true;
    public bool Restore102DbAfterUpgrade { get; set; } = true;
    public bool RestoreSettingsAfterUpgrade { get; set; } = true;
    public bool AutoSetSysFailLevelUnderUpdate { get; set; } = true;
}
