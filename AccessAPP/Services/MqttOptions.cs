using System.Text.Json.Serialization;

namespace AccessAPP.Services;

public sealed class MqttOptions
{
    // Identity (who am I, and which logical network do I belong to)
    public string Name { get; set; } = "cassia-unknown";
    public string NetworkId { get; set; } = "dk-lab";

    // Broker
    public string Host { get; set; } = "prod.statistics.niko-test.nu";
    public int Port { get; set; } = 18883;
    public bool UseTls { get; set; } = false;

    public string? Username { get; set; } = "accessapp";
    public string? Password { get; set; } = "Niko1234!";

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
