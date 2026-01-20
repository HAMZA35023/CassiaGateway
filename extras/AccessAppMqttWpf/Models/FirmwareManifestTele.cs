using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AccessAppMqttWpf.Models;

public sealed class FirmwareManifestTele
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("firmwareManifest")]
    public Dictionary<string, string[]> FirmwareManifest { get; set; } = new();
}
