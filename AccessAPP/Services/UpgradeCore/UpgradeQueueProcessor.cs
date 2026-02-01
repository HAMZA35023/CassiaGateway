using AccessAPP.Logging;
using AccessAPP.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAPP.Services.UpgradeCore
{
    /// <summary>
    /// Live FIFO upgrade queue worker.
    ///
    /// Extracted from CassiaFirmwareUpgradeService.Queue.cs.
    /// No behavioral changes intended; this is a structural refactor only.
    /// </summary>
    internal sealed class UpgradeQueueProcessor
    {
        private readonly CassiaFirmwareUpgradeService _owner;
        private readonly ChipAllocationManager _chip;

        // ---------- QUEUE STATE ----------
        private readonly ConcurrentQueue<UpgradeProgress> _upgradeQueue = new();
        private readonly ConcurrentDictionary<string, byte> _queuedMacs = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _upgradeQueueGate = new();
        private readonly object _queueEditGate = new();
        private readonly SemaphoreSlim _queueSignal = new(0, int.MaxValue);
        private bool _upgradeQueueWorkerRunning;
        private Task? _upgradeQueueWorkerTask;

        public UpgradeQueueProcessor(CassiaFirmwareUpgradeService owner, ChipAllocationManager chip)
        {
            _owner = owner;
            _chip = chip;
        }

        public void OnParallelismChanged(int newValue)
        {
            try
            {
                var n = Math.Clamp(newValue, 1, 32);
                for (int i = 0; i < n; i++)
                    _queueSignal.Release();
            }
            catch
            {
                // ignore
            }
        }

        public Task EnqueueAsync(List<UpgradeProgress> devices, int numbersOfThreadsInParallel = -1)
        {
            if (devices == null || devices.Count == 0)
                return Task.CompletedTask;

            int queued = 0;
            int skippedDuplicate = 0;
            int skippedInvalid = 0;

            foreach (var dev in devices)
            {
                var mac = MacUtils.NormalizeMac(dev?.MacAddress);
                if (string.IsNullOrWhiteSpace(mac))
                {
                    skippedInvalid++;
                    continue;
                }

                // Already running anywhere?
                if (_owner.IsMacInProgress(mac))
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

                CassiaFirmwareUpgradeService.inQueue = _upgradeQueue.Count;
                queued++;

                AppLog.Info($"[UPGRADE QUEUE] ADDED: {mac} type:{dev.DetectotType ?? ""} tgt:{dev.FirmwareVersion ?? ""}");
                _queueSignal.Release();
            }

            AppLog.Info($"[UPGRADE QUEUE] Add request: in={devices.Count}, added={queued}, dup/ignored={skippedDuplicate}, invalid={skippedInvalid}, pending={_upgradeQueue.Count}");

            lock (_upgradeQueueGate)
            {
                if (_upgradeQueueWorkerRunning && _upgradeQueueWorkerTask is { IsCompleted: false })
                    return Task.CompletedTask;

                if (_upgradeQueue.IsEmpty)
                    return Task.CompletedTask;

                _upgradeQueueWorkerRunning = true;

                int threadsParam = numbersOfThreadsInParallel;
                int threadsEffective = threadsParam == -1 ? CassiaFirmwareUpgradeService.GlobalnumberOfParallelThreads : threadsParam;
                AppLog.Info($"[UPGRADE QUEUE] Worker START (threads={threadsEffective})");

                _upgradeQueueWorkerTask = Task.Run(() => UpgradeQueueWorkerLiveAsync(threadsParam));
                return _upgradeQueueWorkerTask;
            }
        }

        public List<(string Mac, string DetectorType, string TargetFw)> GetUpgradeQueueSnapshot()
        {
            return _upgradeQueue
                .ToArray()
                .Where(d => d is not null)
                .Select(d => (MacUtils.NormalizeMac(d!.MacAddress), d!.DetectotType ?? "", d!.FirmwareVersion ?? ""))
                .ToList();
        }

        public int ClearUpgradeQueue()
        {
            int removed = 0;

            lock (_queueEditGate)
            {
                while (_upgradeQueue.TryDequeue(out var dev))
                {
                    removed++;
                    var mac = MacUtils.NormalizeMac(dev?.MacAddress);
                    if (!string.IsNullOrWhiteSpace(mac))
                        _queuedMacs.TryRemove(mac, out _);
                }
            }

            CassiaFirmwareUpgradeService.inQueue = _upgradeQueue.Count;
            AppLog.Info($"[UPGRADE QUEUE] Cleared {removed} queued device(s). Pending now: {_upgradeQueue.Count}");
            return removed;
        }

        public int RemoveFromUpgradeQueue(string mac)
        {
            mac = MacUtils.NormalizeMac(mac);
            if (string.IsNullOrWhiteSpace(mac)) return 0;

            int removed = 0;

            lock (_queueEditGate)
            {
                if (!_queuedMacs.ContainsKey(mac))
                    return 0;

                var items = new List<UpgradeProgress>(capacity: Math.Max(16, _upgradeQueue.Count));
                while (_upgradeQueue.TryDequeue(out var dev))
                {
                    if (dev is null) continue;

                    var m = MacUtils.NormalizeMac(dev.MacAddress);
                    if (string.Equals(m, mac, StringComparison.OrdinalIgnoreCase) && removed == 0)
                    {
                        removed = 1;
                        continue;
                    }

                    items.Add(dev);
                }

                _queuedMacs.Clear();

                foreach (var dev in items)
                {
                    var m = MacUtils.NormalizeMac(dev.MacAddress);
                    if (!string.IsNullOrWhiteSpace(m))
                        _queuedMacs.TryAdd(m, 0);
                    _upgradeQueue.Enqueue(dev);
                }

                if (removed == 1)
                    _queuedMacs.TryRemove(mac, out _);
            }

            CassiaFirmwareUpgradeService.inQueue = _upgradeQueue.Count;
            if (removed == 1)
                AppLog.Info($"[UPGRADE QUEUE] Removed pending device: {mac}. Pending now: {_upgradeQueue.Count}");
            return removed;
        }

        // ------------------------------------------------------------------------------------
        // Worker loop (calls into the owner for per-device behavior)
        // ------------------------------------------------------------------------------------
        private async Task UpgradeQueueWorkerLiveAsync(int numbersOfThreadsInParallel)
        {
            var globalSw = Stopwatch.StartNew();
            var summaries = new ConcurrentBag<CassiaFirmwareUpgradeService.DeviceUpgradeSummary>();
            long totalDeviceMs = 0;
            int totalDevicesProcessed = 0;

            bool fixedParallel = numbersOfThreadsInParallel != -1;

            int CurrentMaxThreads()
            {
                if (fixedParallel)
                    return Math.Max(1, numbersOfThreadsInParallel);
                return Math.Max(1, Volatile.Read(ref CassiaFirmwareUpgradeService.GlobalnumberOfParallelThreads));
            }

            var running = new List<Task>(capacity: Math.Max(1, CurrentMaxThreads()));

            try
            {
                while (true)
                {
                    var maxThreads = CurrentMaxThreads();
                    while (running.Count < maxThreads)
                    {
                        UpgradeProgress? dev;
                        lock (_queueEditGate)
                        {
                            if (!_upgradeQueue.TryDequeue(out dev))
                                break;
                        }

                        CassiaFirmwareUpgradeService.inQueue = _upgradeQueue.Count;

                        var mac = MacUtils.NormalizeMac(dev?.MacAddress);
                        if (!string.IsNullOrWhiteSpace(mac))
                            _queuedMacs.TryRemove(mac, out _);

                        if (dev == null)
                            continue;

                        totalDevicesProcessed++;

                        ChipAllocationManager.ChipLease lease;
                        if (RuntimeVariables.USE_BOTH_CASSIA_CHIPS && maxThreads >= 2)
                            lease = await _chip.AcquireAsync().ConfigureAwait(false);
                        else
                            lease = ChipAllocationManager.Fixed(RuntimeVariables.DEFAULT_CASSIA_CHIP);

                        running.Add(_owner.ProcessSingleDeviceUpgradeAsync(dev, lease, summaries, ms => Interlocked.Add(ref totalDeviceMs, ms)));
                        CassiaFirmwareUpgradeService.inQueue = _upgradeQueue.Count;
                    }

                    for (int i = running.Count - 1; i >= 0; i--)
                    {
                        if (running[i].IsCompleted)
                            running.RemoveAt(i);
                    }

                    if (_upgradeQueue.IsEmpty && running.Count == 0)
                    {
                        bool gotLateSignal = await _queueSignal.WaitAsync(250).ConfigureAwait(false);
                        if (gotLateSignal)
                            continue;

                        lock (_upgradeQueueGate)
                        {
                            if (_upgradeQueue.IsEmpty)
                            {
                                _upgradeQueueWorkerRunning = false;
                                _upgradeQueueWorkerTask = null;
                                break;
                            }
                        }
                    }

                    if (!_upgradeQueue.IsEmpty && running.Count < CurrentMaxThreads())
                    {
                        // loop immediately
                        continue;
                    }

                    if (running.Count > 0)
                    {
                        var tasks = running.ToArray();
                        var completed = await Task.WhenAny(tasks).ConfigureAwait(false);

                        // If there is queued work, and we have capacity, we can wake faster via signal.
                        Task signal = _queueSignal.WaitAsync();
                        await Task.WhenAny(completed, signal).ConfigureAwait(false);
                    }
                    else
                    {
                        if (!_upgradeQueue.IsEmpty)
                            continue;
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

                var finalThreads = fixedParallel ? numbersOfThreadsInParallel : Math.Max(1, Volatile.Read(ref CassiaFirmwareUpgradeService.GlobalnumberOfParallelThreads));
                _owner.PrintUpgradeRunSummary(summaries, totalDevicesProcessed, totalDeviceMs, finalThreads, globalSw);
            }
        }
    }
}
