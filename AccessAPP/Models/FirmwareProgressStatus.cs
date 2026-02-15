namespace AccessAPP.Models
{
    public class FirmwareProgressStatus
    {
        public string MacAddress { get; set; }
        public double Progress { get; set; }
        public string Status { get; set; }  // Optional: e.g. "Connected", "Programming", "Finished", "Failed"
        // Percent points per minute (10s sliding window); null when not applicable.
        public double? SpeedPctPerMin { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
