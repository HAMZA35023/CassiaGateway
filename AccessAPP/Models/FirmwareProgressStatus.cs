namespace AccessAPP.Models
{
    public class FirmwareProgressStatus
    {
        public string MacAddress { get; set; }
        public double Progress { get; set; }
        public string Status { get; set; }  // e.g. "Queued", "Connected", "Programming", "Failed"
        public double? SpeedPctPerMin { get; set; }
        public DateTime LastUpdated { get; set; }
        public string? TargetFirmwareVersion { get; set; }
        public string? DetectorType { get; set; }
        /// <summary>Set when status is "Device Upgrade Completed." — values: "Success", "Failed", "Warn"</summary>
        public string? FinalResult { get; set; }
    }
}
