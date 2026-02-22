using System;
using System.Net;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using AccessAPP.Services.BleAbstractions;
using Tmds.DBus;

namespace AccessAPP.Services.LinuxBle;

/// <summary>
/// IBleReadWriteService implementation using BlueZ D-Bus (Linux native BLE).
///
/// Writes to a GATT characteristic identified by UUID.
/// We detect Application vs Bootloader per-MAC by checking the exposed GATT service UUIDs.
/// This avoids unstable handle numbers during mode switching.
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

            // Resolve write characteristic by UUID (per current mode).
            IGattCharacteristic1? characteristic = null;
            string[]? flags = null;

            var timeoutMs = Math.Max(250, RuntimeVariables.LINUX_BLE_WRITE_FIND_CHAR_TIMEOUT_MS);
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                try
                {
                    var mode = await BlueZHelpers.DetectModeByGattAsync(devicePath, ct);
                    string serviceUuid;
                    string writeUuid;

                    if (mode == BlueZHelpers.BleMode.Bootloader)
                    {
                        serviceUuid = BlueZHelpers.BootServiceUuid;
                        // Best-effort: try BootWriteUuid first; if missing we fall back to BootNotifyUuid.
                        writeUuid = BlueZHelpers.BootWriteUuid;
                    }
                    else
                    {
                        serviceUuid = BlueZHelpers.AppServiceUuid;
                        writeUuid = BlueZHelpers.AppWriteUuid; // provided by you
                    }

                    (characteristic, flags) = await BlueZHelpers.GetCharacteristicByUuidAsync(devicePath, serviceUuid, writeUuid, ct);
                    if (characteristic == null && mode == BlueZHelpers.BleMode.Bootloader)
                    {
                        // Fallback: some bootloaders use the notify characteristic for write too.
                        (characteristic, flags) = await BlueZHelpers.GetCharacteristicByUuidAsync(devicePath, serviceUuid, BlueZHelpers.BootNotifyUuid, ct);
                    }

                    if (characteristic != null)
                        break;
                }
                catch (ArgumentNullException ex)
                {
                    _logger.LogWarning(ex, "LinuxBLE: transient ObjectPath decode error while resolving write UUID for {Mac}; clearing cache and retrying", macAddress);
                    BlueZHelpers.ClearDeviceCache(devicePath);
                }

                await Task.Delay(80, ct);
            }

            if (characteristic == null)
            {
                _logger.LogWarning("LinuxBLE: write characteristic not found for {Mac} (handle param was {Handle}, ignored)", macAddress, handle);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            // Determine write type from Flags.
            var f = flags ?? Array.Empty<string>();
            bool canWriteReq = Array.Exists(f, s => s.Equals("write", StringComparison.OrdinalIgnoreCase));
            bool canWriteCmd = Array.Exists(f, s => s.Equals("write-without-response", StringComparison.OrdinalIgnoreCase));

            // BlueZ enforces GATT properties: if the characteristic doesn't advertise
            // write-without-response, every 'command' attempt will be rejected before it
            // even reaches the device.  Pre-mark so we skip that wasted D-Bus round-trip
            // for every write in this session/mode rather than discovering it on the first call.
            if (!canWriteCmd && !BlueZHelpers.IsCommandRejected(devicePath))
                BlueZHelpers.MarkCommandRejected(devicePath);

            // Cassia path uses ?noresponse=1 to prefer command (write-without-response).
            bool preferNoResponse = queryParams?.Contains("noresponse=1", StringComparison.OrdinalIgnoreCase) == true;

            string writeType;
            if (canWriteCmd && (preferNoResponse || !canWriteReq))
                writeType = "command";
            else if (canWriteReq)
            {
                // When the caller wants noresponse and the characteristic doesn't advertise
                // write-without-response, try command anyway — the Cassia REST path always
                // sends noresponse=1 for bulk/bootloader writes and the device firmware
                // typically accepts ATT_WRITE_CMD regardless of the GATT property.
                // If BlueZ rejects it we fall back to request (cached per-device so we only pay once).
                writeType = (preferNoResponse && !BlueZHelpers.IsCommandRejected(devicePath))
                    ? "command"
                    : "request";
            }
            else if (canWriteCmd)
                writeType = "command";
            else
            {
                _logger.LogError("LinuxBLE: characteristic {Uuid} for {Mac} is not writable. Flags=[{Flags}]",
                    BlueZHelpers.AppWriteUuid, macAddress, string.Join(",", f));

                var dump = await BlueZHelpers.DumpGattAsync(devicePath, ct);
                _logger.LogWarning("LinuxBLE GATT dump for {Mac}:\n{Dump}", macAddress, dump);

                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            byte[] data = BlueZHelpers.HexToBytes(hexValue);

            var options = new Dictionary<string, object> { ["type"] = writeType };

            try
            {
                await characteristic.WriteValueAsync(data, options);
            }
            catch (DBusException ex) when (
                writeType == "command" && !canWriteCmd &&
                (ex.ErrorName is "org.bluez.Error.NotSupported"
                              or "org.bluez.Error.NotPermitted"
                              or "org.bluez.Error.InvalidArguments"))
            {
                // BlueZ enforces GATT properties and rejected command mode.
                // Remember this for all subsequent writes in this session so we stop trying.
                BlueZHelpers.MarkCommandRejected(devicePath);
                _logger.LogDebug("LinuxBLE: command write rejected by BlueZ for {Mac} — switching to request for this session", macAddress);

                if (!canWriteReq)
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);

                var fallback = new Dictionary<string, object> { ["type"] = "request" };
                await characteristic.WriteValueAsync(data, fallback);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        catch (OperationCanceledException)
        {
            return new HttpResponseMessage(HttpStatusCode.RequestTimeout);
        }
        catch (DBusException ex) when (ex.ErrorName == "org.bluez.Error.Failed" && ex.Message.Contains("Operation already in progress", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "LinuxBLE: write attempted while connect is in progress for {Mac}", macAddress);
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        }
        catch (DBusException ex) when (ex.ErrorName == "org.bluez.Error.Failed" && ex.Message.Contains("Not connected", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "LinuxBLE: write failed because device is not connected for {Mac}", macAddress);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
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
