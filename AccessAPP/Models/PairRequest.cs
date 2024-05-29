using Newtonsoft.Json;

namespace AccessAPP.Models
{
    public class PairRequest
    {
        [JsonProperty("iocapability")]
        public string IoCapability { get; set; }

        [JsonProperty("oob")]
        public int Oob { get; set; }

        [JsonProperty("timeout")]
        public int Timeout { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("bond")]
        public int Bond { get; set; }
    }
}
