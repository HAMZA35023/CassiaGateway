using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AccessAPP.Logging;

namespace AccessAPP.Services
{
    public partial class CassiaFirmwareUpgradeService
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<byte[]>> _notificationQueues = new();
        private readonly ConcurrentDictionary<string, ManualResetEvent> _notificationEvents = new();
        // Per-MAC pending byte stream used by sensor/bootloader ReadData to assemble
        // exactly "size" bytes, even if notifications arrive split or coalesced.
        private readonly ConcurrentDictionary<string, Queue<byte>> _notificationPendingBytes = new();
        private readonly ConcurrentDictionary<string, object> _notificationPendingLocks = new();

        private object GetPendingLock(string macContext) =>
            _notificationPendingLocks.GetOrAdd(macContext, _ => new object());

        private static int GetProgrammingNotificationWaitMs()
        {
            int configured = RuntimeVariables.UPGRADE_PROGRAMMING_NOTIFICATION_WAIT_MS;
            return Math.Max(1000, configured);
        }

        private static bool WaitForNotificationOrQueuedData(
            string macContext,
            ConcurrentQueue<byte[]> notificationQueue,
            ManualResetEvent notificationEvent)
        {
            if (!notificationQueue.IsEmpty)
                return true;

            int timeoutMs = GetProgrammingNotificationWaitMs();
            var readSw = System.Diagnostics.Stopwatch.StartNew();
            bool signaled = notificationEvent.WaitOne(TimeSpan.FromMilliseconds(timeoutMs));
            readSw.Stop();
            AppLog.Debug($"[TIMING] ReadData notification wait: {readSw.ElapsedMilliseconds}ms | signaled={signaled} | mac={macContext}");

            // Guard against lost/reset event races: if the queue has data now, continue.
            if (signaled || !notificationQueue.IsEmpty)
                return true;

            AppLog.Warn($"ReadData timeout waiting for notification (mac={macContext}, timeoutMs={timeoutMs})");
            return false;
        }

        private static void SyncNotificationEventState(
            ConcurrentQueue<byte[]>? notificationQueue,
            ManualResetEvent? notificationEvent)
        {
            if (notificationEvent == null)
                return;

            if (notificationQueue != null && !notificationQueue.IsEmpty)
                notificationEvent.Set();
            else
                notificationEvent.Reset();
        }

        public int ReadData(IntPtr buffer, int size, UInt64 customContext)
        {
            string macContext = MacToString(customContext);
            ManualResetEvent? notificationEvent = null;
            ConcurrentQueue<byte[]>? notificationQueue = null;

            AppLog.Verbose("ReadData called here for actor and sensor | maccontext: " + macContext);

            try
            {
                if (!_notificationEvents.TryGetValue(macContext, out notificationEvent) || notificationEvent == null)
                    return ERR_READ;

                if (!_notificationQueues.TryGetValue(macContext, out notificationQueue) || notificationQueue == null)
                {
                    AppLog.Warn("ReadData failed: No notfication queue");
                    return ERR_READ;
                }

                if (size <= 0)
                    return ERR_READ;

                var pending = _notificationPendingBytes.GetOrAdd(macContext, _ => new Queue<byte>());
                var pendingLock = GetPendingLock(macContext);
                var payload = new byte[size];
                int copied = 0;
                int timeoutMs = GetProgrammingNotificationWaitMs();
                var deadlineUtc = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                var readSw = System.Diagnostics.Stopwatch.StartNew();

                while (copied < size)
                {
                    // 1) Consume already buffered bytes first.
                    lock (pendingLock)
                    {
                        while (copied < size && pending.Count > 0)
                            payload[copied++] = pending.Dequeue();
                    }

                    if (copied >= size)
                        break;

                    // 2) Pull next notification chunk (if available) into pending bytes.
                    if (notificationQueue.TryDequeue(out var chunk) && chunk != null && chunk.Length > 0)
                    {
                        lock (pendingLock)
                        {
                            foreach (var b in chunk)
                                pending.Enqueue(b);
                        }
                        continue;
                    }

                    // 3) No new chunk yet -> wait for a notification signal (bounded).
                    int remainingMs = (int)Math.Max(0, (deadlineUtc - DateTime.UtcNow).TotalMilliseconds);
                    if (remainingMs <= 0)
                    {
                        readSw.Stop();
                        AppLog.Warn($"ReadData timeout waiting for full frame (mac={macContext}, requested={size}, received={copied}, timeoutMs={timeoutMs}, elapsedMs={readSw.ElapsedMilliseconds})");
                        return ERR_READ;
                    }

                    bool signaled = notificationEvent.WaitOne(TimeSpan.FromMilliseconds(remainingMs));
                    if (!signaled)
                    {
                        readSw.Stop();
                        AppLog.Warn($"ReadData timeout waiting for notification (mac={macContext}, requested={size}, received={copied}, timeoutMs={timeoutMs}, elapsedMs={readSw.ElapsedMilliseconds})");
                        return ERR_READ;
                    }
                }

                Marshal.Copy(payload, 0, buffer, size);
                readSw.Stop();
                AppLog.Debug($"[TIMING] ReadData assembled frame: {readSw.ElapsedMilliseconds}ms | mac={macContext} | requested={size}");
                AppLog.Verbose($"Read data queue process {macContext} - size: {size} - " + BitConverter.ToString(payload).Replace("-", ""));
                AppLog.Verbose($"ReadData succeeded, bytes read: {size}");
                return ERR_SUCCESS;
            }
            finally
            {
                if (notificationQueue != null && notificationEvent != null)
                    SyncNotificationEventState(notificationQueue, notificationEvent);
            }
        }

        public static int ReadActorData(IntPtr buffer, int size, UInt64 customContext)
        {
            string macContext = MacToString(customContext);
            ManualResetEvent? notificationEvent = null;
            ConcurrentQueue<byte[]>? notificationQueue = null;

            AppLog.Verbose("ReadData called here for actor and sensor | maccontext: " + macContext);

            try
            {
                if (!_ownInstance._notificationEvents.TryGetValue(macContext, out notificationEvent) || notificationEvent == null)
                    return ERR_READ;

                if (!_ownInstance._notificationQueues.TryGetValue(macContext, out notificationQueue) || notificationQueue == null)
                {
                    AppLog.Warn("ReadData failed: No notfication queue");
                    return ERR_READ;
                }

                if (!WaitForNotificationOrQueuedData(macContext, notificationQueue, notificationEvent))
                    return ERR_READ;

                if (!notificationQueue.TryDequeue(out var notificationData))
                {
                    AppLog.Warn("ReadData failed: No data available in queue");
                    return ERR_READ;
                }

                AppLog.Verbose($"Read ACTOR data queue process {macContext} - size {size} - " + BitConverter.ToString(notificationData).Replace("-", ""));
                int bytesToSkip = 7;
                int bytesToCopy = Math.Min(size, notificationData.Length - bytesToSkip);
                if (notificationData.Length <= bytesToSkip)
                {
                    AppLog.Info($"Not enough data to skip {bytesToSkip} bytes. Copy operation skipped.");
                    return ERR_READ;
                }

                Marshal.Copy(notificationData, bytesToSkip, buffer, bytesToCopy);
                return ERR_SUCCESS;
            }
            finally
            {
                SyncNotificationEventState(notificationQueue, notificationEvent);
            }
        }
    }
}
