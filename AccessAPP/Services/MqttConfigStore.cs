using System.Text.Json;

namespace AccessAPP.Services;

public sealed class MqttConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string FilePath { get; }

    public MqttConfigStore(string filePath)
    {
        FilePath = filePath;
    }

    public MqttOptions LoadOrCreateDefault()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var def = new MqttOptions
                {
                    Name = $"cassia-unknown{Random.Shared.Next(1000, 10000)}",
                    NetworkId = "dk-lab",
                    Host = "prod.statistics.niko-test.nu",
                    Port = 18883,
                    UseTls = false,
                    Username = "accessapp",
                    Password = "Niko1234",
                    BaseTopic = "accessapp",
                    KeepAliveSeconds = 30,
                    ReconnectDelaySeconds = 10,
                    SubscribeToAllTarget = true
                };
                Save(def);
                return def;
            }

            var json = File.ReadAllText(FilePath);
            var opts = JsonSerializer.Deserialize<MqttOptions>(json, JsonOptions);
            if (opts == null) throw new InvalidOperationException("MQTT config deserialized to null.");
            return opts;
        }
        catch
        {
            // If config is corrupt, fall back safely (but don’t delete user file).
            return new MqttOptions();
        }
    }

    public void Save(MqttOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");
        var json = JsonSerializer.Serialize(options, JsonOptions);
        File.WriteAllText(FilePath, json);
    }
}
