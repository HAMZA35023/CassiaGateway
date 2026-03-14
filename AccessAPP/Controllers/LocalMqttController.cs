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

        // Prefer mqttHost from the payload (WPF sends its probed outbound IP, which is correct
        // even when the Cassia sits behind NAT and RemoteIpAddress would return the LXC gateway).
        // Fall back to the TCP caller IP for backward compatibility with older WPF clients.
        string? host = null;
        if (!string.IsNullOrWhiteSpace(req.MqttHost))
        {
            host = req.MqttHost.Trim();
        }
        else
        {
            host = HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString();
            if (string.IsNullOrWhiteSpace(host) || host == "0.0.0.0")
            {
                AppLog.Warn("[LocalMqtt] Config push rejected: could not determine caller IP and no mqttHost in payload.");
                return BadRequest(new { error = "Could not determine caller IP." });
            }
        }

        RuntimeVariables.LOCAL_MQTT_HOST = host;
        if (req.MqttPort > 0)
            RuntimeVariables.LOCAL_MQTT_PORT = req.MqttPort;
        if (!string.IsNullOrWhiteSpace(req.NetworkId))
            RuntimeVariables.LOCAL_NETWORK_ID = req.NetworkId.Trim();

        AppLog.Info($"[LocalMqtt] Local broker set to {RuntimeVariables.LOCAL_MQTT_HOST}:{RuntimeVariables.LOCAL_MQTT_PORT}" +
                    (string.IsNullOrEmpty(RuntimeVariables.LOCAL_NETWORK_ID) ? "" : $", localNetworkId={RuntimeVariables.LOCAL_NETWORK_ID}") +
                    " (pushed by WPF)");

        return Ok(new { ok = true, mqttHost = RuntimeVariables.LOCAL_MQTT_HOST, mqttPort = RuntimeVariables.LOCAL_MQTT_PORT });
    }

    public sealed class LocalMqttConfigRequest
    {
        public string? Token    { get; set; }
        public int     MqttPort { get; set; }
        /// <summary>
        /// WPF's own LAN IP (probed via routing table). When set, used instead of the TCP
        /// caller IP — necessary when AccessApp runs in a NAT container (e.g. Cassia LXC).
        /// </summary>
        public string? MqttHost { get; set; }
        /// <summary>
        /// When WPF has "use shared network ID" enabled, this is the WPF's NetworkId.
        /// AccessApp uses it for local broker subscription topics and mirrored telemetry
        /// so all gateways appear on the same network in the local MQTT session.
        /// Empty/null = keep the AccessApp's own configured NetworkId.
        /// </summary>
        public string? NetworkId { get; set; }
    }
}
