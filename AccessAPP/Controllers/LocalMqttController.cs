using AccessAPP.Services;
using Microsoft.AspNetCore.Mvc;

namespace AccessAPP.Controllers;

/// <summary>
/// Allows a WPF AccessAppMqttWpf client on the same LAN to push local MQTT broker settings
/// to this AccessApp instance so it can connect without needing UDP broadcast discovery.
///
/// POST /api/local-mqtt  { "token": "...", "mqttHost": "192.168.x.x", "mqttPort": 1883 }
/// </summary>
[ApiController]
[Route("api/local-mqtt")]
public class LocalMqttController : ControllerBase
{
    /// <summary>Shared secret — must match LocalMqttServerService.LocalToken in the WPF app.</summary>
    private const string SharedToken = "cassia-local-3a7f2b9e1c4d8f06";

    [HttpPost]
    public IActionResult SetBroker([FromBody] LocalMqttConfigRequest req)
    {
        if (req.Token != SharedToken)
        {
            AppLog.Warn("[LocalMqtt] Config push rejected: invalid token.");
            return Unauthorized(new { error = "Invalid token." });
        }

        // Use the caller's IP — this is the WPF machine's address as seen from the Cassia,
        // so it is always the correct IP to reach the local MQTT broker.
        var callerIp = HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString();
        if (string.IsNullOrWhiteSpace(callerIp) || callerIp == "0.0.0.0")
        {
            AppLog.Warn("[LocalMqtt] Config push rejected: could not determine caller IP.");
            return BadRequest(new { error = "Could not determine caller IP." });
        }

        RuntimeVariables.LOCAL_MQTT_HOST = callerIp;
        if (req.MqttPort > 0)
            RuntimeVariables.LOCAL_MQTT_PORT = req.MqttPort;

        AppLog.Info($"[LocalMqtt] Local broker set to {RuntimeVariables.LOCAL_MQTT_HOST}:{RuntimeVariables.LOCAL_MQTT_PORT} (pushed by WPF)");

        return Ok(new { ok = true, mqttHost = RuntimeVariables.LOCAL_MQTT_HOST, mqttPort = RuntimeVariables.LOCAL_MQTT_PORT });
    }

    public sealed class LocalMqttConfigRequest
    {
        public string? Token    { get; set; }
        public int     MqttPort { get; set; }
    }
}
