namespace AccessAPP.Models
{
    public class BulkUpgradeRequest
    {
        public string MacAddress { get; set; }
        public string Pincode { get; set; }
        public bool bActor { get; set; } // true if upgrading actor firmware
        public string DetctorType { get; set; } // p48, p47 etc
        public string FirmwareVersion { get; set; } // v02.27 or v02.28 etc

        public string CurrentFirmwareVersion { get; set; } // v02.26 or v02.27 etc
    }

}
