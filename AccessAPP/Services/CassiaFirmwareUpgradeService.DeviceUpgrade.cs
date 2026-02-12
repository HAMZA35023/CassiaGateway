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
using System.Runtime.InteropServices;
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
                var probe = await ConnectOnlyWithRetryAsync_Internal(
                    maxAttempts: Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS),
                    delayMs: 2000,
                    stageName: "Connected (precheck probe)",
                    macAddress: mac,
                    firmwareVersion: dev.FirmwareVersion,
                    logId: logId,
                    logSuccess: false
                ).ConfigureAwait(false);

                probeConnected = probe.ok;
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
                    // FW read can be flaky; retry a few times before giving up.
                    const int firmwareReadAttempts = 3;
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
                                }
                                catch
                                {
                                    // best-effort disconnect before reconnect flow
                                }

                                dev.CurrentFirmwareVersion = await GetFwVersion(mac, dev.Pincode, disconnect_on_finish: true).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            // Fallback only when probe connect was not available.
                            dev.CurrentFirmwareVersion = await GetFwVersion(mac, dev.Pincode, disconnect_on_finish: true).ConfigureAwait(false);
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

                if (probeConnected)
                {
                    try
                    {
                        await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, mac, 0, chip: GetChipForMac(mac)).ConfigureAwait(false);
                    }
                    catch
                    {
                        // best-effort disconnect after precheck probe
                    }
                }

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

                    // Requirements for success reporting
                    dev.requiresConfigRestore = decisions.RequiresConfigRestore;
                    dev.requires102Restore = decisions.Requires102Restore;

                    await UpgradeDeviceAsync(
                        dev, mac, dev.Pincode, dev.DetectotType, dev.FirmwareVersion,
                        dev.isActorUpgradeNeeded, dev.upgradeBootloader, dev.upgradeSensor, logId
                    ).ConfigureAwait(false);

                    const int maxRetriesPerComponent = 5;

                    while (!dev.IsFullyUpgraded)
                    {
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
                            dev.isActorUpgradeNeeded && !dev.ActorSuccess,
                            dev.upgradeBootloader && !dev.BootloaderSuccess,
                            !dev.SensorSuccess,
                            logId
                        ).ConfigureAwait(false);

                        AppLog.Warn($"[RETRY RESULT] {mac} - {resp.StatusCode} - {resp.Message}");
                        UpgradeLogger.Log(logId, mac, $"Retry result: {resp.StatusCode} - {resp.Message}", resp.Success ? "Success" : "Failed");

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
                logId: logId
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
