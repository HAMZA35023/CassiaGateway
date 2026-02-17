using System.Text.Json.Nodes;

namespace AccessAppMqttWpf.Models;

public sealed class CassiaSettingsCommandResult
{
    public bool Success { get; init; }
    public string Action { get; init; } = "";
    public string RequestId { get; init; } = "";
    public string Cassia { get; init; } = "";
    public string GatewayIp { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Csrf { get; init; }
    public JsonNode? Settings { get; init; }
    public JsonNode? SavePayloadTemplate { get; init; }
    public JsonNode? Raw { get; init; }
}
