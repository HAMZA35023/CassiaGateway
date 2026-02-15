using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using AccessAPP.Services.UpgradeCore;
using AccessAPP.Services.UpgradePipeline;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAPP.Services
{
    public partial class CassiaFirmwareUpgradeService
    {
		internal async Task ProcessSingleDeviceUpgradeAsync(
		    UpgradeProgress dev,
		    ChipAllocationManager.ChipLease chipLease,
		    ConcurrentBag<DeviceUpgradeSummary> summaries,
		    Action<long> addDeviceMs)
        {
            var deviceSw = Stopwatch.StartNew();
            var mac = NormalizeMac(dev.MacAddress);

            // --- CLAIM MAC (prevents double-upgrade anywhere in the app) ---
            if (!_macsInProgress.TryAdd(mac, 0))
            {
                AppLog.Info($"[SKIP] {mac} already upgrading in another task");
deviceSw.Stop();

		        // Release chip lease for skipped items.
		        chipLease.Dispose();

                summaries.Add(UpgradeSummaryFactory.Create(
                    mac: mac,
                    dev: dev,
                    seconds: deviceSw.Elapsed.TotalSeconds,
                    status: "SKIPPED"));

		        return;
            }

		    // Bind this MAC to a Cassia BLE chip for the duration of the upgrade.
		    _chipManager.BindMacToChip(mac, chipLease.Chip);
		    CassiaChipManager.SetChip(mac, chipLease.Chip);
		        AppLog.Info($"[CHIP] {mac} assigned chip={chipLease.Chip}");
Interlocked.Increment(ref UpgradeDevicesInProgress);

            
// Track programming list for MQTT (mac + target fw)
_programmingTargets[mac] = (dev.DetectotType ?? "", dev.FirmwareVersion ?? "");
try
            {
      
                    AppLog.Debug($"[{DateTime.Now:HH:mm:ss.fff}][T{Environment.CurrentManagedThreadId}] " +
                        System.Text.Json.JsonSerializer.Serialize(dev));

                string logId = $"{mac.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";

                // Probe-connect first, then detect boot/app mode (same principle as the upgrade pipeline).
                // This avoids stale/false-negative mode checks before a BLE session is established.
                var probeConnected = false;
                var precheckSessionAlive = false;
                var probe = await ConnectOnlyWithRetryAsync_Internal(
                    maxAttempts: Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS),
                    delayMs: 2000,
                    stageName: "Connected (precheck probe)",
                    macAddress: mac,
                    firmwareVersion: dev.FirmwareVersion,
                    logId: logId,
                    logSuccess: true
                ).ConfigureAwait(false);

                probeConnected = probe.ok;
                precheckSessionAlive = probeConnected;
                var autoForceFromBootMode = false;
                var isInBootMode = false;
                if (probeConnected)
                {
                    const int bootModeChecks = 5;
                    for (int attempt = 1; attempt <= bootModeChecks; attempt++)
                    {
                        if (CheckIfDeviceInBootMode(_gatewayIpAddress, mac))
                        {
                            isInBootMode = true;
                            break;
                        }

                        await Task.Delay(1500).ConfigureAwait(false);
                    }
                }

                if (isInBootMode && !dev.ForceUpdate)
                {
                    dev.ForceUpdate = true;
                    autoForceFromBootMode = true;
                    AppLog.Info($"[{mac}] Device in bootloader mode -> ForceUpdate auto-enabled.");
                }
                if (!isInBootMode)
                {
                    // Connected-session read + reconnect fallback already have internal retries.
                    // Keep the outer loop to a single pass to avoid repeated reconnect storms.
                    const int firmwareReadAttempts = 1;
                    for (int i = 1; i <= firmwareReadAttempts; i++)
                    {
                        if (probeConnected)
                        {
                            // Reuse the existing probe connection/session instead of reconnecting.
                            dev.CurrentFirmwareVersion = await GetFwVersionOnConnectedSessionAsync(
                                mac,
                                dev.Pincode,
                                logId,
                                dev.FirmwareVersion).ConfigureAwait(false);

                            // If connected-session read failed, force a reconnect+login read attempt.
                            if (string.IsNullOrWhiteSpace(dev.CurrentFirmwareVersion))
                            {
                                UpgradeLogger.Log(logId, mac, "FW Read (precheck)", "Connected-session read failed; retry via reconnect+login", dev.FirmwareVersion);
                                try
                                {
                                    await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, mac, 0, chip: GetChipForMac(mac)).ConfigureAwait(false);
                                    precheckSessionAlive = false;
                                }
                                catch
                                {
                                    // best-effort disconnect before reconnect flow
                                }

                                dev.CurrentFirmwareVersion = await GetFwVersion(mac, dev.Pincode, disconnect_on_finish: true).ConfigureAwait(false);
                                precheckSessionAlive = false;
                            }
                        }
                        else
                        {
                            // Fallback only when probe connect was not available.
                            dev.CurrentFirmwareVersion = await GetFwVersion(mac, dev.Pincode, disconnect_on_finish: true).ConfigureAwait(false);
                            precheckSessionAlive = false;
                        }
                        if (!string.IsNullOrWhiteSpace(dev.CurrentFirmwareVersion))
                            break;
                       
                        await Task.Delay(2000).ConfigureAwait(false);
                    }

                    // Fallback: boot mode detection can occasionally be stale/false-negative.
                    // If FW read still failed, re-check boot mode before declaring NoFwRead.
                    if (string.IsNullOrWhiteSpace(dev.CurrentFirmwareVersion))
                    {
                        const int bootRecheckAttempts = 5;
                        for (int attempt = 1; attempt <= bootRecheckAttempts; attempt++)
                        {
                            if (CheckIfDeviceInBootMode(_gatewayIpAddress, mac))
                            {
                                isInBootMode = true;
                                if (!dev.ForceUpdate)
                                {
                                    dev.ForceUpdate = true;
                                    autoForceFromBootMode = true;
                                }
                                AppLog.Info($"[{mac}] Boot mode detected on fallback check (attempt {attempt}/{bootRecheckAttempts}).");
                                break;
                            }

                            await Task.Delay(1500).ConfigureAwait(false);
                        }
                    }
                }

                if (probeConnected && !precheckSessionAlive)
                {
                    // Session already closed in precheck fallback path; avoid redundant disconnect call.
                }
                else if (probeConnected && !(RuntimeVariables.UPGRADE_OPTIMIZE_RECONNECT_FLOW && precheckSessionAlive && !isInBootMode))
                {
                    try
                    {
                        await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, mac, 0, chip: GetChipForMac(mac)).ConfigureAwait(false);
                        precheckSessionAlive = false;
                    }
                    catch
                    {
                        // best-effort disconnect after precheck probe
                    }
                }
                else if (probeConnected && precheckSessionAlive && RuntimeVariables.UPGRADE_OPTIMIZE_RECONNECT_FLOW && !isInBootMode)
                {
                    UpgradeLogger.Log(logId, mac, "Connected (precheck probe)", "Keeping session open for next pipeline step", dev.FirmwareVersion);
                }

                dev.PrecheckSessionAlive = precheckSessionAlive;
                dev.PrecheckBootMode = isInBootMode;

                if (autoForceFromBootMode)
                    UpgradeLogger.Log(logId, mac, "FW precheck", "Device is in bootloader; ForceUpdate auto-enabled.", dev.FirmwareVersion);
                UpgradeLogger.Log(logId, mac, "Current FW Version:", dev.CurrentFirmwareVersion, dev.FirmwareVersion);

                var canProceedWithUpgrade = true;
                if (!isInBootMode && string.IsNullOrWhiteSpace(dev.CurrentFirmwareVersion))
                {
                    // Do not hard-fail precheck on unreadable FW.
                    // In field conditions this can be transient or a boot-mode detection false negative.
                    // Force update path is safer than stopping the run with NoFwRead.
                    if (!dev.ForceUpdate)
                    {
                        dev.ForceUpdate = true;
                        dev.LastFailureReason = "Current FW could not be read while device is not in bootloader. ForceUpdate auto-enabled; continuing.";
                    }
                    else
                    {
                        dev.LastFailureReason = "Current FW could not be read while device is not in bootloader. Continuing because ForceUpdate=true.";
                    }

                    UpgradeLogger.Log(logId, mac, "FW precheck", dev.LastFailureReason, dev.FirmwareVersion);
                    AppLog.Warn($"[{mac}] {dev.LastFailureReason}");
                }

                dev.RetryCount = 0;
                dev.RetryCountActor = 0;
                dev.RetryCountBootloader = 0;
                dev.RetryCountSensor = 0;

                if (canProceedWithUpgrade)
                {
                    AppLog.Info($"[START] {mac}");
                    var decisions = UpgradeDecisionCalculator.Compute(dev);

                    dev.upgradeBootloader = decisions.UpgradeBootloader;
                    dev.upgradeSensor = decisions.UpgradeSensor;
                    if (!dev.upgradeSensor)
                    {
                        UpgradeLogger.Log(logId, mac, "Sensor upgrade skipped (FW already matches target)", "Info", dev.FirmwareVersion);
                        AppLog.Info($"[SKIP] Sensor upgrade for {mac} - current matches target and ForceUpdate=false");
                    }

                    dev.isActorUpgradeNeeded = decisions.ActorUpgradeNeeded;
                    if (decisions.IsDaliMaster && !dev.isActorUpgradeNeeded)
                    {
                        UpgradeLogger.Log(logId, mac, "Actor upgrade skipped (FW already matches target)", "Info", dev.FirmwareVersion);
                        AppLog.Info($"[SKIP] Actor upgrade for {mac} - current matches target and ForceUpdate=false");
                    }

                    // If the sensor is already in boot mode and an actor upgrade is needed,
                    // we must restore the sensor to application mode first. Force bootloader + sensor update.
                    if (dev.PrecheckBootMode && dev.isActorUpgradeNeeded)
                    {
                        bool forced = false;
                        if (!dev.upgradeBootloader)
                        {
                            dev.upgradeBootloader = true;
                            forced = true;
                        }
                        if (!dev.upgradeSensor)
                        {
                            dev.upgradeSensor = true;
                            forced = true;
                        }

                        if (forced)
                        {
                            UpgradeLogger.Log(
                                logId,
                                mac,
                                "Boot mode recovery",
                                "Sensor in boot mode while actor upgrade needed -> forcing bootloader + sensor update",
                                dev.FirmwareVersion);
                            AppLog.Warn($"[{mac}] Sensor in boot mode while actor upgrade needed -> forcing bootloader + sensor update.");
                        }
                    }

                    // Requirements for success reporting
                    dev.requiresConfigRestore = decisions.RequiresConfigRestore;
                    dev.requires102Restore = decisions.Requires102Restore;

                    bool IsActorBlockedByBootMode(ServiceResponse resp)
                    {
                        if (resp == null || resp.Success)
                            return false;
                        if (resp.StatusCode != (int)HttpStatusCode.Conflict)
                            return false;
                        return resp.Message?.Contains("Sensor is already in boot mode", StringComparison.OrdinalIgnoreCase) == true;
                    }

                    void ForceBootModeRecoveryIfNeeded(ServiceResponse resp)
                    {
                        if (!IsActorBlockedByBootMode(resp))
                            return;

                        bool changed = false;
                        if (!dev.upgradeBootloader)
                        {
                            dev.upgradeBootloader = true;
                            changed = true;
                        }
                        if (!dev.upgradeSensor)
                        {
                            dev.upgradeSensor = true;
                            changed = true;
                        }
                        if (dev.BootloaderSuccess)
                        {
                            dev.BootloaderSuccess = false;
                            changed = true;
                        }
                        if (dev.SensorSuccess)
                        {
                            dev.SensorSuccess = false;
                            changed = true;
                        }

                        if (changed)
                        {
                            dev.LastFailureReason = "Actor upgrade blocked by sensor boot mode; forcing bootloader + sensor recovery.";
                            UpgradeLogger.Log(logId, mac, "Boot mode recovery",
                                "Actor upgrade blocked by boot mode -> forcing bootloader + sensor update",
                                dev.FirmwareVersion);
                            AppLog.Warn($"[{mac}] Actor upgrade blocked by sensor boot mode -> forcing bootloader + sensor update.");
                        }
                    }

                    var initialResp = await UpgradeDeviceAsync(
                        dev, mac, dev.Pincode, dev.DetectotType, dev.FirmwareVersion,
                        dev.isActorUpgradeNeeded, dev.upgradeBootloader, dev.upgradeSensor, logId
                    ).ConfigureAwait(false);
                    // The pipeline always disconnects at the end of an attempt.
                    // Never reuse the precheck session on subsequent retries.
                    dev.PrecheckSessionAlive = false;
                    dev.PrecheckBootMode = false;
                    ForceBootModeRecoveryIfNeeded(initialResp);

                    const int maxRetriesPerComponent = 5;

                    while (!dev.IsFullyUpgraded)
                    {
                        bool retryActor = dev.isActorUpgradeNeeded && !dev.ActorSuccess;
                        bool retryBootloader = dev.upgradeBootloader && !dev.BootloaderSuccess;
                        bool retrySensor = !dev.SensorSuccess;
                        bool retryHasFirmwareWork = retryActor || retryBootloader || retrySensor;

                        // Guard against retry cycling with no actionable FW steps.
                        if (!retryHasFirmwareWork)
                        {
                            bool pendingConfig = dev.requiresConfigRestore && !dev.isConfigRestored;
                            bool pending102 = dev.requires102Restore && !dev.restore102Success;
                            string pending = string.Join(", ",
                                new[]
                                {
                                    pendingConfig ? "settings-restore" : null,
                                    pending102 ? "dali-102-restore" : null
                                }.Where(x => !string.IsNullOrWhiteSpace(x)));
                            if (string.IsNullOrWhiteSpace(pending))
                                pending = "unknown-state";

                            dev.LastFailureReason = $"Retry stopped: no firmware actions pending, but upgrade is not fully satisfied ({pending}).";
                            if (!string.Equals(dev.finalUpgradeResult, "Failed", StringComparison.OrdinalIgnoreCase))
                                dev.finalUpgradeResult = "Warn";
                            dev.shouldRetry = false;

                            AppLog.Warn($"[RETRY STOP] {mac} - {dev.LastFailureReason}");
                            UpgradeLogger.Log(logId, mac, dev.LastFailureReason, "Warn", dev.FirmwareVersion);
                            break;
                        }

                        if (!UpgradeRetryPolicy.CanRetryNow(dev, maxRetriesPerComponent))
                        {
                            AppLog.Warn($"[RETRY STOP] {mac} - retries exhausted. " +
                                $"actor:{dev.RetryCountActor} boot:{dev.RetryCountBootloader} sensor:{dev.RetryCountSensor}");
                            UpgradeLogger.Log(logId, mac, "Retries exhausted.", "Info");
                            break;
                        }

                        await Task.Delay(10_000).ConfigureAwait(false);

                        dev.RetryCount++; // total retry rounds (for summary/reporting)

                        var resp = await UpgradeDeviceAsync(
                            dev, mac, dev.Pincode, dev.DetectotType, dev.FirmwareVersion,
                            retryActor,
                            retryBootloader,
                            retrySensor,
                            logId
                        ).ConfigureAwait(false);
                        dev.PrecheckSessionAlive = false;
                        dev.PrecheckBootMode = false;

                        AppLog.Warn($"[RETRY RESULT] {mac} - {resp.StatusCode} - {resp.Message}");
                        UpgradeLogger.Log(logId, mac, $"Retry result: {resp.StatusCode} - {resp.Message}", resp.Success ? "Success" : "Failed");
                        ForceBootModeRecoveryIfNeeded(resp);

                        // HARD FAIL: firmware missing / path issues -> never retry
                        if (!resp.Success && resp.Message != null &&
                            (resp.Message.Contains("Could not find a part of the path", StringComparison.OrdinalIgnoreCase) ||
                             resp.Message.Contains("Firmware file missing", StringComparison.OrdinalIgnoreCase)))
                        {
                            dev.LastFailureReason = resp.Message;
                            UpgradeLogger.Log(logId, mac, "Hard failure (firmware missing). Stopping retries.", "Failed");
                            break;
                        }
                    }

                    bool anyFirmwarePlanned = dev.isActorUpgradeNeeded || dev.upgradeBootloader || dev.upgradeSensor;
                    bool firmwareDone =
                        (!dev.isActorUpgradeNeeded || dev.ActorSuccess) &&
                        (!dev.upgradeBootloader || dev.BootloaderSuccess) &&
                        dev.SensorSuccess;

                    // Only run the post FW read once firmware work is fully satisfied.
                    if (anyFirmwarePlanned && firmwareDone)
                    {
                        await VerifyPostUpgradeFirmwareAsync(dev, mac, logId ?? "", reuseExistingConnection: false).ConfigureAwait(false);
                    }
                }

                deviceSw.Stop();
                addDeviceMs(deviceSw.ElapsedMilliseconds);

                AppLog.Warn($">>>> THREAD END - {mac} " +
                    $"actor:{dev.ActorSuccess}:{dev.RetryCountActor} " +
                    $"bootloader:{dev.BootloaderSuccess}:{dev.RetryCountBootloader} " +
                    $"sensor:{dev.SensorSuccess}:{dev.RetryCountSensor} " +
                    $"restore:{dev.isConfigRestored} " +
                    $"time:{deviceSw.Elapsed.TotalSeconds:F2}s");
                summaries.Add(UpgradeSummaryFactory.Create(
                    mac: mac,
                    dev: dev,
                    seconds: deviceSw.Elapsed.TotalSeconds,
                    status: "OK"));

                UpgradeLogger.Log(logId, mac, "Device Upgrade Completed.", dev.finalUpgradeResult);
                await SendUpgradeResultLogAsync(
                    mac: mac,
                    dev: dev,
                    totalProcessTime: deviceSw.Elapsed,
                    result: dev.finalUpgradeResult,
                    failedStep: dev.LastFailureReason).ConfigureAwait(false);

                if (dev.finalUpgradeResult == "Success" && RuntimeVariables.UPGRADE_POST_UPDATE_BLUE_LED_HOLD_ENABLED)
                    await HoldPostUpdateBlueLedAsync(mac, dev.Pincode).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                deviceSw.Stop();
                addDeviceMs(deviceSw.ElapsedMilliseconds);

                AppLog.Error($" {mac} - {ex.GetType().Name}: {ex.Message}");

				summaries.Add(UpgradeSummaryFactory.Create(
				mac: mac,
				dev: dev,
				seconds: deviceSw.Elapsed.TotalSeconds,
				status: "ERROR",
				error: ex.ToString()));

                var failedStep = string.IsNullOrWhiteSpace(dev.LastFailureReason)
                    ? ex.Message
                    : dev.LastFailureReason;

                await SendUpgradeResultLogAsync(
                    mac: mac,
                    dev: dev,
                    totalProcessTime: deviceSw.Elapsed,
                    result: "Failed",
                    failedStep: failedStep).ConfigureAwait(false);
            }
            finally
            {
                _macsInProgress.TryRemove(mac, out _);

				// Release chip assignment + lease
				_chipManager.UnbindMac(mac);
				CassiaChipManager.ReleaseChip(mac);
				chipLease.Dispose();

                
_programmingTargets.TryRemove(mac, out _);
Interlocked.Decrement(ref UpgradeDevicesInProgress);
            }
        }

        private async Task SendUpgradeResultLogAsync(
            string mac,
            UpgradeProgress dev,
            TimeSpan totalProcessTime,
            string result,
            string? failedStep)
        {
            if (!RuntimeVariables.UPGRADE_RESULT_DB_LOG_ENABLED)
                return;

            string apiUrl = (RuntimeVariables.UPGRADE_RESULT_DB_LOG_URL ?? "").Trim();
            if (string.IsNullOrWhiteSpace(apiUrl))
                return;

            string productNo = "";
            if (!DeviceStorageService.TryGetProductNumber(mac, out productNo))
                productNo = (dev.DetectotType ?? "").Trim();

            string oldSw = (dev.CurrentFirmwareVersion ?? "").Trim();
            string postSw = (dev.PostFirmwareVersion ?? "").Trim();
            string newSw = string.IsNullOrWhiteSpace(postSw)
                ? (dev.FirmwareVersion ?? "").Trim()
                : postSw;

            string actorVersion = ExtractActorAppVersion(string.IsNullOrWhiteSpace(postSw) ? oldSw : postSw);
            if (string.IsNullOrWhiteSpace(actorVersion))
                actorVersion = newSw;

            string mqttCassiaName = (_mqttService.CurrentOptions?.Name ?? "").Trim();
            string mqttNetworkId = (_mqttService.CurrentOptions?.NetworkId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(mqttCassiaName))
                mqttCassiaName = Environment.MachineName;
            if (string.IsNullOrWhiteSpace(mqttNetworkId))
                mqttNetworkId = Environment.UserName;

            string normalizedResult = string.IsNullOrWhiteSpace(result)
                ? (dev.IsFullyUpgraded ? "Success" : "Failed")
                : result.Trim();

            var payload = new
            {
                MAC = mac,
                ProductNo = productNo,
                DateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                OldSW = oldSw,
                NewSW = newSw,
                ActorVersion = actorVersion,
                TestResult = dev.IsFullyUpgraded ? "Pass" : "Fail",
                ExecutionTime = FormatDuration(totalProcessTime),
                Handler = "AccessAPP",
                Result = normalizedResult,
                TotalProcessTime = FormatDuration(totalProcessTime),
                FailedStep = failedStep ?? "",
                User = mqttNetworkId,
                Machine = mqttCassiaName,
                ToolVersion = AccessAPP.Version.AppVersion
            };

            int timeoutMs = Math.Max(1000, RuntimeVariables.UPGRADE_RESULT_DB_LOG_TIMEOUT_MS);

            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                string json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(apiUrl, content, cts.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    AppLog.Info($"[UPGRADE DB LOG] Sent: {mac} ({(int)response.StatusCode})");
                    return;
                }

                string errorBody = await TryReadResponseBodyAsync(response).ConfigureAwait(false);
                AppLog.Warn($"[UPGRADE DB LOG] Failed for {mac}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {errorBody}");
            }
            catch (OperationCanceledException)
            {
                AppLog.Warn($"[UPGRADE DB LOG] Timeout after {timeoutMs}ms for {mac}.");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[UPGRADE DB LOG] Send failed for {mac}: {ex.Message}");
            }
        }

        private async Task HoldPostUpdateBlueLedAsync(string mac, string? pincode)
        {
            try
            {
                int chip = GetChipForMac(mac);
                int maxAttempts = Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS);
                int connectTimeoutMs = Math.Max(5000, RuntimeVariables.UPGRADE_CONNECT_ATTEMPT_TIMEOUT_MS);
                int retryDelayMs = 2000;

                AppLog.Info($"[POST] {mac} reconnecting to hold blue LED.");

                bool connected = false;
                HttpStatusCode lastStatus = 0;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    using var cts = new CancellationTokenSource(connectTimeoutMs);
                    var connect = await _connectService
                        .ConnectToBleDevice(_gatewayIpAddress, _gatewayPort, mac, chip: chip, ct: cts.Token)
                        .ConfigureAwait(false);

                    lastStatus = connect.Status;
                    if (connect.Status == HttpStatusCode.OK || connect.Status == HttpStatusCode.Conflict)
                    {
                        connected = true;
                        break;
                    }

                    AppLog.Warn($"[POST] {mac} connect attempt {attempt}/{maxAttempts} failed ({connect.Status}).");
                    await Task.Delay(retryDelayMs).ConfigureAwait(false);
                }

                if (!connected)
                {
                    AppLog.Warn($"[POST] {mac} could not reconnect for blue LED hold. Last status={lastStatus}.");
                    return;
                }

                bool isBootMode = false;
                try
                {
                    isBootMode = CheckIfDeviceInBootMode(_gatewayIpAddress, mac);
                }
                catch (Exception ex)
                {
                    AppLog.Debug($"[POST] {mac} boot-mode check failed: {ex.Message}");
                }

                if (!isBootMode)
                {
                    var login = await AttemptLoginOnConnectedSessionAsync(
                        _gatewayIpAddress,
                        mac,
                        pincode,
                        CancellationToken.None).ConfigureAwait(false);

                    if (!login.Success)
                    {
                        AppLog.Warn($"[POST] {mac} login failed; skipping blue LED hold.");
                        await SafeDisconnectAsync(mac, chip).ConfigureAwait(false);
                        return;
                    }
                }

                var (ledOk, ledData, ledError) = await SendLedCommandFastAsync(
                    mac,
                    chip,
                    LedBreathBlueCmd,
                    CancellationToken.None).ConfigureAwait(false);

                if (!ledOk)
                {
                    AppLog.Warn($"[POST] {mac} LED command failed: {ledError}");
                    await SafeDisconnectAsync(mac, chip).ConfigureAwait(false);
                    return;
                }

                var data = (ledData ?? "").Trim().ToUpperInvariant();
                var ok = data == "00" || data == "0000" || string.IsNullOrWhiteSpace(data);
                if (!ok)
                {
                    AppLog.Warn($"[POST] {mac} LED command rejected: {data}");
                    await SafeDisconnectAsync(mac, chip).ConfigureAwait(false);
                    return;
                }

                AppLog.Info($"[POST] {mac} blue LED hold active. Waiting for device to disconnect.");
                while (await IsMacConnectedOnGatewayAsync(mac, chip).ConfigureAwait(false))
                {
                    await Task.Delay(2000).ConfigureAwait(false);
                }

                AppLog.Info($"[POST] {mac} disconnected. Blue LED hold ended.");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[POST] {mac} blue LED hold failed: {ex.Message}");
            }
        }

        private async Task<bool> IsMacConnectedOnGatewayAsync(string macAddress, int expectedChip)
        {
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
                    return string.IsNullOrWhiteSpace(state) ||
                           string.Equals(state, "connected", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug($"[POST] Gateway connected-state check failed for {macAddress}: {ex.Message}");
            }

            return false;
        }

        private static string FormatDuration(TimeSpan value)
        {
            if (value < TimeSpan.Zero)
                value = TimeSpan.Zero;

            return value.ToString(@"hh\:mm\:ss");
        }

        private static string ExtractActorAppVersion(string firmwareText)
        {
            if (string.IsNullOrWhiteSpace(firmwareText))
                return "";

            var match = Regex.Match(
                firmwareText,
                @"Actor:\s*App:\s*([0-9]{1,2}\.[0-9]{2})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            return match.Success ? match.Groups[1].Value : "";
        }

        private async Task VerifyPostUpgradeFirmwareAsync(UpgradeProgress dev, string mac, string logId, bool reuseExistingConnection)
        {
            try
            {
                string target = (dev.FirmwareVersion ?? "").Trim();
                if (string.IsNullOrWhiteSpace(target))
                    return;

                int delayMs = Math.Max(0, RuntimeVariables.UPGRADE_POST_UPGRADE_FW_READ_DELAY_MS);
                if (delayMs > 0)
                    await Task.Delay(delayMs).ConfigureAwait(false);

                UpgradeLogger.Log(logId, mac, "FW Read (post-upgrade)", "Starting", dev.FirmwareVersion);
                string postFw = "";
                if (reuseExistingConnection)
                {
                    postFw = await GetFwVersionOnConnectedSessionAsync(
                        mac,
                        dev.Pincode ?? "",
                        logId,
                        dev.FirmwareVersion).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(postFw))
                {
                    postFw = await GetFwVersion(
                        mac,
                        dev.Pincode,
                        disconnect_on_finish: !reuseExistingConnection).ConfigureAwait(false);
                }

                dev.PostFirmwareVersion = postFw;

                if (string.IsNullOrWhiteSpace(postFw))
                {
                    dev.PostUpgradeFwMatch = false;
                    string reason = "Post-upgrade FW read failed (empty response).";
                    dev.LastFailureReason = string.IsNullOrWhiteSpace(dev.LastFailureReason)
                        ? reason
                        : $"{dev.LastFailureReason} | {reason}";
                    dev.finalUpgradeResult = "Failed";
                    UpgradeLogger.Log(logId, mac, "FW Read (post-upgrade)", "Failed (empty response)", dev.FirmwareVersion);
                    return;
                }

                bool match = FirmwareMatchesTarget(postFw, target);
                dev.PostUpgradeFwMatch = match;

                if (!match)
                {
                    string reason = $"Post-upgrade FW mismatch. Target={target}, Actual={postFw}";
                    dev.LastFailureReason = string.IsNullOrWhiteSpace(dev.LastFailureReason)
                        ? reason
                        : $"{dev.LastFailureReason} | {reason}";
                    dev.finalUpgradeResult = "Failed";
                    UpgradeLogger.Log(logId, mac, "FW Read (post-upgrade)", "Mismatch", dev.FirmwareVersion);
                }
                else
                {
                    UpgradeLogger.Log(logId, mac, "FW Read (post-upgrade)", "Match", dev.FirmwareVersion);
                }
            }
            catch (Exception ex)
            {
                dev.PostUpgradeFwMatch = false;
                string reason = $"Post-upgrade FW read failed: {ex.Message}";
                dev.LastFailureReason = string.IsNullOrWhiteSpace(dev.LastFailureReason)
                    ? reason
                    : $"{dev.LastFailureReason} | {reason}";
                dev.finalUpgradeResult = "Failed";
                UpgradeLogger.Log(logId, mac, "FW Read (post-upgrade)", $"Exception: {ex.Message}", dev.FirmwareVersion);
            }
        }

        private static bool FirmwareMatchesTarget(string actual, string target)
        {
            string targetToken = ExtractVersionToken(target);
            if (string.IsNullOrWhiteSpace(targetToken))
                return false;

            if (actual.IndexOf(targetToken, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            var actualTokens = ExtractVersionTokens(actual);
            return actualTokens.Contains(targetToken);
        }

        private static string ExtractVersionToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            var match = Regex.Match(value, @"\d{1,3}\.\d{1,3}", RegexOptions.CultureInvariant);
            return match.Success ? match.Value : "";
        }

        private static HashSet<string> ExtractVersionTokens(string value)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(value))
                return tokens;

            foreach (Match m in Regex.Matches(value, @"\d{1,3}\.\d{1,3}", RegexOptions.CultureInvariant))
            {
                if (m.Success && !string.IsNullOrWhiteSpace(m.Value))
                    tokens.Add(m.Value);
            }

            return tokens;
        }

        private static async Task<string> TryReadResponseBodyAsync(HttpResponseMessage response)
        {
            try
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body))
                    return "";

                string flat = body.Replace("\r", " ").Replace("\n", " ").Trim();
                return flat.Length <= 220 ? flat : flat.Substring(0, 220) + "...";
            }
            catch
            {
                return "";
            }
        }

		internal void PrintUpgradeRunSummary(
            ConcurrentBag<DeviceUpgradeSummary> summaries,
            int totalDevicesProcessed,
            long totalDeviceMs,
            int numbersOfThreadsInParallel,
            Stopwatch globalSw)
        {
            var ordered = summaries
                .OrderBy(s => s.Status == "ERROR" ? 0 : s.Status == "OK" ? 1 : 2)
                .ThenByDescending(s => s.Seconds)
                .ThenBy(s => s.Mac)
                .ToList();

            AppLog.Info("[DEVICE SUMMARY]");
foreach (var s in ordered)
            {
                string a = s.ActorNeeded ? (s.ActorSuccess ? "A:OK" : "A:FAIL") : "A:-";
                string b = s.BootloaderNeeded ? (s.BootloaderSuccess ? "B:OK" : "B:FAIL") : "B:-";
                string se = s.SensorSuccess ? "S:OK" : "S:FAIL";
                string full = s.IsFullyUpgraded ? "FULL:OK" : "FULL:FAIL";
                string cfg = s.ConfigRestored ? "CFG:OK" : "CFG:-";

                AppLog.Warn($"  {s.Mac,-17} {s.DetectorType,-3} " +
                    $"cur:{s.CurrentFw,-8} -> tgt:{s.TargetFw,-8} " +
                    $"{a} {b} {se} {cfg} {full} " +
                    $"retry:{s.RetryTotal} (a:{s.RetryActor},b:{s.RetryBootloader},s:{s.RetrySensor}) " +
                    $"time:{s.Seconds,8:F2}s " +
                    $"status:{s.Status}");
}

            int okCount = ordered.Count(x => x.Status == "OK");
            int errCount = ordered.Count(x => x.Status == "ERROR");
            int skipCount = ordered.Count(x => x.Status == "SKIPPED");

            double avgSecondsPerDevice =
                totalDevicesProcessed > 0 ? (totalDeviceMs / 1000.0) / totalDevicesProcessed : 0.0;

            double wallPerDevice =
                totalDevicesProcessed > 0 ? globalSw.Elapsed.TotalSeconds / totalDevicesProcessed : 0.0;

            AppLog.Error($"[UPGRADE SUMMARY]\n" +
                $"  Devices processed  : {totalDevicesProcessed}\n" +
                $"  OK / ERROR / SKIP  : {okCount} / {errCount} / {skipCount}\n" +
                $"  Parallel threads   : {numbersOfThreadsInParallel}\n" +
                $"  Total wall time    : {globalSw.Elapsed.TotalSeconds:F2}s\n" +
                $"  Avg exec / device  : {avgSecondsPerDevice:F2}s\n" +
                $"  Wall / device      : {wallPerDevice:F2}s");
}


        public async Task<ServiceResponse> ProcessingSensorUpgrade(
            string nodeMac,
            bool bActor,
            bool isBootloader,
            string DetectorType,
            string FirmwareVersion,
            string logId,
            string? pincode = null) // should be moved to firmware services
        {
            AppLog.Info($"Processing Sensor Upgrade started->{nodeMac}");
var response = new ServiceResponse();

            var connProbe = await ConnectOnlyWithRetryAsync(
                maxAttempts: Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS),
                delayMs: 5000,
                stageName: "Connected (ProcessingSensorUpgrade probe)",
                logSuccess: false,
                macAddress: nodeMac,
                FirmwareVersion: FirmwareVersion,
                logId: logId,
                discoverGattOverride: RuntimeVariables.UPGRADE_CONNECT_DISCOVER_GATT_AFTER_BOOT_JUMP < 0
                    ? null
                    : (RuntimeVariables.UPGRADE_CONNECT_DISCOVER_GATT_AFTER_BOOT_JUMP <= 0 ? 0 : 1)
                ).ConfigureAwait(false);

            if (!connProbe.ok)
            {
                UpgradeLogger.Log(logId, nodeMac, "Connected", "Failed", FirmwareVersion);
                response.Success = false;
                response.StatusCode = (int)(connProbe.code == 0 ? HttpStatusCode.ServiceUnavailable : connProbe.code);
                response.Message = "Failed to connect to device.";
                return response;
            }


            bool isAlreadyInBootMode = CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac);

            //var notificationService = new CassiaNotificationService(_configuration);
            if (isAlreadyInBootMode)
            {
                //await Task.Delay(3000);

				bool notificationEnabled = await _notificationService.EnableNotificationAsync(_gatewayIpAddress, nodeMac, bActor, chip: GetChipForMac(nodeMac));

                if (!notificationEnabled)
                {
                    response.Success = false;
                    response.StatusCode = 500;
                    response.Message = "Error Enabling Notifications";
                    return response;
                }
                UpgradeLogger.Log(logId, nodeMac, "NotificationEnabled", "Success");
                AppLog.Info($"bootloader mode achieved and Notification enabled status: {notificationEnabled} -> {nodeMac}");
}
            else
            {
                var loggedIn = await EnsureLoginOnConnectedSessionUnlessBootModeAsync(
                    nodeMac,
                    pincode,
                    logId,
                    FirmwareVersion,
                    stageName: "LoggedIn (ProcessingSensorUpgrade)",
                    maxAttempts: 3).ConfigureAwait(false);

                if (!loggedIn)
                {
                    response.Success = false;
                    response.StatusCode = 401;
                    response.Message = "Failed to login to the device.";
                    return response;
                }

                const int maxAttempts = 5;
                bool bootModeAchieved = false;
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    bootModeAchieved = await SendJumpToBootloader(_gatewayIpAddress, nodeMac, bActor);
                    if (bootModeAchieved)
                    {
                        UpgradeLogger.Log(logId, nodeMac, "BootMode", "Achieved");
                        AppLog.Info($"Device entered boot mode after {attempt + 1} attempts.");
break;
                    }
                    AppLog.Warn($"Attempt {attempt + 1} to enter boot mode failed. Retrying...");
await Task.Delay(3000); // Delay between attempts
                }

                if (!bootModeAchieved)
                {
                    UpgradeLogger.Log(logId, nodeMac, "BootMode", "Failed");
                    response.Success = false;
                    response.StatusCode = 417; // Expectation Failed
                    response.Message = "Failed to enter boot mode.";
                    return response;
                }

            }

            //Step 3: Start Programming the Sensor
            bool programmingResult = ProgramDevice(_gatewayIpAddress, nodeMac, _notificationService, DetectorType, FirmwareVersion, bActor, isBootloader);

            if (programmingResult)
            {

                UpgradeLogger.Log(logId, nodeMac, isBootloader ? "BootLoaderProgrammingComplete" : "SensorProgrammingComplete", "Success");
                response.Success = true;
                response.StatusCode = 200;
                response.Message = "Programming Complete";
                return response;
            }
            else
            {
                UpgradeLogger.Log(logId, nodeMac, isBootloader ? "BootLoaderProgrammingComplete" : "SensorProgrammingComplete", "Failed");
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Programming Failed";
                return response;
            }

        }

        public async Task<ServiceResponse> ProcessingActorUpgrade(
            string nodeMac,
            bool bActor,
            string DetectorType,
            string FirmwareVersion,
            string logId,
            bool skipBootModeValidation = false) // should be moved to firmware services
        {
            var response = new ServiceResponse();
            const int maxRetryAttempts = 3; // Maximum number of retries to put the actor into boot mode
            const int delayBetweenRetries = 5000; // Delay between retries (in milliseconds)
            int retryCount = 0;

            // Step 1: Check if the actor is in boot mode
            if (!skipBootModeValidation)
            {
                while (retryCount < maxRetryAttempts)
                {
                    var isActorInBootMode = await ActorBootCheck(_gatewayIpAddress, nodeMac);

                    if (isActorInBootMode)
                    {
                        AppLog.Info($"Actor {nodeMac} is in boot mode.");
                        break; // Exit the loop if the actor is already in boot mode
                    }
                    else
                    {
                        retryCount++;
                        AppLog.Warn($"Actor {nodeMac} is not in boot mode. Attempting to put it into boot mode. Retry {retryCount}/{maxRetryAttempts}");
                        // Send a command to put the actor into boot mode
                        var jumpToBootloaderSuccess = await SendJumpToBootloader(_gatewayIpAddress, nodeMac, bActor);

                        if (!jumpToBootloaderSuccess)
                        {
                            AppLog.Warn($"Failed to send jump-to-bootloader command for {nodeMac}. Retrying...");
                        }

                        // Wait for a while before retrying
                        await Task.Delay(delayBetweenRetries);
                    }
                }
            }
            else
            {
                // Optimized flow: keep current session (no reconnect loop), but still verify once before programming.
                var isActorInBootMode = await ActorBootCheck(_gatewayIpAddress, nodeMac);
                if (!isActorInBootMode)
                {
                    UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", "Single-check failed on optimized flow, falling back to standard validation");
                    AppLog.Warn($"Optimized actor boot-mode single-check failed for {nodeMac}; falling back to full validation loop.");

                    while (retryCount < maxRetryAttempts)
                    {
                        isActorInBootMode = await ActorBootCheck(_gatewayIpAddress, nodeMac);

                        if (isActorInBootMode)
                        {
                            AppLog.Info($"Actor {nodeMac} is in boot mode.");
                            break;
                        }

                        retryCount++;
                        AppLog.Warn($"Actor {nodeMac} is not in boot mode. Attempting to put it into boot mode. Retry {retryCount}/{maxRetryAttempts}");
                        var jumpToBootloaderSuccess = await SendJumpToBootloader(_gatewayIpAddress, nodeMac, bActor);

                        if (!jumpToBootloaderSuccess)
                            AppLog.Warn($"Failed to send jump-to-bootloader command for {nodeMac}. Retrying...");

                        await Task.Delay(delayBetweenRetries);
                    }
                }
                else
                {
                    UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", "Optimized single-check passed (no reconnect validation loop)");
                    AppLog.Info($"Optimized actor boot-mode single-check passed for {nodeMac}.");
                }
            }

            // If after max retries the actor is still not in boot mode, return an error response
            if (!skipBootModeValidation && retryCount >= maxRetryAttempts)
            {
                UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", "Failed");
                AppLog.Warn($"Failed to put actor {nodeMac} into boot mode after {maxRetryAttempts} attempts.");
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Failed to put actor into boot mode.";
                return response;
            }

            // Step 2: Enable notifications

            AppLog.Info($"Bootloader mode achieved for {nodeMac}.");
// Step 3: Start programming the actor
            var programmingResult = ProgramDevice(_gatewayIpAddress, nodeMac, _notificationService, DetectorType, FirmwareVersion, bActor, false);

            if (programmingResult)
            {
                UpgradeLogger.Log(logId, nodeMac, "ActorProgrammingComplete", "Success");
                response.Success = true;
                response.StatusCode = 200;
                response.Message = "Programming Complete";
                return response;
            }
            else
            {
                UpgradeLogger.Log(logId, nodeMac, "ActorProgrammingComplete", "Failed");
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Programming Failed";
                return response;
            }
        }

        

        
        

        public bool ProgramDevice(string gatewayIpAddress, string nodeMac, CassiaNotificationService cassiaNotificationService, string DetectorType, string FirmwareVersion, bool bActor, bool isBootloader)
        {
            AppLog.Info($"Actor is going to be programmed? : {bActor}");
try
            {
                // Remember what we are programming so ProgressUpdate can include the stage
                // in MQTT progress messages.
                if (_ownInstance != null)
                {
                    var stage = bActor ? "actor" : (isBootloader ? "bootloader" : "sensor");
                    _ownInstance._programmingStageByMac[nodeMac] = stage;
                }

                InitializeNotificationSubscription(nodeMac, cassiaNotificationService);
                MacAddress = nodeMac;


                Bootloader_Utils.CyBtldr_CommunicationsData m_comm_data = new Bootloader_Utils.CyBtldr_CommunicationsData();
                m_comm_data.OpenConnection = OpenConnection;
                m_comm_data.CloseConnection = CloseConnection;
                m_comm_data.CustomContext = MacToInt64(nodeMac);
                ReturnCodes local_status = 0x00;
                string firmwarePath = "";

                // Phase 1 - Return relative path string
                firmwarePath = FirmwareResolver.ResolveFirmwareFile(DetectorType, FirmwareVersion, bActor, isBootloader);
                AppLog.Info($"Firmware path resolved: {firmwarePath}");
if (!File.Exists(firmwarePath))
                {
                    AppLog.Fatal($" Firmware file missing: {firmwarePath}");
return false;
                }
                if (bActor)
                {
                    AppLog.Info($"Programming Actor  - {nodeMac}");
m_comm_data.WriteData = WriteActorData;
                    m_comm_data.ReadData = ReadActorData;
                    m_comm_data.MaxTransferSize = 72;
                }
                else if (isBootloader)
                {
                    AppLog.Info($"Programming Bootloader  - {nodeMac}");
m_comm_data.WriteData = WriteSensorData;
                    m_comm_data.ReadData = ReadData;
                    m_comm_data.MaxTransferSize = 265;
                }
                else
                {

                    AppLog.Info($"Programming Sensor - {nodeMac}");
m_comm_data.WriteData = WriteSensorData;
                    m_comm_data.ReadData = ReadData;
                    m_comm_data.MaxTransferSize = 265;
                }

                // Load all expected rows
                HashSet<string> allRowsH = new HashSet<string>();
                HashSet<string> tmpH = null;
                allRows.TryRemove(nodeMac, out tmpH);
                completedRows.TryRemove(nodeMac, out tmpH);
                tmpH = new HashSet<string>();
                completedRows.TryAdd(nodeMac, tmpH);

                foreach (string line in File.ReadAllLines(firmwarePath).Skip(1)) // skip CYACD header
                {
                    if (line.StartsWith(":"))
                    {
                        string arrayId = line.Substring(1, 2);
                        string rowNumber = line.Substring(3, 4);
                        string key = $"{arrayId}:{rowNumber}";
                        allRowsH.Add(key);
                    }
                }

                allRows.TryAdd(nodeMac, allRowsH);


                // Call programming function
                local_status = bActor
                    ? (ReturnCodes)Bootloader_Utils.CyBtldr_Program(firmwarePath, null, _appID, ref m_comm_data, Upd)
                    : (ReturnCodes)Bootloader_Utils.CyBtldr_Program(firmwarePath, _securityKey, _appID, ref m_comm_data, Upd);

                // Handle failure
                if (local_status != ReturnCodes.CYRET_SUCCESS)
                {
                    AppLog.Warn("Programming failed - status: " + local_status);
_deviceStorageService.MarkFirmwareFailed(nodeMac);
                }

                return local_status == ReturnCodes.CYRET_SUCCESS;
            }
            finally
            {
                // Clear stage mapping when this programming call finishes (success or fail)
                _ownInstance?._programmingStageByMac.TryRemove(nodeMac, out _);
                //UnsubscribeNotification(nodeMac, cassiaNotificationService);
            }
        }




    }
}
