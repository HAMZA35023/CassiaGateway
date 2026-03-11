using AccessAPP.Models;
using System.Collections.Concurrent;
using System.Net.Mail;

namespace AccessAPP.Services
{
    public class DeviceStorageService
    {
        private static DeviceStorageService? _ownInstance;

        // Thread-safe dictionary to store devices by their MAC address
        private readonly ConcurrentDictionary<string, ScannedDevicesView> _deviceList = new();
        private readonly ConcurrentDictionary<string, FirmwareProgressStatus> _progressStatus = new();

        private readonly IMqttService _mqtt;

        // Per-MAC throttling to avoid MQTT spam
        private readonly ConcurrentDictionary<string, DateTime> _lastDevicePublishUtc = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastProgressPublishUtc = new();

        // Track online/offline (stale) transitions so we can always publish a message
        // when a device goes offline or comes back.
        private readonly ConcurrentDictionary<string, bool> _isStale = new();

        private static readonly TimeSpan DevicePublishInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ProgressPublishInterval = TimeSpan.FromMilliseconds(1000);

        private readonly ConcurrentDictionary<string, RssiWindowState> _rssiState
    = new ConcurrentDictionary<string, RssiWindowState>();

        private readonly Timer _staleTimer;

        private static readonly TimeSpan RssiAverageWindow = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan StaleCheckInterval = TimeSpan.FromSeconds(30);
        // After this, the device is removed from the device list entirely (not just marked stale).
        // This prevents "get-device-list" from showing devices that are no longer available.
        private static readonly TimeSpan RemoveAfter = TimeSpan.FromMinutes(10);
        private readonly object _staleSuppressionGate = new();
        private bool _scanWasPausedUnderUpgrade;
        private DateTimeOffset _suppressStaleUntilUtc = DateTimeOffset.MinValue;

        private sealed class RssiSample
        {
            public DateTimeOffset Ts { get; init; }
            public int Rssi { get; init; }
        }

        private sealed class RssiWindowState
        {
            public readonly object Gate = new object();
            public readonly Queue<RssiSample> Samples = new Queue<RssiSample>();
            public DateTimeOffset LastSeenUtc = DateTimeOffset.MinValue;

            // Optional: keep a cached average so the stale-timer can set rssi=-127 without touching samples
            public int LastAverageRssi = -127;
        }

        public DeviceStorageService(IMqttService mqtt)
        {
            _mqtt = mqtt;
            _ownInstance = this;
            _staleTimer = new Timer(_ =>
            {
                try
                {
                    PruneStaleDevices();
                }
                catch (Exception ex)
                {
                    AppLog.Error($"[DeviceStorage] PruneStaleDevices error: {ex.Message}");
}
            }, null, dueTime: StaleCheckInterval, period: StaleCheckInterval);
        }

        public void Dispose()
        {
            _staleTimer?.Dispose();
            if (ReferenceEquals(_ownInstance, this))
                _ownInstance = null;
        }

