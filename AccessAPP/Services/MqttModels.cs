using System.Text.Json.Serialization;

using AccessAPP.Models;

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

    /// <summary>
    /// Optional detector settings patch to apply after successful upgrade.
    /// </summary>
    [JsonPropertyName("DetectorSettings")]
    public DetectorSettingsPatch? DetectorSettings { get; set; }

    /// <summary>
    /// If true, run DALI commissioning 0x0400 with SearchType=0x00 after update
    /// (address all control gear devices and assign to zone 1).
    /// Default is false when omitted.
    /// </summary>
    [JsonPropertyName("RunDaliAddressAllToZone1AfterUpdate")]
    public bool? RunDaliAddressAllToZone1AfterUpdate { get; set; }

    /// <summary>
    /// If true, run DALI commissioning total-new 102 scan (0x0400, SearchType=0x01) after update.
    /// Default is false when omitted.
    /// </summary>
    [JsonPropertyName("RunDali102TotalNewScanAfterUpdate")]
    public bool? RunDali102TotalNewScanAfterUpdate { get; set; }

    /// <summary>
    /// If true, run DALI commissioning total-new 103 scan (0x0400, SearchType=0x03) after update.
    /// Default is false when omitted.
    /// </summary>
    [JsonPropertyName("RunDali103TotalNewScanAfterUpdate")]
    public bool? RunDali103TotalNewScanAfterUpdate { get; set; }
}

// NEW: change MQTT scope (only NetworkId) at runtime
public sealed class SetMqttScopeCommand
{
    public string? NetworkId { get; set; }
}


public sealed class GetFwVersionCommand
{
    public List<string> Sensors { get; set; } = new();

    // Optional: if your devices require a pincode for Connect+Login.
    public string? Pincode { get; set; }
}

public sealed class DetectorSettingsCommand
{
    public string? RequestId { get; set; }
    public List<string> Sensors { get; set; } = new();
    public string? Pincode { get; set; }
    public string? DetectorType { get; set; }
    public string? FirmwareVersion { get; set; }
    public DetectorSettingsPatch? Settings { get; set; }
    public bool? WriteOnlyChanged { get; set; }
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
    // Percent points per minute (10s sliding window); null when not applicable.
    public double? SpeedPctPerMin { get; set; }
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
    // OS uptime (seconds). Derived from Environment.TickCount64.
    public long uptimeSeconds { get; set; }

    // BLE backend in use (e.g. "windows-native", "linux-native", "cassia")
    public string? backend { get; set; }

    // Optional LTE/4G modem status (ZTE MF79U)
    public string? cellularState { get; set; }
    public string? cellularNetworkType { get; set; }
    public int? cellularSignalBar { get; set; }
    public int? cellularRssiDbm { get; set; }
    public int? cellularLteRsrpDbm { get; set; }
    public int? cellularLteRsrqDb { get; set; }
    public int? cellularLteSnrDb { get; set; }
    public string? cellularProvider { get; set; }
    public DateTimeOffset? cellularPolledAtUtc { get; set; }
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

public sealed class GetPirPeakCommand
{
    public List<string> Sensors { get; set; } = new();
    public string? Pincode { get; set; }
    public string? RequestId { get; set; }
}

public sealed class SetWalktestCommand
{
    public List<string> Sensors { get; set; } = new();
    public bool Enabled { get; set; }
    public string? Pincode { get; set; }
    public string? RequestId { get; set; }
}
