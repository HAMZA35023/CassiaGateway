using AccessAPP.Logging;
using AccessAPP.Services.Helper_Classes;
using AccessAPP.Services.HelperClasses;
using System.Net;

namespace AccessAPP.Services;

public partial class CassiaFirmwareUpgradeService
{
    // Lazily computed request telegram for GetPirPeakStatus (0x0246, no payload).
    private static readonly string s_getPirPeakHex = BuildGetPirPeakHex();

    private static string BuildGetPirPeakHex()
    {
        var header = new TelegramHeader(0x01, 0x0246, 7);
        var telegram = new Telegram(header, Array.Empty<byte>());
        return telegram.getBytes();
    }

    /// <summary>
    /// Reads PIR peak status from a device that is already connected and logged in.
    /// Sends telegram 0x0246, parses 76-byte reply 0x0247.
    /// </summary>
    internal async Task<PirPeakStatusData?> ReadPirPeakStatusAsync(string mac, CancellationToken ct = default)
    {
        try
        {
            var response = await _connectService
                .GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, mac, s_getPirPeakHex)
                .ConfigureAwait(false);

            if (response.Status != HttpStatusCode.OK || string.IsNullOrEmpty(response.Data))
                return null;

            // Response: 14-hex-char header + payload hex
            if (response.Data.Length < 14)
                return null;

            var reply = new GenericTelegramReply(response.Data);
            var hexPayload = reply.DataResult;

            if (string.IsNullOrEmpty(hexPayload) || hexPayload.Length < 152)
                return null;

            var bytes = Convert.FromHexString(hexPayload[..152]);
            return PirPeakStatusData.Parse(bytes);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[PirPeak] ReadPirPeakStatusAsync failed for {mac}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Connects to a sensor, reads PIR peak status, then disconnects.
    /// Returns the parsed data or null on failure.
    /// </summary>
    public async Task<(string Mac, PirPeakStatusData? Data, string? Error)> GetPirPeakForMacAsync(
        string mac,
        string pincode,
        CancellationToken ct = default)
    {
        var chip = GetChipForMac(mac);
        try
        {
            var cl = await ConnectAndLoginWithRetryForPipelineAsync(
                _gatewayIpAddress, _gatewayPort, mac, pincode ?? "",
                logId: null, firmwareVersion: null,
                maxAttempts: 2, delayBetweenAttemptsMs: 1000).ConfigureAwait(false);

            if (!cl.Success)
                return (mac, null, $"connect/login failed: {cl.Message}");

            var data = await ReadPirPeakStatusAsync(mac, ct).ConfigureAwait(false);
            return (mac, data, data == null ? "no data returned" : null);
        }
        catch (OperationCanceledException)
        {
            return (mac, null, "cancelled");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[PirPeak] GetPirPeakForMacAsync exception for {mac}: {ex.Message}");
            return (mac, null, ex.Message);
        }
        finally
        {
            try
            {
                await _connectService
                    .DisconnectFromBleDevice(_gatewayIpAddress, mac, chip: chip)
                    .ConfigureAwait(false);
            }
            catch { }
        }
    }
}
