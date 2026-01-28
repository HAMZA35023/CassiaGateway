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

    // Disconnect devices via MQTT (single or list)
    event Func<DisconnectDevicesCommand, Task>? DisconnectDevicesRequested;

    // Identify device: Connect (+ optional pincode check + optional login), wait X seconds, disconnect.
    event Func<IdentifyCommand, Task>? IdentifyRequested;

    // New: FW manifest request command
    event Func<GetFirmwareManifestCommand, Task>? GetFirmwareManifestRequested;
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
