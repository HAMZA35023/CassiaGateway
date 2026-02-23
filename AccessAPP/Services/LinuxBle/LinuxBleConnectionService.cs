using System.Net;
using AccessAPP.Models;
using AccessAPP.Services.BleAbstractions;
using AccessAPP.Services.HelperClasses;

namespace AccessAPP.Services.LinuxBle;

/// <summary>
/// IBleConnectionService implementation using BlueZ D-Bus (Linux native BLE).
///
/// Maps Cassia-style connect/disconnect/login calls onto BlueZ Device1 and
/// GattCharacteristic1 D-Bus methods.
///
/// Parameters such as <c>gatewayIpAddress</c>, <c>gatewayPort</c>, and <c>chip</c>
/// are accepted for interface compatibility but are not used — BlueZ manages the
/// local adapter directly.
/// </summary>
public class LinuxBleConnectionService : IBleConnectionService
{
    private readonly IBleNotificationService _notificationService;
    private readonly ILogger<LinuxBleConnectionService> _logger;

    // Semaphore kept for interface compatibility; the Linux path does not use it
    // for REST serialisation but it is wired in Program.cs.
    public SemaphoreSlim semaphore { get; } = new SemaphoreSlim(1, 1);

    public LinuxBleConnectionService(
        IBleNotificationService notificationService,
        ILogger<LinuxBleConnectionService> logger)
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
            // Round-robin across configured HCI adapters so that parallel upgrade workers
            // are spread evenly instead of all piling onto the same adapter.
            var bleAdapter = BlueZHelpers.GetNextConnectAdapter(macAddress);
            var devicePath = BlueZHelpers.DevicePath(bleAdapter, macAddress);
            var device = await BlueZHelpers.GetDeviceAsync(devicePath);

            // ── Guard: wait for BlueZ to finish any in-progress disconnect ──────────────
            // When a connect attempt immediately follows a disconnect (precheck or retry),
            // BlueZ finalises the BLE link tear-down asynchronously.  Calling ConnectAsync
            // while the device still reports Connected=true causes BlueZ to take 20–30 s
            // to complete the re-connection.  Waiting for Connected=false here keeps the
            // ConnectAsync path fast (2–5 s instead of 20–30 s).
            //
            // NOTE: All device.GetAsync<bool>() calls here use a 2-second per-call timeout
            // via .WaitAsync().  After a firmware reboot or rapid BLE state changes, BlueZ
            // can become temporarily unresponsive and the raw D-Bus property-get would hang
            // indefinitely — the outer CancellationToken cannot interrupt it because it is
            // not threaded through to GetAsync.
            try
            {
                using var connGuardCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (await device.GetAsync<bool>("Connected").WaitAsync(connGuardCts.Token))
                {
                    _logger.LogDebug(
                        "LinuxBLE: {Mac} Connected=true before ConnectAsync — disconnecting first to clear stale BlueZ state",
                        macAddress);
                    try { await device.DisconnectAsync().WaitAsync(ct); } catch { /* ignore — we just want BlueZ to start the teardown */ }

                    var teardownEnd = DateTime.UtcNow.AddMilliseconds(6000);
                    while (DateTime.UtcNow < teardownEnd && !ct.IsCancellationRequested)
                    {
                        try
                        {
                            using var teardownPollCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            if (!await device.GetAsync<bool>("Connected").WaitAsync(teardownPollCts.Token)) break;
                        }
                        catch { break; }
                        await Task.Delay(150, ct);
                    }
                    _logger.LogDebug("LinuxBLE: {Mac} Connected=false — proceeding with ConnectAsync", macAddress);
                }
            }
            catch { /* ignore — proceed to ConnectAsync regardless */ }

            try
            {
                // WaitAsync propagates the outer CancellationToken so a timed-out connect attempt
                // doesn't leave the calling thread blocked until BlueZ times out internally.
                await device.ConnectAsync().WaitAsync(ct);
            }
            catch (Tmds.DBus.DBusException ex) when (
                ex.ErrorName == "org.bluez.Error.Failed" &&
                (ex.Message.Contains("Software caused connection abort", StringComparison.OrdinalIgnoreCase) ||
                 ex.Message.Contains("le-connection-abort-by-local", StringComparison.OrdinalIgnoreCase)))
            {
                // BlueZ fires these errors when the local BLE stack aborts a connection attempt,
                // typically because a previous session is still being torn down or the HCI
                // controller rejected the request.  The device often remains connected from
                // the prior session — fall through and verify Connected + ServicesResolved below
                // instead of returning 503 early and losing all post-connect GATT setup.
                _logger.LogDebug("LinuxBLE: ConnectAsync '{Error}' for {Mac} — verifying Connected state", ex.Message, macAddress);
            }

