using Newtonsoft.Json;

namespace AccessAPP.Models
{
    public class PairResponse
    {
        public int PairingStatusCode { get; set; }
        public string PairingStatus { get; set; }
        public string Message { get; set; }
    }
}
