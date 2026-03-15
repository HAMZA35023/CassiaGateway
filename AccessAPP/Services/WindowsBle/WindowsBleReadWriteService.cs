#if WINDOWS
using System.Net;
using AccessAPP.Services.BleAbstractions;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace AccessAPP.Services.WindowsBle;

/// <summary>
/// <see cref="IBleReadWriteService"/> implementation using Windows.Devices.Bluetooth WinRT APIs.
///
/// Resolves the write characteristic by GATT UUID (based on current app/boot mode) and
/// calls <c>WriteValueWithResultAsync</c>.  The <c>gatewayIpAddress</c>, <c>handle</c>,
/// and <c>chip</c> parameters are accepted for interface compatibility but are not used.
/// </summary>
public class WindowsBleReadWriteService : IBleReadWriteService
{
    private readonly ILogger<WindowsBleReadWriteService> _logger;

    // Cassia-compat — not used by the Windows path.
    public SemaphoreSlim? semaphore { get; set; } = null;

    public WindowsBleReadWriteService(ILogger<WindowsBleReadWriteService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public void WriteBleMessageSync(
        string gatewayIpAddress, string macAddress, int handle,
        string hexValue, string queryParams, int chip = -1)
    {
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
            var device = await WindowsBleHelpers.GetOrOpenDeviceAsync(macAddress, ct)
                .ConfigureAwait(false);

            if (device == null)
            {
                _logger.LogWarning("WinBLE Write: {Mac} device not found", macAddress);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            // Resolve write characteristic based on current GATT mode.
            GattCharacteristic? characteristic = null;
            var timeoutMs = Math.Max(250, RuntimeVariables.LINUX_BLE_WRITE_FIND_CHAR_TIMEOUT_MS);
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                // Use cached mode where possible — DetectModeAsync calls GetGattServicesAsync(Uncached)
                // which refreshes the OS GATT cache and invalidates previously obtained GattDeviceService
                // handles.  GetCachedModeAsync only performs a fresh scan when the cache is empty
                // (first connect or after InvalidateModeCache) and flushes the char/svc handle caches
                // afterward so they are re-fetched cleanly.
                var mode = await WindowsBleHelpers.GetCachedModeAsync(macAddress, device, ct).ConfigureAwait(false);
                _logger.LogDebug("WinBLE Write: {Mac} mode={Mode} data={Bytes}b", macAddress, mode, hexValue.Length / 2);

                if (mode == WindowsBleHelpers.BleMode.Bootloader)
                {
                    characteristic = await WindowsBleHelpers.GetCharacteristicAsync(
                        macAddress, device, WindowsBleHelpers.BootServiceUuid, WindowsBleHelpers.BootWriteUuid, ct)
                        .ConfigureAwait(false);
                    // Some bootloaders expose only the notify characteristic for writes.
                    if (characteristic == null)
                        characteristic = await WindowsBleHelpers.GetCharacteristicAsync(
                            macAddress, device, WindowsBleHelpers.BootServiceUuid, WindowsBleHelpers.BootNotifyUuid, ct)
                            .ConfigureAwait(false);
                }
                else if (mode == WindowsBleHelpers.BleMode.Application)
                {
                    characteristic = await WindowsBleHelpers.GetCharacteristicAsync(
                        macAddress, device, WindowsBleHelpers.AppServiceUuid, WindowsBleHelpers.AppWriteUuid, ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    // Mode Unknown: GATT discovery still in progress — probe both.
                    WindowsBleHelpers.InvalidateModeCache(macAddress);
                    characteristic = await WindowsBleHelpers.GetCharacteristicAsync(
                        macAddress, device, WindowsBleHelpers.BootServiceUuid, WindowsBleHelpers.BootWriteUuid, ct)
                        .ConfigureAwait(false);
                    if (characteristic == null)
                        characteristic = await WindowsBleHelpers.GetCharacteristicAsync(
                            macAddress, device, WindowsBleHelpers.AppServiceUuid, WindowsBleHelpers.AppWriteUuid, ct)
                            .ConfigureAwait(false);
                }

                if (characteristic != null)
                    break;

                await Task.Delay(80, ct).ConfigureAwait(false);
            }

            if (characteristic == null)
            {
                _logger.LogWarning("WinBLE Write: {Mac} — write characteristic not found (handle param was {Handle}, ignored)", macAddress, handle);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            // Choose write type: noresponse=1 → WriteWithoutResponse, else WriteWithResponse.
            bool noResponse = queryParams?.Contains("noresponse=1", StringComparison.OrdinalIgnoreCase) == true;
            var writeOption = noResponse ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;

            // Check characteristic properties to make sure write is supported.
            bool canWriteReq = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write);
            bool canWriteCmd = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);

            if (noResponse && canWriteCmd)
                writeOption = GattWriteOption.WriteWithoutResponse;
            else if (canWriteReq)
                writeOption = GattWriteOption.WriteWithResponse;
            else if (canWriteCmd)
                writeOption = GattWriteOption.WriteWithoutResponse;
            else
            {
                _logger.LogError("WinBLE Write: {Mac} characteristic is not writable (properties={Props})",
                    macAddress, characteristic.CharacteristicProperties);
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            byte[] data = WindowsBleHelpers.HexToBytes(hexValue);
            var writer = new DataWriter();
            writer.WriteBytes(data);
            var buffer = writer.DetachBuffer();

            _logger.LogDebug("WinBLE Write: {Mac} WriteValueAsync writeOption={Option} bytes={Bytes}",
                macAddress, writeOption, data.Length);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            GattWriteResult writeResult = await characteristic
                .WriteValueWithResultAsync(buffer, writeOption)
                .AsTask(ct).ConfigureAwait(false);
            sw.Stop();

            if (writeResult.Status == GattCommunicationStatus.Success)
            {
                _logger.LogDebug("WinBLE Write: {Mac} OK ({Ms}ms)", macAddress, sw.ElapsedMilliseconds);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            _logger.LogWarning("WinBLE Write: {Mac} failed status={Status} protocolError={ProtocolError} ({Ms}ms)",
                macAddress, writeResult.Status, writeResult.ProtocolError, sw.ElapsedMilliseconds);

            return writeResult.Status switch
            {
                GattCommunicationStatus.Unreachable => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                GattCommunicationStatus.AccessDenied => new HttpResponseMessage(HttpStatusCode.Forbidden),
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("WinBLE Write: {Mac} canceled/timed-out — returning 408", macAddress);
            return new HttpResponseMessage(HttpStatusCode.RequestTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WinBLE Write: {Mac} WriteValue failed (handle {Handle})", macAddress, handle);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    /// <inheritdoc/>
    public Task<HttpResponseMessage> WriteBleMessage(
        string gatewayIpAddress, string macAddress, int handle,
        string hexValue, string queryParams, int chip = -1, CancellationToken ct = default)
        => WriteBleMessageAsync(gatewayIpAddress, macAddress, handle, hexValue, queryParams, chip, ct);

    public void Dispose() { /* no unmanaged resources */ }
}
#endif
