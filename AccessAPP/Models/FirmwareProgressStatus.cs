namespace AccessAPP.Models
{
    public class FirmwareProgressStatus
    {
        public string MacAddress { get; set; }
        public double Progress { get; set; }
        public string Status { get; set; }  // Optional: e.g. "Connected", "Programming", "Finished", "Failed"
        public DateTime LastUpdated { get; set; }
    }
}
