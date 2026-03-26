using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AccessAppMqttWpf.Services;

public sealed class SettingsStore
{
    private readonly string _path;
    public SettingsStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessAppMqttWpf",
        "appsettings.json");

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
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}

public sealed class AppSettings
{
    public MqttSettings mqtt { get; set; } = new();
    public AccessAppSettings accessapp { get; set; } = new();
    public LocalServerSettings localServer { get; set; } = new();
}

public sealed class MqttSettings
{
    public string host { get; set; } = "acd270e774e848e8a55de829dc58bc6c.s1.eu.hivemq.cloud";
    public int port { get; set; } = 8883;
    public string topic { get; set; } = "accessapp/#";
    public string username { get; set; } = "accessapp";
    public string password { get; set; } = "Niko1234!";
    public bool useTls { get; set; } = true;
    public bool ignoreTlsErrors { get; set; } = false;
}

public sealed class LocalServerSettings
{
    public bool enabled { get; set; } = false;
    public int mqttPort { get; set; } = 1883;
    public string accessAppChannel { get; set; } = "stable";
    public string manifestBaseUrl { get; set; } = "https://prod.statistics.niko-test.nu/accessapp";
    public string accessAppRuntime { get; set; } = "windows-x64";

    /// <summary>
    /// If non-empty, use this directory instead of downloading from the manifest.
    /// </summary>
    public string localAccessAppPath { get; set; } = "";

    public bool autoStartAccessApp { get; set; } = false;
    public bool autoStartLocalServer { get; set; } = false;

    /// <summary>
    /// When true, WPF pushes its own NetworkId to all discovered AccessApp instances so
    /// they all subscribe and publish under the same network ID on the local broker.
    /// Only affects the local MQTT connection — each AccessApp's own configured ID is unchanged.
    /// </summary>
    public bool useSharedNetworkId { get; set; } = true;

    /// <summary>
    /// When true, WPF probes its own outbound IP and sends it as <c>mqttHost</c> in the
    /// config-push payload so AccessApp knows which address to connect to.
    /// When false, AccessApp derives the MQTT host from the TCP connection's remote IP —
    /// simpler, but only correct when there is no NAT between WPF and the AccessApp container.
    /// </summary>
    public bool sendMqttHost { get; set; } = true;
}

public sealed class AccessAppSettings
{
    public string networkId { get; set; } = "dk-lab";
    public string commandTopicTemplate { get; set; } = "accessapp/{networkId}/cmd/{cassia}/{command}";
    public string defaultCommand { get; set; } = "start-update";
    public string theme { get; set; } = "Light";

    // UI option: reflash sensor firmware even if current FW already matches target
    public bool forceUpdate { get; set; } = false;

    // If true, auto-set parallel programmers based on queued model mix:
    // DALI master only (P47/P48) => 4, otherwise => 2.
    public bool autoSetWorkersByModel { get; set; } = false;

    // If true, force specific runtime variables to false before every start-update.
    public bool productionUpdate { get; set; } = false;

    // Host BLE tab: if true, remove rows that are no longer present in latest scan snapshot.
    public bool hostBleAutoRemoveStaleDevices { get; set; } = false;

    /// <summary>
    /// Remembers the selected firmware per detector model across app restarts/resync.
    /// Keys are typically "P41", "P42", "P46", "P47", "P48".
    /// </summary>
    public Dictionary<string, string> selectedFirmwareByModel { get; set; } = new();

    /// <summary>
    /// Optional detector settings profile file path per detector model.
    /// When configured, the profile patch is included as DetectorSettings in start-update payload.
    /// Keys are typically "P41", "P42", "P46", "P47", "P48".
    /// </summary>
    public Dictionary<string, string> detectorSettingsProfileByModel { get; set; } = new();
}
