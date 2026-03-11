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

    public event Action<bool, string>? StatusChanged; // isRunning, message

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