        /// <summary>
        /// Returns the full device list snapshot in a single, serialization-friendly DTO.
        /// Intended for MQTT "get-device-list".
        /// </summary>
        public static IReadOnlyList<DeviceListItem> GetDeviceListSnapshot()
        {
            var inst = _ownInstance;
            if (inst is null)
                return Array.Empty<DeviceListItem>();

            // Ensure the snapshot does not include devices that have not been seen for a while.
            // (We already do this on a timer, but doing it here guarantees freshness even if
            //  the caller asks right before the next timer tick.)
            inst.PruneStaleDevices();

            var now = DateTimeOffset.UtcNow;

            // NOTE: values are mutable objects; copy fields into a DTO.
            return inst._deviceList
                .Select(kvp =>
                {
                    var mac = kvp.Key;
                    var d = kvp.Value;

                    DateTimeOffset lastSeenUtc = DateTimeOffset.MinValue;
                    int avgRssi = d?.rssi ?? -127;

                    if (inst._rssiState.TryGetValue(mac, out var state))
                    {
                        lock (state.Gate)
                        {
                            lastSeenUtc = state.LastSeenUtc;
                            avgRssi = state.LastAverageRssi;
                        }
                    }

                    bool isStale = lastSeenUtc != DateTimeOffset.MinValue && (now - lastSeenUtc) > StaleAfter;

                    return new DeviceListItem
                    {
                        MacAddress = mac,
                        Rssi = avgRssi,
                        DetectorType = d?.DetectorType,
                        DetectorFamily = d?.DetectorFamily,
                        ProductNumber = d?.ProductNumber,
                        Name = d?.name,
                        LastSeenUtc = lastSeenUtc,
                        IsStale = isStale
                    };
                })
                .OrderBy(x => x.MacAddress, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static int ClearAllDevices()
        {
            var inst = _ownInstance;
            if (inst is null)
                return 0;

            var removed = inst._deviceList.Count;
            inst._deviceList.Clear();
            inst._rssiState.Clear();
            inst._lastDevicePublishUtc.Clear();
            inst._lastProgressPublishUtc.Clear();
            inst._isStale.Clear();
            return removed;
        }

        public static bool TryGetDeviceName(string mac, out string name)
        {
            name = "";
            var inst = _ownInstance;
            if (inst is null) return false;
            if (string.IsNullOrWhiteSpace(mac)) return false;

            if (!inst._deviceList.TryGetValue(mac, out var dev) || dev is null)
                return false;

            name = (dev.name ?? "").Trim();
            return !string.IsNullOrWhiteSpace(name);
        }

        public static bool TryGetProductNumber(string mac, out string productNumber)
        {
            productNumber = "";
            var inst = _ownInstance;
            if (inst is null) return false;
            if (string.IsNullOrWhiteSpace(mac)) return false;

            if (!inst._deviceList.TryGetValue(mac, out var dev) || dev is null)
                return false;

            productNumber = (dev.ProductNumber ?? "").Trim();
            return !string.IsNullOrWhiteSpace(productNumber);
        }

        // Add or update devices based on MAC address and filter by RSSI
        public void AddOrUpdateDevice(ScannedDevicesView device, int minRssi)
        {
            // Keep minRssi as input, but DO NOT filter/remove based on it.
            _ = minRssi;

            string macAddress = device.bdaddrs.FirstOrDefault()?.Bdaddr;
            if (string.IsNullOrEmpty(macAddress)) return;

            var now = DateTimeOffset.UtcNow;

            // --- Update RSSI rolling window state (3 minutes average) ---
            var state = _rssiState.GetOrAdd(macAddress, _ => new RssiWindowState());

            int avgRssi;
            lock (state.Gate)
            {
                state.LastSeenUtc = now;

                // Add sample
                state.Samples.Enqueue(new RssiSample { Ts = now, Rssi = device.rssi });

                // Trim older than 3 minutes
                var cutoff = now - RssiAverageWindow;
                while (state.Samples.Count > 0 && state.Samples.Peek().Ts < cutoff)
                    state.Samples.Dequeue();

                // Compute average over remaining samples
                if (state.Samples.Count == 0)
                {
                    avgRssi = -127;
                }
                else
                {
                    long sum = 0;
                    foreach (var s in state.Samples) sum += s.Rssi;
                    avgRssi = (int)Math.Round(sum / (double)state.Samples.Count);
                }

                state.LastAverageRssi = avgRssi;
            }

            // --- Store/update device in global dictionary ---
            var wasStale = _isStale.TryGetValue(macAddress, out var s0) && s0;

            _deviceList.AddOrUpdate(macAddress, device, (key, existingDevice) =>
            {
                // RSSI is always updated (averaged)
                existingDevice.rssi = avgRssi;

                bool updatedFromScanData = false;

                // Only update name & enriched fields if scanData is present
                if (!string.IsNullOrEmpty(device.scanData))
                {
                    existingDevice.name = device.name;

                    existingDevice.bdaddrs = device.bdaddrs;
                    existingDevice.chipId = device.chipId;
                    existingDevice.evtType = device.evtType;
                    existingDevice.adData = device.adData;
                    existingDevice.scanData = device.scanData;

                    existingDevice.ProductNumber = device.ProductNumber;
                    existingDevice.DetectorFamily = device.DetectorFamily;
                    existingDevice.DetectorType = device.DetectorType;
                    existingDevice.DetectorOutputInfo = device.DetectorOutputInfo;
                    existingDevice.DetectorDescription = device.DetectorDescription;
                    existingDevice.DetectorShortDescription = device.DetectorShortDescription;
                    existingDevice.Range = device.Range;
                    existingDevice.DetectorMountDescription = device.DetectorMountDescription;
                    existingDevice.LockedHex = device.LockedHex;
                    existingDevice.IsLocked = device.IsLocked;

                    updatedFromScanData = true;
                }
                /*
                AppLog.Info($"[SCAN UPDATE] MAC={macAddress} | " +
                    $"RSSI(avg3m)={avgRssi} | " +
                    $"ScanDataUpdate={(updatedFromScanData ? "YES" : "NO")}");
*/
                return existingDevice;
            });


            AppLog.Verbose($"Device {macAddress} added/updated with RSSI(avg 3m): {avgRssi} (raw={device.rssi})");
// MQTT publish (throttled) - publish the updated/averaged device
            // (If PublishDeviceThrottled uses the passed device, ensure it publishes avg.
            //  easiest: set device.rssi = avgRssi before publishing)
            device.rssi = avgRssi;

            // If the device was previously stale/offline, always publish immediately when it comes back.
            // Otherwise, keep the normal throttled behavior.
            if (wasStale)
                _isStale[macAddress] = false;

            PublishDevice(macAddress, device, force: wasStale);
        }

        private void MarkStaleDevices()
        {
            // Timer tick: mark stale (RSSI=-127) and remove very old devices.
            PruneStaleDevices();
        }

        private static bool IsScanPausedUnderUpgrade()
            => !RuntimeVariables.BLE_SCAN_UNDER_PROGRAMMING &&
               CassiaFirmwareUpgradeService.GetProgrammingCount() > 1;

        private static TimeSpan GetStaleAfterScanResumeDelay()
        {
            var delayMs = Math.Max(0, RuntimeVariables.BLE_STALE_DELAY_AFTER_SCAN_RESUME_MS);
            return TimeSpan.FromMilliseconds(delayMs);
        }

        private bool ShouldSuppressStaleUpdates(DateTimeOffset now)
        {
            var scanPaused = IsScanPausedUnderUpgrade();

            lock (_staleSuppressionGate)
            {
                if (scanPaused)
                {
                    _scanWasPausedUnderUpgrade = true;
                    return true;
                }

                if (_scanWasPausedUnderUpgrade)
                {
                    var staleAfterScanResumeDelay = GetStaleAfterScanResumeDelay();
                    _scanWasPausedUnderUpgrade = false;
                    _suppressStaleUntilUtc = now.Add(staleAfterScanResumeDelay);
                    AppLog.Info($"[DeviceStorage] Scan resumed. Delaying stale device updates for {staleAfterScanResumeDelay.TotalSeconds:0}s.");
                }

                return now < _suppressStaleUntilUtc;
            }
        }

        private void PruneStaleDevices()
        {
            var now = DateTimeOffset.UtcNow;
            var suppressStaleUpdates = ShouldSuppressStaleUpdates(now);

            foreach (var kvp in _deviceList)
            {
                var mac = kvp.Key;

                if (!_rssiState.TryGetValue(mac, out var state))
                    continue;

                DateTimeOffset lastSeen;
                lock (state.Gate)
                {
                    lastSeen = state.LastSeenUtc;
                }

                if (lastSeen == DateTimeOffset.MinValue)
                    continue;

                // 1) Mark as stale after StaleAfter
                if (!suppressStaleUpdates && (now - lastSeen) > StaleAfter)
                {
                    _deviceList.AddOrUpdate(mac,
                        _ => kvp.Value,
                        (_, existing) =>
                        {
                            if (existing.rssi != -127)
                            {
                                existing.rssi = -127;
                                AppLog.Info($"Device {mac} is stale (>{StaleAfter.TotalMinutes:0}m no announces). RSSI set to -127.");
// Always notify stale/offline transition.
                                _isStale[mac] = true;
                                PublishDevice(mac, existing, force: true);
                                return existing;
                            }
                            // Already stale; keep normal throttled publishes.
                            PublishDevice(mac, existing, force: false);
                            return existing;
                        });
                }

                // 2) Remove entirely after RemoveAfter
                if ((now - lastSeen) > RemoveAfter)
                {
                    if (_deviceList.TryRemove(mac, out _))
                    {
                        _rssiState.TryRemove(mac, out _);
                        _lastDevicePublishUtc.TryRemove(mac, out _);
                        _isStale.TryRemove(mac, out _);
                        AppLog.Info($"Device {mac} removed from device-list (>{RemoveAfter.TotalMinutes:0}m not seen).");
}
                }
            }
        }


        public void UpdateFirmwareProgress(string mac, double progress, string status = "Programming", double? speedPctPerMin = null, string? targetFirmwareVersion = null, string? detectorType = null, string? finalResult = null)
        {
            _progressStatus.AddOrUpdate(mac,
                new FirmwareProgressStatus
                {
                    MacAddress = mac,
                    Progress = progress,
                    Status = status,
                    SpeedPctPerMin = speedPctPerMin,
                    LastUpdated = DateTime.UtcNow,
                    TargetFirmwareVersion = targetFirmwareVersion,
                    DetectorType = detectorType,
                    FinalResult = finalResult
                },
                (key, existing) =>
                {
                    existing.Progress = Math.Min(progress, 100);
                    existing.Status = status;
                    existing.SpeedPctPerMin = speedPctPerMin;
                    existing.LastUpdated = DateTime.UtcNow;
                    if (targetFirmwareVersion != null) existing.TargetFirmwareVersion = targetFirmwareVersion;
                    if (detectorType != null) existing.DetectorType = detectorType;
                    if (finalResult != null) existing.FinalResult = finalResult;
                    return existing;
                });

            // MQTT publish (throttled, but always sends 100%)
            var p = Math.Min(progress, 100);
            PublishProgressThrottled(mac, p, status, speedPctPerMin);
        }

        public void MarkFirmwareFailed(string mac)
        {
            _progressStatus.AddOrUpdate(mac,
                new FirmwareProgressStatus
                {
                    MacAddress = mac,
                    Progress = 0,
                    Status = "Failed",
                    SpeedPctPerMin = null,
                    LastUpdated = DateTime.UtcNow
                },
                (key, existing) =>
                {
                    existing.Status = "Failed";
                    existing.SpeedPctPerMin = null;
                    existing.LastUpdated = DateTime.UtcNow;
                    return existing;
                });

            // Send progress + log immediately (no throttle on failure)
            _ = SafeMqtt(async ct =>
            {
                await _mqtt.PublishUpdateProgressAsync(new UpdateProgressMessage
                {
                    Mac = mac,
                    ProgressPercent = 0,
                    Stage = "Failed",
                    SpeedPctPerMin = null
                }, ct);

                await _mqtt.PublishLogAsync(new LogMessage
                {
                    Level = "error",
                    Mac = mac,
                    Message = "Firmware update failed"
                }, ct);
            });
        }

        public List<FirmwareProgressStatus> GetAllFirmwareProgress()
        {
            return _progressStatus.Values.ToList();
        }

        public bool RemoveFirmwareProgress(string mac)
        {
            return _progressStatus.TryRemove(mac, out _);
        }

        public int ClearQueuedProgress()
        {
            var queued = _progressStatus.Where(kvp => kvp.Value.Status == "Queued").Select(kvp => kvp.Key).ToList();
            int removed = 0;
            foreach (var mac in queued)
                if (_progressStatus.TryRemove(mac, out _)) removed++;
            return removed;
        }

        // Get the list of devices
        public List<ScannedDevicesView> GetFilteredDevices()
        {
            return _deviceList.Values
                .OrderByDescending(d => d.rssi)
                .ToList();
        }

        // ---------------- MQTT helpers ----------------

        private static readonly TimeSpan GlobalMinPublishInterval = TimeSpan.FromSeconds(1);
        private DateTime _lastGlobalDevicePublishUtc = DateTime.MinValue;

        private void PublishDevice(string mac, ScannedDevicesView device, bool force)
        {
            var now = DateTime.UtcNow;

            if (!force)
            {
                // 🔒 Global throttle (ALL MACs)
                if ((now - _lastGlobalDevicePublishUtc) < GlobalMinPublishInterval)
                    return;

                // Per-MAC throttle
                if (_lastDevicePublishUtc.TryGetValue(mac, out var last) &&
                    (now - last) < DevicePublishInterval)
                    return;
            }

            _lastDevicePublishUtc[mac] = now;
            _lastGlobalDevicePublishUtc = now;

            _ = SafeMqtt(async ct =>
            {
                await _mqtt.PublishDiscoveredDevicesAsync(new DiscoveredDevicesMessage
                {
                    Devices =
                    {
                        new DiscoveredDevice
                        {
                            Mac = mac,
                            Rssi = device.rssi,
                            DetectorType = device.DetectorType,
                            DetectorFamily = device.DetectorFamily,
                            ProductNumber = device.ProductNumber,
                            Name = device.name
                        }
                    }
                }, ct);
            });
        }


        private void PublishProgressThrottled(string mac, double progress, string status, double? speedPctPerMin)
        {
            var now = DateTime.UtcNow;

            bool force = progress >= 100.0 - 0.0001;
            if (!force)
            {
                if (_lastProgressPublishUtc.TryGetValue(mac, out var last) && (now - last) < ProgressPublishInterval)
                    return;
            }

            _lastProgressPublishUtc[mac] = now;

            _ = SafeMqtt(async ct =>
            {
                await _mqtt.PublishUpdateProgressAsync(new UpdateProgressMessage
                {
                    Mac = mac,
                    ProgressPercent = progress,
                    Stage = status,
                    SpeedPctPerMin = speedPctPerMin
                }, ct);
            });
        }

        private static async Task SafeMqtt(Func<CancellationToken, Task> action)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await action(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[MQTT] publish failed: {ex.Message}");
}
        }
    }
}
