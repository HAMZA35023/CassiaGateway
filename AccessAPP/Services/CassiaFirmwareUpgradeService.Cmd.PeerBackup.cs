using AccessAPP.Logging;
using AccessAPP.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAPP.Services;

public partial class CassiaFirmwareUpgradeService
{
    /// <summary>
    /// Broadcasts a get-device-backup request to all peers on the same MQTT network and
    /// waits up to <paramref name="timeoutMs"/> milliseconds for a response containing the
    /// settings snapshot. Returns null if no peer responds in time.
    /// </summary>
    public async Task<DeviceSettingsSnapshot?> RequestPeerSnapshotAsync(
        string macAddress, string? logId, int timeoutMs = 8000)
    {
        if (_mqttService is null) return null;

        var requestId = Guid.NewGuid().ToString("N")[..8];
        var tcs = new TaskCompletionSource<DeviceSettingsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<DeviceBackupResponseCommand, Task> handler = cmd =>
        {
            if (string.Equals(cmd.RequestId, requestId, StringComparison.OrdinalIgnoreCase) &&
                cmd.Snapshot?.MacAddress != null)
            {
                tcs.TrySetResult(cmd.Snapshot);
            }
            return Task.CompletedTask;
        };

        _mqttService.DeviceBackupResponseReceived += handler;
        try
        {
            AppLog.Info($"[PeerBackup] Broadcasting get-device-backup for {macAddress} (requestId={requestId})");
            UpgradeLogger.Log(logId ?? "", macAddress, "Requesting peer backup via MQTT", "Info");

            await _mqttService.BroadcastDeviceBackupRequestAsync(new GetDeviceBackupCommand
            {
                MacAddress = macAddress,
                RequestId = requestId,
                RequesterName = _mqttService.CurrentOptions.Name
            }).ConfigureAwait(false);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                AppLog.Info($"[PeerBackup] No peer responded for {macAddress} within {timeoutMs}ms");
                UpgradeLogger.Log(logId ?? "", macAddress, "No peer backup found", "Info");
                return null;
            }

            var snapshot = await tcs.Task;
            AppLog.Info($"[PeerBackup] Received snapshot from peer for {macAddress}");
            return snapshot;
        }
        finally
        {
            _mqttService.DeviceBackupResponseReceived -= handler;
        }
    }
}
