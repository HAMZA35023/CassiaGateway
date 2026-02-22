using System;
using System.Net;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
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
        string devicePath;
        try
        {
            devicePath = BlueZHelpers.DevicePath(adapter, macAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LinuxBLE: invalid adapter/mac. adapter={Adapter} mac={Mac}", adapter, macAddress);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }

        // Ensure connected (writes fail with org.bluez.Error.Failed: Not connected otherwise)
        var device = await BlueZHelpers.GetDeviceAsync(devicePath);
        if (!await device.GetAsync<bool>("Connected"))
        {
            try { await device.ConnectAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "LinuxBLE: ConnectAsync failed before write for {Mac}", macAddress); }
        }

        // Wait for ServicesResolved (there is no Device1.DiscoverServices method in BlueZ)
        await BlueZHelpers.WaitForServicesResolvedAsync(devicePath, 2500, ct);

        // Detect mode (bootloader vs application) from exposed GATT UUIDs.
        var mode = await BlueZHelpers.DetectModeByGattAsync(devicePath, ct);
        string targetCharUuid = mode == BlueZHelpers.BleGattMode.Bootloader
            ? BlueZHelpers.BootCharUuid
            : BlueZHelpers.AppCharUuid; // default to app if unknown

        // Resolve characteristic by UUID (more stable than handle) with a short retry window.
        IGattCharacteristic1? characteristic = null;
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, RuntimeVariables.LINUX_BLE_WRITE_FIND_CHAR_TIMEOUT_MS));
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            try
            {
                characteristic = await BlueZHelpers.GetCharacteristicByUuidAsync(devicePath, targetCharUuid, ct);
            }
            catch (ArgumentNullException ex)
            {
                _logger.LogWarning(ex, "LinuxBLE: transient ObjectPath decode error while resolving UUID {Uuid} for {Mac}; clearing cache and retrying", targetCharUuid, macAddress);
                BlueZHelpers.ClearDeviceCache(devicePath);
                characteristic = null;
            }

            if (characteristic != null) break;
            await Task.Delay(100, ct);
        }

        if (characteristic == null)
        {
            _logger.LogWarning("LinuxBLE: characteristic UUID {Uuid} not found for {Mac} (handle was {Handle})", targetCharUuid, macAddress, handle);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        // Determine supported write mode from Flags to avoid org.bluez.Error.NotSupported.
        string[] flags = Array.Empty<string>();
        try
        {
            flags = await characteristic.GetAsync<string[]>("Flags");
        }
        catch { /* ignore */ }

        bool supportsWriteReq = flags.Any(f => string.Equals(f, "write", StringComparison.OrdinalIgnoreCase));
        bool supportsWriteCmd = flags.Any(f => string.Equals(f, "write-without-response", StringComparison.OrdinalIgnoreCase));

        bool requestedNoResponse = queryParams?.Contains("noresponse=1", StringComparison.OrdinalIgnoreCase) == true;

        string? writeType = null;
        if (requestedNoResponse && supportsWriteCmd) writeType = "command";
        else if (!requestedNoResponse && supportsWriteReq) writeType = "request";
        else if (supportsWriteCmd) writeType = "command";
        else if (supportsWriteReq) writeType = "request";

        if (writeType == null)
        {
            _logger.LogError("LinuxBLE: characteristic {Uuid} for {Mac} is not writable. Flags=[{Flags}]", targetCharUuid, macAddress, string.Join(",", flags));
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        byte[] data = BlueZHelpers.HexToBytes(hexValue);

        var options = new Dictionary<string, object> { ["type"] = writeType };
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
