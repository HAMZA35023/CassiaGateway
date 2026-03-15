using MQTTnet;
using MQTTnet.Server;
using System;
using System.Threading.Tasks;

namespace AccessAppMqttWpf.Services;

/// <summary>
/// Hosts a local MQTT broker using MQTTnet. Token-authenticated connections only.
/// Lifetime is managed by MainViewModel.
/// </summary>
public sealed class LocalMqttServerService : IDisposable
{
    /// <summary>Shared secret used by all local-network participants (WPF + AccessApp).</summary>
    public const string LocalToken = "cassia-local-3a7f2b9e1c4d8f06";

    private MqttServer? _server;
    private readonly MqttFactory _factory = new();

    public bool IsRunning => _server != null;
    public int Port { get; private set; }

    public event Action<bool, string>? StatusChanged;  // isRunning, message
    /// <summary>
    /// Fired when a non-localhost client successfully authenticates.
    /// Argument is the remote IP address string (e.g. "192.168.1.42").
    /// Use this to discover AccessApp instances that connected via the local MQTT server
    /// without needing inbound UDP on the WPF machine.
    /// </summary>
    public event Action<string>? RemoteClientConnected;

    public async Task StartAsync(int port)
    {
        if (_server != null) return;

        var options = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(port)
            .Build();

        _server = _factory.CreateMqttServer(options);

        // Accept only clients presenting the shared token as password
        _server.ValidatingConnectionAsync += args =>
        {
            args.ReasonCode = args.Password == LocalToken
                ? MQTTnet.Protocol.MqttConnectReasonCode.Success
                : MQTTnet.Protocol.MqttConnectReasonCode.BadAuthenticationMethod;
            return Task.CompletedTask;
        };

        // Detect remote AccessApp connections — gives us the client IP without needing inbound UDP
        _server.ClientConnectedAsync += args =>
        {
            try
            {
                var endpoint = args.Endpoint ?? "";
                // Endpoint is "ip:port" — take everything before the last colon
                var lastColon = endpoint.LastIndexOf(':');
                var ip = lastColon > 0 ? endpoint[..lastColon] : endpoint;
                ip = ip.Trim('[', ']'); // strip IPv6 brackets if present

                AppLog.Info($"[LocalMqttServer] Client connected: endpoint={endpoint}, resolved ip={ip}");

                if (!string.IsNullOrWhiteSpace(ip)
                    && ip != "127.0.0.1" && ip != "::1" && ip != "0.0.0.0")
                {
                    AppLog.Info($"[LocalMqttServer] Remote client authenticated from {ip} — firing RemoteClientConnected");
                    RemoteClientConnected?.Invoke(ip);
                }
            }
            catch { /* best-effort */ }
            return Task.CompletedTask;
        };

        await _server.StartAsync();
        Port = port;
        StatusChanged?.Invoke(true, $"Running on port {port}");
    }

    public async Task StopAsync()
    {
        if (_server == null) return;

        try
        {
            await _server.StopAsync();
            _server.Dispose();
        }
        catch { }
        finally
        {
            _server = null;
            Port = 0;
            StatusChanged?.Invoke(false, "Stopped");
        }
    }

    public void Dispose()
    {
        if (_server != null)
            _ = StopAsync();
    }
}
