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

    // Placeholders to publish
    Task PublishDiscoveredDevicesAsync(DiscoveredDevicesMessage msg, CancellationToken ct = default);
    Task PublishUpdateProgressAsync(UpdateProgressMessage msg, CancellationToken ct = default);
    Task PublishLogAsync(LogMessage msg, CancellationToken ct = default);
    Task PublishRespAsync(string msg, CancellationToken ct = default);

    // Events when commands arrive
    event Func<StartUpdateCommand, Task>? StartUpdateRequested;
    event Func<GetFwVersionCommand, Task>? GetFwVersionRequested;
}
