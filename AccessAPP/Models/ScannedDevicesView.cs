namespace AccessAPP.Models
{
    public class ScannedDevicesView
    {
        public List<Bdaddre> bdaddrs { get; set; }
        public int chipId { get; set; }
        public int evtType { get; set; }
        public int rssi { get; set; }
        public string adData { get; set; }
        public string scanData { get; set; }

        public string name { get; set; }

        // ➕ New Metadata Fields
        public string ProductNumber { get; set; }
        public string DetectorFamily { get; set; }
        public string DetectorType { get; set; }
        public string DetectorOutputInfo { get; set; }
        public string DetectorDescription { get; set; }
        public string DetectorShortDescription { get; set; }
        public int Range { get; set; }
        public string DetectorMountDescription { get; set; } = string.Empty;
        public string LockedHex { get; set; }
        public bool? IsLocked { get; set; }
    }
}
