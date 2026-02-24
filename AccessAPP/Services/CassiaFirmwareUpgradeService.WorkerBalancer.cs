using AccessAPP.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace AccessAPP.Services
{
    public partial class CassiaFirmwareUpgradeService
    {
        private sealed class WorkerBalancerState
        {
            public int LowStreak;
            public int RecoveryStreak;
            public bool IsSlow;
        }

        private readonly object _workerBalancerGate = new();
        private readonly Dictionary<string, WorkerBalancerState> _workerBalancerStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _workerBalancerSlowWorkers = new(StringComparer.OrdinalIgnoreCase);

        internal void OnWorkerRateSample(string macAddress, double ratePctPerMin, double progressPct)
        {
            var mac = NormalizeMac(macAddress);
            if (string.IsNullOrWhiteSpace(mac))
                return;

            if (!RuntimeVariables.UPGRADE_WORKER_BALANCER_ENABLED)
            {
                ClearWorkerBalancerStateForMac(mac);
                return;
            }

            var threshold = Math.Max(0.1, RuntimeVariables.UPGRADE_WORKER_BALANCER_SLOW_THRESHOLD_PCT_PER_MIN);
            var recoveryThreshold = threshold + 1.0;
            var minProgress = Math.Clamp(RuntimeVariables.UPGRADE_WORKER_BALANCER_MIN_PROGRESS_PCT, 0, 99);
            var lowStreakRequired = Math.Max(1, RuntimeVariables.UPGRADE_WORKER_BALANCER_LOW_STREAK);
            var recoveryStreakRequired = Math.Max(1, RuntimeVariables.UPGRADE_WORKER_BALANCER_RECOVERY_STREAK);
            var minActiveWorkers = Math.Max(2, RuntimeVariables.UPGRADE_WORKER_BALANCER_MIN_ACTIVE_WORKERS);
            var activeWorkers = Math.Max(0, Volatile.Read(ref UpgradeDevicesInProgress));

            lock (_workerBalancerGate)
            {
                if (!_workerBalancerStates.TryGetValue(mac, out var state))
                {
                    state = new WorkerBalancerState();
                    _workerBalancerStates[mac] = state;
                }

                if (progressPct >= 100.0 - 0.0001)
                {
                    RemoveWorkerSlowStateLocked(mac, "done");
                    _workerBalancerStates.Remove(mac);
                    return;
                }

                // Ignore startup/noise samples.
                if (progressPct < minProgress || ratePctPerMin <= 0.0)
                {
                    state.LowStreak = 0;
                    state.RecoveryStreak = 0;
                    return;
                }

                bool isLow = ratePctPerMin < threshold;
                if (isLow)
                {
                    state.LowStreak++;
                    state.RecoveryStreak = 0;
                }
                else if (ratePctPerMin >= recoveryThreshold)
                {
                    state.RecoveryStreak++;
                    state.LowStreak = 0;
                }
                else
                {
                    state.LowStreak = 0;
                    state.RecoveryStreak = 0;
                }

                if (!state.IsSlow &&
                    activeWorkers >= minActiveWorkers &&
                    state.LowStreak >= lowStreakRequired)
                {
                    state.IsSlow = true;
                    _workerBalancerSlowWorkers[mac] = 0;
                    AppLog.Warn($"[BALANCER] Slow worker detected: {mac} rate={ratePctPerMin:F2}%/min (<{threshold:F2}). active={activeWorkers}");
                    return;
                }

                if (state.IsSlow &&
                    (activeWorkers < minActiveWorkers || state.RecoveryStreak >= recoveryStreakRequired))
                {
                    RemoveWorkerSlowStateLocked(mac, activeWorkers < minActiveWorkers ? "active-workers-low" : "recovered");
                }
            }
        }

        internal void ClearWorkerBalancerStateForMac(string macAddress)
        {
            var mac = NormalizeMac(macAddress);
            if (string.IsNullOrWhiteSpace(mac))
                return;

            lock (_workerBalancerGate)
            {
                RemoveWorkerSlowStateLocked(mac, "clear");
                _workerBalancerStates.Remove(mac);
            }
        }

        internal int GetWorkerBalancerExtraDelayMs(string macAddress)
        {
            if (!RuntimeVariables.UPGRADE_WORKER_BALANCER_ENABLED)
                return 0;

            var reliefMs = Math.Max(0, RuntimeVariables.UPGRADE_WORKER_BALANCER_RELIEF_DELAY_MS);
            if (reliefMs <= 0 || _workerBalancerSlowWorkers.IsEmpty)
                return 0;

            var minActiveWorkers = Math.Max(2, RuntimeVariables.UPGRADE_WORKER_BALANCER_MIN_ACTIVE_WORKERS);
            var activeWorkers = Math.Max(0, Volatile.Read(ref UpgradeDevicesInProgress));
            if (activeWorkers < minActiveWorkers)
                return 0;

            var mac = NormalizeMac(macAddress);
            if (!string.IsNullOrWhiteSpace(mac) && _workerBalancerSlowWorkers.ContainsKey(mac))
                return 0;

            return reliefMs;
        }

        private void RemoveWorkerSlowStateLocked(string mac, string reason)
        {
            if (!_workerBalancerSlowWorkers.TryRemove(mac, out _))
                return;

            if (_workerBalancerStates.TryGetValue(mac, out var state))
            {
                state.IsSlow = false;
                state.LowStreak = 0;
                state.RecoveryStreak = 0;
            }

            AppLog.Info($"[BALANCER] Slow worker cleared: {mac} ({reason}).");
        }
    }
}
