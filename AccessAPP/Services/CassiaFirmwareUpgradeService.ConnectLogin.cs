using AccessAPP.Logging;
using AccessAPP.Services.HelperClasses;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAPP.Services
{
    public partial class CassiaFirmwareUpgradeService
    {
        private sealed class ConnectLoginResult
        {
            public bool Success { get; init; }
            public int StatusCode { get; init; }
            public string Message { get; init; } = "";
            public dynamic? LoginResponseBody { get; init; } // keep dynamic if you don't have a strong type here
            public string? RawStatus { get; init; }
        }

        private static int GetConnectAttemptTimeoutMs()
            => Math.Max(5000, RuntimeVariables.UPGRADE_CONNECT_ATTEMPT_TIMEOUT_MS);

        private static int GetConnectStabilizationDelayMs()
            => Math.Max(0, RuntimeVariables.UPGRADE_CONNECT_STABILIZATION_DELAY_MS);

        private async Task<bool> IsMacReportedConnectedOnGatewayAsync(string macAddress, int expectedChip)
        {
            if (!RuntimeVariables.UPGRADE_CONNECT_TRUST_GATEWAY_CONNECTED_STATE)
                return false;

            try
            {
                var connected = await _connectService
                    .GetConnectedBleDevices(_gatewayIpAddress, _gatewayPort)
                    .ConfigureAwait(false);

                if (connected?.nodes == null || connected.nodes.Count == 0)
                    return false;

                var targetMac = NormalizeMac(macAddress);
                foreach (var node in connected.nodes)
                {
                    if (node?.bdaddrs == null)
                        continue;

                    var nodeMac = NormalizeMac(node.bdaddrs.Bdaddr);
                    if (!string.Equals(nodeMac, targetMac, StringComparison.OrdinalIgnoreCase))
                        continue;

                    int nodeChip = node.chipId >= 0 ? node.chipId : node.chip;
                    if (expectedChip >= 0 && nodeChip >= 0 && nodeChip != expectedChip)
                        continue;

                    var state = node.connectionState ?? "";
                    if (string.IsNullOrWhiteSpace(state) || string.Equals(state, "connected", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Gateway connected-state check failed for {macAddress}: {ex.Message}");
            }

            return false;
        }

        private async Task<bool> WaitForGatewayConnectedStateAsync(string macAddress, int expectedChip, CancellationToken ct = default)
        {
            if (!RuntimeVariables.UPGRADE_CONNECT_TRUST_GATEWAY_CONNECTED_STATE)
                return false;

            int checks = Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_GATEWAY_STATE_CHECK_ATTEMPTS);
            int delayMs = Math.Max(50, RuntimeVariables.UPGRADE_CONNECT_GATEWAY_STATE_CHECK_DELAY_MS);

            for (int i = 1; i <= checks; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsMacReportedConnectedOnGatewayAsync(macAddress, expectedChip).ConfigureAwait(false))
                    return true;

                if (i < checks)
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }

            return false;
        }


        private async Task<ConnectLoginResult> ConnectAndLoginWithRetryAsync(
            string gatewayIp,
            int gatewayPort,
            string macAddress,
            string? pincode,
            string? logId,
            string? firmwareVersion,
            int maxAttempts = 3,
            int delayBetweenAttemptsMs = 2000)
        {
            Exception? lastEx = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                bool failedThisAttempt = false;
                int chip = GetChipForMac(macAddress);
                int timeoutMs = GetConnectAttemptTimeoutMs();

                using var cts = new CancellationTokenSource(timeoutMs);

                try
                {
                    UpgradeLogger.Log(logId, macAddress,
                        $"Connect+Login attempt {attempt}/{maxAttempts} (timeout {timeoutMs / 1000}s)",
                        "Info", firmwareVersion);

                    AppLog.Info($" Connect+Login attempt {attempt}/{maxAttempts} for {macAddress}");

                    // 1) Connect
                    var connectionResult = await _connectService
                        .ConnectToBleDevice(gatewayIp, gatewayPort, macAddress, chip: chip, ct: cts.Token)
                        .ConfigureAwait(false);

                    bool connected = connectionResult.Status == HttpStatusCode.OK;
                    if (!connected)
                    {
                        connected = await WaitForGatewayConnectedStateAsync(macAddress, chip, cts.Token).ConfigureAwait(false);
                        if (connected)
                        {
                            UpgradeLogger.Log(logId, macAddress, "Connected",
                                $"Recovered via gateway state (attempt {attempt}/{maxAttempts})",
                                firmwareVersion);
                            AppLog.Info($"Connect returned {connectionResult.Status} for {macAddress}, but gateway reports connected on chip {chip}. Continuing.");
                        }
                    }

                    if (!connected)
                    {
                        lastEx = new Exception($"Connect failed (HTTP {(int)connectionResult.Status} {connectionResult.Status}).");
                        failedThisAttempt = true;

                        await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, chip).ConfigureAwait(false);

                        UpgradeLogger.Log(logId, macAddress,
                            $"Connect+Login failed on attempt {attempt}/{maxAttempts}. Disconnected chip {chip}. Retrying after 3s.",
                            "Warn", firmwareVersion);
                        AppLog.Info($"Connect+Login failed for {macAddress} (attempt {attempt}/{maxAttempts}). Disconnected chip {chip}; retrying after 3s.");
                    }
                    else
                    {
                        int stabilizeMs = GetConnectStabilizationDelayMs();
                        if (stabilizeMs > 0)
                            await Task.Delay(stabilizeMs, cts.Token).ConfigureAwait(false);

                        bool isAlreadyInBootMode = CheckIfDeviceInBootMode(_gatewayIpAddress, macAddress);
                        if (isAlreadyInBootMode)
                        {
                            await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0, chip: chip).ConfigureAwait(false);
                            return new ConnectLoginResult
                            {
                                Success = false,
                                StatusCode = 409, // Conflict
                                Message = "Device is in boot mode."
                            };
                        }

                        UpgradeLogger.Log(logId, macAddress, "Connected", "Success", firmwareVersion);

                        cts.Token.ThrowIfCancellationRequested();

                        // 2) Login
                        var loginResult = await _connectService
                            .AttemptLogin(gatewayIp, macAddress, cts.Token)
                            .ConfigureAwait(false);

                        bool pincodeReq = loginResult.ResponseBody.PincodeRequired;
                        if (pincodeReq && !string.IsNullOrEmpty(pincode))
                        {
                            var checkPincodeResponse = await _cassiaPinCodeService
                                .CheckPincode(gatewayIp, macAddress, pincode)
                                .ConfigureAwait(false);

                            loginResult.ResponseBody = checkPincodeResponse.ResponseBody;
                            loginResult.ResponseBody.PincodeRequired = pincodeReq;
                        }

                        var statusText = loginResult.Status?.ToString() ?? "";

                        if (!string.Equals(statusText, "OK", StringComparison.OrdinalIgnoreCase))
                        {
                            lastEx = new Exception($"Login failed (Status={statusText}).");
                            failedThisAttempt = true;

                            await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, chip).ConfigureAwait(false);

                            UpgradeLogger.Log(logId, macAddress,
                                $"Connect+Login failed on attempt {attempt}/{maxAttempts}. Disconnected chip {chip}. Retrying after 3s.",
                                "Warn", firmwareVersion);
                            AppLog.Info($"Connect+Login failed for {macAddress} (attempt {attempt}/{maxAttempts}). Disconnected chip {chip}; retrying after 3s.");
                        }
                        else
                        {
                            UpgradeLogger.Log(logId, macAddress, "LoggedIn", "Success", firmwareVersion);

                            return new ConnectLoginResult
                            {
                                Success = true,
                                StatusCode = 200,
                                Message = "Connected + logged in",
                                LoginResponseBody = loginResult.ResponseBody,
                                RawStatus = statusText
                            };
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    lastEx = new TimeoutException($"Connect+Login timed out after {timeoutMs / 1000} seconds");
                    failedThisAttempt = true;

                    await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, chip).ConfigureAwait(false);

                    UpgradeLogger.Log(logId, macAddress,
                        $"Connect+Login timeout on attempt {attempt}/{maxAttempts}. Disconnected chip {chip}. Retrying after 3s.",
                        "Warn", firmwareVersion);
                    AppLog.Info($"Connect+Login timeout for {macAddress} (attempt {attempt}/{maxAttempts}). Disconnected chip {chip}; retrying after 3s.");
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    failedThisAttempt = true;
                    UpgradeLogger.Log(logId, macAddress,
                        $"Connect+Login exception attempt {attempt}/{maxAttempts}: {ex.Message}",
                        "Warn", firmwareVersion);
                    await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, chip).ConfigureAwait(false);
                    
                    UpgradeLogger.Log(logId, macAddress,
                        $"Connect+Login exception on attempt {attempt}/{maxAttempts}. Disconnected chip {chip}. Retrying after 3s.",
                        "Warn", firmwareVersion);
                    AppLog.Info($"Connect+Login exception for {macAddress} (attempt {attempt}/{maxAttempts}). Disconnected chip {chip}; retrying after 3s.");


                }

                if (attempt < maxAttempts && failedThisAttempt)
                {
                    int extraDelay = RuntimeVariables.UPGRADE_DELAY_AFTER_FAILED_CONNECT_MS;
                    int baseDelay = Math.Max(3000, delayBetweenAttemptsMs);
                    if (extraDelay > 0)
                        baseDelay += extraDelay;

                    await Task.Delay(baseDelay).ConfigureAwait(false);
                }
            }

            int finalChip = GetChipForMac(macAddress);
            await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, finalChip).ConfigureAwait(false);

            UpgradeLogger.Log(logId, macAddress,
                $"All Connect+Login attempts failed. Disconnected chip {finalChip}.",
                "Warn", firmwareVersion);
            AppLog.Info($"All Connect+Login attempts failed for {macAddress}. Disconnected chip {finalChip}.");
            await Task.Delay(3000).ConfigureAwait(false);

            return new ConnectLoginResult
            {
                Success = false,
                StatusCode = 500,
                Message = $"Connect+Login failed after retries. Last error: {lastEx?.Message}"
            };
        }

        async Task<(bool ok, HttpStatusCode code, string msg)> ConnectOnlyWithRetryAsync(
                int maxAttempts,
                int delayMs,
                string stageName,
                string macAddress,
                string FirmwareVersion,
                string logId,
                bool logSuccess = true)
        {
            HttpStatusCode last = 0;
            string lastMsg = "Connect failed";

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                bool failedThisAttempt = false;

                try
                {
                    int chip = GetChipForMac(macAddress);
                    int timeoutMs = GetConnectAttemptTimeoutMs();
                    using var cts = new CancellationTokenSource(timeoutMs);

                    var cr = await _connectService
                        .ConnectToBleDevice(_gatewayIpAddress, 80, macAddress, chip: chip, ct: cts.Token)
                        .ConfigureAwait(false);
                    last = cr.Status;

                    bool connected = cr.Status == HttpStatusCode.OK;
                    if (!connected)
                    {
                        connected = await WaitForGatewayConnectedStateAsync(macAddress, chip, cts.Token).ConfigureAwait(false);
                        if (connected)
                        {
                            UpgradeLogger.Log(logId, macAddress, stageName, $"Recovered via gateway state (attempt {attempt}/{maxAttempts})", FirmwareVersion);
                            AppLog.Info($"{stageName}: connect returned {cr.Status} for {macAddress}, but gateway reports connected on chip {chip}. Continuing.");
                        }
                    }

                    if (connected)
                    {
                        int stabilizeMs = GetConnectStabilizationDelayMs();
                        if (stabilizeMs > 0)
                            await Task.Delay(stabilizeMs, cts.Token).ConfigureAwait(false);

                        if (logSuccess)
                            UpgradeLogger.Log(logId, macAddress, stageName, $"Success (attempt {attempt}/{maxAttempts})", FirmwareVersion);
                        return (true, HttpStatusCode.OK, "OK");
                    }

                    UpgradeLogger.Log(logId, macAddress, stageName, $"Failed (attempt {attempt}/{maxAttempts})", FirmwareVersion);
                    lastMsg = $"Connect failed ({cr.Status}) {cr.Data}";
                    failedThisAttempt = true;

                    AppLog.Info($"{stageName}: failed connect for {macAddress} (attempt {attempt}/{maxAttempts}). Disconnecting chip {chip}.");
                    await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, chip).ConfigureAwait(false);
                    AppLog.Info($"{stageName}: disconnected chip {chip} for {macAddress} after failed attempt {attempt}/{maxAttempts}.");
                }
                catch (Exception ex)
                {
                    UpgradeLogger.Log(logId, macAddress, stageName, $"Exception (attempt {attempt}/{maxAttempts}): {ex.Message}", FirmwareVersion);
                    lastMsg = ex.Message;
                    failedThisAttempt = true;

                    int chip = GetChipForMac(macAddress);
                    AppLog.Info($"{stageName}: exception for {macAddress} (attempt {attempt}/{maxAttempts}). Disconnecting chip {chip}.");
                    await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, chip).ConfigureAwait(false);
                    AppLog.Info($"{stageName}: disconnected chip {chip} for {macAddress} after exception attempt {attempt}/{maxAttempts}.");
                }

                // extra cooldown for 417 right after boot transitions
                if (attempt < maxAttempts && failedThisAttempt)
                {
                    int waitMs = (int)last == 417 ? delayMs + 4000 : delayMs;
                    int extraDelay = RuntimeVariables.UPGRADE_DELAY_AFTER_FAILED_CONNECT_MS;
                    int baseDelay = Math.Max(3000, waitMs);
                    if (extraDelay > 0)
                        baseDelay += extraDelay;

                    await Task.Delay(baseDelay).ConfigureAwait(false);
                }
            }
            int finalChip2 = GetChipForMac(macAddress);
            await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, finalChip2).ConfigureAwait(false);

            await Task.Delay(5000).ConfigureAwait(false);

            return (false, last, lastMsg);
        }

    }
}
