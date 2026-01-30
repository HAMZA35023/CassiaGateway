using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AccessAPP.Services;

public class CassiaReadWriteService : IDisposable
{
    // IMPORTANT:
    // - Reuse ONE HttpClient for the whole process (keep-alive)
    // - Limit max concurrent connections to avoid overloading Cassia local REST
    private static readonly HttpClient httpClient = new HttpClient(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        EnableMultipleHttp2Connections = false
    });

    // Keep a semaphore for request serialization/backpressure.
    // NOTE: Some older code assigns a shared semaphore instance (cassiaReadWrite.semaphore = ...).
    // To stay compatible, we allow setting it.
    private SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

    public SemaphoreSlim semaphore
    {
        get => _semaphore;
        set
        {
            if (value != null) _semaphore = value;
        }
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
        _semaphore.Wait();
        try
        {
            var effectiveChip = chip;
            if (effectiveChip < 0 && CassiaChipManager.TryGetChip(macAddress, out var resolvedChip))
                effectiveChip = resolvedChip;

            string endpoint = $"http://{gatewayIpAddress}/gatt/nodes/{macAddress}/handle/{handle}/value/{hexValue}{queryParams}";
            if (effectiveChip >= 0) endpoint = AppendQueryParam(endpoint, "chip", effectiveChip.ToString());

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                Console.WriteLine($"Bad Write {macAddress} reason: {body}");
            }

            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error {macAddress}: {ex}");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Async write used by several services. Returns the HTTP response for backwards compatibility.
    /// Prefer WriteBleMessageSync inside the bootloader transfer loop.
    /// </summary>
    public async Task<HttpResponseMessage> WriteBleMessageAsync(string gatewayIpAddress, string macAddress, int handle, string hexValue, string queryParams, int chip = -1, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var effectiveChip = chip;
            if (effectiveChip < 0 && CassiaChipManager.TryGetChip(macAddress, out var resolvedChip))
                effectiveChip = resolvedChip;

            string endpoint = $"http://{gatewayIpAddress}/gatt/nodes/{macAddress}/handle/{handle}/value/{hexValue}{queryParams}";
            if (effectiveChip >= 0) endpoint = AppendQueryParam(endpoint, "chip", effectiveChip.ToString());

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            // Do NOT dispose here – caller may need StatusCode/headers/body.
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Console.WriteLine($"Bad Write {macAddress} reason: {body}");
            }

            response.EnsureSuccessStatusCode();
            return response;
        }
        finally
        {
            _semaphore.Release();
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
