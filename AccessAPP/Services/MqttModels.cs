using System.Text.Json.Serialization;

namespace AccessAPP.Services;

public enum MqttCommandType
{
    StartUpdate,
    GetFwVersion
}

public sealed class StartUpdateCommand
{
    public List<StartUpdateRequest> Requests { get; set; } = new();

    // Optional: keep for backwards compatibility if older senders used it
    public List<string> Sensors { get; set; } = new();
}


public sealed class StartUpdateRequest
{
    [JsonPropertyName("DetectorType")]
    public string? DetectorType { get; set; }

    [JsonPropertyName("FirmwareVersion")]
    public string? FirmwareVersion { get; set; }

    [JsonPropertyName("MacAddress")]
    public string? MacAddress { get; set; }

    [JsonPropertyName("Pincode")]
    public string? Pincode { get; set; }

    /// <summary>
    /// If true, forces re-programming even when current FW matches target.
    /// Default is false when omitted.
    /// </summary>
    [JsonPropertyName("ForceUpdate")]
    public bool? ForceUpdate { get; set; }
}

// NEW: change MQTT scope (only NetworkId) at runtime
public sealed class SetMqttScopeCommand
{
    public string? NetworkId { get; set; }
}



// NEW: change Cassia gateway name at runtime (persists to mqtt.json)
public sealed class SetCassiaNameCommand
{
    public string? Name { get; set; }
}

// NEW: change both MQTT scope + gateway name (persists to mqtt.json)
public sealed class SetIdentityCommand
{
    public string? NetworkId { get; set; }
    public string? Name { get; set; }
}

public sealed class GetFwVersionCommand
{
    public List<string> Sensors { get; set; } = new();

    // Optional: if your devices require a pincode for Connect+Login.
    public string? Pincode { get; set; }
}

public sealed class DisconnectDevicesCommand
{
    public List<string> Sensors { get; set; } = new();
}

public sealed class DiscoveredDevicesMessage
{
    public string Name { get; set; } = "";
    public string NetworkId { get; set; } = "";
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;

    // Placeholder structure
    public List<DiscoveredDevice> Devices { get; set; } = new();
}

public sealed class DiscoveredDevice
{
    public string Mac { get; set; } = "";
    public int? Rssi { get; set; }
    public string? DetectorType { get; set; }
    public string? DetectorFamily { get; set; }

    public string? ProductNumber { get; set; }

    public string? Name { get; set; }
}

public sealed class UpdateProgressMessage
{
    public string Name { get; set; } = "";
    public string NetworkId { get; set; } = "";
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;

    // Placeholder
    public string Mac { get; set; } = "";
    public double ProgressPercent { get; set; }
    public string? Stage { get; set; }
    public string? FirmwareTarget { get; set; }
}

public sealed class LogMessage
{
    public string Name { get; set; } = "";
    public string NetworkId { get; set; } = "";
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;

    public string Level { get; set; } = "info"; // info/warn/error/debug/verbose
    public string Message { get; set; } = "";
    public string? Mac { get; set; }
    public string? LogId { get; set; }
}

public sealed class StatusMessage
{
    public string Name { get; set; } = "";
    public string NetworkId { get; set; } = "";
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;

    public string State { get; set; } = "online"; // online/offline/etc.
    public string? Version { get; set; } = AccessAPP.Version.AppVersion;
    public int queue { get; set; }
    public int programming { get; set; }

    public double totalSpeedpct { get; set; }
    }

/// <summary>
/// DTO used by MQTT "get-device-list" so the full device list can be returned in ONE message.
/// </summary>
public sealed class DeviceListItem
{
    public string MacAddress { get; set; } = "";
    public int Rssi { get; set; }
    public string? DetectorType { get; set; }
    public string? DetectorFamily { get; set; }
    public string? ProductNumber { get; set; }
    public string? Name { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public bool IsStale { get; set; }
}
