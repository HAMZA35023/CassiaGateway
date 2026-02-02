using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AccessAPP;
using AccessAPP.Services;

public class CassiaReadWriteService : IDisposable
{
    // IMPORTANT:
    // - Reuse ONE HttpClient for the whole process (keep-alive)
    // - Limit max concurrent connections to avoid overloading Cassia local REST
    private static readonly HttpClient httpClient = new HttpClient(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 50,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        EnableMultipleHttp2Connections = false
    });

    // Concurrency control:
// We want parallelism across the two Cassia BLE chips, but still keep backpressure
// so we don't overload the local REST API.
//
// Policy:
// - Per-chip limiter (chip 0 and chip 1 are independent)
// - Optional global limiter for backwards-compatibility if some code assigns it
//   (but we don't set it by default).
private readonly SemaphoreSlim _chip0Semaphore = new SemaphoreSlim(RuntimeVariables.CASSIA_MAX_INFLIGHT_PER_CHIP, RuntimeVariables.CASSIA_MAX_INFLIGHT_PER_CHIP);
private readonly SemaphoreSlim _chip1Semaphore = new SemaphoreSlim(RuntimeVariables.CASSIA_MAX_INFLIGHT_PER_CHIP, RuntimeVariables.CASSIA_MAX_INFLIGHT_PER_CHIP);

// Backwards-compatible global limiter (avoid using this if you want dual-chip scaling)
private SemaphoreSlim? _globalSemaphore = null;
public SemaphoreSlim? semaphore
{
    get => _globalSemaphore;
    set => _globalSemaphore = value;
}

private SemaphoreSlim GetChipSemaphore(int chip)
{
    return chip == 1 ? _chip1Semaphore : _chip0Semaphore;
}

private static string AppendQueryParam(string url, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (string.IsNullOrWhiteSpace(key)) return url;

        var keyEq = key + "=";
        if (url.Contains("?" + keyEq, StringComparison.OrdinalIgnoreCase) ||
            url.Contains("&" + keyEq, StringComparison.OrdinalIgnoreCase))
            return url;

        return url.Contains('?')
            ? (url + "&" + keyEq + Uri.EscapeDataString(value ?? ""))
            : (url + "?" + keyEq + Uri.EscapeDataString(value ?? ""));
    }

    /// <summary>
    /// Synchronous write used by bootloader/programming code paths that expect the write to be completed
    /// before proceeding (and that immediately wait for a notification/response).
    /// </summary>
    public void WriteBleMessageSync(string gatewayIpAddress, string macAddress, int handle, string hexValue, string queryParams, int chip = -1)
    {
        var effectiveChip = chip;
        if (effectiveChip < 0 && CassiaChipManager.TryGetChip(macAddress, out var resolvedChip))
            effectiveChip = resolvedChip;

        var chipSem = GetChipSemaphore(effectiveChip);
        chipSem.Wait();
        _globalSemaphore?.Wait();

        try
        {
            string endpoint = $"http://{gatewayIpAddress}/gatt/nodes/{macAddress}/handle/{handle}/value/{hexValue}{queryParams}";
            if (effectiveChip >= 0) endpoint = AppendQueryParam(endpoint, "chip", effectiveChip.ToString());

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = httpClient.Send(request);
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            _globalSemaphore?.Release();
            chipSem.Release();
        }
    }

    /// <summary>
    /// Async write used by several services. Returns the HTTP response for backwards compatibility.
    /// Prefer WriteBleMessageSync inside the bootloader transfer loop.
    /// </summary>
    /// <summary>
/// Async REST call. Use this for “control plane” operations (connect/login/read/etc.).
/// Prefer WriteBleMessageSync inside the bootloader transfer loop.
/// </summary>
public async Task<HttpResponseMessage> WriteBleMessageAsync(string gatewayIpAddress, string macAddress, int handle, string hexValue, string queryParams, int chip = -1, CancellationToken ct = default)
    {
        var effectiveChip = chip;
        if (effectiveChip < 0 && CassiaChipManager.TryGetChip(macAddress, out var resolvedChip))
            effectiveChip = resolvedChip;

        var chipSem = GetChipSemaphore(effectiveChip);
        await chipSem.WaitAsync(ct).ConfigureAwait(false);
        if (_globalSemaphore != null)
            await _globalSemaphore.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            string endpoint = $"http://{gatewayIpAddress}/gatt/nodes/{macAddress}/handle/{handle}/value/{hexValue}{queryParams}";
            if (effectiveChip >= 0) endpoint = AppendQueryParam(endpoint, "chip", effectiveChip.ToString());

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return response; // caller must dispose
        }
        catch
        {
            // Ensure we don't leak the global semaphore on exceptions
            throw;
        }
        finally
        {
            _globalSemaphore?.Release();
            chipSem.Release();
        }
    }

    /// <summary>
    /// Backwards-compatible alias (older code referenced WriteBleMessage).
    /// </summary>
    public Task<HttpResponseMessage> WriteBleMessage(string gatewayIpAddress, string macAddress, int handle, string hexValue, string queryParams, int chip = -1, CancellationToken ct = default)
        => WriteBleMessageAsync(gatewayIpAddress, macAddress, handle, hexValue, queryParams, chip, ct);

    public void Dispose()
    {
        // Keep static httpClient alive for process lifetime.
    }
}
