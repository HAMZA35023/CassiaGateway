namespace AccessAPP.Models
{
    public class ScannedDevicesView
    {
        public List<Bdaddre> bdaddrs { get; set; }
        public int chipId { get; set; }
        public int evtType { get; set; }
        public int rssi { get; set; }
        public string adData { get; set; }
        public string name { get; set; }
    }
}
