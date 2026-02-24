using AccessAPP.Logging;
using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using System;
using System.Collections.Concurrent;
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
        public async Task<IdentifyResult> IdentifyDeviceAsync(
            string macAddress,
            string? pincode,
            int secondsToStayConnected = 15,
            int maxConnectAttempts = 1,
            CancellationToken ct = default,
            Func<object, Task>? report = null)
        {
            var mac = (macAddress ?? string.Empty).Trim();
            var result = new IdentifyResult { Mac = mac };

            async Task SafeReportAsync(object payload)
            {
                if (report == null) return;
                try { await report(payload).ConfigureAwait(false); }
                catch { /* ignore reporting errors */ }
            }

            if (string.IsNullOrWhiteSpace(mac))
            {
                result.Success = false;
                result.Mac = macAddress ?? "";
                result.Error = "Missing mac address";
                await SafeReportAsync(new
                {
                    success = false,
                    stage = "failed",
                    mac = result.Mac,
                    errorStep = "validate",
                    error = result.Error,
                    time = DateTimeOffset.UtcNow
                }).ConfigureAwait(false);
                return result;
            }

            secondsToStayConnected = secondsToStayConnected <= 0 ? 15 : secondsToStayConnected;
            maxConnectAttempts = maxConnectAttempts <= 0 ? 1 : maxConnectAttempts;

            bool connected = false;

            try
            {
                // 1) Connect with retry
                ResponseModel? lastConnect = null;
                for (int attempt = 1; attempt <= maxConnectAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();

                    lastConnect = await _connectService
						.ConnectToBleDevice(_gatewayIpAddress, _gatewayPort, mac, chip: GetChipForMac(mac))
                        .ConfigureAwait(false);

                    if (lastConnect != null && lastConnect.Status == HttpStatusCode.OK)
                    {
                        connected = true;
                        result.Connected = true;
                        await SafeReportAsync(new
                        {
                            success = true,
                            stage = "connected",
                            mac = mac,
                            time = DateTimeOffset.UtcNow,
                            connectAttempt = attempt,
                            maxConnectAttempts
                        }).ConfigureAwait(false);
                        break;
                    }

                    result.ErrorStep = "connect";
                    result.Error = lastConnect?.Data ?? "Connect failed";

                    if (attempt < maxConnectAttempts)
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                }

                if (!connected)
                {
                    result.Success = false;
                    await SafeReportAsync(new
                    {
                        success = false,
                        stage = "failed",
                        mac = mac,
                        time = DateTimeOffset.UtcNow,
                        errorStep = result.ErrorStep,
                        error = result.Error,
                        maxConnectAttempts
                    }).ConfigureAwait(false);
                    return result;
                }

                // 2) Detect boot mode
                result.IsBootMode = CheckIfDeviceInBootMode(_gatewayIpAddress, mac);

                await SafeReportAsync(new
                {
                    success = true,
                    stage = "bootmode-check",
                    mac = mac,
                    time = DateTimeOffset.UtcNow,
                    isBootMode = result.IsBootMode
                }).ConfigureAwait(false);

                // 3) Optional pincode + login (skip in boot mode)
                if (result.IsBootMode)
                {
                    result.LoginSkippedBootMode = true;
                    result.PincodeOk = true; // not applicable in boot mode

                    await SafeReportAsync(new
                    {
                        success = true,
                        stage = "login-skipped",
                        reason = "bootmode",
                        mac = mac,
                        time = DateTimeOffset.UtcNow
                    }).ConfigureAwait(false);
                }
                else
                {
                    // Login (FIRST) – determines whether a pincode is required.
                    // We must NOT try pincode checks up front, because many devices never require it.
                    var login = await _connectService.AttemptLogin(_gatewayIpAddress, mac).ConfigureAwait(false);
                    var pincodeRequired = login?.ResponseBody?.PincodeRequired == true;
                    var pinAccepted = login?.ResponseBody?.PinCodeAccepted == true;
                    result.LoggedIn = login?.ResponseBody?.Status == HttpStatusCode.OK;

                    if (result.LoggedIn)
                    {
                        // Logged in without needing pincode (or it was already accepted).
                        result.PincodeOk = true;

                        await SafeReportAsync(new
                        {
                            success = true,
                            stage = "logged-in",
                            mac = mac,
                            time = DateTimeOffset.UtcNow,
                            pincodeRequired
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        // If pincode is required and not accepted, and we have a pincode, then try pincode + login again.
                        if (pincodeRequired && !pinAccepted)
                        {
                            await SafeReportAsync(new
                            {
                                success = true,
                                stage = "pincode-required",
                                mac = mac,
                                time = DateTimeOffset.UtcNow
                            }).ConfigureAwait(false);

                            if (string.IsNullOrWhiteSpace(pincode))
                            {
                                result.Success = false;
                                result.PincodeOk = false;
                                result.ErrorStep = "pincode";
                                result.Error = "Pincode required, but no pincode was provided.";

                                await SafeReportAsync(new
                                {
                                    success = false,
                                    stage = "failed",
                                    mac = mac,
                                    time = DateTimeOffset.UtcNow,
                                    errorStep = result.ErrorStep,
                                    error = result.Error
                                }).ConfigureAwait(false);
                                return result;
                            }

                            try
                            {
                                var check = await _cassiaPinCodeService
                                    .CheckPincode(_gatewayIpAddress, mac, pincode)
                                    .ConfigureAwait(false);

                                result.PincodeOk = check?.ResponseBody?.PinCodeAccepted == true;
                                if (!result.PincodeOk)
                                {
                                    result.Success = false;
                                    result.ErrorStep = "pincode";
                                    result.Error = check?.ResponseBody?.Data ?? "Pincode not accepted";

                                    await SafeReportAsync(new
                                    {
                                        success = false,
                                        stage = "failed",
                                        mac = mac,
                                        time = DateTimeOffset.UtcNow,
                                        errorStep = result.ErrorStep,
                                        error = result.Error
                                    }).ConfigureAwait(false);
                                    return result;
                                }

                                await SafeReportAsync(new
                                {
                                    success = true,
                                    stage = "pincode-ok",
                                    mac = mac,
                                    time = DateTimeOffset.UtcNow
                                }).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                result.Success = false;
                                result.PincodeOk = false;
                                result.ErrorStep = "pincode";
                                result.Error = ex.Message;

                                await SafeReportAsync(new
                                {
                                    success = false,
                                    stage = "failed",
                                    mac = mac,
                                    time = DateTimeOffset.UtcNow,
                                    errorStep = result.ErrorStep,
                                    error = result.Error
                                }).ConfigureAwait(false);
                                return result;
                            }

                            // Retry login after successful pincode.
                            var login2 = await _connectService.AttemptLogin(_gatewayIpAddress, mac).ConfigureAwait(false);
                            result.LoggedIn = login2?.ResponseBody?.Status == HttpStatusCode.OK;
                            if (!result.LoggedIn)
                            {
                                result.Success = false;
                                result.ErrorStep = "login";
                                result.Error = login2?.ResponseBody?.Data ?? (login2?.Status ?? "Login failed");

                                await SafeReportAsync(new
                                {
                                    success = false,
                                    stage = "failed",
                                    mac = mac,
                                    time = DateTimeOffset.UtcNow,
                                    errorStep = result.ErrorStep,
                                    error = result.Error
                                }).ConfigureAwait(false);
                                return result;
                            }

                            await SafeReportAsync(new
                            {
                                success = true,
                                stage = "logged-in",
                                mac = mac,
                                time = DateTimeOffset.UtcNow,
                                pincodeRequired = true,
                                pincodeUsed = true
                            }).ConfigureAwait(false);
                        }
                        else
                        {
                            // Login failed for another reason.
                            result.Success = false;
                            result.ErrorStep = "login";
                            result.Error = login?.ResponseBody?.Data ?? (login?.Status ?? "Login failed");

                            await SafeReportAsync(new
                            {
                                success = false,
                                stage = "failed",
                                mac = mac,
                                time = DateTimeOffset.UtcNow,
                                errorStep = result.ErrorStep,
                                error = result.Error
                            }).ConfigureAwait(false);
                            return result;
                        }
                    }
                }

                // 4) Hold connection
                var remaining = TimeSpan.FromSeconds(secondsToStayConnected);
                var step = TimeSpan.FromMilliseconds(250);
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < remaining)
                {
                    ct.ThrowIfCancellationRequested();
                    var delay = remaining - sw.Elapsed;
                    if (delay <= TimeSpan.Zero) break;
                    await Task.Delay(delay < step ? delay : step, ct).ConfigureAwait(false);
                }

                result.Success = true;
                result.SecondsConnected = secondsToStayConnected;

                await SafeReportAsync(new
                {
                    success = true,
                    stage = "holding",
                    mac = mac,
                    time = DateTimeOffset.UtcNow,
                    seconds = secondsToStayConnected
                }).ConfigureAwait(false);
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorStep = result.ErrorStep ?? "canceled";
                result.Error = "Operation canceled";

                await SafeReportAsync(new
                {
                    success = false,
                    stage = "failed",
                    mac = mac,
                    time = DateTimeOffset.UtcNow,
                    errorStep = result.ErrorStep,
                    error = result.Error
                }).ConfigureAwait(false);
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorStep = result.ErrorStep ?? "exception";
                result.Error = ex.Message;

                await SafeReportAsync(new
                {
                    success = false,
                    stage = "failed",
                    mac = mac,
                    time = DateTimeOffset.UtcNow,
                    errorStep = result.ErrorStep,
                    error = result.Error
                }).ConfigureAwait(false);
                return result;
            }
            finally
            {
                // Best-effort disconnect if we managed to connect.
                if (connected)
                {
                    try
                    {
						var resp = await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, mac, 0, chip: GetChipForMac(mac)).ConfigureAwait(false);
                        result.Disconnected = resp != null && resp.Status == HttpStatusCode.OK;
                    }
                    catch
                    {
                        result.Disconnected = false;
                    }

                    await SafeReportAsync(new
                    {
                        success = result.Disconnected,
                        stage = "disconnected",
                        mac = mac,
                        time = DateTimeOffset.UtcNow
                    }).ConfigureAwait(false);
                }
            }
        }

        public sealed class IdentifyResult
        {
            public bool Success { get; set; }
            public string Mac { get; set; } = "";
            public bool Connected { get; set; }
            public bool LoggedIn { get; set; }
            public bool LoginSkippedBootMode { get; set; }
            public bool IsBootMode { get; set; }
            public bool PincodeOk { get; set; } = true;
            public bool Disconnected { get; set; }
            public int SecondsConnected { get; set; }
            public string? ErrorStep { get; set; }
            public string? Error { get; set; }
        }

