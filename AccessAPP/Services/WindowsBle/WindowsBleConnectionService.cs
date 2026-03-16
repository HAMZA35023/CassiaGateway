#if WINDOWS
using System.Net;
using AccessAPP.Models;
using AccessAPP.Services.BleAbstractions;
using AccessAPP.Services.HelperClasses;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace AccessAPP.Services.WindowsBle;

/// <summary>
/// <see cref="IBleConnectionService"/> implementation using Windows.Devices.Bluetooth WinRT APIs.
///
/// Maps Cassia-style connect/disconnect/login calls onto the Windows BLE stack.
/// Parameters <c>gatewayIpAddress</c>, <c>gatewayPort</c>, and <c>chip</c> are accepted for
/// interface compatibility but are not used — Windows manages the local adapter directly.
/// </summary>
public class WindowsBleConnectionService : IBleConnectionService
{
    private readonly IBleNotificationService _notificationService;
    private readonly ILogger<WindowsBleConnectionService> _logger;

    // Semaphore kept for interface compatibility (not used for Windows serialisation).
    public SemaphoreSlim semaphore { get; } = new SemaphoreSlim(1, 1);

    public WindowsBleConnectionService(
        IBleNotificationService notificationService,
        ILogger<WindowsBleConnectionService> logger)
    {
        _notificationService = notificationService;
        _logger = logger;
    }

    // ── Connect / Disconnect ─────────────────────────────────────────────────

