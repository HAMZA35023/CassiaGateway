namespace AccessAPP.Models
{
    public class FirmwareUpgradeRequest
    {
        public string MacAddress { get; set; }
        public string Pincode { get; set; }
        public bool bActor { get; set; }
        public string DetectorType { get; set; }
        public string FirmwareVersion { get; set; }
    }
}
