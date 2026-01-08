using System.Text.Json.Serialization;

namespace AccessAPP.Services;

public sealed class MqttOptions
{
    // Identity (who am I, and which logical network do I belong to)
    public string Name { get; set; } = "cassia-unknown";
    public string NetworkId { get; set; } = "default";

    // Broker
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1883;
    public bool UseTls { get; set; } = false;

    public string? Username { get; set; }
    public string? Password { get; set; }

    // Topic base
    public string BaseTopic { get; set; } = "accessapp";

    // Operational
    public int KeepAliveSeconds { get; set; } = 30;
    public int ReconnectDelaySeconds { get; set; } = 5;

    // If true, will also subscribe to ".../cmd/all/#"
    public bool SubscribeToAllTarget { get; set; } = true;

    [JsonIgnore]
    public string ClientId => $"{Name}-{NetworkId}-{Environment.MachineName}";
}
