using System.Threading.Tasks;
using AccessAPP.Models;

namespace AccessAPP.Services
{
    public interface IDeviceSettingsBackupService
    {
        /// <summary>
        /// Reads settings (later via BLE) and stores them to a JSON file.
        /// Returns the created file path and the snapshot (if you want to log/inspect).
        /// </summary>
        Task<(string filePath, DeviceSettingsSnapshot snapshot)> BackupToFileAsync(
            string macAddress,
            string pincode,
            string detectorType,
            string firmwareVersion,
            string? logId);

        /// <summary>
        /// Loads settings from JSON file and writes them back (later via BLE).
        /// </summary>
        Task<ServiceResponse> RestoreFromFileAsync(
            string macAddress,
            string pincode,
            string detectorType,
            string firmwareVersion,
            string backupFilePath,
            string? logId);
    }
}
