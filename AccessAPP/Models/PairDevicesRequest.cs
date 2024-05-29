namespace AccessAPP.Models
{
    public class PairDevicesRequest
    {
        public List<string> macAddresses { get; set; }
        public string IoCapability { get; set; }
        public int Oob { get; set; }
        public int Timeout { get; set; }
        public string Type { get; set; }
        public int Bond { get; set; }
    }
}
