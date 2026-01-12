using System;
using System.IO;
using System.Text.Json;

namespace AccessAppMqttWpf.Services;

public sealed class SettingsStore
{
    private readonly string _path;
    public SettingsStore(string? path = null) => _path = path ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}

public sealed class AppSettings
{
    public MqttSettings mqtt { get; set; } = new();
    public AccessAppSettings accessapp { get; set; } = new();
}

public sealed class MqttSettings
{
    public string host { get; set; } = "prod.statistics.niko-test.nu";
    public int port { get; set; } = 18883;
    public string topic { get; set; } = "accessapp/#";
    public string username { get; set; } = "accessapp";
    public string password { get; set; } = "Niko1234!";
    public bool useTls { get; set; } = false;
    public bool ignoreTlsErrors { get; set; } = true;
}

public sealed class AccessAppSettings
{
    public string networkId { get; set; } = "dk-lab";
    public string commandTopicTemplate { get; set; } = "accessapp/{networkId}/cmd/{cassia}/{command}";
    public string defaultCommand { get; set; } = "start-update";
}
