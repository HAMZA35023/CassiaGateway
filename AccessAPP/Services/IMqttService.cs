using AccessAPP.Models;

namespace AccessAPP.Services;

public interface IMqttService : IAsyncDisposable
{
    MqttOptions CurrentOptions { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    // Allow other classes to configure identity/network at runtime
    Task UpdateIdentityAsync(string name, string networkId, bool persist = true, CancellationToken ct = default);

    // Optional: broker config at runtime
    Task UpdateBrokerAsync(string host, int port, string? username, string? password, bool useTls, bool persist = true, CancellationToken ct = default);

    // Change MQTT scope at runtime (only NetworkId).
    Task UpdateScopeAsync(string networkId, bool persist = true, CancellationToken ct = default);

    // Placeholders to publish
    Task PublishDiscoveredDevicesAsync(DiscoveredDevicesMessage msg, CancellationToken ct = default);
    Task PublishUpdateProgressAsync(UpdateProgressMessage msg, CancellationToken ct = default);
    Task PublishLogAsync(LogMessage msg, CancellationToken ct = default);

    // New: publish FW manifest response
    Task PublishFirmwareManifestAsync(FirmwareManifestResponse msg, CancellationToken ct = default);

    // Existing: generic response
    Task PublishRespAsync(string msg, CancellationToken ct = default);

    // Generic telemetry publisher for structured responses (used by command handlers).
    Task PublishTeleJsonAsync(string leaf, object payload, CancellationToken ct = default);

    // Events when commands arrive
    event Func<StartUpdateCommand, Task>? StartUpdateRequested;
    event Func<GetFwVersionCommand, Task>? GetFwVersionRequested;
    event Func<DetectorSettingsCommand, Task>? GetDetectorSettingsRequested;
    event Func<DetectorSettingsCommand, Task>? SetDetectorSettingsRequested;

    // Disconnect devices via MQTT (single or list)
    event Func<DisconnectDevicesCommand, Task>? DisconnectDevicesRequested;

    // Identify device: Connect (+ optional pincode check + optional login), wait X seconds, disconnect.
    event Func<IdentifyCommand, Task>? IdentifyRequested;

    // LED range visualization: connect/login/set LED based on RSSI and keep links open.
    event Func<LedRangeVisualizeCommand, Task>? LedRangeVisualizeRequested;

    // Explicit disconnect for previously held LED-range links.
    event Func<LedRangeDisconnectCommand, Task>? LedRangeDisconnectRequested;

    // New: FW manifest request command
    event Func<GetFirmwareManifestCommand, Task>? GetFirmwareManifestRequested;

    // Trigger local AccessAPP updater process.
    event Func<SelfUpdateCommand, Task>? SelfUpdateRequested;

    // Set updater channel persisted on device.
    event Func<SetUpdateChannelCommand, Task>? SetUpdateChannelRequested;

    // Reboot the host system.
    event Func<RebootCommand, Task>? RebootRequested;

    // PIR peak status polling.
    event Func<GetPirPeakCommand, Task>? GetPirPeakRequested;

    // Walk-test enable/disable (multi-color LED).
    event Func<SetWalktestCommand, Task>? SetWalktestRequested;
}

// New: command DTO for request payload
// Keep it small and tolerant (payload can be {} or empty).
public sealed class GetFirmwareManifestCommand
{
    // Optional correlation id if you want to match request/response in a UI later
    public string? RequestId { get; set; }

    // Optional: future filtering (if you later want single detector type only)
    public string? DetectorType { get; set; }
}

/// <summary>
/// MQTT command payload for device identification.
/// The gateway will connect to a device, optionally check pincode and login (skipped in boot mode),
/// stay connected for a specified duration, then disconnect.
/// </summary>
public sealed class IdentifyCommand
{
    /// <summary>One or more MAC addresses to identify.</summary>
    public List<string> Sensors { get; set; } = new();

    /// <summary>Optional pincode (application mode only). If empty/null, no pincode check is performed.</summary>
    public string? Pincode { get; set; }

    /// <summary>How long to stay connected before disconnecting. Default 15 seconds.</summary>
    public int Seconds { get; set; } = 15;

    /// <summary>Maximum connect attempts. Default 1.</summary>
    public int MaxConnectAttempts { get; set; } = 1;

    /// <summary>Optional correlation id.</summary>
    public string? RequestId { get; set; }
}

public sealed class LedRangeVisualizeCommand
{
    public string? RequestId { get; set; }
    public string? Pincode { get; set; }
    public int? MinRssi { get; set; }
    public List<string> Sensors { get; set; } = new();
    public List<string> Models { get; set; } = new();
    public int MaxConnectAttempts { get; set; } = 3;
    public bool UseBothChips { get; set; } = true;
}

public sealed class LedRangeDisconnectCommand
{
    public string? RequestId { get; set; }
    public List<string> Sensors { get; set; } = new();
    public bool ForceAll { get; set; }
}

public sealed class SelfUpdateCommand
{
    public string? RequestId { get; set; }
    public bool DryRun { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public string? ConfigPath { get; set; }
    public string? UpdaterPath { get; set; }
    public bool RestartService { get; set; } = true;
    public string ServiceName { get; set; } = "accessapp";
    public string? UpdateChannel { get; set; }
    public string? ChannelFilePath { get; set; }
}

public sealed class SetUpdateChannelCommand
{
    public string? RequestId { get; set; }
    public string? Channel { get; set; }
    public string? ChannelFilePath { get; set; }
}

public sealed class RebootCommand
{
    public string? RequestId { get; set; }
    public int DelaySeconds { get; set; } = 2;
}
