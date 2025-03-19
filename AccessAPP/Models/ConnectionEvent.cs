using Newtonsoft.Json;

namespace AccessAPP.Models
{
    public class ConnectionEvent
    {
        [JsonProperty("handle")]
        public string Handle { get; set; }

        [JsonProperty("chipId")]
        public int ChipId { get; set; }

        [JsonProperty("connectionState")]
        public string ConnectionState { get; set; }
    }
}
