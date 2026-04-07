using AccessAPP.Logging;
using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAPP.Services
{
    public partial class CassiaFirmwareUpgradeService
    {
        internal async Task<string> GetFwVersionOnConnectedSessionAsync(
            string macAddress,
            string pincode,
            string? logId = null,
            string? firmwareVersion = null)
        {
            try
            {
                int loginAttempts = Math.Max(1, RuntimeVariables.UPGRADE_LOGIN_RETRIES_PER_CONNECTED_SESSION);
                int loginDelayMs = Math.Max(100, RuntimeVariables.UPGRADE_LOGIN_RETRY_DELAY_MS);
                int loginTimeoutMs = Math.Max(500, RuntimeVariables.UPGRADE_PRECHECK_LOGIN_ATTEMPT_TIMEOUT_MS);
                int settleBeforeLoginMs = Math.Max(0, RuntimeVariables.UPGRADE_PRECHECK_LOGIN_SETTLE_DELAY_MS);

                bool loggedIn = false;
                for (int loginAttempt = 1; loginAttempt <= loginAttempts; loginAttempt++)
                {
                    try
                    {
                        if (loginAttempt == 1 && settleBeforeLoginMs > 0)
                        {
                            AppLog.Debug($"FW precheck login settle for {macAddress}: waiting {settleBeforeLoginMs}ms before login.");
                            await Task.Delay(settleBeforeLoginMs).ConfigureAwait(false);
                        }

                        using var loginCts = new CancellationTokenSource(loginTimeoutMs);
                        var loginResult = await _connectService
                            .AttemptLogin(_gatewayIpAddress, macAddress, loginCts.Token)
                            .ConfigureAwait(false);

                        bool pinReq = loginResult.ResponseBody.PincodeRequired;
                        if (pinReq && !string.IsNullOrEmpty(pincode))
                        {
                            var check = await _cassiaPinCodeService.CheckPincode(_gatewayIpAddress, macAddress, pincode).ConfigureAwait(false);
                            loginResult.ResponseBody = check.ResponseBody;
                            loginResult.ResponseBody.PincodeRequired = pinReq;
                        }

                        var statusText = loginResult.Status?.ToString() ?? "";
                        bool statusOk = string.Equals(statusText, "OK", StringComparison.OrdinalIgnoreCase);
                        bool pinOk = !pinReq || loginResult.ResponseBody.PinCodeAccepted;

                        if (statusOk && pinOk)
                        {
                            loggedIn = true;
                            AppLog.Debug($"FW precheck login succeeded for {macAddress} on attempt {loginAttempt}/{loginAttempts}. pinRequired={pinReq}.");
                            UpgradeLogger.Log(logId ?? "", macAddress, "LoggedIn (precheck FW read)", $"Success (attempt {loginAttempt}/{loginAttempts})", firmwareVersion ?? "");
                            break;
                        }

                        var failureReason = pinReq && !pinOk
                            ? "pincode required/invalid"
                            : $"status={statusText}";
                        AppLog.Debug($"FW precheck login failed for {macAddress} on attempt {loginAttempt}/{loginAttempts}: {failureReason}.");
                        UpgradeLogger.Log(logId ?? "", macAddress, "LoggedIn (precheck FW read)", $"Failed (attempt {loginAttempt}/{loginAttempts}) - {failureReason}", firmwareVersion ?? "");

                        if (loginAttempt == 1 && string.Equals(statusText, "Error", StringComparison.OrdinalIgnoreCase))
                        {
                            AppLog.Debug($"FW precheck login returned Status=Error on first attempt for {macAddress}; short-circuiting retries to force reconnect.");
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        UpgradeLogger.Log(logId ?? "", macAddress, "LoggedIn (precheck FW read)", $"Timeout (attempt {loginAttempt}/{loginAttempts}, {loginTimeoutMs / 1000}s)", firmwareVersion ?? "");
                    }
                    catch (Exception ex)
                    {
                        UpgradeLogger.Log(logId ?? "", macAddress, "LoggedIn (precheck FW read)", $"Exception (attempt {loginAttempt}/{loginAttempts}): {ex.Message}", firmwareVersion ?? "");
                        AppLog.Warn($"Login precheck attempt {loginAttempt}/{loginAttempts} failed for {macAddress}: {ex.Message}");
                    }

                    if (loginAttempt < loginAttempts)
                        await Task.Delay(loginDelayMs).ConfigureAwait(false);
                }

                if (!loggedIn)
                    return "";

                int delayBeforeReadMs = Math.Max(0, RuntimeVariables.UPGRADE_PRECHECK_DELAY_AFTER_LOGIN_BEFORE_FW_READ_MS);
                if (delayBeforeReadMs > 0)
                {
                    AppLog.Debug($"FW precheck read delay for {macAddress}: waiting {delayBeforeReadMs}ms after login before first FW read.");
                    await Task.Delay(delayBeforeReadMs).ConfigureAwait(false);
                }

                int fwReadAttempts = Math.Max(1, RuntimeVariables.UPGRADE_FW_READ_ATTEMPTS);
                int fwReadRetryDelayMs = Math.Max(100, RuntimeVariables.UPGRADE_FW_READ_RETRY_DELAY_MS);
                for (int attempt = 1; attempt <= fwReadAttempts; attempt++)
                {
                    try
                    {
                        var fw = await ReadFirmwareVersionAsync(macAddress).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(fw))
                        {
                            AppLog.Debug($"FW precheck read succeeded for {macAddress} on attempt {attempt}/{fwReadAttempts}.");
                            UpgradeLogger.Log(logId ?? "", macAddress, "FW Read (precheck)", $"Success (attempt {attempt}/{fwReadAttempts})", firmwareVersion ?? "");
                            return fw;
                        }

                        AppLog.Debug($"FW precheck read returned empty for {macAddress} on attempt {attempt}/{fwReadAttempts}.");
                        UpgradeLogger.Log(logId ?? "", macAddress, "FW Read (precheck)", $"Failed (attempt {attempt}/{fwReadAttempts})", firmwareVersion ?? "");
                    }
                    catch (Exception ex)
                    {
                        UpgradeLogger.Log(logId ?? "", macAddress, "FW Read (precheck)", $"Exception (attempt {attempt}/{fwReadAttempts}): {ex.Message}", firmwareVersion ?? "");
                        AppLog.Warn($"FW read precheck attempt {attempt}/{fwReadAttempts} failed for {macAddress}: {ex.Message}");
                    }

                    if (attempt < fwReadAttempts)
                        await Task.Delay(fwReadRetryDelayMs).ConfigureAwait(false);
                }

                return "";
            }
            catch (Exception ex)
            {
                AppLog.Error($" GetFwVersionOnConnectedSession exception for {macAddress}: {ex}");
                UpgradeLogger.Log(logId ?? "", macAddress, "LoggedIn/FW Read (precheck)", $"Exception: {ex.Message}", firmwareVersion ?? "");
                return "";
            }
        }

        internal async Task<string> ReadFirmwareVersionAsync(string macAddress)
        {
            string sensorInfo = await ReadFirmwarePartWithRetryAsync(macAddress, "Sensor", "01290107005A5E").ConfigureAwait(false);
            string actorInfo = await ReadFirmwarePartWithRetryAsync(macAddress, "Actor", "012B01070032B3").ConfigureAwait(false);

            AppLog.Info($"{macAddress} - Get this Version: Sensor: {sensorInfo} | Actor: {actorInfo}");
            if (string.IsNullOrWhiteSpace(sensorInfo) && string.IsNullOrWhiteSpace(actorInfo))
                return "";
            return $"Sensor: {sensorInfo} | Actor: {actorInfo}";
        }

        private async Task<string> ReadFirmwarePartWithRetryAsync(string macAddress, string partName, string commandHex)
        {
            int attempts = Math.Max(1, RuntimeVariables.UPGRADE_FW_COMMAND_RETRY_ATTEMPTS);
            int delayMs = Math.Max(100, RuntimeVariables.UPGRADE_FW_COMMAND_RETRY_DELAY_MS);

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    var response = await _connectService
                        .GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, macAddress, commandHex)
                        .ConfigureAwait(false);

                    if (response.Status == HttpStatusCode.OK && !string.IsNullOrEmpty(response.Data))
                        return ScanDataParser.ParseSoftwareVersionFromResponse(response.Data);

                    AppLog.Warn($"FW read {partName} failed for {macAddress} (attempt {attempt}/{attempts}): status={response.Status}, data={response.Data}");
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"FW read {partName} exception for {macAddress} (attempt {attempt}/{attempts}): {ex.Message}");
                }

                if (attempt < attempts)
                    await Task.Delay(delayMs).ConfigureAwait(false);
            }

            return "";
        }

        public async Task<string> GetFwVersion(
            string macAddress,
            string pincode,
            bool disconnect_on_finish = false,
            string? logId = null,
            string? firmwareVersion = null,
            int? maxConnectLoginAttempts = null)
        {
            try
            {
                int connectLoginAttempts = maxConnectLoginAttempts.HasValue
                    ? Math.Max(1, maxConnectLoginAttempts.Value)
                    : Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS);

                var cl = await ConnectAndLoginWithRetryAsync(
                    _gatewayIpAddress, 80, macAddress, pincode, logId, firmwareVersion,
                    maxAttempts: connectLoginAttempts,
                    delayBetweenAttemptsMs: 2000).ConfigureAwait(false);
                if (!cl.Success)
                {
                    AppLog.Warn($" Connect+login failed for {macAddress}: {cl.Message}");
return "";
                }
                else
                {
                    return await ReadFirmwareVersionAsync(macAddress).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($" GetFwVersion exception for {macAddress}: {ex}");
}
            finally
            {
                if (disconnect_on_finish)
                {
					await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, chip: GetChipForMac(macAddress));
                }
            }
            return "";
        }

        /// <summary>
        /// Disconnect a device from the Cassia gateway (best-effort).
        /// Intended for MQTT command "disconnect-devices".
        /// </summary>
    }
}
