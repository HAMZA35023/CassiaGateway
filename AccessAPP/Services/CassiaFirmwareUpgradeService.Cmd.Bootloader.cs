using AccessAPP.Logging;
using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using AccessAPP.Services.LinuxBle;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAPP.Services
{
    public partial class CassiaFirmwareUpgradeService
    {
        public async Task<bool> SendJumpToBootloader(string gatewayIpAddress, string nodeMac, bool bActor)
        {
            //var cassiaReadWrite = new CassiaReadWriteService();
            string value = "0101000800D9CB01";
            if (bActor)
            {
                value = "0101000800D9CB02";
            }

		    // IMPORTANT: Always dispose the HTTP response to return the connection to the pool.
		    using var response = await cassiaReadWriteService.WriteBleMessageAsync(gatewayIpAddress, nodeMac, 19, value, "?noresponse=1", chip: GetChipForMac(nodeMac));

            return response.IsSuccessStatusCode;
        }

        public bool CheckIfDeviceInBootMode(string gatewayIpAddress, string nodeMac)
        {
            if (RuntimeVariables.BLE_BACKEND.Equals("linux-native", StringComparison.OrdinalIgnoreCase))
                return CheckIfDeviceInBootModeLinux(nodeMac);

            int chip = GetChipForMac(nodeMac);
            string endpoint = $"http://{gatewayIpAddress}/gatt/nodes/{nodeMac}/characteristics?chip={chip}";

            HttpClient _httpClientTmp = new HttpClient();
            var maxAttempts = Math.Max(1, RuntimeVariables.BOOTMODE_RETRY_COUNT);
            var retryDelayMs = Math.Max(0, RuntimeVariables.BOOTMODE_RETRY_DELAY_MS);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    // Use synchronous version of HttpClient with GetAwaiter().GetResult()
                    using var response = _httpClientTmp.GetAsync(endpoint).GetAwaiter().GetResult();

                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        var jsonResponse = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        var characteristics = JsonConvert.DeserializeObject<List<CharacteristicModel>>(jsonResponse);

                        if (characteristics == null || characteristics.Count == 0)
                            return false;

                        // Cassia can transiently expose a mixed/old characteristic set right after reconnect.
                        // In that ambiguous state, prefer Application mode to avoid false-positive boot mode
                        // and a direct programming attempt on the wrong GATT profile.
                        bool hasBoot = characteristics.Any(charac =>
                            string.Equals(charac.Uuid, BlueZHelpers.BootNotifyUuid, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(charac.Uuid, BlueZHelpers.BootWriteUuid, StringComparison.OrdinalIgnoreCase));

                        bool hasApp = characteristics.Any(charac =>
                            string.Equals(charac.Uuid, BlueZHelpers.AppNotifyUuid, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(charac.Uuid, BlueZHelpers.AppWriteUuid, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(charac.Uuid, BlueZHelpers.AppServiceUuid, StringComparison.OrdinalIgnoreCase));

                        if (hasBoot && hasApp)
                        {
                            AppLog.Warn($"CheckIfDeviceInBootMode: ambiguous app+boot characteristic set for {nodeMac}; preferring application mode.");
                            return false;
                        }

                        return hasBoot;
                    }

                    return false;
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts && ex.InnerException is System.Net.Sockets.SocketException se && se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
                {
                    if (retryDelayMs > 0)
                        Thread.Sleep(retryDelayMs);
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Error checking boot mode for {nodeMac}", ex);
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Linux-native boot-mode check: queries BlueZ GATT objects for the
        /// boot-mode characteristic UUID instead of calling the Cassia REST API.
        /// </summary>
        private bool CheckIfDeviceInBootModeLinux(string nodeMac)
        {
            var devicePath = BlueZHelpers.DevicePath(BlueZHelpers.GetDeviceAdapter(nodeMac), nodeMac);

            var maxAttempts = Math.Max(1, RuntimeVariables.LINUX_BLE_BOOTMODE_RETRY_COUNT);
            var retryDelayMs = Math.Max(0, RuntimeVariables.LINUX_BLE_BOOTMODE_RETRY_DELAY_MS);
            var gattModeTimeoutMs = Math.Max(500, RuntimeVariables.LINUX_BLE_BOOTMODE_CHECK_TIMEOUT_MS);
            var bootCharLookupTimeoutMs = Math.Max(500, RuntimeVariables.LINUX_BLE_BOOTMODE_CHAR_LOOKUP_TIMEOUT_MS);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    // Use DetectModeByGattAsync which checks _modeCache first.
                    // ConnectToBleDevice pre-warms _modeCache with DetectModeByGattAsync
                    // before returning, so on the common path this is an instant cache hit
                    // with zero D-Bus calls — no delay between connect and login.
                    // On cache miss (e.g. ServicesResolved timed out during connect) it
                    // falls back to a full GATT scan bounded by a 5-second timeout.
                    using var bootCheckCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(gattModeTimeoutMs));
                    var mode = BlueZHelpers.DetectModeByGattAsync(devicePath, bootCheckCts.Token)
                        .WaitAsync(bootCheckCts.Token).GetAwaiter().GetResult();

                    if (mode == BlueZHelpers.BleMode.Bootloader)
                        return true;

                    // Fallback: when mode is still unknown right after reconnect/jump,
                    // do a direct boot characteristic lookup before concluding "not boot mode".
                    if ((mode == BlueZHelpers.BleMode.Unknown || mode == BlueZHelpers.BleMode.Application) &&
                        TryDetectBootCharacteristicLinux(devicePath, nodeMac, bootCharLookupTimeoutMs))
                    {
                        AppLog.Debug($"CheckIfDeviceInBootModeLinux: boot characteristic fallback detected for {nodeMac}.");
                        return true;
                    }

                    // Definitive non-boot result.
                    if (mode == BlueZHelpers.BleMode.Application)
                        return false;

                    // mode == Unknown without fallback hit: retry if attempts remain.
                    if (attempt < maxAttempts)
                    {
                        BlueZHelpers.InvalidateCharCache(devicePath);
                        if (retryDelayMs > 0)
                            Thread.Sleep(retryDelayMs);
                        continue;
                    }

                    return false;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    AppLog.Warn($"CheckIfDeviceInBootModeLinux: attempt {attempt} failed for {nodeMac}: {ex.Message}");
                    BlueZHelpers.InvalidateCharCache(devicePath);
                    if (retryDelayMs > 0) Thread.Sleep(retryDelayMs);
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Error checking boot mode (linux) for {nodeMac}", ex);
                    return false;
                }
            }

            return false;
        }

        private static bool TryDetectBootCharacteristicLinux(string devicePath, string nodeMac, int timeoutMs)
        {
            try
            {
                BlueZHelpers.ClearNotFoundUuidCache(devicePath, BlueZHelpers.BootNotifyUuid);
                BlueZHelpers.ClearNotFoundUuidCache(devicePath, BlueZHelpers.BootWriteUuid);

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(250, timeoutMs)));
                var notify = BlueZHelpers.GetCharacteristicByUuidAsync(
                    devicePath,
                    BlueZHelpers.BootServiceUuid,
                    BlueZHelpers.BootNotifyUuid,
                    cts.Token).WaitAsync(cts.Token).GetAwaiter().GetResult();

                if (notify.characteristic != null)
                    return true;

                var write = BlueZHelpers.GetCharacteristicByUuidAsync(
                    devicePath,
                    BlueZHelpers.BootServiceUuid,
                    BlueZHelpers.BootWriteUuid,
                    cts.Token).WaitAsync(cts.Token).GetAwaiter().GetResult();

                return write.characteristic != null;
            }
            catch (Exception ex)
            {
                AppLog.Debug($"CheckIfDeviceInBootModeLinux: fallback characteristic probe failed for {nodeMac}: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> ActorBootCheck(string gatewayIpAddress, string nodeMac)
        {
            try
            {
                string hexData = "0117000700D9E7"; // Command to trigger boot mode check
                //CassiaReadWriteService cassiaReadWriteService = new CassiaReadWriteService();

                var cassiaListener = _notificationService;
                {
                    var bootCheckResultTask = new TaskCompletionSource<bool>();

                    // Subscribe to notifications
                    cassiaListener.Subscribe(nodeMac, (sender, data) =>
                    {
                        // AppLog.Verbose($"Notification received for {nodeMac}: {data}");
// Parse notification data
                        byte[] notificationData = ParseHexStringToByteArray(data);

                        // Logic to verify boot mode based on the received data
                        if (notificationData != null && notificationData.Length > 0)
                        {
                            // Convert notification data to a string for comparison
                            string receivedHex = BitConverter.ToString(notificationData).Replace("-", "");

                            if (receivedHex == "0118000800092301") // Actor is in boot mode
                            {
                                AppLog.Info($"Actor {nodeMac} is in boot mode.");
bootCheckResultTask.TrySetResult(true);
                            }
                            else if (receivedHex == "0118000800092300") // Actor is not in boot mode
                            {
                                AppLog.Info($"Actor {nodeMac} is not in boot mode.");
bootCheckResultTask.TrySetResult(false);
                            }
                            else
                            {
                                AppLog.Warn($"Unexpected response received: {receivedHex}");
}
                        }
                    });

				// Send the write message to trigger the notification
				// IMPORTANT: Always dispose the HTTP response to return the connection to the pool.
				using var _ = await cassiaReadWriteService.WriteBleMessageAsync(gatewayIpAddress, nodeMac, 19, hexData, "?noresponse=1", chip: GetChipForMac(nodeMac));

                    // Wait for the boot check result or timeout
                    var bootCheckTask = bootCheckResultTask.Task;
                    var timeoutMs = Math.Max(30_000, RuntimeVariables.UPGRADE_ACTOR_BOOTMODE_CHECK_TIMEOUT_MS);
                    var timeoutTask = Task.Delay(timeoutMs);
                    var completedTask = await Task.WhenAny(bootCheckTask, timeoutTask);

                    // Unsubscribe from notifications
                    cassiaListener.Unsubscribe(nodeMac);

                    // Check if the boot check task completed
                    if (completedTask == bootCheckTask)
                    {
                        return await bootCheckTask;
                    }
                    else
                    {
                        // Handle timeout
                        AppLog.Warn($"ActorBootCheck timed out for {nodeMac}");
return false;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Error in ActorBootCheck", ex);
return false;
            }
        }

        private static void PurgeInstance(string macContext)
        {
            _lastInstanceRate.TryRemove(macContext, out _);
            _macRate10s.TryRemove(macContext, out _);

            // Optional: if you want to release lock objects too
            _macLocks.TryRemove(macContext, out _);
        }


        /// <summary>
        /// Method that updates the progres bar
        /// </summary>
        /// <param name="arrayID"></param>
        /// <param name="rowNum"></param>
        public static void ProgressUpdate(byte arrayID, ushort rowNum, UInt64 customContext)
        {
            static string Pad(string value, int width)
                => value.Length >= width ? value : value.PadRight(width);

            string key = $"{arrayID:X2}:{rowNum:X4}";
            string macContext = MacToString(customContext);

            var gate = _macLocks.GetOrAdd(macContext, _ => new object());

            double progress;

            lock (gate)
            {
                if (!completedRows.TryGetValue(macContext, out var completedRowsH) || completedRowsH == null)
                    return;

                if (!allRows.TryGetValue(macContext, out var allRowsH) || allRowsH == null || allRowsH.Count == 0)
                    return;

                // Only advance on first-seen row
                if (!completedRowsH.Add(key))
                    return;

                progress = Math.Min(
                    100.0,
                    (completedRowsH.Count / (double)allRowsH.Count) * 100.0
                );
            }

            // ---- DONE: purge instance so global speeds drop immediately ----
            if (progress >= 100.0 - 0.0001)
            {
                PurgeInstance(macContext);
                _ownInstance?.ClearWorkerBalancerStateForMac(macContext);

                double globalTotalAfterPurge = 0.0;
                foreach (var rate in _lastInstanceRate.Values)
                    globalTotalAfterPurge += rate;

                double globalAvgAfterPurge =
                    _lastInstanceRate.Count > 0
                        ? globalTotalAfterPurge / _lastInstanceRate.Count
                        : 0.0;

                AppLog.Debug($"{Pad($"[{macContext}]", 22)} " +
                    $"{Pad($"Array:{arrayID:X2}", 10)} " +
                    $"{Pad($"Row:{rowNum:X4}", 10)} | " +
                    $"{Pad($"{progress,6:F2}%", 8)} | " +
                    $"{Pad("DONE", 6)} | " +
                    $"{Pad("Total:", 8)} {globalTotalAfterPurge,7:F2}%/min | " +
                    $"{Pad("Avg:", 6)} {globalAvgAfterPurge,7:F2}%/min (10s avg)");
                var stage = _ownInstance?._programmingStageByMac.TryGetValue(macContext, out var s1) == true
                    ? s1
                    : "";
                var msg = string.IsNullOrWhiteSpace(stage) ? "Programming" : $"Programming {stage}";
                _deviceStorageService.UpdateFirmwareProgress(macContext, 100.0, msg, null);
                totalSpeed = Math.Round(globalTotalAfterPurge, 2);
                totalSpeedAvg10s = Math.Round(globalTotalAfterPurge, 2);
                return;
            }

            // ---- Per-instance rate (%/min, 10s avg) ----
            var tracker = _macRate10s.GetOrAdd(macContext, _ => new SlidingRate10s());
            var ratePerMinThisMac = tracker.AddAndGetRatePerMinute(progress);
            _ownInstance?.OnWorkerRateSample(macContext, ratePerMinThisMac, progress);

            // Cache latest rate so global speeds are correct
            _lastInstanceRate[macContext] = ratePerMinThisMac;

            // ---- Global total + average rates ----
            double globalTotalRatePerMin = 0.0;
            foreach (var rate in _lastInstanceRate.Values)
                globalTotalRatePerMin += rate;

            double globalAvgRatePerMin =
                _lastInstanceRate.Count > 0
                    ? globalTotalRatePerMin / _lastInstanceRate.Count
                    : 0.0;

            AppLog.Debug($"{Pad($"[{macContext}]", 22)} " +
                $"{Pad($"Array:{arrayID:X2}", 10)} " +
                $"{Pad($"Row:{rowNum:X4}", 10)} | " +
                $"{Pad($"{progress,6:F2}%", 8)} | " +
                $"{Pad($"{ratePerMinThisMac,7:F2}%/min", 14)} | " +
                $"{Pad("Total:", 8)} {globalTotalRatePerMin,7:F2}%/min | " +
                $"{Pad("Avg:", 6)} {globalAvgRatePerMin,7:F2}%/min (10s avg)");
            var stage2 = _ownInstance?._programmingStageByMac.TryGetValue(macContext, out var s2) == true
                ? s2
                : "";
            var msg2 = string.IsNullOrWhiteSpace(stage2) ? "Programming" : $"Programming {stage2}";
            _deviceStorageService.UpdateFirmwareProgress(macContext, progress, msg2, ratePerMinThisMac);
            totalSpeed = Math.Round(globalTotalRatePerMin, 2);
            totalSpeedAvg10s = Math.Round(globalTotalRatePerMin, 2);

        }


        public void SetTotalRows(int rows)
        {
            totalRows = rows > 0 ? rows : 1; // Avoid division by zero
        }


        public static bool GetHidDevice()
        {
            return (true);
        }

        /// <summary>
        /// Checks if the USB device is connected and opens if it is present
        /// Returns a success or failure
        /// </summary>
        public static int OpenConnection(UInt64 customContext)
        {
            int status = 0;
            status = GetHidDevice() ? ERR_SUCCESS : ERR_OPEN;

            return status;
        }

        /// <summary>
        /// Closes the previously opened USB device and returns the status
        /// </summary>
        public static int CloseConnection(UInt64 customContext)
        {
            int status = 0;
            return status;

        }

        public void InitializeNotificationSubscription(string macAddress, BleAbstractions.IBleNotificationService cassiaNotificationService)
        {
            // Unsubscribe from all previous subscriptions
            //foreach (var subscribedMac in _subscribedMacAddresses)
            //{
            //AppLog.Verbose($"Unsubscribing from notifications for {subscribedMac}");
//    cassiaNotificationService.Unsubscribe(subscribedMac);
            //}

            cassiaNotificationService.Unsubscribe(macAddress);

            ConcurrentQueue<byte[]> _tmpCheck = null;

            //if (_notificationQueues.TryGetValue(macAddress, out _tmpCheck) && _tmpCheck != null)
            {
                _notificationEvents.TryRemove(macAddress, out _);
                _notificationQueues.TryRemove(macAddress, out _);
                _notificationPendingBytes.TryRemove(macAddress, out _);
                _notificationPendingLocks.TryRemove(macAddress, out _);
                //_lastNotificationDataRead.TryRemove(macAddress, out _);
            }


            _notificationQueues.TryAdd(macAddress, new ConcurrentQueue<byte[]>());
            _notificationPendingBytes.TryAdd(macAddress, new Queue<byte>());

            _notificationEvents.TryAdd(macAddress, new ManualResetEvent(false));


            //// Clear the list of subscribed MAC addresses
            //_subscribedMacAddresses.Clear();

            //// Add the new MAC address to the subscribed set
            //_subscribedMacAddresses.Add(macAddress);

            // Subscribe to notifications for the new MAC address
            cassiaNotificationService.Subscribe(macAddress, (sender, data) =>
            {
                    AppLog.Verbose($"Notification received for {macAddress}: {data}");
// Parse the notification data into a byte array
                byte[] parsedData = ParseHexStringToByteArray(data);

                // Enqueue the data into the notification queue
                ConcurrentQueue<byte[]> _notificationQueue = null;
                if (_notificationQueues.TryGetValue(macAddress, out _notificationQueue) && _notificationQueue != null)
                {
                    _notificationQueue.Enqueue(parsedData);
                }

                // Signal that new data is available
                ManualResetEvent _notificationEvent = null;
                if (_notificationEvents.TryGetValue(macAddress, out _notificationEvent) && _notificationEvent != null)
                {
                    _notificationEvent.Set();
                }
            });
        }

        public void UnsubscribeNotification(string macAddress, BleAbstractions.IBleNotificationService cassiaNotificationService)
        {
            // Check if the MAC address is subscribed
            ConcurrentQueue<byte[]> _tmpCheck = null;

            if (_notificationQueues.TryGetValue(macAddress, out _tmpCheck) && _tmpCheck != null)
            //if (_subscribedMacAddresses.Contains(macAddress))
            {
                AppLog.Info($"Unsubscribing from notifications for {macAddress}");
cassiaNotificationService.Unsubscribe(macAddress);
                //_subscribedMacAddresses.Remove(macAddress);
                _notificationQueues.TryRemove(macAddress, out _tmpCheck);
                ManualResetEvent evt = null;
                _notificationEvents.TryRemove(macAddress, out evt);
                _notificationPendingBytes.TryRemove(macAddress, out _);
                _notificationPendingLocks.TryRemove(macAddress, out _);
                //_lastNotificationDataRead.TryRemove(macAddress, out _);
            }
        }


        private byte[] ParseHexStringToByteArray(string hexString)
        {
            int numberOfBytes = hexString.Length / 2;
            byte[] bytes = new byte[numberOfBytes];
            for (int i = 0; i < numberOfBytes; i++)
            {
                bytes[i] = Convert.ToByte(hexString.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

    }
}
