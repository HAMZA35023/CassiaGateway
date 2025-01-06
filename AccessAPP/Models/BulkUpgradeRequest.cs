namespace AccessAPP.Models
{
    public class BulkUpgradeRequest
    {
        public List<string> MacAddresses { get; set; }
        public string Pincode { get; set; }
        public bool bActor { get; set; } // true if upgrading actor firmware
    }

}
