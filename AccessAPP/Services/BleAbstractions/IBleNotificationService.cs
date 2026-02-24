namespace AccessAPP.Services.BleAbstractions;

/// <summary>
/// Abstracts GATT notification subscriptions.
/// The Cassia implementation uses a persistent SSE stream from the gateway.
/// The Linux native implementation subscribes to BlueZ PropertiesChanged D-Bus signals.
/// </summary>
public interface IBleNotificationService
{
    /// <summary>Shared semaphore wired to the connection service (Cassia compat).</summary>
    SemaphoreSlim? semaphore { get; set; }

    /// <summary>Set to true if the underlying SSE/listener was force-restarted.</summary>
    bool forcedRestartedSSE { get; set; }

    /// <summary>
    /// Enable GATT notifications on the device (writes to the CCCD descriptor).
    /// </summary>
    Task<bool> EnableNotificationAsync(string gatewayIpAddress, string nodeMac, bool bActor, int chip = -1);

    /// <summary>
    /// Subscribe to characteristic notifications for a MAC address.
    /// Returns a token used to unsubscribe.
    /// </summary>
    Guid Subscribe(string macAddress, EventHandler<string> handler);

    /// <summary>Unsubscribe a specific subscription token.</summary>
    void Unsubscribe(string macAddress, Guid token);

    /// <summary>Unsubscribe all handlers for a MAC address.</summary>
    void Unsubscribe(string macAddress);
}
