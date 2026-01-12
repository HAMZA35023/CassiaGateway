using Newtonsoft.Json;

namespace AccessAPP.Models
{
    public class MqttConfig
    {
        [JsonProperty("name")]
        public string Name { get; set; } = "cassia-01";

        [JsonProperty("networkId")]
        public string NetworkId { get; set; } = "dk-lab";

        [JsonProperty("host")]
        public string Host { get; set; } = "prod.statistics.niko-test.nu";

        [JsonProperty("port")]
        public int Port { get; set; } = 18883;

        [JsonProperty("useTls")]
        public bool UseTls { get; set; } = false;

        [JsonProperty("username")]
        public string Username { get; set; } = "accessapp";

        [JsonProperty("password")]
        public string Password { get; set; } = "Niko1234!";

        [JsonProperty("baseTopic")]
        public string BaseTopic { get; set; } = "accessapp";

        [JsonProperty("keepAliveSeconds")]
        public int KeepAliveSeconds { get; set; } = 30;

        [JsonProperty("reconnectDelaySeconds")]
        public int ReconnectDelaySeconds { get; set; } = 10;

        [JsonProperty("subscribeToAllTarget")]
        public bool SubscribeToAllTarget { get; set; } = true;
    }
}
