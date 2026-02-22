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

        public int ReadData(IntPtr buffer, int size, UInt64 customContext)
                {
                    string macContext = MacToString(customContext);
                    ManualResetEvent _notificationEvent = null;
                    AppLog.Verbose("ReadData called here for actor and sensor | maccontext: " + macContext);
        try
                    {
                        // Wait for notification data to be available

                        if (_notificationEvents.TryGetValue(macContext, out _notificationEvent) && _notificationEvent != null)
                        {
                            //if (!_notificationEvent.WaitOne(TimeSpan.FromSeconds(15)))
                            //{
                            //   var resultEnable = _ownInstance._notificationService.EnableNotificationAsync("192.168.100.90", macContext, false);
                            //   resultEnable.Wait();
                            //   if (!resultEnable.Result)
                            //   {
                            //        Thread.Sleep(10000);
                            //        resultEnable = _ownInstance._notificationService.EnableNotificationAsync("192.168.100.90", macContext, false);
                            //        resultEnable.Wait();
                            //   }
                            //}

                            var _readSw = System.Diagnostics.Stopwatch.StartNew();
                            bool _readSignaled = _notificationEvent.WaitOne(TimeSpan.FromSeconds(20));
                            _readSw.Stop();
                            AppLog.Debug($"[TIMING] ReadData notification wait: {_readSw.ElapsedMilliseconds}ms | signaled={_readSignaled} | mac={macContext}");
                            if (!_readSignaled)
                            {
                                AppLog.Warn("ReadData timeout waiting for notification");
        //byte[] lastReadNotif = null;
                                //if (_ownInstance._lastNotificationDataRead.TryGetValue(macContext, out lastReadNotif) && lastReadNotif != null)
                                //{
                                // AppLog.Verbose($"Read data BACKUP {macContext} - " + BitConverter.ToString(lastReadNotif).Replace("-", ""));
        //    // Copy the notification data into the provided buffer
                                //    int bytesToCopy = Math.Min(size, lastReadNotif.Length);
                                //    Marshal.Copy(lastReadNotif, 0, buffer, bytesToCopy);

                                //    _ownInstance._lastNotificationDataRead.TryRemove(macContext, out _);

                                //    Thread.Sleep(5000);

                                //    // AppLog.Verbose($"ReadData succeeded, bytes read: {bytesToCopy}");
        //    return ERR_SUCCESS; // Success
                                //}
                                //else
                                {
                                    return ERR_READ; // Timeout or no data available
                                }
                            }
                        }
                        else
                        {
                            return ERR_READ; // Timeout or no data available
                        }

                        ConcurrentQueue<byte[]> _notificationQueue = null;
                        if (_notificationQueues.TryGetValue(macContext, out _notificationQueue) && _notificationQueue != null)
                        {


                            // Dequeue the notification data
                            if (_notificationQueue.TryDequeue(out var notificationData))
                            {
                                //_ownInstance._lastNotificationDataRead.TryRemove(macContext, out _);
                                    AppLog.Verbose($"Read data queue process {macContext} - size: {size} - " + BitConverter.ToString(notificationData).Replace("-", ""));
        // Copy the notification data into the provided buffer
                                int bytesToCopy = Math.Min(size, notificationData.Length);
                                Marshal.Copy(notificationData, 0, buffer, bytesToCopy);

                                //_ownInstance._lastNotificationDataRead.TryAdd(macContext, notificationData);

                                AppLog.Verbose($"ReadData succeeded, bytes read: {bytesToCopy}");
        return ERR_SUCCESS; // Success
                            }
                            else
                            {
                                AppLog.Warn("ReadData failed: No data available in queue");
        return ERR_READ; // No data available
                            }
                        }
                        else
                        {
                            AppLog.Warn("ReadData failed: No notfication queue");
        return ERR_READ; // No data available
                        }
                    }
                    finally
                    {
                        // Reset the event so it can wait for the next notification
                        if (_notificationEvent != null)
                        {
                            _notificationEvent.Reset();
                        }
                    }

                }

        public static int ReadActorData(IntPtr buffer, int size, UInt64 customContext)
                {
                    string macContext = MacToString(customContext);
                    ManualResetEvent _notificationEvent = null;
                    AppLog.Verbose("ReadData called here for actor and sensor | maccontext: " + macContext);
        try
                    {
                        // Wait for notification data to be available
                        if (_ownInstance._notificationEvents.TryGetValue(macContext, out _notificationEvent) && _notificationEvent != null)
                        {
                            //if (!_notificationEvent.WaitOne(TimeSpan.FromSeconds(15)))
                            //{
                            //    var resultEnable = _ownInstance._notificationService.EnableNotificationAsync("192.168.100.90", macContext, true);
                            //    resultEnable.Wait();
                            //    if (!resultEnable.Result)
                            //    {
                            //        Thread.Sleep(10000);
                            //        resultEnable = _ownInstance._notificationService.EnableNotificationAsync("192.168.100.90", macContext, true);
                            //        resultEnable.Wait();
                            //    }
                            //}

                            if (!_notificationEvent.WaitOne(TimeSpan.FromSeconds(20)))
                            {
                                byte[] lastReadNotif = null;
                                //if (_ownInstance._lastNotificationDataRead.TryGetValue(macContext, out lastReadNotif) && lastReadNotif != null)
                                //{
                                // AppLog.Verbose($"Read ACTOR BACKUP process {macContext} - " + BitConverter.ToString(lastReadNotif).Replace("-", ""));
        //    int bytesToSkip = 7;
                                //    int bytesToCopy = Math.Min(size, lastReadNotif.Length - bytesToSkip);

                                //    // Ensure there are enough bytes to skip
                                //    if (lastReadNotif.Length > bytesToSkip)
                                //    {
                                //        Marshal.Copy(lastReadNotif, bytesToSkip, buffer, bytesToCopy);
                                //        _ownInstance._lastNotificationDataRead.TryRemove(macContext, out _);
                                //        // AppLog.Verbose($"Skipped {bytesToSkip} bytes and copied {bytesToCopy} bytes.");
        //        Thread.Sleep(5000);
                                //        return ERR_SUCCESS;
                                //    }
                                //    else
                                //    {
                                // AppLog.Verbose($"Not enough data to skip {bytesToSkip} bytes. Copy operation skipped.");
        //        return ERR_READ; // Return an appropriate error code
                                //    }
                                //}
                                //else
                                {
                                    AppLog.Warn("ReadData timeout waiting for notification");
        return ERR_READ; // Timeout or no data available
                                }
                            }
                        }
                        else
                        {
                            return ERR_READ; // Timeout or no data available
                        }

                        ConcurrentQueue<byte[]> _notificationQueue = null;
                        if (_ownInstance._notificationQueues.TryGetValue(macContext, out _notificationQueue) && _notificationQueue != null)
                        {

                            // Dequeue the notification data
                            if (_notificationQueue.TryDequeue(out var notificationData))
                            {
                                //_ownInstance._lastNotificationDataRead.TryRemove(macContext, out _);
                                    AppLog.Verbose($"Read ACTOR data queue process {macContext} - size {size} - " + BitConverter.ToString(notificationData).Replace("-", ""));
        // Copy the notification data into the provided buffer
                                int bytesToSkip = 7;
                                int bytesToCopy = Math.Min(size, notificationData.Length - bytesToSkip);

                                // Ensure there are enough bytes to skip
                                if (notificationData.Length > bytesToSkip)
                                {
                                    Marshal.Copy(notificationData, bytesToSkip, buffer, bytesToCopy);
                                    //_ownInstance._lastNotificationDataRead.TryAdd(macContext, notificationData);
                                    // AppLog.Verbose($"Skipped {bytesToSkip} bytes and copied {bytesToCopy} bytes.");
        }
                                else
                                {
                                    AppLog.Info($"Not enough data to skip {bytesToSkip} bytes. Copy operation skipped.");
        return ERR_READ; // Return an appropriate error code
                                }


                                // AppLog.Verbose($"ReadData succeeded, bytes read: {bytesToCopy}");
        return ERR_SUCCESS; // Success
                            }
                            else
                            {
                                AppLog.Warn("ReadData failed: No data available in queue");
        return ERR_READ; // No data available
                            }
                        }
                        else
                        {
                            AppLog.Warn("ReadData failed: No notfication queue");
        return ERR_READ; // No data available
                        }
                    }
                    finally
                    {
                        // Reset the event so it can wait for the next notification
                        if (_notificationEvent != null)
                        {
                            _notificationEvent.Reset();
                        }
                    }

                }
    }
}