// ---------- QUEUE STATE ----------
// (moved to CassiaFirmwareUpgradeService.Queue.cs)

        

        

        /// <summary>
        /// Method that writes to the USB device
        /// </summary>
        /// <param name="buffer">Pointer to an array where data written to USB device is stored </param>
        /// <param name="size"> Size of the Buffer </param>
        /// <returns></returns>

        ///Sensor Programming

        private static int ResolveActorWriteDelayMs()
        {
            int actorDelay = RuntimeVariables.UPGRADE_ACTOR_WRITE_SLEEP_MS;
            if (actorDelay <= 0)
                actorDelay = RuntimeVariables.WRITE_SLEEP_MS;
            return Math.Max(0, actorDelay);
        }

        private static void SleepWithDynamicWriteDelay(string macAddress, int baseDelayMs)
        {
            int delayMs = Math.Max(0, baseDelayMs);
            int extraDelay = _ownInstance?.GetWorkerBalancerExtraDelayMs(macAddress) ?? 0;
            int total = delayMs + Math.Max(0, extraDelay);
            if (total > 0)
                Thread.Sleep(total);
        }

        public static int WriteSensorData(IntPtr buffer, int size, UInt64 customContext)
        {
            bool status = false;
            byte[] data = new byte[size];
            Marshal.Copy(buffer, data, 0, size);

            if (GetHidDevice())
            {
                string hexData = BitConverter.ToString(data).Replace("-", "");


                string macContext = MacToString(customContext);

                try
                {
                    var _writeSw = Stopwatch.StartNew();
					_ownInstance.cassiaReadWriteService.WriteBleMessageSync(_ownInstance._gatewayIpAddress, macContext, 14, hexData, "", chip: _ownInstance.GetChipForMac(macContext));
                    _writeSw.Stop();
                    AppLog.Debug($"[TIMING] WriteSensorData BLE write: {_writeSw.ElapsedMilliseconds}ms | mac={macContext} | size={size}");
                    SleepWithDynamicWriteDelay(macContext, ResolveActorWriteDelayMs());

                    status = true;
                }
                catch
                {
                }

                //second try
                if (!status)
                {
                    Thread.Sleep(1000);

                    try
                    {

                        // AppLog.Verbose($"Data Sent: {hexData} | macContext: {macContext}");
// AppLog.Verbose($"size of buffer: {size}");
//SendMessage(data);
                        AppLog.Info($"Trying again... (waited)");
_ownInstance.cassiaReadWriteService.WriteBleMessageSync(_ownInstance._gatewayIpAddress, macContext, 14, hexData, "", chip: _ownInstance.GetChipForMac(macContext));
                        SleepWithDynamicWriteDelay(macContext, ResolveActorWriteDelayMs());

                        status = true;
                    }
                    catch
                    {
                    }
                }

                if (status)
                    return ERR_SUCCESS;
                else
                    return ERR_WRITE;
            }
            else
                return ERR_WRITE;
        }

        ///Actor Programming
        public static int WriteActorData(IntPtr buffer, int size, UInt64 customContext)
        {
            bool status = false;
            byte[] data = new byte[size];
            Marshal.Copy(buffer, data, 0, size);

            // Log the data being written
            // AppLog.Verbose($"WriteData called: Buffer size={size} Data={BitConverter.ToString(data)}");
if (GetHidDevice())
            {
                // Prepare and send BLE message for actor
                BleMessage bleMessage = new BleMessage
                {
                    _BleMessageType = BleMessage.BleMsgId.ActorBootPacket,
                    _BleMessageDataBuffer = data
                };

                string macContext = MacToString(customContext);

                try
                {
                    // Encode the message
                    if (!bleMessage.EncodeGetBleTelegram())
                        throw new Exception("Failed to encode BLE telegram.");


                    // AppLog.Verbose($"macContext: {macContext}");
// Send the BLE message asynchronously
                    SendBleMessageAsync(bleMessage, macContext).GetAwaiter().GetResult();

                    status = true;
                }
                catch (Exception ex)
                {
                    AppLog.Error("Error in WriteData", ex);
}

                if (!status)
                {
                    Thread.Sleep(1000);
                    try
                    {
                        AppLog.Info($"Trying again... (waited)");
// Encode the message
                        if (!bleMessage.EncodeGetBleTelegram())
                            throw new Exception("Failed to encode BLE telegram.");

                        // AppLog.Verbose($"macContext: {macContext}");
// Send the BLE message asynchronously
                        SendBleMessageAsync(bleMessage, macContext).GetAwaiter().GetResult();

                        status = true;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("Error in WriteData", ex);
}
                }

                return status ? ERR_SUCCESS : ERR_WRITE;
            }
            else
            {
                return ERR_WRITE;
            }
        }

        private static async Task SendBleMessageAsync(BleMessage message, string macAddress)
        {
            // AppLog.Verbose($"Sending BLE message of size {message._BleMessageBuffer.Length}");
            int configuredChunkSize = RuntimeVariables.ACTOR_CHUNK_SIZE;
            if (configuredChunkSize <= 0)
                configuredChunkSize = 80;

            int interChunkSleepMs = RuntimeVariables.ACTOR_INTER_CHUNK_SLEEP_MS;
            if (interChunkSleepMs < 0)
                interChunkSleepMs = 0;

            if (message._BleMessageBuffer.Length > configuredChunkSize) // configurable chunking
            {
                int bytesSent = 0;
                int remainingBytes = message._BleMessageBuffer.Length;

                while (remainingBytes > 0)
                {
                    int chunkSize = Math.Min(configuredChunkSize, remainingBytes);
                    byte[] chunk = new byte[chunkSize];
                    Array.Copy(message._BleMessageBuffer, bytesSent, chunk, 0, chunkSize);

                    await SendChunk(chunk, macAddress);
                    bytesSent += chunkSize;
                    remainingBytes -= chunkSize;

                    // AppLog.Verbose($"Sent chunk of size {chunkSize}. Remaining: {remainingBytes}");
                    if (remainingBytes > 0)
                    {
                        SleepWithDynamicWriteDelay(macAddress, interChunkSleepMs);
                    }
                    else
                    {
                        SleepWithDynamicWriteDelay(macAddress, ResolveActorWriteDelayMs());
                    }

                }
            }
            else
            {
                await SendChunk(message._BleMessageBuffer, macAddress);
                //await Task.Delay(100); // Adjust delay as needed
                SleepWithDynamicWriteDelay(macAddress, ResolveActorWriteDelayMs());
            }
        }

        private static async Task SendChunk(byte[] chunk, string macAddress)
        {
            // Actual sending logic (e.g., via BLE GATT write)
            //CassiaReadWriteService cassiaReadWriteService = new CassiaReadWriteService();
            string hexData = BitConverter.ToString(chunk).Replace("-", "");
            // AppLog.Verbose($"Data Sent: {hexData} -> mac: {macAddress}");
// IMPORTANT: Always dispose the HTTP response to return the connection to the pool.
		    using var _ = await _ownInstance.cassiaReadWriteService.WriteBleMessageAsync(
		        _ownInstance._gatewayIpAddress,
		        macAddress,
		        19,
		        hexData,
		        "?noresponse=1",
		        chip: _ownInstance.GetChipForMac(macAddress));

        }


    }
}
