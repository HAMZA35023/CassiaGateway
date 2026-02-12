using AccessAPP.Logging;
using AccessAPP.Services.HelperClasses;
using System;
using System.Net;
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

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                try
                {
                    UpgradeLogger.Log(logId, macAddress,
                        $"Connect+Login attempt {attempt}/{maxAttempts} (timeout 10s)",
                        "Info", firmwareVersion);

                    AppLog.Info($" Connect+Login attempt {attempt}/{maxAttempts} for {macAddress}");
// ---- Run connect + login with timeout ----
                    var attemptTask = Task.Run(async () =>
                    {
                        // 1) Connect
                        var connectionResult = await _connectService
							.ConnectToBleDevice(gatewayIp, gatewayPort, macAddress, chip: GetChipForMac(macAddress))
                            .ConfigureAwait(false);

                        if (connectionResult.Status != HttpStatusCode.OK)
                        {
                            return new ConnectLoginResult
                            {
                                Success = false,
                                StatusCode = (int)connectionResult.Status,
                                Message = $"Connect failed (HTTP {(int)connectionResult.Status} {connectionResult.Status})."
                            };
                        }

                        cts.Token.ThrowIfCancellationRequested();

                        bool isAlreadyInBootMode = CheckIfDeviceInBootMode(_gatewayIpAddress, macAddress);
                        if (isAlreadyInBootMode)
                        {
							await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0, chip: GetChipForMac(macAddress));
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
                            return new ConnectLoginResult
                            {
                                Success = false,
                                StatusCode = 401,
                                Message = $"Login failed (Status={statusText}).",
                                LoginResponseBody = loginResult.ResponseBody,
                                RawStatus = statusText
                            };
                        }

                        UpgradeLogger.Log(logId, macAddress, "LoggedIn", "Success", firmwareVersion);

                        return new ConnectLoginResult
                        {
                            Success = true,
                            StatusCode = 200,
                            Message = "Connected + logged in",
                            LoginResponseBody = loginResult.ResponseBody,
                            RawStatus = statusText
                        };
                    }, cts.Token);

                    var completed = await Task.WhenAny(attemptTask, Task.Delay(Timeout.Infinite, cts.Token));

                    if (completed != attemptTask)
                        throw new TimeoutException("Connect+Login timed out after 10 seconds");

                    var result = await attemptTask.ConfigureAwait(false);
                    if (result.Success)
                        return result;

                    lastEx = new Exception(result.Message);
                    failedThisAttempt = true;

                    int chip = GetChipForMac(macAddress);
                    await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, chip).ConfigureAwait(false);

                    UpgradeLogger.Log(logId, macAddress,
                        $"Connect+Login failed on attempt {attempt}/{maxAttempts}. Disconnected chip {chip}. Retrying after 3s.",
                        "Warn", firmwareVersion);
                    AppLog.Info($"Connect+Login failed for {macAddress} (attempt {attempt}/{maxAttempts}). Disconnected chip {chip}; retrying after 3s.");
                }
                catch (OperationCanceledException)
                {
                    lastEx = new TimeoutException("Connect+Login timed out after 10 seconds");
                    failedThisAttempt = true;

                    int chip = GetChipForMac(macAddress);
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
                    int chip = GetChipForMac(macAddress);
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
                    var cr = await _connectService.ConnectToBleDevice(_gatewayIpAddress, 80, macAddress, chip: GetChipForMac(macAddress)).ConfigureAwait(false);
                    last = cr.Status;

                    if (cr.Status == HttpStatusCode.OK)
                    {
                        if (logSuccess)
                            UpgradeLogger.Log(logId, macAddress, stageName, $"Success (attempt {attempt}/{maxAttempts})", FirmwareVersion);
                        return (true, cr.Status, "OK");
                    }

                    UpgradeLogger.Log(logId, macAddress, stageName, $"Failed (attempt {attempt}/{maxAttempts})", FirmwareVersion);
                    lastMsg = $"Connect failed ({cr.Status})";
                    failedThisAttempt = true;

                    int chip = GetChipForMac(macAddress);
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
