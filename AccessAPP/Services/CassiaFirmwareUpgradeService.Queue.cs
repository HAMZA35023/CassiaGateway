using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using AccessAPP.Logging;
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
    // Split-out: upgrade queue + live worker (no behavior changes; moved from CassiaFirmwareUpgradeService.cs)
    public partial class CassiaFirmwareUpgradeService
    {
        // ---------- QUEUE STATE ----------
        private readonly ConcurrentQueue<UpgradeProgress> _upgradeQueue = new();
        private readonly ConcurrentDictionary<string, byte> _queuedMacs = new(); // pending membership set
        private readonly object _upgradeQueueGate = new();

        // Guards mutations of the underlying ConcurrentQueue when we need
        // stronger-than-eventual-consistency operations (e.g. remove single item).
        private readonly object _queueEditGate = new();

        // Wake-up signal for worker when new items arrive
        private readonly SemaphoreSlim _queueSignal = new(0, int.MaxValue);

        private bool _upgradeQueueWorkerRunning;
        private Task? _upgradeQueueWorkerTask;

        private static string NormalizeMac(string? mac)
            => (mac ?? string.Empty).Trim().ToUpperInvariant();

        /// <summary>
        /// Queue-enabled FIFO entrypoint.
        /// First call starts the worker; subsequent calls only enqueue and return quickly.
        /// Dedup: won't enqueue same MAC twice while pending or running.
        /// </summary>
        private Task UpgradeDevicesInParallel(List<UpgradeProgress> devices, int numbersOfThreadsInParallel = -1)
        {
            if (devices == null || devices.Count == 0)
                return Task.CompletedTask;

            int queued = 0;
            int skippedDuplicate = 0;
            int skippedInvalid = 0;

            foreach (var dev in devices)
            {
                var mac = NormalizeMac(dev?.MacAddress);

                if (string.IsNullOrWhiteSpace(mac))
                {
                    skippedInvalid++;
                    continue;
                }

                // Already running anywhere?
                if (_macsInProgress.ContainsKey(mac))
                {
                    skippedDuplicate++;
                    AppLog.Info($"[UPGRADE QUEUE] SKIP add (already running): {mac}");
continue;
                }

                // Already queued pending?
                if (!_queuedMacs.TryAdd(mac, 0))
                {
                    skippedDuplicate++;
                    AppLog.Info($"[UPGRADE QUEUE] SKIP add (already queued): {mac}");
continue;
                }

                dev.MacAddress = mac;

                lock (_queueEditGate)
                {
                    _upgradeQueue.Enqueue(dev);
                }
                inQueue = _upgradeQueue.Count;
                queued++;

                AppLog.Info($"[UPGRADE QUEUE] ADDED: {mac} " +
                    $"type:{dev.DetectotType ?? ""} " +
                    $"tgt:{dev.FirmwareVersion ?? ""}");
// Wake the worker so it can start this immediately (if there is capacity)
                _queueSignal.Release();
            }

            AppLog.Info($"[UPGRADE QUEUE] Add request: in={devices.Count}, added={queued}, dup/ignored={skippedDuplicate}, invalid={skippedInvalid}, pending={_upgradeQueue.Count}");
lock (_upgradeQueueGate)
            {
                // If the previous worker task died/completed but the flag didn't get cleared due to a race,
                // allow restart safely.
                if (_upgradeQueueWorkerRunning && _upgradeQueueWorkerTask is { IsCompleted: false })
                    return Task.CompletedTask;

                if (_upgradeQueue.IsEmpty)
                    return Task.CompletedTask;

                _upgradeQueueWorkerRunning = true;

                // Pass -1 to indicate "follow GlobalnumberOfParallelThreads dynamically".
                // If a caller provides a fixed value, keep it fixed for the run.
                int threadsParam = numbersOfThreadsInParallel;
                int threadsEffective = threadsParam == -1 ? GlobalnumberOfParallelThreads : threadsParam;

                AppLog.Info($"[UPGRADE QUEUE] Worker START (threads={threadsEffective})");
_upgradeQueueWorkerTask = Task.Run(() => UpgradeQueueWorkerLiveAsync(threadsParam));
                return _upgradeQueueWorkerTask;
            }
        }

        /// <summary>
        /// Live queue worker: starts new device upgrades as soon as there is capacity,
        /// without waiting for the current "batch" to finish.
        /// </summary>
        private async Task UpgradeQueueWorkerLiveAsync(int numbersOfThreadsInParallel)
        {
            // Per-run summary (run = from first enqueue until queue+running becomes empty)
            var globalSw = Stopwatch.StartNew();
            long totalDeviceMs = 0;
            int totalDevicesProcessed = 0;

            var summaries = new ConcurrentBag<DeviceUpgradeSummary>();

            // If the worker was started with a fixed value, keep it fixed.
            // If started with a value derived from GlobalnumberOfParallelThreads (normal case),
            // allow runtime changes to take effect.
            bool fixedParallel = numbersOfThreadsInParallel != -1;

            int CurrentMaxThreads()
            {
                if (fixedParallel) return Math.Max(1, numbersOfThreadsInParallel);
                return Math.Max(1, Volatile.Read(ref GlobalnumberOfParallelThreads));
            }

            var running = new List<Task>(capacity: Math.Max(1, CurrentMaxThreads()));

            try
            {
                while (true)
                {
                    // Fill capacity immediately from FIFO
                    var maxThreads = CurrentMaxThreads();
                    while (running.Count < maxThreads)
                    {
                        UpgradeProgress? dev;
                        lock (_queueEditGate)
                        {
                            if (!_upgradeQueue.TryDequeue(out dev))
                                break;
                        }
                        inQueue = _upgradeQueue.Count;
                        var mac = NormalizeMac(dev?.MacAddress);
                        if (!string.IsNullOrWhiteSpace(mac))
                            _queuedMacs.TryRemove(mac, out _); // leaving pending queue

                        if (dev == null)
                            continue;

                        totalDevicesProcessed++;

						// Start immediately. On Cassia X2000 we can use both BLE chips; when enabled,
						// each in-flight upgrade is assigned a chip (0/1) and all REST calls for that MAC
						// are forced to that chip.
						ChipLease lease;
						if (RuntimeVariables.USE_BOTH_CASSIA_CHIPS && maxThreads >= 2)
							lease = await _chipAllocator.AcquireAsync().ConfigureAwait(false);
						else
							lease = ChipLease.Fixed(RuntimeVariables.DEFAULT_CASSIA_CHIP);

						running.Add(ProcessSingleDeviceUpgradeAsync(dev, lease, summaries, ms => Interlocked.Add(ref totalDeviceMs, ms)));
                        inQueue = _upgradeQueue.Count;
                    }

                    // Clean out completed tasks
                    for (int i = running.Count - 1; i >= 0; i--)
                    {
                        if (running[i].IsCompleted)
                            running.RemoveAt(i);
                    }

                    // If nothing queued and nothing running -> stop the run,
                    // BUT avoid a race where an enqueue happens right as we decide to stop.
                    // We "linger" briefly waiting for a signal.
                    if (_upgradeQueue.IsEmpty && running.Count == 0)
                    {
                        // If someone enqueued right now, they release _queueSignal.
                        // Wait a tiny bit to catch it.
                        bool gotLateSignal = await _queueSignal.WaitAsync(250).ConfigureAwait(false);

                        if (gotLateSignal)
                        {
                            // New work arrived during shutdown window; continue loop and drain queue.
                            continue;
                        }

                        lock (_upgradeQueueGate)
                        {
                            // Double-check again under the gate before stopping.
                            if (_upgradeQueue.IsEmpty)
                            {
                                _upgradeQueueWorkerRunning = false;
                                _upgradeQueueWorkerTask = null;
                                break;
                            }
                        }
                    }



                    // Wait for either:
                    // - a running device completes (freeing capacity)
                    // - a new device is enqueued (signal)
                    //
                    // IMPORTANT:
                    // We must never block on the signal if the queue already contains items,
                    // because the signal tokens can be consumed earlier while we were at capacity.
                    // In that case, relying on the signal would stall the worker even though work is queued.
					// If we have queued work AND available worker capacity, start it immediately.
					// This is critical when multiple items were enqueued while we were at capacity,
					// because the signal tokens can be consumed earlier. We must not stall here.
					var maxNow = CurrentMaxThreads();
					if (!_upgradeQueue.IsEmpty && running.Count < maxNow)
						continue;

					if (running.Count > 0)
                    {
                        Task completion = Task.WhenAny(running);
                        Task signal = _queueSignal.WaitAsync();
                        await Task.WhenAny(completion, signal).ConfigureAwait(false);
                    }
                    else
                    {
                        // No running tasks. If we already have queued work, loop immediately and start it.
                        if (!_upgradeQueue.IsEmpty)
                            continue;

                        // Otherwise wait until something is enqueued.
                        await _queueSignal.WaitAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Info($"[UPGRADE QUEUE] Worker crashed: {ex.GetType().Name}: {ex.Message}");
}
            finally
            {
                globalSw.Stop();
                AppLog.Info($"[UPGRADE QUEUE] Worker STOP (run complete). wall={globalSw.Elapsed.TotalSeconds:F2}s, processed={totalDevicesProcessed}, pending={_upgradeQueue.Count}");
// Optional: print run summary at end (same style as before)
                // Use current (dynamic) configured threads for summary readability.
                var finalThreads = fixedParallel ? numbersOfThreadsInParallel : Math.Max(1, Volatile.Read(ref GlobalnumberOfParallelThreads));
                PrintUpgradeRunSummary(summaries, totalDevicesProcessed, totalDeviceMs, finalThreads, globalSw);
            }
        }

        /// <summary>
        /// Clears only pending items in the FIFO queue (does not cancel in-progress upgrades).
        /// Returns how many were removed.
        /// </summary>
        public int ClearUpgradeQueue()
        {
            int removed = 0;

            lock (_queueEditGate)
            {
                while (_upgradeQueue.TryDequeue(out var dev))
                {
                    removed++;
                    var mac = NormalizeMac(dev?.MacAddress);
                    if (!string.IsNullOrWhiteSpace(mac))
                        _queuedMacs.TryRemove(mac, out _);
                }
            }
            inQueue = _upgradeQueue.Count;
            AppLog.Info($"[UPGRADE QUEUE] Cleared {removed} queued device(s). Pending now: {_upgradeQueue.Count}");
return removed;
        }

        /// <summary>
        /// Removes a single MAC from the pending FIFO queue (does not cancel in-progress upgrades).
        /// Returns 1 if removed, 0 if not found/pending.
        /// </summary>
        public int RemoveFromUpgradeQueue(string mac)
        {
            mac = NormalizeMac(mac);
            if (string.IsNullOrWhiteSpace(mac)) return 0;

            int removed = 0;

            lock (_queueEditGate)
            {
                // Quick exit if it isn't marked as queued
                if (!_queuedMacs.ContainsKey(mac))
                    return 0;

                var items = new List<UpgradeProgress>(capacity: Math.Max(16, _upgradeQueue.Count));
                while (_upgradeQueue.TryDequeue(out var dev))
                {
                    if (dev is null)
                        continue;

                    var m = NormalizeMac(dev.MacAddress);
                    if (string.Equals(m, mac, StringComparison.OrdinalIgnoreCase) && removed == 0)
                    {
                        removed = 1;
                        // do not re-enqueue
                        continue;
                    }

                    items.Add(dev);
                }

                _queuedMacs.Clear();

                foreach (var dev in items)
                {
                    var m = NormalizeMac(dev.MacAddress);
                    if (!string.IsNullOrWhiteSpace(m))
                        _queuedMacs.TryAdd(m, 0);
                    _upgradeQueue.Enqueue(dev);
                }

                if (removed == 1)
                    _queuedMacs.TryRemove(mac, out _);
            }

            inQueue = _upgradeQueue.Count;
            if (removed == 1)
                AppLog.Info($"[UPGRADE QUEUE] Removed pending device: {mac}. Pending now: {_upgradeQueue.Count}");
return removed;
        }

        public static int RemoveFromUpgradeQueuePending(string mac)
        {
            var inst = _ownInstance;
            return inst is null ? 0 : inst.RemoveFromUpgradeQueue(mac);
        }

        public List<(string Mac, string DetectorType, string TargetFw)> GetUpgradeQueueSnapshot()
        {
            return _upgradeQueue
                .ToArray()
                .Select(d => (NormalizeMac(d.MacAddress), d.DetectotType ?? "", d.FirmwareVersion ?? ""))
                .ToList();
        }

        // ------------------------------------------------------------------------------------
        // Per-device logic extracted from your original tasks delegate (same behavior)
        // ------------------------------------------------------------------------------------
		private async Task ProcessSingleDeviceUpgradeAsync(
		    UpgradeProgress dev,
		    ChipLease chipLease,
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

                summaries.Add(new DeviceUpgradeSummary
                {
                    Mac = mac,
                    DetectorType = dev.DetectotType ?? "",
                    TargetFw = dev.FirmwareVersion ?? "",
                    CurrentFw = dev.CurrentFirmwareVersion ?? "",
                    Seconds = deviceSw.Elapsed.TotalSeconds,
                    Status = "SKIPPED"
                });

		        return;
            }

		    // Bind this MAC to a Cassia BLE chip for the duration of the upgrade.
		    _chipByMac[mac] = chipLease.Chip;
		    CassiaChipManager.SetChip(mac, chipLease.Chip);
		        AppLog.Info($"[CHIP] {mac} assigned chip={chipLease.Chip}");
Interlocked.Increment(ref UpgradeDevicesInProgress);

            
// Track programming list for MQTT (mac + target fw)
_programmingTargets[mac] = (dev.DetectotType ?? "", dev.FirmwareVersion ?? "");
try
            {
      
                    AppLog.Debug($"[{DateTime.Now:HH:mm:ss.fff}][T{Environment.CurrentManagedThreadId}] " +
                        System.Text.Json.JsonSerializer.Serialize(dev));


                if (!CheckIfDeviceInBootMode(_gatewayIpAddress, mac))
                {
                    // FW read can be flaky; retry a few times before giving up.
                    for (int i = 1; i <= 3; i++)
                    {
                        dev.CurrentFirmwareVersion = await GetFwVersion(mac, dev.Pincode);
                        if (!string.IsNullOrWhiteSpace(dev.CurrentFirmwareVersion))
                            break;
                       
                        await Task.Delay(2000).ConfigureAwait(false);
                    }
                }

                string logId = $"{mac.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";
                UpgradeLogger.Log(logId, mac, "Current FW Version:", dev.CurrentFirmwareVersion, dev.FirmwareVersion);

                dev.RetryCount = 0;
                dev.RetryCountActor = 0;
                dev.RetryCountBootloader = 0;
                dev.RetryCountSensor = 0;

                AppLog.Info($"[START] {mac}");
dev.upgradeBootloader = FirmwareResolver.ShouldUpgradeBootloader(
                    dev.DetectotType,
                    dev.FirmwareVersion,
                    dev.CurrentFirmwareVersion
                );

                // Skip sensor upgrade when current App FW matches target, unless forced.
                dev.upgradeSensor = dev.ForceUpdate || !FirmwareResolver.IsSameAppVersion(dev.CurrentFirmwareVersion, dev.FirmwareVersion);
                if (!dev.upgradeSensor)
                {
                    UpgradeLogger.Log(logId, mac, "Sensor upgrade skipped (FW already matches target)", "Info", dev.FirmwareVersion);
                    AppLog.Info($"[SKIP] Sensor upgrade for {mac} - current matches target and ForceUpdate=false");
}

                var isDaliMaster = dev.DetectotType == "P48" || dev.DetectotType == "P47";

                // Skip actor upgrade when current Actor App FW matches target, unless forced.
                dev.isActorUpgradeNeeded = isDaliMaster && (dev.ForceUpdate || !FirmwareResolver.IsSameActorAppVersion(dev.CurrentFirmwareVersion, dev.FirmwareVersion));
                if (isDaliMaster && !dev.isActorUpgradeNeeded)
                {
                    UpgradeLogger.Log(logId, mac, "Actor upgrade skipped (FW already matches target)", "Info", dev.FirmwareVersion);
                    AppLog.Info($"[SKIP] Actor upgrade for {mac} - current matches target and ForceUpdate=false");
}

                // Requirements for success reporting
                dev.requiresConfigRestore = RuntimeVariables.RestoreSettingsAfterUpgrade && (dev.DetectotType == "P48" || dev.DetectotType == "P47" || dev.DetectotType == "P46" || dev.DetectotType == "P49" || dev.DetectotType == "P41" || dev.DetectotType == "P42");
                dev.requires102Restore = RuntimeVariables.Restore102DBAfterUpgrade && (dev.DetectotType == "P48" || dev.DetectotType == "P47");

                await UpgradeDeviceAsync(
                    dev, mac, dev.Pincode, dev.DetectotType, dev.FirmwareVersion,
                    dev.isActorUpgradeNeeded, dev.upgradeBootloader, dev.upgradeSensor, logId
                ).ConfigureAwait(false);

                int maxRetriesPerComponent = 5;

                bool CanRetryNow()
                {
                    // IMPORTANT: RetryCountActor/Sensor/Bootloader are incremented inside UpgradeDeviceAsync before each attempt.
                    // So we only *check* here, we do NOT increment here (avoids double-counting and off-by-one behavior).
                    bool actorOk =
                        !dev.isActorUpgradeNeeded || dev.ActorSuccess || dev.RetryCountActor < 2 * maxRetriesPerComponent;

                    bool bootOk =
                        !dev.upgradeBootloader || dev.BootloaderSuccess || dev.RetryCountBootloader < maxRetriesPerComponent;

                    bool sensorOk =
                        dev.SensorSuccess || dev.RetryCountSensor < maxRetriesPerComponent;

                    return actorOk && bootOk && sensorOk && dev.shouldRetry && dev.RetryCount <= 10;
                }

                while (!dev.IsFullyUpgraded)
                {
                    if (!CanRetryNow())
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


                deviceSw.Stop();
                addDeviceMs(deviceSw.ElapsedMilliseconds);

                AppLog.Warn($">>>> THREAD END - {mac} " +
                    $"actor:{dev.ActorSuccess}:{dev.RetryCountActor} " +
                    $"bootloader:{dev.BootloaderSuccess}:{dev.RetryCountBootloader} " +
                    $"sensor:{dev.SensorSuccess}:{dev.RetryCountSensor} " +
                    $"restore:{dev.isConfigRestored} " +
                    $"time:{deviceSw.Elapsed.TotalSeconds:F2}s");
summaries.Add(new DeviceUpgradeSummary
                {
                    Mac = mac,
                    DetectorType = dev.DetectotType ?? "",
                    TargetFw = dev.FirmwareVersion ?? "",
                    CurrentFw = dev.CurrentFirmwareVersion ?? "",

                    ActorNeeded = dev.isActorUpgradeNeeded,
                    BootloaderNeeded = dev.upgradeBootloader,

                    ActorSuccess = dev.ActorSuccess,
                    BootloaderSuccess = dev.BootloaderSuccess,
                    SensorSuccess = dev.SensorSuccess,
                    IsFullyUpgraded = dev.IsFullyUpgraded,
                    ConfigRestored = dev.isConfigRestored,

                    RetryTotal = dev.RetryCount,
                    RetryActor = dev.RetryCountActor,
                    RetryBootloader = dev.RetryCountBootloader,
                    RetrySensor = dev.RetryCountSensor,

                    Seconds = deviceSw.Elapsed.TotalSeconds,
                    Status = "OK"
                });

                UpgradeLogger.Log(logId, mac, "Device Upgrade Completed.", dev.finalUpgradeResult);
            }
            catch (Exception ex)
            {
                deviceSw.Stop();
                addDeviceMs(deviceSw.ElapsedMilliseconds);

                AppLog.Error($" {mac} - {ex.GetType().Name}: {ex.Message}");
summaries.Add(new DeviceUpgradeSummary
                {
                    Mac = mac,
                    DetectorType = dev.DetectotType ?? "",
                    TargetFw = dev.FirmwareVersion ?? "",
                    CurrentFw = dev.CurrentFirmwareVersion ?? "",

                    ActorNeeded = dev.isActorUpgradeNeeded,
                    BootloaderNeeded = dev.upgradeBootloader,

                    ActorSuccess = dev.ActorSuccess,
                    BootloaderSuccess = dev.BootloaderSuccess,
                    SensorSuccess = dev.SensorSuccess,
                    IsFullyUpgraded = dev.IsFullyUpgraded,
                    ConfigRestored = dev.isConfigRestored,

                    RetryTotal = dev.RetryCount,
                    RetryActor = dev.RetryCountActor,
                    RetryBootloader = dev.RetryCountBootloader,
                    RetrySensor = dev.RetryCountSensor,

                    Seconds = deviceSw.Elapsed.TotalSeconds,
                    Status = "ERROR",
                    Error = ex.ToString()
                });
            }
            finally
            {
                _macsInProgress.TryRemove(mac, out _);

				// Release chip assignment + lease
				_chipByMac.TryRemove(mac, out _);
				CassiaChipManager.ReleaseChip(mac);
				chipLease.Dispose();

                
_programmingTargets.TryRemove(mac, out _);
Interlocked.Decrement(ref UpgradeDevicesInProgress);
            }
        }

        private void PrintUpgradeRunSummary(
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


        public async Task<ServiceResponse> ProcessingSensorUpgrade(string nodeMac, bool bActor, bool isBootloader, string DetectorType, string FirmwareVersion, string logId) // should be moved to firmware services
        {
            AppLog.Info($"Processing Sensor Upgrade started->{nodeMac}");
var response = new ServiceResponse();

            var connProbe = await ConnectOnlyWithRetryAsync(
                maxAttempts: 5,
                delayMs: 2000,
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

        public async Task<ServiceResponse> ProcessingActorUpgrade(string nodeMac, bool bActor, string DetectorType, string FirmwareVersion, string logId) // should be moved to firmware services
        {
            var response = new ServiceResponse();
            const int maxRetryAttempts = 3; // Maximum number of retries to put the actor into boot mode
            const int delayBetweenRetries = 5000; // Delay between retries (in milliseconds)
            int retryCount = 0;

            // Step 1: Check if the actor is in boot mode
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

            // If after max retries the actor is still not in boot mode, return an error response
            if (retryCount >= maxRetryAttempts)
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
                UpgradeLogger.Log(logId, nodeMac, "ActorProgrammingComplete", "Success");
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Programming Failed";
                return response;
            }
        }

        public static UInt64 MacToInt64(string macAddress)
        {
            string hex = macAddress.Replace(":", "");
            return Convert.ToUInt64(hex, 16);
        }

        public static string MacToString(UInt64 macAddress)
        {
            return string.Join(":",
                                BitConverter.GetBytes(macAddress).Reverse()
                                .Select(b => b.ToString("X2"))).Substring(6);
        }
        Bootloader_Utils.CyBtldr_ProgressUpdate Upd = new Bootloader_Utils.CyBtldr_ProgressUpdate(CassiaFirmwareUpgradeService.ProgressUpdate);

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