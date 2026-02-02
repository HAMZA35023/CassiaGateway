namespace AccessAPP.Services.UpgradePipeline;

internal static class UpgradePipelineSupport
{
    internal static bool SupportsSettingsBackup(string detectorType)
        => detectorType == "P48" || detectorType == "P47" || detectorType == "P46" || detectorType == "P49" || detectorType == "P41" || detectorType == "P42";

    internal static bool IsDaliMaster(string detectorType)
        => detectorType == "P48" || detectorType == "P47";
}
