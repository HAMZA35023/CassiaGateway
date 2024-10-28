using AccessAPP.Models;
using System.Collections.Concurrent;

namespace AccessAPP.Services
{
    public class DeviceStorageService
    {
        // Thread-safe dictionary to store devices by their MAC address
        private readonly ConcurrentDictionary<string, ScannedDevicesView> _deviceList = new ConcurrentDictionary<string, ScannedDevicesView>();

        // Add or update devices based on MAC address and filter by RSSI
        public void AddOrUpdateDevice(ScannedDevicesView device, int minRssi)
        {
            // Use the MAC address as the unique key
            string macAddress = device.bdaddrs.FirstOrDefault()?.Bdaddr;

            if (string.IsNullOrEmpty(macAddress)) return;

            // Only add or update the device if its RSSI is above the threshold
            if (device.rssi <= minRssi)
            {
                _deviceList.AddOrUpdate(macAddress, device, (key, existingDevice) =>
                {
                    // Update the existing device's RSSI or other details
                    existingDevice.rssi = device.rssi;
                    existingDevice.name = device.name;
                    // Add other fields you want to update
                    return existingDevice;
                });

                Console.WriteLine($"Device {macAddress} added/updated with RSSI: {device.rssi}");
            }
            else
            {
                // If the device is already in the list and now below the threshold, remove it
                if (_deviceList.ContainsKey(macAddress))
                {
                    _deviceList.TryRemove(macAddress, out _);
                    Console.WriteLine($"Device {macAddress} removed due to low RSSI: {device.rssi}");
                }
            }
        }

        // Get the list of devices
        public List<ScannedDevicesView> GetFilteredDevices()
        {
            return _deviceList.Values.ToList();
        }
    }



}
