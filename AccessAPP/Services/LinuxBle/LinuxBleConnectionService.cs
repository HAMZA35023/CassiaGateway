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
            var devicePath = BlueZHelpers.DevicePath(RuntimeVariables.LINUX_BLE_ADAPTER, macAddress);
            var device = await BlueZHelpers.GetDeviceAsync(devicePath);

            try
            {
                await device.ConnectAsync();
            }
            catch (Tmds.DBus.DBusException ex) when (
                ex.ErrorName == "org.bluez.Error.Failed" &&
                ex.Message.Contains("Software caused connection abort", StringComparison.OrdinalIgnoreCase))
            {
                // BlueZ fires this when the OS-level link is being torn down and re-established
                // concurrently (e.g. previous session still cleaning up). The connection usually
                // completes anyway — fall through and verify Connected + ServicesResolved below.
                _logger.LogDebug("LinuxBLE: ConnectAsync 'Software caused connection abort' for {Mac} — verifying state", macAddress);
            }

            // Wait for BlueZ to report Connected=true; otherwise writes may fail with Not connected.
            var connectedDeadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, 1500));
            while (!ct.IsCancellationRequested && DateTime.UtcNow < connectedDeadline)
            {
                try
                {
                    if (await device.GetAsync<bool>("Connected")) break;
                }
                catch { /* ignore */ }
                await Task.Delay(100, ct);
            }

            // ServicesResolved is the supported way to ensure GATT is ready in BlueZ (there is no DiscoverServices method).
            // Use a generous timeout — after "Software caused connection abort" re-discovery can be slow.
            var resolved = await BlueZHelpers.WaitForServicesResolvedAsync(devicePath, 8000, 100, ct);
            if (!resolved)
                _logger.LogWarning("LinuxBLE: ServicesResolved timed out for {Mac} — writes may fail", macAddress);

            // Invalidate stale characteristic cache from a previous session.
            BlueZHelpers.InvalidateCharCache(devicePath);

            // Dump GATT table at debug level so we can see what's exposed after connect.
            var gattDump = await BlueZHelpers.DumpGattAsync(devicePath, ct);
            _logger.LogDebug("LinuxBLE: GATT on connect for {Mac}:\n{Dump}", macAddress, gattDump);

            // Request shorter connection interval to reduce per-round-trip latency during writes.
            // Uses btmgmt or hcitool; silently ignored if neither is available or lacks permissions.
            await BlueZHelpers.TryRequestShortConnectionIntervalAsync(
                RuntimeVariables.LINUX_BLE_ADAPTER, macAddress, _logger);

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
            return new ResponseModel { MacAddress = macAddress, Status = HttpStatusCode.RequestTimeout, Data = "Connect canceled" };
        }
        catch (Exception ex)
        {
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
            var devicePath = BlueZHelpers.DevicePath(RuntimeVariables.LINUX_BLE_ADAPTER, macAddress);
            var device = await BlueZHelpers.GetDeviceAsync(devicePath);

            await device.DisconnectAsync();

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
            if (delayMs > 0) await Task.Delay(delayMs, ct);

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

            // Write the login telegram to the control characteristic.
            var rw = new LinuxBleReadWriteService(
                _logger.CreateLogger<LinuxBleReadWriteService>());

            using var writeResp = await rw.WriteBleMessageAsync(
                gatewayIpAddress, macAddress,
                RuntimeVariables.LINUX_BLE_CONTROL_HANDLE,
                hexLoginValue, "?noresponse=1", ct: ct);
            writeStatus = writeResp.StatusCode;

            using var reg = ct.Register(() => loginResultTask.TrySetCanceled(ct));

            var completed = await Task.WhenAny(loginResultTask.Task,
                Task.Delay(TimeSpan.FromSeconds(120), ct));

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