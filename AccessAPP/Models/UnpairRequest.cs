using Newtonsoft.Json;

namespace AccessAPP.Models
{
    public class UnpairDevicesRequest
    {
        public List<string> MacAddresses { get; set; }
    }
}