            // Wait for BlueZ to report Connected=true; otherwise writes may fail with Not connected.
            var connectedDeadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, 1500));
            while (!ct.IsCancellationRequested && DateTime.UtcNow < connectedDeadline)
            {
                try
                {
                    using var connPollCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    if (await device.GetAsync<bool>("Connected").WaitAsync(connPollCts.Token)) break;
                }
                catch { /* ignore */ }
                await Task.Delay(100, ct);
            }

            // ServicesResolved is the supported way to ensure GATT is ready in BlueZ (there is no DiscoverServices method).
            // After a firmware reboot the device re-advertises in app mode but BlueZ needs extra time to
            // re-discover the new GATT table — use the configurable timeout (default 10 s).
            var resolved = await BlueZHelpers.WaitForServicesResolvedAsync(
                devicePath, RuntimeVariables.LINUX_BLE_SERVICES_RESOLVED_TIMEOUT_MS, 100, ct);
            if (!resolved)
                _logger.LogWarning("LinuxBLE: ServicesResolved timed out for {Mac} — writes may fail", macAddress);

            // Pin this adapter for the active connection so the scanner cannot race
            // and overwrite _macToAdapter while notification/write services are running.
            BlueZHelpers.SetConnectedAdapter(macAddress, bleAdapter);

            // Invalidate stale characteristic cache from a previous session.
            BlueZHelpers.InvalidateCharCache(devicePath);

            // Dump GATT table at debug level so we can see what's exposed after connect.
            var gattDump = await BlueZHelpers.DumpGattAsync(devicePath, ct);
            _logger.LogDebug("LinuxBLE: GATT on connect for {Mac}:\n{Dump}", macAddress, gattDump);

            // ── Pre-warm GATT mode + notify characteristic cache ─────────────────────────
            // EnsureNotifyingAsync is fired as a background task (fire-and-forget) from
            // Subscribe(), which is called inside AttemptLoginAsync() roughly 800 ms before
            // the first BLE write.  If EnsureNotifyingAsync has not yet called StartNotify by
            // the time the device replies to the login write, the notification is lost and
            // login times out ("status=Canceled").
            //
            // By detecting the mode and pre-scanning the notify characteristic here (while we
            // are already waiting for the GATT table), both _modeCache and the characteristic
            // proxy cache are populated before ConnectToBleDevice returns.  EnsureNotifyingAsync
            // then gets instant cache hits and finishes in <10 ms — well before the first write.
            if (resolved)
            {
                try
                {
                    var mode = await BlueZHelpers.DetectModeByGattAsync(devicePath, ct);
                    if (mode != BlueZHelpers.BleMode.Unknown)
                    {
                        var svcUuid = mode == BlueZHelpers.BleMode.Bootloader
                            ? BlueZHelpers.BootServiceUuid : BlueZHelpers.AppServiceUuid;
                        var notifyUuid = mode == BlueZHelpers.BleMode.Bootloader
                            ? BlueZHelpers.BootNotifyUuid : BlueZHelpers.AppNotifyUuid;
                        await BlueZHelpers.GetCharacteristicByUuidAsync(devicePath, svcUuid, notifyUuid, ct);
                        _logger.LogDebug("LinuxBLE: pre-warmed GATT cache for {Mac} mode={Mode}", macAddress, mode);
                    }
                    else
                    {
                        _logger.LogDebug("LinuxBLE: GATT mode unknown for {Mac} after connect — EnsureNotifyingAsync will detect on first subscribe", macAddress);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "LinuxBLE: GATT pre-warm failed for {Mac} (non-fatal)", macAddress);
                }
            }

            // Request shorter connection interval to reduce per-round-trip latency during writes.
            // Disabled by default (LINUX_BLE_ENABLE_CI_UPDATE=false): btmgmt conn-update can cause
            // some device firmware to disconnect immediately after receiving the L2CAP/LLCP request.
            // Enable only after confirming the target firmware handles conn-update without disconnecting.
            if (RuntimeVariables.LINUX_BLE_ENABLE_CI_UPDATE)
            {
                await BlueZHelpers.TryRequestShortConnectionIntervalAsync(
                    bleAdapter, macAddress, _logger);
            }

            // Final sanity check: verify the device is still connected before returning success.
            // btmgmt conn-update and other post-connect operations can trigger an asynchronous
            // disconnection; catching it here means the caller gets a 503 and can retry cleanly
            // instead of proceeding into a login that will fail with "Not connected".
            try
            {
                using var finalConnCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (!await device.GetAsync<bool>("Connected").WaitAsync(finalConnCts.Token))
                {
                    _logger.LogWarning(
                        "LinuxBLE: {Mac} disconnected during post-connect setup — returning error so caller can retry",
                        macAddress);
                    BlueZHelpers.ClearConnectedAdapter(macAddress);
                    return new ResponseModel
                    {
                        MacAddress = macAddress,
                        Status = System.Net.HttpStatusCode.ServiceUnavailable,
                        Data = "Device disconnected after connect",
                        Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                }
            }
            catch { /* ignore — proceed; write will fail immediately if not connected */ }

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
            BlueZHelpers.ClearConnectedAdapter(macAddress);
            return new ResponseModel { MacAddress = macAddress, Status = HttpStatusCode.RequestTimeout, Data = "Connect canceled" };
        }
        catch (Exception ex)
        {
            BlueZHelpers.ClearConnectedAdapter(macAddress);
            _logger.LogError(ex, "LinuxBLE: ConnectToBleDevice failed for {Mac}", macAddress);
            return new ResponseModel
            {
                MacAddress = macAddress,
                Status = HttpStatusCode.ServiceUnavailable,
                Data = ex.Message,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
    }

    public async Task<ResponseModel> DisconnectFromBleDevice(
        string gatewayIpAddress, string macAddress, int retries = 1, int chip = -1)
    {
        try
        {
            var bleAdapter = BlueZHelpers.GetDeviceAdapter(macAddress);
            var devicePath = BlueZHelpers.DevicePath(bleAdapter, macAddress);
            var device = await BlueZHelpers.GetDeviceAsync(devicePath);

            // Guard against a hung D-Bus call — BlueZ can be unresponsive after a firmware
            // reboot or rapid BLE state changes.  Five seconds is more than enough for a
            // normal disconnect; if it times out the caller catches the exception and treats
            // the disconnect as attempted (device will drop on its own shortly after).
            using var disconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await device.DisconnectAsync().WaitAsync(disconnectCts.Token);

            BlueZHelpers.ClearConnectedAdapter(macAddress);
            BlueZHelpers.InvalidateCharCache(devicePath);
            _notificationService.Unsubscribe(macAddress);

            return new ResponseModel { MacAddress = macAddress, Status = HttpStatusCode.OK, Data = "disconnected" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LinuxBLE: DisconnectFromBleDevice failed for {Mac}", macAddress);
            return new ResponseModel { MacAddress = macAddress, Status = HttpStatusCode.OK, Data = "disconnect attempted" };
        }
    }

    // ── Login ────────────────────────────────────────────────────────────────

    public Task<LoginResponseModel> AttemptLogin(string gatewayIpAddress, string macAddress)
        => AttemptLoginAsync(gatewayIpAddress, macAddress, CancellationToken.None);

    public Task<LoginResponseModel> AttemptLogin(string gatewayIpAddress, string macAddress, CancellationToken ct)
        => AttemptLoginAsync(gatewayIpAddress, macAddress, ct);

    private async Task<LoginResponseModel> AttemptLoginAsync(string gatewayIpAddress, string macAddress, CancellationToken ct)
    {
        Guid subToken = Guid.Empty;
        var loginResultTask = new TaskCompletionSource<LoginResponseModel>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            int delayMs = Math.Max(0, RuntimeVariables.UPGRADE_LOGIN_DELAY_AFTER_CONNECT_MS);
            _logger.LogDebug("LinuxBLE Login: {Mac} — start (delayMs={Delay})", macAddress, delayMs);
            if (delayMs > 0)
            {
                _logger.LogDebug("LinuxBLE Login: {Mac} — waiting {Delay}ms before login write", macAddress, delayMs);
                await Task.Delay(delayMs, ct);
            }

            string hexLoginValue = new LoginTelegram().Create();
            HttpStatusCode writeStatus = HttpStatusCode.OK;

            _logger.LogDebug("LinuxBLE Login: {Mac} — subscribing for notification", macAddress);
            subToken = _notificationService.Subscribe(macAddress, (sender, data) =>
            {
                var loginReply = new LoginTelegramReply(data);
                _logger.LogDebug("LinuxBLE Login: {Mac} — notification received telegram={Type}", macAddress, loginReply.TelegramType);
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

            // Write the login telegram to the control characteristic.
            var rw = new LinuxBleReadWriteService(
                _logger.CreateLogger<LinuxBleReadWriteService>());

            _logger.LogDebug("LinuxBLE Login: {Mac} — writing login telegram ({Bytes}b)", macAddress, hexLoginValue.Length / 2);
            var loginWriteSw = System.Diagnostics.Stopwatch.StartNew();
            using var writeResp = await rw.WriteBleMessageAsync(
                gatewayIpAddress, macAddress,
                RuntimeVariables.LINUX_BLE_CONTROL_HANDLE,
                hexLoginValue, "?noresponse=1", ct: ct);
            loginWriteSw.Stop();
            writeStatus = writeResp.StatusCode;
            _logger.LogDebug("LinuxBLE Login: {Mac} — write result={Status} ({Ms}ms)", macAddress, writeStatus, loginWriteSw.ElapsedMilliseconds);

            // Fast-fail: if the write returned ServiceUnavailable (org.bluez.Error.Failed: Not connected)
            // the device has already disconnected.  Return immediately instead of burning the full 8-second
            // login timeout waiting for a notification that will never arrive.
            if (writeStatus == HttpStatusCode.ServiceUnavailable)
            {
                _logger.LogWarning("LinuxBLE: login write returned Not Connected for {Mac} — aborting login early", macAddress);
                return MakeLoginTimeout(macAddress, "Login write failed: device not connected.", HttpStatusCode.RequestTimeout, "Canceled");
            }

            _logger.LogDebug("LinuxBLE Login: {Mac} — waiting for login notification (timeout=120s, ct.CanBeCanceled={CanCancel})", macAddress, ct.CanBeCanceled);
            using var reg = ct.Register(() => loginResultTask.TrySetCanceled(ct));

            var completed = await Task.WhenAny(loginResultTask.Task,
                Task.Delay(TimeSpan.FromSeconds(120), ct));

            if (completed == loginResultTask.Task)
            {
                _logger.LogDebug("LinuxBLE Login: {Mac} — notification received, login complete", macAddress);
                return await loginResultTask.Task;
            }

            _logger.LogDebug("LinuxBLE Login: {Mac} — timed out waiting for notification (ct.IsCancellationRequested={Canceled})", macAddress, ct.IsCancellationRequested);
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

    private static LoginResponseModel MakeLoginTimeout(string mac, string msg, HttpStatusCode status, string statusText) =>
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

    // ── Data read (write + wait for notification) ───────────────────────────

    public async Task<DataResponseModel> GetDataFromBleDevice(
        string gatewayIpAddress, int gatewayPort, string macAddress, string value)
    {
        var rw = new LinuxBleReadWriteService(
            _logger.CreateLogger<LinuxBleReadWriteService>());

        HttpStatusCode writeStatus;
        using (var wr = await rw.WriteBleMessageAsync(gatewayIpAddress, macAddress,
                   RuntimeVariables.LINUX_BLE_CONTROL_HANDLE, value, "?noresponse=1"))
        {
            writeStatus = wr.StatusCode;
        }

        var tcs = new TaskCompletionSource<DataResponseModel>();
        Guid token = Guid.Empty;

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

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(120)));
        _notificationService.Unsubscribe(macAddress, token);

        if (completed == tcs.Task) return await tcs.Task;

        return new DataResponseModel
        {
            MacAddress = macAddress,
            Data = "Timeout",
            Status = HttpStatusCode.RequestTimeout,
            Time = DateTimeOffset.Now.ToUnixTimeMilliseconds()
        };
    }

    // ── Light control ────────────────────────────────────────────────────────

    public async Task<ResponseModel> SendControlToLight(string gatewayIpAddress, string macAddress, string hexControlValue)
    {
        var rw = new LinuxBleReadWriteService(
            _logger.CreateLogger<LinuxBleReadWriteService>());

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

    public async Task<ConnectedDevicesView> GetConnectedBleDevices(string gatewayIpAddress, int gatewayPort)
    {
        try
        {
            var objMgr = await BlueZHelpers.GetObjectManagerAsync();
            var objects = await objMgr.GetManagedObjectsAsync();

            var nodes = new List<Node>();
            foreach (var (path, interfaces) in objects)
            {
                if (!interfaces.TryGetValue("org.bluez.Device1", out var props)) continue;

                bool connected = props.TryGetValue("Connected", out var c) && c is bool b && b;
                if (!connected) continue;

                string mac = props.TryGetValue("Address", out var a) && a is string s ? s : string.Empty;
                if (!string.IsNullOrEmpty(mac))
                    nodes.Add(new Node { bdaddrs = new Bdaddre { Bdaddr = mac }, connectionState = "connected" });
            }

            return new ConnectedDevicesView { nodes = nodes };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LinuxBLE: GetConnectedBleDevices failed");
            return new ConnectedDevicesView { nodes = new List<Node>() };
        }
    }

    // ── Batch connect ────────────────────────────────────────────────────────

    public async Task<ResponseModel> BatchConnectDevices(string gatewayIpAddress, List<string> macAddresses)
    {
        var tasks = macAddresses.Select(mac =>
            ConnectToBleDevice(gatewayIpAddress, 0, mac));
        var results = await Task.WhenAll(tasks);
        bool allOk = results.All(r => r.Status == HttpStatusCode.OK);
        return new ResponseModel
        {
            MacAddress = string.Join(",", macAddresses),
            Status = allOk ? HttpStatusCode.OK : HttpStatusCode.MultiStatus,
            Data = allOk ? "all connected" : "some failed"
        };
    }

    // ── Pairing (local file store, same as Cassia path) ──────────────────────

    public PairResponse PairDevice(string gatewayIpAddress, int gatewayPort, PairDevicesRequest pairDevicesRequest)
    {
        const string dir = "PairRequests";
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "pairRequest.txt");
        foreach (var mac in pairDevicesRequest.macAddresses)
            File.AppendAllText(filePath, mac + Environment.NewLine);

        return new PairResponse { PairingStatus = "Success", Message = $"Devices paired: {string.Join(",", pairDevicesRequest.macAddresses)}" };
    }

    public UnpairResponse UnpairDevice(string gatewayIpAddress, int gatewayPort, UnpairDevicesRequest unpairDevicesRequest)
    {
        var filePath = Path.Combine("PairRequests", "pairRequest.txt");
        if (File.Exists(filePath))
        {
            var lines = File.ReadAllLines(filePath)
                .Where(l => !unpairDevicesRequest.MacAddresses.Contains(l.Trim()))
                .ToArray();
            File.WriteAllLines(filePath, lines);
        }
        return new UnpairResponse { MacAddress = string.Join(",", unpairDevicesRequest.MacAddresses), Status = "Unpairing successful" };
    }

    public List<string> GetPairedDevices()
    {
        var filePath = Path.Combine("PairRequests", "pairRequest.txt");
        return File.Exists(filePath) ? File.ReadAllLines(filePath).ToList() : new List<string>();
    }

    // ── Helper: ILogger<T> for child services ───────────────────────────────

    private ILogger<T> CreateLogger<T>() => _logger.CreateLogger<T>();
}

// Extension to allow creating a typed ILogger from an existing ILogger
file static class LoggerExtensions
{
    public static ILogger<T> CreateLogger<T>(this ILogger logger)
    {
        // Use the category name of T since we don't have ILoggerFactory here.
        // The logger factory is not available without injection, so we cast or wrap.
        if (logger is ILoggerFactory factory) return factory.CreateLogger<T>();
        // Fallback: wrap using a no-op typed logger that delegates to the parent.
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