    public async Task<ResponseModel> ConnectToBleDevice(
        string gatewayIpAddress, int gatewayPort, string macAddress,
        int chip = -1, bool useGlobalLock = true, int? discoverGattOverride = null,
        CancellationToken ct = default)
    {
        try
        {
            _logger.LogDebug("WinBLE Connect: {Mac} — opening device session", macAddress);

            var device = await WindowsBleHelpers.GetOrOpenDeviceAsync(macAddress, ct)
                .ConfigureAwait(false);

            if (device == null)
            {
                _logger.LogWarning("WinBLE Connect: {Mac} — device not found (not advertising?)", macAddress);
                return new ResponseModel
                {
                    MacAddress = macAddress,
                    Status = HttpStatusCode.NotFound,
                    Data = "Device not found — ensure it is advertising and in range.",
                    Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }

            // Trigger GATT discovery (uncached so we always get fresh service list).
            _logger.LogDebug("WinBLE Connect: {Mac} — discovering GATT services", macAddress);
            var svcResult = await device
                .GetGattServicesAsync(BluetoothCacheMode.Uncached)
                .AsTask(ct).ConfigureAwait(false);

            if (svcResult.Status != GattCommunicationStatus.Success)
            {
                _logger.LogWarning("WinBLE Connect: {Mac} — GATT discovery failed ({Status})", macAddress, svcResult.Status);
                WindowsBleHelpers.DisposeDevice(macAddress);
                return new ResponseModel
                {
                    MacAddress = macAddress,
                    Status = HttpStatusCode.ServiceUnavailable,
                    Data = $"GATT discovery failed: {svcResult.Status}",
                    Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }

            // Cache all discovered services and clear stale char handles.
            // NEVER dispose GattDeviceService proxies from Uncached scans — see
            // WindowsBleHelpers lifetime rule.  CacheDiscoveredServices handles both.
            WindowsBleHelpers.CacheDiscoveredServices(macAddress, svcResult.Services);

            // Pre-arm the notification pipeline before the first write (avoids missing the
            // login response notification that arrives quickly after the first write).
            if (_notificationService is WindowsBleNotificationService winNotify)
            {
                try
                {
                    int notifyWarmMs = Math.Clamp(RuntimeVariables.LINUX_BLE_LOGIN_NOTIFY_READY_TIMEOUT_MS, 1000, 10000);
                    using var notifyWarmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    notifyWarmCts.CancelAfter(notifyWarmMs);
                    await winNotify.EnsureNotifyingReadyAsync(macAddress, notifyWarmCts.Token)
                        .ConfigureAwait(false);
                    _logger.LogDebug("WinBLE Connect: {Mac} — notify pipeline pre-armed", macAddress);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "WinBLE Connect: {Mac} — pre-arm notify failed (non-fatal)", macAddress);
                }
            }

            // Final sanity check: confirm device is still connected.
            if (device.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                _logger.LogWarning("WinBLE Connect: {Mac} disconnected during post-connect setup", macAddress);
                WindowsBleHelpers.DisposeDevice(macAddress);
                return new ResponseModel
                {
                    MacAddress = macAddress,
                    Status = HttpStatusCode.ServiceUnavailable,
                    Data = "Device disconnected after connect",
                    Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }

            _logger.LogDebug("WinBLE Connect: {Mac} — connected OK", macAddress);
            return new ResponseModel
            {
                MacAddress = macAddress,
                Data = "connected",
                Status = HttpStatusCode.OK,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
        catch (OperationCanceledException)
        {
            WindowsBleHelpers.DisposeDevice(macAddress);
            return new ResponseModel
            {
                MacAddress = macAddress,
                Status = HttpStatusCode.RequestTimeout,
                Data = "Connect canceled",
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WinBLE Connect: ConnectToBleDevice failed for {Mac}", macAddress);
            WindowsBleHelpers.DisposeDevice(macAddress);
            return new ResponseModel
            {
                MacAddress = macAddress,
                Status = HttpStatusCode.ServiceUnavailable,
                Data = ex.Message,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
    }

    public Task<ResponseModel> DisconnectFromBleDevice(
        string gatewayIpAddress, string macAddress, int retries = 1, int chip = -1)
    {
        try
        {
            _notificationService.Unsubscribe(macAddress);
            WindowsBleHelpers.DisposeDevice(macAddress);
            _logger.LogDebug("WinBLE Disconnect: {Mac} — session disposed", macAddress);
            return Task.FromResult(new ResponseModel
            {
                MacAddress = macAddress,
                Status = HttpStatusCode.OK,
                Data = "disconnected"
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WinBLE Disconnect: {Mac} — error during disconnect (treating as success)", macAddress);
            return Task.FromResult(new ResponseModel
            {
                MacAddress = macAddress,
                Status = HttpStatusCode.OK,
                Data = "disconnect attempted"
            });
        }
    }

    // ── Login ────────────────────────────────────────────────────────────────

    public Task<LoginResponseModel> AttemptLogin(string gatewayIpAddress, string macAddress)
        => AttemptLoginAsync(gatewayIpAddress, macAddress, CancellationToken.None);

    public Task<LoginResponseModel> AttemptLogin(string gatewayIpAddress, string macAddress, CancellationToken ct)
        => AttemptLoginAsync(gatewayIpAddress, macAddress, ct);

    private async Task<LoginResponseModel> AttemptLoginAsync(
        string gatewayIpAddress, string macAddress, CancellationToken ct)
    {
        Guid subToken = Guid.Empty;
        var loginResultTask = new TaskCompletionSource<LoginResponseModel>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            int delayMs = Math.Max(0, RuntimeVariables.UPGRADE_LOGIN_DELAY_AFTER_CONNECT_MS);
            if (delayMs > 0)
                await Task.Delay(delayMs, ct);

            string hexLoginValue = new LoginTelegram().Create();
            HttpStatusCode writeStatus = HttpStatusCode.OK;

            subToken = _notificationService.Subscribe(macAddress, (sender, data) =>
            {
                var loginReply = new LoginTelegramReply(data);
                if (loginReply.TelegramType == "1100")
                {
                    var result = loginReply.GetResult();
                    var responseBody = Helper.CreateResponseWithMessage(
                        macAddress,
                        new { StatusCode = writeStatus },
                        result.Msg,
                        result.PincodeRequired);

                    loginResultTask.TrySetResult(new LoginResponseModel
                    {
                        Status = writeStatus.ToString(),
                        ResponseBody = responseBody
                    });
                }
            });

            // Ensure notify pipeline is ready before the login write.
            if (_notificationService is WindowsBleNotificationService winNotify)
            {
                int notifyReadyMs = Math.Max(1000, RuntimeVariables.LINUX_BLE_LOGIN_NOTIFY_READY_TIMEOUT_MS);
                try
                {
                    using var notifyReadyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    notifyReadyCts.CancelAfter(notifyReadyMs);
                    await winNotify.EnsureNotifyingReadyAsync(macAddress, notifyReadyCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException)
                {
                    return MakeLoginTimeout(macAddress,
                        $"Notify readiness timeout before login write ({notifyReadyMs} ms).",
                        HttpStatusCode.RequestTimeout, "Canceled");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "WinBLE Login: {Mac} notify readiness wait failed", macAddress);
                    return MakeLoginTimeout(macAddress,
                        "Notify readiness failed before login write.",
                        HttpStatusCode.RequestTimeout, "Canceled");
                }
            }

            var rw = new WindowsBleReadWriteService(_logger.CreateLogger<WindowsBleReadWriteService>());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var loginWriteResp = await rw.WriteBleMessageAsync(
                gatewayIpAddress, macAddress,
                RuntimeVariables.LINUX_BLE_CONTROL_HANDLE,
                hexLoginValue, "?noresponse=1", ct: ct);
            sw.Stop();

            writeStatus = loginWriteResp.StatusCode;
            _logger.LogDebug("WinBLE Login: {Mac} write result={Status} ({Ms}ms)", macAddress, writeStatus, sw.ElapsedMilliseconds);

            if (writeStatus == HttpStatusCode.ServiceUnavailable)
            {
                return MakeLoginTimeout(macAddress, "Login write failed: device not connected.",
                    HttpStatusCode.RequestTimeout, "Canceled");
            }

            using var reg = ct.Register(() => loginResultTask.TrySetCanceled(ct));
            var completed = await Task.WhenAny(loginResultTask.Task, Task.Delay(TimeSpan.FromSeconds(120), ct));

            if (completed == loginResultTask.Task)
                return await loginResultTask.Task;

            return ct.IsCancellationRequested
                ? MakeLoginTimeout(macAddress, "Login canceled by timeout.", HttpStatusCode.RequestTimeout, "Canceled")
                : MakeLoginTimeout(macAddress, "Login response timeout.", HttpStatusCode.RequestTimeout, "Timeout");
        }
        catch (OperationCanceledException)
        {
            return MakeLoginTimeout(macAddress, "Login canceled.", HttpStatusCode.RequestTimeout, "Canceled");
        }
        catch (Exception ex)
        {
            return MakeLoginTimeout(macAddress, $"Exception: {ex.Message}", HttpStatusCode.InternalServerError, "Error");
        }
        finally
        {
            if (subToken != Guid.Empty) _notificationService.Unsubscribe(macAddress, subToken);
        }
    }

    private static LoginResponseModel MakeLoginTimeout(
        string mac, string msg, HttpStatusCode status, string statusText) =>
        new()
        {
            Status = statusText,
            ResponseBody = new ResponseModel
            {
                MacAddress = mac,
                Data = msg,
                Status = status,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };

    // ── Data read (write + wait for notification) ────────────────────────────

    public async Task<DataResponseModel> GetDataFromBleDevice(
        string gatewayIpAddress, int gatewayPort, string macAddress, string value, int notificationTimeoutMs = 0)
    {
        var tcs = new TaskCompletionSource<DataResponseModel>();
        Guid token = Guid.Empty;
        HttpStatusCode writeStatus = HttpStatusCode.RequestTimeout;

        token = _notificationService.Subscribe(macAddress, (_, data) =>
        {
            var reply = new GenericTelegramReply(data);
            tcs.TrySetResult(new DataResponseModel
            {
                MacAddress = macAddress,
                Data = reply.DataResult,
                Status = writeStatus,
                Time = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            });
        });

        try
        {
            var rw = new WindowsBleReadWriteService(_logger.CreateLogger<WindowsBleReadWriteService>());
            using var wr = await rw.WriteBleMessageAsync(
                gatewayIpAddress, macAddress,
                RuntimeVariables.LINUX_BLE_CONTROL_HANDLE,
                value, "?noresponse=1");
            writeStatus = wr.StatusCode;

            if (writeStatus != HttpStatusCode.OK)
                return new DataResponseModel { MacAddress = macAddress, Data = "WriteFailed", Status = writeStatus, Time = DateTimeOffset.Now.ToUnixTimeMilliseconds() };

            int timeoutMs = Math.Clamp(
                notificationTimeoutMs > 0 ? notificationTimeoutMs : RuntimeVariables.LINUX_BLE_DATA_NOTIFICATION_TIMEOUT_MS,
                2000, 120000);
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));

            if (completed == tcs.Task)
                return await tcs.Task;

            return new DataResponseModel { MacAddress = macAddress, Data = "Timeout", Status = HttpStatusCode.RequestTimeout, Time = DateTimeOffset.Now.ToUnixTimeMilliseconds() };
        }
        finally
        {
            if (token != Guid.Empty) _notificationService.Unsubscribe(macAddress, token);
        }
    }

    // ── Light control ────────────────────────────────────────────────────────

    public async Task<ResponseModel> SendControlToLight(string gatewayIpAddress, string macAddress, string hexControlValue)
    {
        var rw = new WindowsBleReadWriteService(_logger.CreateLogger<WindowsBleReadWriteService>());
        using var resp = await rw.WriteBleMessageAsync(gatewayIpAddress, macAddress,
            RuntimeVariables.LINUX_BLE_CONTROL_HANDLE, hexControlValue, "?noresponse=1");
        return new ResponseModel
        {
            MacAddress = macAddress,
            Status = resp.StatusCode,
            Data = resp.IsSuccessStatusCode ? "ok" : "write failed",
            Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    // ── Connected devices ────────────────────────────────────────────────────

    public Task<ConnectedDevicesView> GetConnectedBleDevices(string gatewayIpAddress, int gatewayPort)
    {
        // WindowsBleHelpers does not expose an iterator over sessions publicly,
        // so we return an empty list — connected state is tracked internally per-session.
        return Task.FromResult(new ConnectedDevicesView { nodes = new List<Node>() });
    }

    // ── Batch connect ────────────────────────────────────────────────────────

    public async Task<ResponseModel> BatchConnectDevices(string gatewayIpAddress, List<string> macAddresses)
    {
        var tasks = macAddresses.Select(mac => ConnectToBleDevice(gatewayIpAddress, 0, mac));
        var results = await Task.WhenAll(tasks);
        bool allOk = results.All(r => r.Status == HttpStatusCode.OK);
        return new ResponseModel
        {
            MacAddress = string.Join(",", macAddresses),
            Status = allOk ? HttpStatusCode.OK : HttpStatusCode.MultiStatus,
            Data = allOk ? "all connected" : "some failed"
        };
    }

    // ── Pairing (local file store, same as Cassia / Linux paths) ─────────────

    public PairResponse PairDevice(string gatewayIpAddress, int gatewayPort, PairDevicesRequest req)
    {
        const string dir = "PairRequests";
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "pairRequest.txt");
        foreach (var mac in req.macAddresses)
            File.AppendAllText(filePath, mac + Environment.NewLine);
        return new PairResponse { PairingStatus = "Success", Message = $"Devices paired: {string.Join(",", req.macAddresses)}" };
    }

    public UnpairResponse UnpairDevice(string gatewayIpAddress, int gatewayPort, UnpairDevicesRequest req)
    {
        var filePath = Path.Combine("PairRequests", "pairRequest.txt");
        if (File.Exists(filePath))
        {
            var lines = File.ReadAllLines(filePath)
                .Where(l => !req.MacAddresses.Contains(l.Trim()))
                .ToArray();
            File.WriteAllLines(filePath, lines);
        }
        return new UnpairResponse { MacAddress = string.Join(",", req.MacAddresses), Status = "Unpairing successful" };
    }

    public List<string> GetPairedDevices()
    {
        var filePath = Path.Combine("PairRequests", "pairRequest.txt");
        return File.Exists(filePath) ? File.ReadAllLines(filePath).ToList() : new List<string>();
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private ILogger<T> CreateLogger<T>() => _logger.CreateLogger<T>();
}

// Extension to create a typed ILogger from an existing ILogger (mirrors LinuxBle pattern).
file static class LoggerExtensions
{
    public static ILogger<T> CreateLogger<T>(this ILogger logger)
    {
        if (logger is ILoggerFactory factory) return factory.CreateLogger<T>();
        return new TypedLoggerWrapper<T>(logger);
    }

    private sealed class TypedLoggerWrapper<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
#endif
