using AccessAPP.Logging;

namespace AccessAPP.Services;

public partial class CassiaFirmwareUpgradeService
{
    // ── Pre-built BLE telegrams ───────────────────────────────────────────────
    // Type 0x040A (DaliCommandLogging), 1-byte payload: 0x01 = enable, 0x00 = disable.
    // Telegram layout: [0x01, type_lo, type_hi, len_lo, len_hi, crc_lo, crc_hi, payload]
    // CRC covers bytes 0-4.  start/stop share the same header CRC (payload is excluded).
    private static readonly string s_daliLogStartHex = BuildDaliLogCmd(enable: true);
    private static readonly string s_daliLogStopHex  = BuildDaliLogCmd(enable: false);

    // Expected response type for start/stop ACK
    private const ushort DaliLogAckType = 0x042C;

    /// <summary>
    /// Builds an 8-byte DaliCommandLogging telegram (type 0x040A) with the given enable flag.
    /// </summary>
    private static string BuildDaliLogCmd(bool enable)
    {
        const ushort type  = 0x040A;
        const ushort total = 8;                         // 7-byte header + 1-byte payload
        var h = new byte[total];
        h[0] = 0x01;
        h[1] = (byte)(type & 0xFF);
        h[2] = (byte)(type >> 8);
        h[3] = (byte)(total & 0xFF);
        h[4] = (byte)(total >> 8);
        ushort crc = PirPeakCrc16(h.AsSpan(0, 5));     // CRC only over header bytes 0-4
        h[5] = (byte)(crc & 0xFF);
        h[6] = (byte)(crc >> 8);
        h[7] = enable ? (byte)0x01 : (byte)0x00;
        return Convert.ToHexString(h);
    }

    // ── Public session entry point ────────────────────────────────────────────

    /// <summary>
    /// Connects to <paramref name="mac"/>, starts DALI bus logging, and streams raw 5-byte
    /// frames (as a hex string) to <paramref name="onBatch"/> every 500 ms until
    /// <paramref name="ct"/> is cancelled.  Sends the stop command and disconnects on exit.
    /// </summary>
    public async Task RunDaliLogSessionAsync(
        string mac,
        Func<string, Task> onBatch,
        CancellationToken ct)
    {
        var chip = GetChipForMac(mac);

        var pending     = new List<byte>();
        var pendingLock = new object();
        Guid? subToken  = null;

        // Duplicate suppression: Cassia gateway sometimes delivers the same BLE
        // notification 2–3 times in quick succession via SSE.  Track the last
        // seq byte added; skip a frame whose seq matches the most recent one.
        int _lastAddedSeq = -1;

        // ── Notification handler (fires from background thread) ───────────────
        void OnNotification(object? sender, string hexData)
        {
            try
            {
                var bytes = Convert.FromHexString(hexData);
                if (bytes.Length < 7) return;

                ushort teleType = (ushort)(bytes[1] | (bytes[2] << 8));
                if (teleType != 0x0442) return;         // ignore everything except DaliLogData

                int payloadLen = bytes.Length - 7;
                int entryCount = payloadLen / 5;
                if (entryCount == 0) return;

                lock (pendingLock)
                {
                    for (int i = 0; i < entryCount; i++)
                    {
                        int offset = 7 + i * 5;
                        byte seq = bytes[offset + 1];

                        // Skip exact-duplicate notifications: same seq as the frame
                        // most recently appended to this batch.
                        if (seq == _lastAddedSeq) continue;
                        _lastAddedSeq = seq;

                        pending.Add(bytes[offset]);
                        pending.Add(bytes[offset + 1]);
                        pending.Add(bytes[offset + 2]);
                        pending.Add(bytes[offset + 3]);
                        pending.Add(bytes[offset + 4]);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug($"[DaliLog] OnNotification parse error: {ex.Message}");
            }
        }

        try
        {
            // ── Connect + login ────────────────────────────────────────────────
            var cl = await ConnectAndLoginWithRetryForPipelineAsync(
                _gatewayIpAddress, _gatewayPort, mac, pincode: "",
                logId: null, firmwareVersion: null,
                maxAttempts: 2, delayBetweenAttemptsMs: 1000).ConfigureAwait(false);

            if (!cl.Success)
            {
                AppLog.Warn($"[DaliLog] Connect/login failed for {mac}: {cl.Message}");
                return;
            }

            // ── Subscribe to GATT notifications before sending start command ──
            // Persistent subscription receives all BLE notifications for this MAC.
            // Only 0x0442 (DaliLogData) frames are forwarded; the start-command ACK
            // (0x042C) that also fires this handler is silently ignored.
            subToken = _notificationService.Subscribe(mac, OnNotification);

            // ── Send start command (GetDataFromBleDevice handles the ACK) ─────
            await _connectService
                .GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, mac, s_daliLogStartHex)
                .ConfigureAwait(false);

            AppLog.Info($"[DaliLog] Session started for {mac}");

            // ── Batch loop: drain pending raw bytes every 500 ms ──────────────
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);

                byte[]? batch;
                lock (pendingLock)
                {
                    if (pending.Count == 0) continue;
                    batch = pending.ToArray();
                    pending.Clear();
                }

                await onBatch(Convert.ToHexString(batch)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal stop */ }
        catch (Exception ex)
        {
            AppLog.Warn($"[DaliLog] Session error for {mac}: {ex.Message}");
        }
        finally
        {
            // Unsubscribe first so stray notifications don't arrive during stop/disconnect
            if (subToken.HasValue)
                _notificationService.Unsubscribe(mac, subToken.Value);

            // Send stop command
            try
            {
                await _connectService
                    .GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, mac, s_daliLogStopHex)
                    .ConfigureAwait(false);
            }
            catch { }

            // Disconnect BLE
            try
            {
                await _connectService
                    .DisconnectFromBleDevice(_gatewayIpAddress, mac, chip: chip)
                    .ConfigureAwait(false);
            }
            catch { }

            AppLog.Info($"[DaliLog] Session stopped for {mac}");
        }
    }
}
