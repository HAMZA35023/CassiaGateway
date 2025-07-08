using AccessAPP.Models;
using System.Collections.Concurrent;

namespace AccessAPP.Services
{
    public class DeviceStorageService
    {
        // Thread-safe dictionary to store devices by their MAC address
        private readonly ConcurrentDictionary<string, ScannedDevicesView> _deviceList = new ConcurrentDictionary<string, ScannedDevicesView>();
        private readonly ConcurrentDictionary<string, FirmwareProgressStatus> _progressStatus = new ConcurrentDictionary<string, FirmwareProgressStatus>();

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
            }
            else
            {
                if (_deviceList.ContainsKey(macAddress))
                {
                    _deviceList.TryRemove(macAddress, out _);
                    Console.WriteLine($"Device {macAddress} removed due to low RSSI: {device.rssi}");
                }
            }
        }


        public void UpdateFirmwareProgress(string mac, double progress, string status= "Programming")
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
        }

        public void MarkFirmwareFailed(string mac)
        {
            _progressStatus.AddOrUpdate(mac,
                new FirmwareProgressStatus
                {
                    MacAddress = mac,
                    Progress = 0, // fallback if not already tracked
                    Status = "Failed",
                    LastUpdated = DateTime.UtcNow
                },
                (key, existing) =>
                {
                    existing.Status = "Failed";
                    existing.LastUpdated = DateTime.UtcNow;
                    return existing;
                });
        }

        public List<FirmwareProgressStatus> GetAllFirmwareProgress()
        {
            return _progressStatus.Values.ToList();
        }

        // Get the list of devices
        public List<ScannedDevicesView> GetFilteredDevices()
        {
            return _deviceList.Values.ToList();
        }
    }



}
