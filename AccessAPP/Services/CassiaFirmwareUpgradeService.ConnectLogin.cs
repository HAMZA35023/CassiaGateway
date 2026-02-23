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

        private sealed class LoginAttemptResult
        {
            public bool Success { get; init; }
            public string StatusText { get; init; } = "";
            public string Message { get; init; } = "";
            public dynamic? ResponseBody { get; init; }
        }

        private static readonly SemaphoreSlim Chip0ConnectLoginGate = new(1, 1);
        private static readonly SemaphoreSlim Chip1ConnectLoginGate = new(1, 1);

        private static int GetConnectAttemptTimeoutMs()
            => Math.Max(5000, RuntimeVariables.UPGRADE_CONNECT_ATTEMPT_TIMEOUT_MS);

        private static int GetConnectStabilizationDelayMs()
            // Keep a hard minimum settle delay before login after connect.
            => Math.Max(500, RuntimeVariables.UPGRADE_CONNECT_STABILIZATION_DELAY_MS);

        private static int GetConnectTransient500RetriesPerAttempt()
            => Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_TRANSIENT_500_RETRIES_PER_ATTEMPT);

        private static int GetConnectTransient500RetryDelayMs()
            => Math.Max(50, RuntimeVariables.UPGRADE_CONNECT_TRANSIENT_500_RETRY_DELAY_MS);

        private static int GetLoginAttemptTimeoutMs()
            => Math.Max(2000, RuntimeVariables.UPGRADE_LOGIN_ATTEMPT_TIMEOUT_MS);

        private static int GetLoginRetriesPerConnectedSession()
            => Math.Max(1, RuntimeVariables.UPGRADE_LOGIN_RETRIES_PER_CONNECTED_SESSION);

        private static int GetLoginRetryDelayMs()
            => Math.Max(100, RuntimeVariables.UPGRADE_LOGIN_RETRY_DELAY_MS);

        private static int GetGatewayStateChecksOn500()
        {
            int configured = RuntimeVariables.UPGRADE_CONNECT_GATEWAY_STATE_CHECK_ATTEMPTS_ON_500;
            if (configured > 0)
                return Math.Max(1, configured);

            return Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_GATEWAY_STATE_CHECK_ATTEMPTS);
        }

        private static int GetGatewayStateCheckDelayOn500Ms()
        {
            int configured = RuntimeVariables.UPGRADE_CONNECT_GATEWAY_STATE_CHECK_DELAY_MS_ON_500;
            if (configured > 0)
                return Math.Max(50, configured);

            return Math.Max(50, RuntimeVariables.UPGRADE_CONNECT_GATEWAY_STATE_CHECK_DELAY_MS);
        }

        private static int GetGatewayStateChecksOn500PreRetry()
            => Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_GATEWAY_STATE_CHECK_ATTEMPTS_ON_500_PRE_RETRY);

        private static int GetGatewayStateCheckDelayOn500PreRetryMs()
            => Math.Max(50, RuntimeVariables.UPGRADE_CONNECT_GATEWAY_STATE_CHECK_DELAY_MS_ON_500_PRE_RETRY);

        private static int GetGatewayStateInitialDelayOn500Ms()
            => Math.Max(0, RuntimeVariables.UPGRADE_CONNECT_GATEWAY_STATE_CHECK_INITIAL_DELAY_MS_ON_500);

        private static int GetGatewayStateInitialDelayOn500PreRetryMs()
            => Math.Max(0, RuntimeVariables.UPGRADE_CONNECT_GATEWAY_STATE_CHECK_INITIAL_DELAY_MS_ON_500_PRE_RETRY);

        private static bool ShouldSkipDisconnectAfterFailedConnect(HttpStatusCode connectStatus)
            => RuntimeVariables.UPGRADE_OPTIMIZE_RECONNECT_FLOW &&
               RuntimeVariables.UPGRADE_CONNECT_SKIP_DISCONNECT_ON_500 &&
               connectStatus == HttpStatusCode.InternalServerError;

        private static bool ShouldUsePerChipConnectGate()
            => RuntimeVariables.UPGRADE_CONNECT_LOGIN_USE_PER_CHIP_GATE;

        private static SemaphoreSlim GetConnectFlowGateForChip(int chip)
            => chip == 1 ? Chip1ConnectLoginGate : Chip0ConnectLoginGate;

        private static int CalculateRetryDelayMs(int requestedDelayMs, int attemptNumber, HttpStatusCode lastStatusCode = 0)
        {
            int delayMs = Math.Max(500, requestedDelayMs);
            if (lastStatusCode == HttpStatusCode.ExpectationFailed)
                delayMs += 2000;

            double multiplier = Math.Max(100, RuntimeVariables.UPGRADE_CONNECT_RETRY_BACKOFF_MULTIPLIER_X100) / 100.0;
            int n = Math.Max(1, attemptNumber);
            double scaled = delayMs * Math.Pow(multiplier, n - 1);

            int maxDelay = Math.Max(delayMs, RuntimeVariables.UPGRADE_CONNECT_RETRY_BACKOFF_MAX_MS);
            int withBackoff = (int)Math.Min(maxDelay, Math.Round(scaled));

            int extra = Math.Max(0, RuntimeVariables.UPGRADE_DELAY_AFTER_FAILED_CONNECT_MS);
            int withExtra = withBackoff + extra;

            int jitterPct = Math.Clamp(RuntimeVariables.UPGRADE_CONNECT_RETRY_JITTER_PCT, 0, 90);
            if (jitterPct <= 0)
                return withExtra;

            double jitterRange = withExtra * (jitterPct / 100.0);
            int jitter = (int)Math.Round((Random.Shared.NextDouble() * 2.0 - 1.0) * jitterRange);
            return Math.Max(200, withExtra + jitter);
        }

        private static string RetryDelayText(int delayMs)
            => $"{Math.Max(1, (int)Math.Ceiling(delayMs / 1000.0))}s";

        private static string LimitLogText(string? value, int maxLen = 180)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            string flat = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (flat.Length <= maxLen)
                return flat;

            return flat.Substring(0, maxLen) + "...";
        }

        private async Task<LoginAttemptResult> AttemptLoginOnConnectedSessionAsync(
            string gatewayIp,
            string macAddress,
            string? pincode,
            CancellationToken outerCt)
        {
            int attempts = GetLoginRetriesPerConnectedSession();
            int retryDelayMs = GetLoginRetryDelayMs();
            int timeoutMs = GetLoginAttemptTimeoutMs();

            string lastStatus = "";
            string lastMessage = "Login failed";

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    outerCt.ThrowIfCancellationRequested();
                    using var loginCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                    loginCts.CancelAfter(timeoutMs);

                    var loginResult = await _connectService
                        .AttemptLogin(gatewayIp, macAddress, loginCts.Token)
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
                    bool statusOk = string.Equals(statusText, "OK", StringComparison.OrdinalIgnoreCase);
                    bool pinOk = !pincodeReq || loginResult.ResponseBody.PinCodeAccepted;

                    if (statusOk && pinOk)
                    {
                        return new LoginAttemptResult
                        {
                            Success = true,
                            StatusText = statusText,
                            Message = "OK",
                            ResponseBody = loginResult.ResponseBody
                        };
                    }

                    lastStatus = statusText;
                    lastMessage = pincodeReq && !pinOk
                        ? "Pincode required/invalid."
                        : $"Status={statusText}";
                }
                catch (OperationCanceledException)
                {
                    if (outerCt.IsCancellationRequested)
                        throw;

                    lastStatus = "Timeout";
                    lastMessage = $"Login timed out after {timeoutMs / 1000}s.";
                }
                catch (Exception ex)
                {
                    lastStatus = "Exception";
                    lastMessage = ex.Message;
                }

                if (attempt < attempts)
                    await Task.Delay(retryDelayMs, outerCt).ConfigureAwait(false);
            }

            return new LoginAttemptResult
            {
                Success = false,
                StatusText = lastStatus,
                Message = lastMessage
            };
        }

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
                    if (node == null)
                        continue;

                    var nodeMac = NormalizeMac(node.bdaddrs?.Bdaddr);
                    if (string.IsNullOrWhiteSpace(nodeMac))
                        nodeMac = NormalizeMac(node.id);

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

        private async Task<bool> WaitForGatewayConnectedStateAsync(
            string macAddress,
            int expectedChip,
            int checks,
            int delayMs,
            CancellationToken ct = default)
        {
            if (!RuntimeVariables.UPGRADE_CONNECT_TRUST_GATEWAY_CONNECTED_STATE)
                return false;

            int actualChecks = Math.Max(1, checks);
            int actualDelayMs = Math.Max(50, delayMs);
            AppLog.Debug($"Gateway connected-state probe for {macAddress} chip {expectedChip}: checks={actualChecks}, delay={actualDelayMs}ms.");

            for (int i = 1; i <= actualChecks; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsMacReportedConnectedOnGatewayAsync(macAddress, expectedChip).ConfigureAwait(false))
                {
                    AppLog.Debug($"Gateway connected-state probe for {macAddress} chip {expectedChip}: connected on check {i}/{actualChecks}.");
                    return true;
                }

                if (i < actualChecks)
                    await Task.Delay(actualDelayMs, ct).ConfigureAwait(false);
            }

            AppLog.Debug($"Gateway connected-state probe for {macAddress} chip {expectedChip}: not connected after {actualChecks} checks.");
            return false;
        }

        private async Task<bool> WaitForGatewayConnectedStateAsync(string macAddress, int expectedChip, CancellationToken ct = default)
        {
            return await WaitForGatewayConnectedStateAsync(
                macAddress,
                expectedChip,
                Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_GATEWAY_STATE_CHECK_ATTEMPTS),
                Math.Max(50, RuntimeVariables.UPGRADE_CONNECT_GATEWAY_STATE_CHECK_DELAY_MS),
                ct).ConfigureAwait(false);
        }

        private async Task<bool> WaitForGatewayConnectedStateAfterConnectErrorAsync(
            string macAddress,
            int expectedChip,
            HttpStatusCode connectStatus,
            int connectTry,
            int maxConnectTries,
            CancellationToken ct = default)
        {
            if (connectStatus == HttpStatusCode.InternalServerError)
            {
                // On transient 500 before a same-attempt re-connect try, do only a very light
                // state probe to avoid hammering /gap/nodes.
                if (connectTry < maxConnectTries)
                {
                    int initialDelayMs = GetGatewayStateInitialDelayOn500PreRetryMs();
                    if (initialDelayMs > 0)
                    {
                        AppLog.Debug($"Connect state probe (pre-retry) for {macAddress} chip {expectedChip}: waiting {initialDelayMs}ms before first check.");
                        await Task.Delay(initialDelayMs, ct).ConfigureAwait(false);
                    }

                    int checks = GetGatewayStateChecksOn500PreRetry();
                    int delayMs = GetGatewayStateCheckDelayOn500PreRetryMs();
                    AppLog.Debug($"Connect state probe (pre-retry) for {macAddress} chip {expectedChip}: HTTP 500, checks={checks}, delay={delayMs}ms.");
                    bool connectedPreRetry = await WaitForGatewayConnectedStateAsync(
                        macAddress,
                        expectedChip,
                        checks,
                        delayMs,
                        ct).ConfigureAwait(false);
                    AppLog.Debug($"Connect state probe (pre-retry) result for {macAddress} chip {expectedChip}: connected={connectedPreRetry}.");
                    return connectedPreRetry;
                }

                int finalInitialDelayMs = GetGatewayStateInitialDelayOn500Ms();
                if (finalInitialDelayMs > 0)
                {
                    AppLog.Debug($"Connect state probe (final 500 check) for {macAddress} chip {expectedChip}: waiting {finalInitialDelayMs}ms before first check.");
                    await Task.Delay(finalInitialDelayMs, ct).ConfigureAwait(false);
                }

                int fullChecks = GetGatewayStateChecksOn500();
                int fullDelayMs = GetGatewayStateCheckDelayOn500Ms();
                AppLog.Debug($"Connect state probe (final 500 check) for {macAddress} chip {expectedChip}: checks={fullChecks}, delay={fullDelayMs}ms.");
                bool connectedAfterFinal500 = await WaitForGatewayConnectedStateAsync(
                    macAddress,
                    expectedChip,
                    fullChecks,
                    fullDelayMs,
                    ct).ConfigureAwait(false);
                AppLog.Debug($"Connect state probe (final 500 check) result for {macAddress} chip {expectedChip}: connected={connectedAfterFinal500}.");
                return connectedAfterFinal500;
            }

            bool connectedGeneric = await WaitForGatewayConnectedStateAsync(macAddress, expectedChip, ct).ConfigureAwait(false);
            AppLog.Debug($"Connect state probe after HTTP {(int)connectStatus} {connectStatus} for {macAddress} chip {expectedChip}: connected={connectedGeneric}.");
            return connectedGeneric;
        }


        private async Task<ConnectLoginResult> ConnectAndLoginWithRetryAsync(
            string gatewayIp,
            int gatewayPort,
            string macAddress,
            string? pincode,
            string? logId,
            string? firmwareVersion,
            int maxAttempts = 3,
            int delayBetweenAttemptsMs = 2000,
            bool bootModeIsRetryable = false)
        {
            Exception? lastEx = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                bool failedThisAttempt = false;
                int chip = GetChipForMac(macAddress);
                int timeoutMs = GetConnectAttemptTimeoutMs();
                HttpStatusCode lastAttemptStatus = 0;
                bool touchedGateway = false;
                var chipGate = GetConnectFlowGateForChip(chip);
                bool gateHeld = false;

                using var cts = new CancellationTokenSource(timeoutMs);

                try
                {
                    if (ShouldUsePerChipConnectGate())
                    {
                        await chipGate.WaitAsync(cts.Token).ConfigureAwait(false);
                        gateHeld = true;
                    }

                    UpgradeLogger.Log(logId, macAddress,
                        $"Connect+Login attempt {attempt}/{maxAttempts} (timeout {timeoutMs / 1000}s)",
                        "Info", firmwareVersion);

                    AppLog.Info($" Connect+Login attempt {attempt}/{maxAttempts} for {macAddress}");

                    // 1) Connect
                    bool connected = false;
                    HttpStatusCode connectStatus = 0;
                    int transient500Retries = RuntimeVariables.UPGRADE_OPTIMIZE_RECONNECT_FLOW
                        ? GetConnectTransient500RetriesPerAttempt()
                        : 1;

                    for (int connectTry = 1; connectTry <= transient500Retries; connectTry++)
                    {
                        touchedGateway = true;
                        var connectionResult = await _connectService
                            .ConnectToBleDevice(gatewayIp, gatewayPort, macAddress, chip: chip, ct: cts.Token)
                            .ConfigureAwait(false);

                        connectStatus = connectionResult.Status;
                        lastAttemptStatus = connectStatus;
                        string connectResponseData = LimitLogText(connectionResult.Data);
                        AppLog.Debug($"Connect+Login connect response for {macAddress} chip {chip}: HTTP {(int)connectStatus} {connectStatus} (attempt {attempt}/{maxAttempts}, try {connectTry}/{transient500Retries}). Body='{connectResponseData}'.");
                        connected = connectionResult.Status == HttpStatusCode.OK;
                        if (!connected)
                        {
                            connected = await WaitForGatewayConnectedStateAfterConnectErrorAsync(
                                macAddress,
                                chip,
                                connectionResult.Status,
                                connectTry,
                                transient500Retries,
                                cts.Token).ConfigureAwait(false);
                            if (connected)
                            {
                                UpgradeLogger.Log(logId, macAddress, "Connected",
                                    $"Recovered via gateway state (attempt {attempt}/{maxAttempts})",
                                    firmwareVersion);
                                AppLog.Info($"Connect returned {connectionResult.Status} for {macAddress}, but gateway reports connected on chip {chip}. Continuing.");
                            }
                        }

                        if (connected)
                            break;

                        bool canQuickRetry500 =
                            connectStatus == HttpStatusCode.InternalServerError &&
                            connectTry < transient500Retries;

                        if (!canQuickRetry500)
                            break;

                        int quickRetryDelay = GetConnectTransient500RetryDelayMs();
                        AppLog.Debug($"Connect transient 500 for {macAddress} (attempt {attempt}/{maxAttempts}, in-attempt retry {connectTry}/{transient500Retries}). Waiting {quickRetryDelay}ms.");
                        await Task.Delay(quickRetryDelay, cts.Token).ConfigureAwait(false);
                    }

                    if (!connected)
                    {
                        lastEx = new Exception($"Connect failed (HTTP {(int)connectStatus} {connectStatus}).");
                        failedThisAttempt = true;
                        int retryDelayMs = CalculateRetryDelayMs(delayBetweenAttemptsMs, attempt, connectStatus);
                        AppLog.Debug($"Connect+Login connect not established for {macAddress} on attempt {attempt}/{maxAttempts}. Retry delay={retryDelayMs}ms, lastStatus={(int)connectStatus} {connectStatus}.");
                        bool skipDisconnect = ShouldSkipDisconnectAfterFailedConnect(connectStatus);

                        if (touchedGateway && !skipDisconnect)
                            await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, chip).ConfigureAwait(false);
                        else if (touchedGateway && skipDisconnect)
                            AppLog.Debug($"Connect+Login connect failure for {macAddress}: skipping per-attempt disconnect because status={(int)connectStatus} {connectStatus}.");

                        string disconnectText = skipDisconnect ? "Skipped disconnect" : $"Disconnected chip {chip}";
                        UpgradeLogger.Log(logId, macAddress,
                            $"Connect+Login failed on attempt {attempt}/{maxAttempts}. {disconnectText}. Retrying after {RetryDelayText(retryDelayMs)}.",
                            "Warn", firmwareVersion);
                        AppLog.Info($"Connect+Login failed for {macAddress} (attempt {attempt}/{maxAttempts}). {disconnectText}; retrying after {RetryDelayText(retryDelayMs)}.");
                    }
                    else
                    {
                        int stabilizeMs = GetConnectStabilizationDelayMs();
                        if (stabilizeMs > 0)
                        {
                            AppLog.Debug($"Connect+Login connected for {macAddress} on chip {chip}. Waiting {stabilizeMs}ms before login.");
                            await Task.Delay(stabilizeMs, cts.Token).ConfigureAwait(false);
                        }

                        // Only check boot mode if BlueZ has finished GATT re-discovery
                        // (ServicesResolved=true). When ServicesResolved is false the
                        // ObjectManager may hold stale objects from the *previous* mode
                        // (e.g. bootloader GATT after a firmware reboot into app mode),
                        // which would cause a false-positive 409 that aborts ALL retries.
                        // When ServicesResolved is false we proceed to login; a genuine
                        // boot-mode device will fail login and we retry from there.
                        bool servicesReadyForBootCheck = true;
                        if (RuntimeVariables.BLE_BACKEND.Equals("linux-native", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var _dp = LinuxBle.BlueZHelpers.DevicePath(
                                    LinuxBle.BlueZHelpers.GetDeviceAdapter(macAddress), macAddress);
                                var _dev = await LinuxBle.BlueZHelpers.GetDeviceAsync(_dp).ConfigureAwait(false);
                                servicesReadyForBootCheck = await _dev.GetAsync<bool>("ServicesResolved").ConfigureAwait(false);
                            }
                            catch { servicesReadyForBootCheck = false; }

                            if (!servicesReadyForBootCheck)
                                AppLog.Debug($"Connect+Login: ServicesResolved=false for {macAddress} — skipping boot-mode check (stale GATT).");
                        }

                        bool isAlreadyInBootMode = servicesReadyForBootCheck && CheckIfDeviceInBootMode(_gatewayIpAddress, macAddress);
                        if (isAlreadyInBootMode && !bootModeIsRetryable)
                        {
                            if (touchedGateway)
                                await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0, chip: chip).ConfigureAwait(false);
                            return new ConnectLoginResult
                            {
                                Success = false,
                                StatusCode = 409, // Conflict
                                Message = "Device is in boot mode."
                            };
                        }

                        if (isAlreadyInBootMode) // bootModeIsRetryable=true: device still transitioning post-upgrade
                        {
                            // The device has not yet finished rebooting from bootloader into app mode.
                            // Treat this as a transient failure — allow the retry loop to continue
                            // instead of returning a fatal 409 that kills all remaining attempts.
                            lastEx = new Exception($"Device still in boot mode on attempt {attempt}/{maxAttempts} (post-upgrade transition).");
                            AppLog.Info($"Connect+Login: {macAddress} still in boot mode on attempt {attempt}/{maxAttempts}; waiting for app-mode transition.");
                            UpgradeLogger.Log(logId, macAddress,
                                $"Connect+Login: boot mode on attempt {attempt}/{maxAttempts}; device still transitioning, will retry.",
                                "Warn", firmwareVersion);
                            failedThisAttempt = true;
                            if (touchedGateway)
                                await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0, chip: chip).ConfigureAwait(false);
                        }
                        else
                        {
                            UpgradeLogger.Log(logId, macAddress, "Connected", "Success", firmwareVersion);

                            cts.Token.ThrowIfCancellationRequested();

                            // 2) Login
                            AppLog.Debug($"Connect+Login starting login for {macAddress} (attempt {attempt}/{maxAttempts}).");
                            var loginResult = await AttemptLoginOnConnectedSessionAsync(
                                gatewayIp,
                                macAddress,
                                pincode,
                                cts.Token).ConfigureAwait(false);

                            if (!loginResult.Success)
                            {
                                lastEx = new Exception($"Login failed ({loginResult.Message})");
                                failedThisAttempt = true;
                                int retryDelayMs = CalculateRetryDelayMs(delayBetweenAttemptsMs, attempt, lastAttemptStatus);
                                AppLog.Debug($"Connect+Login login failed for {macAddress} on attempt {attempt}/{maxAttempts}. Detail='{loginResult.Message}'. Retry delay={retryDelayMs}ms.");

                                if (touchedGateway)
                                    await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, chip).ConfigureAwait(false);

                                UpgradeLogger.Log(logId, macAddress,
                                    $"Connect+Login failed on attempt {attempt}/{maxAttempts}. Disconnected chip {chip}. Retrying after {RetryDelayText(retryDelayMs)}.",
                                    "Warn", firmwareVersion);
                                AppLog.Info($"Connect+Login failed for {macAddress} (attempt {attempt}/{maxAttempts}). Login detail: {loginResult.Message}. Disconnected chip {chip}; retrying after {RetryDelayText(retryDelayMs)}.");
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
                                    RawStatus = loginResult.StatusText
                                };
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    lastEx = new TimeoutException($"Connect+Login timed out after {timeoutMs / 1000} seconds");
                    failedThisAttempt = true;
                    if (lastAttemptStatus == 0)
                        lastAttemptStatus = HttpStatusCode.RequestTimeout;
                    int retryDelayMs = CalculateRetryDelayMs(delayBetweenAttemptsMs, attempt, lastAttemptStatus);
                    AppLog.Debug($"Connect+Login timeout for {macAddress} on attempt {attempt}/{maxAttempts}. Retry delay={retryDelayMs}ms.");

                    if (touchedGateway)
                    {
                        await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, chip).ConfigureAwait(false);

                        UpgradeLogger.Log(logId, macAddress,
                            $"Connect+Login timeout on attempt {attempt}/{maxAttempts}. Disconnected chip {chip}. Retrying after {RetryDelayText(retryDelayMs)}.",
                            "Warn", firmwareVersion);
                        AppLog.Info($"Connect+Login timeout for {macAddress} (attempt {attempt}/{maxAttempts}). Disconnected chip {chip}; retrying after {RetryDelayText(retryDelayMs)}.");
                    }
                    else
                    {
                        UpgradeLogger.Log(logId, macAddress,
                            $"Connect+Login timeout on attempt {attempt}/{maxAttempts} before gateway operation. Retrying after {RetryDelayText(retryDelayMs)}.",
                            "Warn", firmwareVersion);
                        AppLog.Info($"Connect+Login timeout for {macAddress} (attempt {attempt}/{maxAttempts}) before gateway operation; retrying after {RetryDelayText(retryDelayMs)}.");
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    failedThisAttempt = true;
                    int retryDelayMs = CalculateRetryDelayMs(delayBetweenAttemptsMs, attempt, lastAttemptStatus);
                    AppLog.Debug($"Connect+Login exception for {macAddress} on attempt {attempt}/{maxAttempts}. Retry delay={retryDelayMs}ms. Exception={ex.Message}");
                    UpgradeLogger.Log(logId, macAddress,
                        $"Connect+Login exception attempt {attempt}/{maxAttempts}: {ex.Message}",
                        "Warn", firmwareVersion);

                    if (touchedGateway)
                        await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, chip).ConfigureAwait(false);

                    if (touchedGateway)
                    {
                        UpgradeLogger.Log(logId, macAddress,
                            $"Connect+Login exception on attempt {attempt}/{maxAttempts}. Disconnected chip {chip}. Retrying after {RetryDelayText(retryDelayMs)}.",
                            "Warn", firmwareVersion);
                        AppLog.Info($"Connect+Login exception for {macAddress} (attempt {attempt}/{maxAttempts}). Disconnected chip {chip}; retrying after {RetryDelayText(retryDelayMs)}.");
                    }
                    else
                    {
                        UpgradeLogger.Log(logId, macAddress,
                            $"Connect+Login exception on attempt {attempt}/{maxAttempts} before gateway operation. Retrying after {RetryDelayText(retryDelayMs)}.",
                            "Warn", firmwareVersion);
                        AppLog.Info($"Connect+Login exception for {macAddress} (attempt {attempt}/{maxAttempts}) before gateway operation; retrying after {RetryDelayText(retryDelayMs)}.");
                    }
                }
                finally
                {
                    if (gateHeld)
                        chipGate.Release();
                }

                if (attempt < maxAttempts && failedThisAttempt)
                {
                    int retryDelayMs = CalculateRetryDelayMs(delayBetweenAttemptsMs, attempt, lastAttemptStatus);
                    await Task.Delay(retryDelayMs).ConfigureAwait(false);
                }
            }

            int finalChip = GetChipForMac(macAddress);
            await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, finalChip).ConfigureAwait(false);

            UpgradeLogger.Log(logId, macAddress,
                $"All Connect+Login attempts failed. Disconnected chip {finalChip}.",
                "Warn", firmwareVersion);
            AppLog.Info($"All Connect+Login attempts failed for {macAddress}. Disconnected chip {finalChip}.");

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
                bool logSuccess = true,
                int? discoverGattOverride = null)
        {
            HttpStatusCode last = 0;
            string lastMsg = "Connect failed";

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                bool failedThisAttempt = false;
                bool touchedGateway = false;
                int chip = GetChipForMac(macAddress);
                var chipGate = GetConnectFlowGateForChip(chip);
                bool gateHeld = false;

                try
                {
                    int timeoutMs = GetConnectAttemptTimeoutMs();
                    using var cts = new CancellationTokenSource(timeoutMs);

                    if (ShouldUsePerChipConnectGate())
                    {
                        await chipGate.WaitAsync(cts.Token).ConfigureAwait(false);
                        gateHeld = true;
                    }

                    bool connected = false;
                    int transient500Retries = RuntimeVariables.UPGRADE_OPTIMIZE_RECONNECT_FLOW
                        ? GetConnectTransient500RetriesPerAttempt()
                        : 1;

                    string connectData = "";
                    for (int connectTry = 1; connectTry <= transient500Retries; connectTry++)
                    {
                        touchedGateway = true;
                        var cr = await _connectService
                            .ConnectToBleDevice(_gatewayIpAddress, 80, macAddress, chip: chip, discoverGattOverride: discoverGattOverride, ct: cts.Token)
                            .ConfigureAwait(false);

                        last = cr.Status;
                        connectData = cr.Data ?? "";
                        string connectResponseData = LimitLogText(connectData);
                        AppLog.Debug($"{stageName}: connect response for {macAddress} chip {chip}: HTTP {(int)last} {last} (attempt {attempt}/{maxAttempts}, try {connectTry}/{transient500Retries}). Body='{connectResponseData}'.");
                        connected = cr.Status == HttpStatusCode.OK;
                        if (!connected)
                        {
                            connected = await WaitForGatewayConnectedStateAfterConnectErrorAsync(
                                macAddress,
                                chip,
                                cr.Status,
                                connectTry,
                                transient500Retries,
                                cts.Token).ConfigureAwait(false);
                            if (connected)
                            {
                                UpgradeLogger.Log(logId, macAddress, stageName, $"Recovered via gateway state (attempt {attempt}/{maxAttempts})", FirmwareVersion);
                                AppLog.Info($"{stageName}: connect returned {cr.Status} for {macAddress}, but gateway reports connected on chip {chip}. Continuing.");
                            }
                        }

                        if (connected)
                            break;

                        bool canQuickRetry500 =
                            last == HttpStatusCode.InternalServerError &&
                            connectTry < transient500Retries;

                        if (!canQuickRetry500)
                            break;

                        int quickRetryDelay = GetConnectTransient500RetryDelayMs();
                        AppLog.Debug($"{stageName}: transient 500 for {macAddress} (attempt {attempt}/{maxAttempts}, in-attempt retry {connectTry}/{transient500Retries}). Waiting {quickRetryDelay}ms.");
                        await Task.Delay(quickRetryDelay, cts.Token).ConfigureAwait(false);
                    }

                    if (connected)
                    {
                        int stabilizeMs = GetConnectStabilizationDelayMs();
                        if (stabilizeMs > 0)
                        {
                            AppLog.Debug($"{stageName}: connected for {macAddress} on chip {chip}. Waiting {stabilizeMs}ms settle.");
                            await Task.Delay(stabilizeMs, cts.Token).ConfigureAwait(false);
                        }

                        if (logSuccess)
                            UpgradeLogger.Log(logId, macAddress, stageName, $"Success (attempt {attempt}/{maxAttempts})", FirmwareVersion);
                        return (true, HttpStatusCode.OK, "OK");
                    }

                    UpgradeLogger.Log(logId, macAddress, stageName, $"Failed (attempt {attempt}/{maxAttempts})", FirmwareVersion);
                    lastMsg = $"Connect failed ({last}) {connectData}";
                    failedThisAttempt = true;
                    AppLog.Debug($"{stageName}: connect not established for {macAddress} on attempt {attempt}/{maxAttempts}. lastStatus={(int)last} {last}.");
                    bool skipDisconnect = ShouldSkipDisconnectAfterFailedConnect(last);

                    if (touchedGateway && !skipDisconnect)
                        await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, chip).ConfigureAwait(false);
                    else if (touchedGateway && skipDisconnect)
                        AppLog.Debug($"{stageName}: skipping per-attempt disconnect for {macAddress} because status={(int)last} {last}.");
                }
                catch (OperationCanceledException ex)
                {
                    last = HttpStatusCode.RequestTimeout;
                    lastMsg = ex.Message;
                    failedThisAttempt = true;

                    if (touchedGateway)
                        await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, chip).ConfigureAwait(false);

                    UpgradeLogger.Log(logId, macAddress, stageName, $"Timeout (attempt {attempt}/{maxAttempts})", FirmwareVersion);
                    AppLog.Debug($"{stageName}: timeout for {macAddress} on attempt {attempt}/{maxAttempts}.");
                }
                catch (Exception ex)
                {
                    UpgradeLogger.Log(logId, macAddress, stageName, $"Exception (attempt {attempt}/{maxAttempts}): {ex.Message}", FirmwareVersion);
                    lastMsg = ex.Message;
                    failedThisAttempt = true;
                    AppLog.Debug($"{stageName}: exception for {macAddress} on attempt {attempt}/{maxAttempts}: {ex.Message}");

                    if (touchedGateway)
                        await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, chip).ConfigureAwait(false);
                }
                finally
                {
                    if (gateHeld)
                        chipGate.Release();
                }

                if (attempt < maxAttempts && failedThisAttempt)
                {
                    int retryDelayMs = CalculateRetryDelayMs(delayMs, attempt, last);
                    AppLog.Debug($"{stageName}: retry delay for {macAddress} before attempt {attempt + 1}/{maxAttempts} is {retryDelayMs}ms (lastStatus={(int)last} {last}).");
                    await Task.Delay(retryDelayMs).ConfigureAwait(false);
                }
            }
            int finalChip2 = GetChipForMac(macAddress);
            await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 1, finalChip2).ConfigureAwait(false);

            return (false, last, lastMsg);
        }

    }
}
