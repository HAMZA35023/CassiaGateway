using System.Net;
using AccessAPP.Services.BleAbstractions;
using Tmds.DBus;

namespace AccessAPP.Services.LinuxBle;

/// <summary>
/// IBleReadWriteService implementation using BlueZ D-Bus (Linux native BLE).
///
/// Writes to a GATT characteristic identified by its handle number.
/// After connecting, BlueZ enumerates GattCharacteristic1 objects; this service
/// looks them up by Handle and calls WriteValue.
///
/// The <paramref name="gatewayIpAddress"/> and chip parameters are ignored —
/// they are Cassia-specific concepts that have no meaning with a local adapter.
/// </summary>
public class LinuxBleReadWriteService : IBleReadWriteService
{
    private readonly ILogger<LinuxBleReadWriteService> _logger;

    // IBleReadWriteService: global semaphore (not used by Linux path, kept for interface compat).
    public SemaphoreSlim? semaphore { get; set; } = null;

    public LinuxBleReadWriteService(ILogger<LinuxBleReadWriteService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public void WriteBleMessageSync(string gatewayIpAddress, string macAddress, int handle, string hexValue, string queryParams, int chip = -1)
    {
        // Run the async implementation synchronously on the current thread.
        // This mirrors the Cassia implementation which does httpClient.Send (sync).
        WriteBleMessageAsync(gatewayIpAddress, macAddress, handle, hexValue, queryParams, chip)
            .GetAwaiter()
            .GetResult()
            .Dispose();
    }

    /// <inheritdoc/>
    public async Task<HttpResponseMessage> WriteBleMessageAsync(
        string gatewayIpAddress,
        string macAddress,
        int handle,
        string hexValue,
        string queryParams,
        int chip = -1,
        CancellationToken ct = default)
    {
        try
        {
            var adapter = RuntimeVariables.LINUX_BLE_ADAPTER;
            var devicePath = BlueZHelpers.DevicePath(adapter, macAddress);
            var characteristic = await BlueZHelpers.GetCharacteristicAsync(devicePath, handle);

            if (characteristic == null)
            {
                _logger.LogWarning("LinuxBLE: characteristic handle {Handle} not found for {Mac}", handle, macAddress);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            byte[] data = BlueZHelpers.HexToBytes(hexValue);

            // Use write-without-response ("command") when the Cassia path would use ?noresponse=1.
            bool noResponse = queryParams?.Contains("noresponse=1", StringComparison.OrdinalIgnoreCase) == true;
            var options = new Dictionary<string, object>
            {
                ["type"] = noResponse ? "command" : "request"
            };

            await characteristic.WriteValueAsync(data, options);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        catch (OperationCanceledException)
        {
            return new HttpResponseMessage(HttpStatusCode.RequestTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LinuxBLE: WriteValue failed for {Mac} handle {Handle}", macAddress, handle);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    /// <inheritdoc/>
    public Task<HttpResponseMessage> WriteBleMessage(string gatewayIpAddress, string macAddress, int handle, string hexValue, string queryParams, int chip = -1, CancellationToken ct = default)
        => WriteBleMessageAsync(gatewayIpAddress, macAddress, handle, hexValue, queryParams, chip, ct);

    public void Dispose() { /* no unmanaged resources */ }
}
