using Newtonsoft.Json;

namespace AccessAPP.Models
{
    public class CharacteristicModel
    {
        [JsonProperty("handle")]
        public int Handle { get; set; }

        [JsonProperty("properties")]
        public int Properties { get; set; }

        [JsonProperty("uuid")]
        public string Uuid { get; set; }
    }
}
