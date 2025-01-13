namespace AccessAPP.Models
{
    public class BulkUpgradeRequest
    {
        public string MacAddress { get; set; }
        public string Pincode { get; set; }
        public bool bActor { get; set; } // true if upgrading actor firmware
    }

}
