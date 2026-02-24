namespace AccessAPP.Services.BleAbstractions;

/// <summary>
/// Abstracts BLE characteristic read/write operations.
/// The Cassia implementation performs HTTP REST calls to the gateway.
/// The Linux native implementation performs D-Bus calls to BlueZ directly.
/// </summary>
public interface IBleReadWriteService : IDisposable
{
    /// <summary>Optional global concurrency semaphore (Cassia backwards-compat).</summary>
    SemaphoreSlim? semaphore { get; set; }

    /// <summary>
    /// Synchronous write used by bootloader programming paths.
    /// Blocks until the write request completes.
    /// </summary>
    void WriteBleMessageSync(string gatewayIpAddress, string macAddress, int handle, string hexValue, string queryParams, int chip = -1);

    /// <summary>
    /// Async write. Returns an HttpResponseMessage whose StatusCode indicates success/failure.
    /// Callers must dispose the returned response.
    /// </summary>
    Task<HttpResponseMessage> WriteBleMessageAsync(string gatewayIpAddress, string macAddress, int handle, string hexValue, string queryParams, int chip = -1, CancellationToken ct = default);

    /// <summary>Backwards-compatible alias for WriteBleMessageAsync.</summary>
    Task<HttpResponseMessage> WriteBleMessage(string gatewayIpAddress, string macAddress, int handle, string hexValue, string queryParams, int chip = -1, CancellationToken ct = default);
}
