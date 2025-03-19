namespace AccessAPP.Models
{
    public class BatchLightControlRequest
    {
        public List<string> MacAddresses { get; set; }
        public string HexLoginValue { get; set; }
    }
}
