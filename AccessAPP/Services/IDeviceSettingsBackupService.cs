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

        /// <summary>
        /// Reads settings from a connected detector and returns an in-memory snapshot.
        /// </summary>
        Task<DeviceSettingsSnapshot> CaptureSnapshotAsync(
            string macAddress,
            string detectorType,
            string firmwareVersion);

        /// <summary>
        /// Applies an override patch by first reading current settings, merging selected fields,
        /// and then writing only changed sections back to the connected detector.
        /// </summary>
        Task<ServiceResponse> ApplyOverridesAsync(
            string macAddress,
            string detectorType,
            string firmwareVersion,
            DetectorSettingsPatch overrides,
            bool writeOnlyChanged = true);

        /// <summary>
        /// Returns the locally stored snapshot for the given MAC, or null if no backup file exists.
        /// </summary>
        Task<DeviceSettingsSnapshot?> TryGetSnapshotAsync(string macAddress);

        /// <summary>
        /// Saves a snapshot received from a peer to the local backup file.
        /// Returns the file path that was written.
        /// </summary>
        Task<string> SaveSnapshotAsync(DeviceSettingsSnapshot snapshot);
    }
}
