using AccessAPP.Models;
using System.Collections.Concurrent;

namespace AccessAPP.Services
{
    public class DeviceStorageService
    {
        // Thread-safe dictionary to store devices by their MAC address
        private readonly ConcurrentDictionary<string, ScannedDevicesView> _deviceList = new();
        private readonly ConcurrentDictionary<string, FirmwareProgressStatus> _progressStatus = new();

        private readonly IMqttService _mqtt;

        // Per-MAC throttling to avoid MQTT spam
        private readonly ConcurrentDictionary<string, DateTime> _lastDevicePublishUtc = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastProgressPublishUtc = new();

        private static readonly TimeSpan DevicePublishInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ProgressPublishInterval = TimeSpan.FromMilliseconds(500);

        public DeviceStorageService(IMqttService mqtt)
        {
            _mqtt = mqtt;
        }

        // Add or update devices based on MAC address and filter by RSSI
        public void AddOrUpdateDevice(ScannedDevicesView device, int minRssi)
        {
            string macAddress = device.bdaddrs.FirstOrDefault()?.Bdaddr;
            if (string.IsNullOrEmpty(macAddress)) return;

            if (device.rssi <= minRssi)
            {
                _deviceList.AddOrUpdate(macAddress, device, (key, existingDevice) =>
                {
                    // Always update volatile fields like RSSI and name
                    existingDevice.rssi = device.rssi;
                    existingDevice.name = device.name;

                    // Only overwrite scanData-enriched fields if scanData is present
                    if (!string.IsNullOrEmpty(device.scanData))
                    {
                        existingDevice.scanData = device.scanData;
                        existingDevice.adData = device.adData;

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
                    }

                    return existingDevice;
                });

                Console.WriteLine($"Device {macAddress} added/updated with RSSI: {device.rssi}");

                // MQTT publish (throttled)
                PublishDeviceThrottled(macAddress, device);
            }
            else
            {
                if (_deviceList.ContainsKey(macAddress))
                {
                    _deviceList.TryRemove(macAddress, out _);
                    Console.WriteLine($"Device {macAddress} removed due to low RSSI: {device.rssi}");

                    // Optional: publish a log about removal
                    _ = SafeMqtt(async ct =>
                    {
                        await _mqtt.PublishLogAsync(new LogMessage
                        {
                            Level = "info",
                            Mac = macAddress,
                            Message = $"Device removed due to RSSI filter (rssi={device.rssi}, min={minRssi})"
                        }, ct);
                    });
                }
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

        private void PublishDeviceThrottled(string mac, ScannedDevicesView device)
        {
            var now = DateTime.UtcNow;

            if (_lastDevicePublishUtc.TryGetValue(mac, out var last) && (now - last) < DevicePublishInterval)
                return;

            _lastDevicePublishUtc[mac] = now;

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
