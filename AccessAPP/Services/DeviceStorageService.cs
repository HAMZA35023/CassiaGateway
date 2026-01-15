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

        private static readonly TimeSpan DevicePublishInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ProgressPublishInterval = TimeSpan.FromMilliseconds(1000);

        private readonly ConcurrentDictionary<string, RssiWindowState> _rssiState
    = new ConcurrentDictionary<string, RssiWindowState>();

        private readonly Timer _staleTimer;

        private static readonly TimeSpan RssiAverageWindow = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan StaleCheckInterval = TimeSpan.FromSeconds(30);

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
                    MarkStaleDevices();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DeviceStorage] MarkStaleDevices error: {ex.Message}");
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
                Console.WriteLine(
                    $"[SCAN UPDATE] MAC={macAddress} | " +
                    $"RSSI(avg3m)={avgRssi} | " +
                    $"ScanDataUpdate={(updatedFromScanData ? "YES" : "NO")}"
                );
                */
                return existingDevice;
            });


            //Console.WriteLine($"Device {macAddress} added/updated with RSSI(avg 3m): {avgRssi} (raw={device.rssi})");

            // MQTT publish (throttled) - publish the updated/averaged device
            // (If PublishDeviceThrottled uses the passed device, ensure it publishes avg.
            //  easiest: set device.rssi = avgRssi before publishing)
            device.rssi = avgRssi;
            PublishDeviceThrottled(macAddress, device);
        }

        private void MarkStaleDevices()
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var kvp in _deviceList)
            {
                var mac = kvp.Key;

                if (!_rssiState.TryGetValue(mac, out var state))
                    continue;

                bool isStale;
                lock (state.Gate)
                {
                    isStale = (now - state.LastSeenUtc) > StaleAfter;
                }

                if (!isStale)
                    continue;

                // Set RSSI to -127 if stale (do NOT remove)
                _deviceList.AddOrUpdate(mac,
                    _ => kvp.Value, // should not happen often, but safe
                    (_, existing) =>
                    {
                        if (existing.rssi != -127)
                        {
                            existing.rssi = -127;
                            Console.WriteLine($"Device {mac} is stale (>2m no announces). RSSI set to -127.");
                        }
                        PublishDeviceThrottled(mac, kvp.Value);

                        return existing;
                    });

                // Optional: publish stale update (if you want)
                //PublishDeviceThrottled(mac, kvp.Value);
            }
        }


        public void UpdateFirmwareProgress(string mac, double progress, string status = "Programming")
        {
            _progressStatus.AddOrUpdate(mac,
                new FirmwareProgressStatus
                {
                    MacAddress = mac,
                    Progress = progress,
                    Status = status,
                    LastUpdated = DateTime.UtcNow
                },
                (key, existing) =>
                {
                    existing.Progress = Math.Min(progress, 100);
                    existing.Status = status;
                    existing.LastUpdated = DateTime.UtcNow;
                    return existing;
                });

            // MQTT publish (throttled)
            PublishProgressThrottled(mac, Math.Min(progress, 100), status);
        }

        public void MarkFirmwareFailed(string mac)
        {
            _progressStatus.AddOrUpdate(mac,
                new FirmwareProgressStatus
                {
                    MacAddress = mac,
                    Progress = 0,
                    Status = "Failed",
                    LastUpdated = DateTime.UtcNow
                },
                (key, existing) =>
                {
                    existing.Status = "Failed";
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
                    Stage = "Failed"
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

        private void PublishDeviceThrottled(string mac, ScannedDevicesView device)
        {
            var now = DateTime.UtcNow;

            // 🔒 Global throttle (ALL MACs)
            if ((now - _lastGlobalDevicePublishUtc) < GlobalMinPublishInterval)
                return;

            // Optional: still keep per-MAC throttling if you want both
            if (_lastDevicePublishUtc.TryGetValue(mac, out var last) &&
                (now - last) < DevicePublishInterval)
                return;

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


        private void PublishProgressThrottled(string mac, double progress, string status)
        {
            var now = DateTime.UtcNow;

            if (_lastProgressPublishUtc.TryGetValue(mac, out var last) && (now - last) < ProgressPublishInterval)
                return;

            _lastProgressPublishUtc[mac] = now;

            _ = SafeMqtt(async ct =>
            {
                await _mqtt.PublishUpdateProgressAsync(new UpdateProgressMessage
                {
                    Mac = mac,
                    ProgressPercent = progress,
                    Stage = status
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
                Console.WriteLine($"[MQTT] publish failed: {ex.Message}");
            }
        }
    }
}
