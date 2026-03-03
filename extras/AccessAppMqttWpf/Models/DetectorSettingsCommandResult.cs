using System.Text.Json.Nodes;

namespace AccessAppMqttWpf.Models;

public sealed class DetectorSettingsCommandResult
{
    public bool Success { get; init; }
    public string Action { get; init; } = "";
    public string RequestId { get; init; } = "";
    public string Cassia { get; init; } = "";
    public string Mac { get; init; } = "";
    public string DetectorType { get; init; } = "";
    public string FirmwareVersion { get; init; } = "";
    public string Message { get; init; } = "";
    public JsonNode? Settings { get; init; }
    public JsonNode? Raw { get; init; }
}
