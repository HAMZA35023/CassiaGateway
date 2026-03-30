using AccessAPP.Logging;
using AccessAPP.Services.HelperClasses;
using System.Buffers.Binary;
using System.Net;

namespace AccessAPP.Services;

public partial class CassiaFirmwareUpgradeService
{
    // Lazily computed request telegrams (little-endian, matching P40 protocol).
    private static readonly string s_getPirPeakHex   = BuildHex7(0x0246);
    private static readonly string s_getTrigLevelHex = BuildHex7(0x0240); // 01-40-02-07-00-AF-AD

    /// <summary>Builds a 7-byte header-only telegram (no payload).</summary>
    private static string BuildHex7(ushort type)
    {
        const ushort len = 7;
        var h = new byte[7];
        h[0] = 0x01;
        h[1] = (byte)(type & 0xFF);
        h[2] = (byte)(type >> 8);
        h[3] = (byte)(len & 0xFF);
        h[4] = (byte)(len >> 8);
        ushort crc = PirPeakCrc16(h.AsSpan(0, 5));
        h[5] = (byte)(crc & 0xFF);
        h[6] = (byte)(crc >> 8);
        return Convert.ToHexString(h);
    }

    private static ushort PirPeakCrc16(ReadOnlySpan<byte> data, ushort crc = 0x8005, ushort poly = 0x1021)
    {
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0
                    ? (ushort)(((crc << 1) ^ poly) & 0xFFFF)
                    : (ushort)(crc << 1);
        }
        return crc;
    }

    /// <summary>
    /// Reads the walktest active flag from the device's user config (byte 4, bits 0x05).
    /// Called once after login in RunPirPeakSessionAsync.
    /// </summary>
    private async Task<bool?> ReadWalktestStateAsync(string mac)
    {
        try
        {
            var hex = await GetUserConfig(mac).ConfigureAwait(false);
            if (string.IsNullOrEmpty(hex) || hex.Length < (UserConfigWalktestByteIndex + 1) * 2)
                return null;
            var bytes = Convert.FromHexString(hex);
            return (bytes[UserConfigWalktestByteIndex] & UserConfigWalktestMask) != 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[PirPeak] ReadWalktestStateAsync failed for {mac}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads the configured PIR trigger levels via opcode 0x0240.
    /// Response payload: 6 bytes — three uint16 LE (A, B, C).
    /// </summary>
    private async Task<(ushort A, ushort B, ushort C)?> ReadPirTriggerLevelsAsync(string mac)
    {
        try
        {
            var response = await _connectService
                .GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, mac, s_getTrigLevelHex)
                .ConfigureAwait(false);

            if (response.Status != HttpStatusCode.OK || string.IsNullOrEmpty(response.Data))
            {
                AppLog.Warn($"[PirPeak] ReadPirTriggerLevelsAsync: bad response for {mac}: status={response.Status}");
                return null;
            }

            if (response.Data.Length < 12)
            {
                AppLog.Warn($"[PirPeak] ReadPirTriggerLevelsAsync: payload too short for {mac}: len={response.Data.Length} (need 12)");
                return null;
            }

            var bytes = Convert.FromHexString(response.Data[..12]);
            var a = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2));
            var b = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2));
            var c = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
            AppLog.Info($"[PirPeak] TriggerLevels for {mac}: A={a} B={b} C={c}");
            return (a, b, c);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[PirPeak] ReadPirTriggerLevelsAsync failed for {mac}: {ex.Message}");
            return null;
        }
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
            {
                AppLog.Warn($"[PirPeak] ReadPirPeakStatusAsync: bad response for {mac}: status={response.Status}");
                return null;
            }

            // GetDataFromBleDevice returns payload only (header already stripped).
            // 76-byte payload = 152 hex chars.
            var hexPayload = response.Data;

            if (hexPayload.Length < 152)
            {
                AppLog.Warn($"[PirPeak] ReadPirPeakStatusAsync: payload too short for {mac}: len={hexPayload.Length} (need 152)");
                return null;
            }

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
    /// Connects once, reads trigger levels, then polls PIR peak at <paramref name="intervalMs"/> until
    /// <paramref name="ct"/> is cancelled. Calls <paramref name="onSample"/> for each successful reading.
    /// Disconnects on exit.
    /// </summary>
    public async Task RunPirPeakSessionAsync(
        string mac,
        string pincode,
        int intervalMs,
        Func<PirPeakStatusData, Task> onSample,
        CancellationToken ct)
    {
        var chip = GetChipForMac(mac);
        try
        {
            var cl = await ConnectAndLoginWithRetryForPipelineAsync(
                _gatewayIpAddress, _gatewayPort, mac, pincode,
                logId: null, firmwareVersion: null,
                maxAttempts: 2, delayBetweenAttemptsMs: 1000).ConfigureAwait(false);

            if (!cl.Success)
            {
                AppLog.Warn($"[PirPeak] Session connect/login failed for {mac}: {cl.Message}");
                return;
            }

            // Read trigger levels and walktest state once after login
            var trig     = await ReadPirTriggerLevelsAsync(mac).ConfigureAwait(false);
            var walktest = await ReadWalktestStateAsync(mac).ConfigureAwait(false);

            AppLog.Info($"[PirPeak] Session started for {mac}, interval={intervalMs}ms, walktest={walktest}");

            while (!ct.IsCancellationRequested)
            {
                var data = await ReadPirPeakStatusAsync(mac, ct).ConfigureAwait(false);
                if (data != null)
                {
                    if (trig.HasValue)
                        data = data with { TrigA = trig.Value.A, TrigB = trig.Value.B, TrigC = trig.Value.C };
                    if (walktest.HasValue)
                        data = data with { WalktestActive = walktest };
                    await onSample(data).ConfigureAwait(false);
                }
                else
                    AppLog.Debug($"[PirPeak] No data from {mac} this cycle");

                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop — swallow
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[PirPeak] Session error for {mac}: {ex.Message}");
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
            AppLog.Info($"[PirPeak] Session stopped for {mac}");
        }
    }
}
