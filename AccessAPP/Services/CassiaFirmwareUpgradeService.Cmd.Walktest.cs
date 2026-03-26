using AccessAPP.Logging;
using System.Net;

namespace AccessAPP.Services;

public partial class CassiaFirmwareUpgradeService
{
    // cfg_system_common_func1 is byte index 4 in the user config.
    // Bit 2 (0x04) = walktest_active, Bit 0 (0x01) = walktest_multicolor_leds.
    private const int UserConfigWalktestByteIndex = 4;
    private const byte UserConfigWalktestMask = 0x05;

    /// <summary>
    /// Connects to a sensor, reads user config, sets or clears the walktest bits, writes back,
    /// then disconnects. Returns (mac, success, error).
    /// </summary>
    public async Task<(string Mac, bool Success, string? Error)> SetWalktestForMacAsync(
        string mac,
        bool enabled,
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
                return (mac, false, $"connect/login failed: {cl.Message}");

            // Read current user config
            var hexCurrent = await GetUserConfig(mac).ConfigureAwait(false);
            if (string.IsNullOrEmpty(hexCurrent) || hexCurrent.Length < (UserConfigWalktestByteIndex + 1) * 2)
                return (mac, false, "user config read failed or too short");

            // Modify the walktest byte in-place
            var bytes = Convert.FromHexString(hexCurrent);
            if (enabled)
                bytes[UserConfigWalktestByteIndex] |= UserConfigWalktestMask;
            else
                bytes[UserConfigWalktestByteIndex] = (byte)(bytes[UserConfigWalktestByteIndex] & ~UserConfigWalktestMask);

            var hexNew = Convert.ToHexString(bytes);
            var ok = await SetUserConfig(mac, hexNew).ConfigureAwait(false);

            if (!ok)
                return (mac, false, "user config write failed");

            AppLog.Info($"[Walktest] {mac}: walktest {(enabled ? "enabled" : "disabled")}");
            return (mac, true, null);
        }
        catch (OperationCanceledException)
        {
            return (mac, false, "cancelled");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[Walktest] SetWalktestForMacAsync exception for {mac}: {ex.Message}");
            return (mac, false, ex.Message);
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
