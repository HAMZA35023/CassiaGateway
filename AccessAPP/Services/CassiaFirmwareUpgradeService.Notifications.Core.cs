using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using AccessAPP.Logging;

namespace AccessAPP.Services
{
    public partial class CassiaFirmwareUpgradeService
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<byte[]>> _notificationQueues = new();
        private readonly ConcurrentDictionary<string, ManualResetEvent> _notificationEvents = new();

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

                if (!WaitForNotificationOrQueuedData(macContext, notificationQueue, notificationEvent))
                    return ERR_READ;

                if (!notificationQueue.TryDequeue(out var notificationData))
                {
                    AppLog.Warn("ReadData failed: No data available in queue");
                    return ERR_READ;
                }

                AppLog.Verbose($"Read data queue process {macContext} - size: {size} - " + BitConverter.ToString(notificationData).Replace("-", ""));
                int bytesToCopy = Math.Min(size, notificationData.Length);
                Marshal.Copy(notificationData, 0, buffer, bytesToCopy);
                AppLog.Verbose($"ReadData succeeded, bytes read: {bytesToCopy}");
                return ERR_SUCCESS;
            }
            finally
            {
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
