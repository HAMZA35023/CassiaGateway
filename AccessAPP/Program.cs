using AccessAPP;
using AccessAPP.Services;
using AccessAPP.Services.BleAbstractions;
using AccessAPP.Services.LinuxBle;
using AccessAPP.Models;
using AccessAPP.Logging;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

LoggingBootstrapper.TryConfigureSerilog(builder);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register HttpClient
builder.Services.AddHttpClient();

// Register services

// ── Cassia BLE backend (concrete classes) ──────────────────────────────────
builder.Services.AddSingleton<CassiaConnectService>();
builder.Services.AddSingleton<CassiaNotificationService>();
builder.Services.AddSingleton<CassiaReadWriteService>();

// ── Linux native BLE backend ───────────────────────────────────────────────
builder.Services.AddSingleton<LinuxBleConnectionService>();
builder.Services.AddSingleton<LinuxBleNotificationService>();
builder.Services.AddSingleton<LinuxBleReadWriteService>();
builder.Services.AddSingleton<LinuxNativeScanDevice>();

// ── BLE interface → concrete mapping (resolved lazily after runtime.json) ─
// Factories run on first resolution, which happens AFTER LoadFromDisk() below,
// so BLE_BACKEND already reflects any override from runtime.json.
builder.Services.AddSingleton<IBleConnectionService>(sp =>
    RuntimeVariables.BLE_BACKEND.Equals("linux-native", StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<LinuxBleConnectionService>()
        : (IBleConnectionService)sp.GetRequiredService<CassiaConnectService>());

builder.Services.AddSingleton<IBleNotificationService>(sp =>
    RuntimeVariables.BLE_BACKEND.Equals("linux-native", StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<LinuxBleNotificationService>()
        : (IBleNotificationService)sp.GetRequiredService<CassiaNotificationService>());

builder.Services.AddSingleton<IBleReadWriteService>(sp =>
    RuntimeVariables.BLE_BACKEND.Equals("linux-native", StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<LinuxBleReadWriteService>()
        : (IBleReadWriteService)sp.GetRequiredService<CassiaReadWriteService>());

// ── Shared / always-on services ────────────────────────────────────────────
builder.Services.AddSingleton<CassiaScanService>();
builder.Services.AddSingleton<ScanBleDevice>();
builder.Services.AddSingleton<CassiaPinCodeService>();
builder.Services.AddSingleton<DeviceStorageService>();
builder.Services.AddSingleton<CassiaFirmwareUpgradeService>();
builder.Services.AddScoped<FirmwareUploadService>();
builder.Services.AddSingleton<FirmwareManifestService>();
builder.Services.AddSingleton<LedRangeLocalStateStore>();
builder.Services.AddSingleton<AccessAppSelfUpdater>();
builder.Services.AddSingleton<SystemRebootService>();
builder.Services.AddSingleton<Modem4GStatusService>();
builder.Services.AddSingleton<CassiaWebSettingsService>();

// ? Add CORS policy
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngularApp", policy =>
    {
        policy.WithOrigins("http://localhost:4200", "http://localhost:60000")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// --- MQTT (Cassia multi-unit) ---
builder.Services.AddSingleton<MqttConfigStore>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var env = sp.GetRequiredService<IHostEnvironment>();

    // Put in appsettings.json if you want, fallback is fine on Cassia
    var path = cfg.GetValue<string>("Mqtt:ConfigPath");
    if (string.IsNullOrWhiteSpace(path))
        path = "mqtt.json";
    if (!Path.IsPathRooted(path))
        path = Path.Combine(env.ContentRootPath, path);

    return new MqttConfigStore(path);
});


// --- Runtime variables (optional persistence via runtime.json) ---
builder.Services.AddSingleton<RuntimeVariablesStore>();

builder.Services.AddSingleton<IMqttService, MqttService>();

var app = builder.Build();

// Start BLE scanning when the application starts
using (var scope = app.Services.CreateScope())
{
    var serviceProvider = scope.ServiceProvider;

    // Load runtime variable overrides BEFORE constructing services that read them on init.
    var runtimeStore = serviceProvider.GetRequiredService<RuntimeVariablesStore>();
    var loadResult = runtimeStore.LoadFromDisk();
    if (loadResult.applied.Count > 0)
        AppLog.Info($"Runtime variables loaded: {loadResult.applied.Count} applied from {runtimeStore.FilePath}");
    if (loadResult.errors.Count > 0)
        AppLog.Warn($"Runtime variables load errors: {string.Join(", ", loadResult.errors.Select(kv => $"{kv.Key}={kv.Value}"))}");

    // Start the BLE backend chosen by BLE_BACKEND (evaluated after runtime.json is loaded).
    if (RuntimeVariables.BLE_BACKEND.Equals("linux-native", StringComparison.OrdinalIgnoreCase))
    {
        AppLog.Info("BLE backend: linux-native (BlueZ D-Bus)");
        // Constructing the singleton starts the BlueZ scan loop.
        serviceProvider.GetRequiredService<LinuxNativeScanDevice>();
    }
    else
    {
        AppLog.Info("BLE backend: cassia (REST/SSE)");
        // Wire the shared semaphore so the notification SSE listener serialises
        // against the connect service (original behaviour).
        var cassiaConnectService = serviceProvider.GetRequiredService<CassiaConnectService>();
        var cassiaNotificationService = serviceProvider.GetRequiredService<CassiaNotificationService>();
        cassiaNotificationService.semaphore = cassiaConnectService.semaphore;
        // Constructing the singleton starts the Cassia SSE scan loops.
        serviceProvider.GetRequiredService<ScanBleDevice>();
    }

    // Start MQTT service
    var mqttService = serviceProvider.GetRequiredService<IMqttService>();
     _ = mqttService.StartAsync();

    // Hook incoming MQTT commands to your services
    var firmwareUpgradeService = serviceProvider.GetRequiredService<CassiaFirmwareUpgradeService>();
    var deviceStorageService = serviceProvider.GetRequiredService<DeviceStorageService>();
    var manifestSvc = app.Services.GetRequiredService<FirmwareManifestService>();
    var selfUpdater = app.Services.GetRequiredService<AccessAppSelfUpdater>();
    var rebootService = app.Services.GetRequiredService<SystemRebootService>();

    mqttService.StartUpdateRequested += cmd =>
    {
        // 1) publish queued immediately
        foreach (var r in cmd.Requests)
        {
            var mac = r.MacAddress?.Trim();
            if (string.IsNullOrWhiteSpace(mac)) continue;

            deviceStorageService.UpdateFirmwareProgress(mac, 0, "Queued");
        }

        // 2) map to UpgradeProgress list
        var upgrades = cmd.Requests
            .Where(r => !string.IsNullOrWhiteSpace(r.MacAddress))
            .Select(r => new UpgradeProgress
            {
                MacAddress = r.MacAddress!.Trim(),
                Pincode = r.Pincode ?? "",
                DetectotType = r.DetectorType ?? "",
                FirmwareVersion = r.FirmwareVersion ?? "",
                ForceUpdate = r.ForceUpdate ?? false
            })
            .ToList();

        // 3) enqueue into your FIFO upgrader (queue-aware method)
        _ = firmwareUpgradeService.EnqueueUpgradesAsync(upgrades);

        return Task.CompletedTask;
    };


    mqttService.GetFwVersionRequested += async cmd =>
    {
        var macs = (cmd.Sensors ?? new List<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pincode = cmd.Pincode ?? "";

        var results = new List<object>();
        var failed = new List<object>();

        foreach (var mac in macs)
        {
            try
            {
                var v = await firmwareUpgradeService.GetFwVersion(mac, pincode, true);
                if (string.IsNullOrWhiteSpace(v))
                    failed.Add(new { mac, error = "Could not retrieve FW version" });
                else
                    results.Add(new { mac, version = v });
            }
            catch (Exception ex)
            {
                failed.Add(new { mac, error = ex.Message });
            }
        }

        var resp = new
        {
            success = failed.Count == 0,
            message = "FW version query completed",
            name = mqttService.CurrentOptions.Name,
            networkId = mqttService.CurrentOptions.NetworkId,
            time = DateTimeOffset.UtcNow,
            requested = macs,
            pincode = string.IsNullOrWhiteSpace(pincode) ? null : "(provided)",
            results,
            failed
        };

        await mqttService.PublishTeleJsonAsync("fw-version", resp);
    };

    mqttService.DisconnectDevicesRequested += async cmd =>
    {
        var macs = (cmd.Sensors ?? new List<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<object>();

        bool allOk = true;

        foreach (var mac in macs)
        {
            try
            {
                var ok = await firmwareUpgradeService.DisconnectDeviceAsync(mac);
                if (!ok) allOk = false;
                results.Add(new { mac, success = ok });
            }
            catch (Exception ex)
            {
                allOk = false;
                results.Add(new { mac, success = false, error = ex.Message });
            }
        }

        var resp = new
        {
            success = allOk,
            message = "Disconnect command processed",
            name = mqttService.CurrentOptions.Name,
            networkId = mqttService.CurrentOptions.NetworkId,
            time = DateTimeOffset.UtcNow,
            requested = macs,
            results
        };

        await mqttService.PublishTeleJsonAsync("disconnect", resp);
    };

    mqttService.IdentifyRequested += async cmd =>
    {
        var macs = (cmd.Sensors ?? new List<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var seconds = cmd.Seconds <= 0 ? 15 : cmd.Seconds;
        var maxAttempts = cmd.MaxConnectAttempts <= 0 ? 1 : cmd.MaxConnectAttempts;
        var pincode = cmd.Pincode ?? "";

        // If caller sent a single string via tolerant parsing, it may land in Sensors empty + RequestId etc.
        // In that case, do nothing and respond with an error.
        if (macs.Count == 0)
        {
            var bad = new
            {
                success = false,
                stage = "failed",
                message = "No sensors/mac addresses provided. Send payload like {\"sensors\":[\"AA:BB:...\"],\"seconds\":15,\"maxConnectAttempts\":1}.",
                requestId = cmd.RequestId,
                name = mqttService.CurrentOptions.Name,
                networkId = mqttService.CurrentOptions.NetworkId,
                time = DateTimeOffset.UtcNow
            };

            await mqttService.PublishTeleJsonAsync("identify", bad);
            return;
        }

        // Process sequentially to avoid colliding BLE connect/login flows.
        foreach (var mac in macs)
        {
            await firmwareUpgradeService.IdentifyDeviceAsync(
                macAddress: mac,
                pincode: string.IsNullOrWhiteSpace(pincode) ? null : pincode,
                secondsToStayConnected: seconds,
                maxConnectAttempts: maxAttempts,
                ct: CancellationToken.None,
                report: async (stagePayload) =>
                {
                    await mqttService.PublishTeleJsonAsync("identify", new
                    {
                        name = mqttService.CurrentOptions.Name,
                        networkId = mqttService.CurrentOptions.NetworkId,
                        requestId = cmd.RequestId,
                        data = stagePayload
                    });
                });
        }
    };

    mqttService.LedRangeVisualizeRequested += cmd =>
    {
        var snapshot = DeviceStorageService.GetDeviceListSnapshot();
        var requestId = string.IsNullOrWhiteSpace(cmd.RequestId) ? Guid.NewGuid().ToString("N") : cmd.RequestId!;
        cmd.RequestId = requestId;

        _ = Task.Run(async () =>
        {
            try
            {
                await firmwareUpgradeService.RunLedRangeVisualizationAsync(
                    snapshot,
                    cmd,
                    report: async stagePayload =>
                    {
                        await mqttService.PublishTeleJsonAsync("led-range", new
                        {
                            name = mqttService.CurrentOptions.Name,
                            networkId = mqttService.CurrentOptions.NetworkId,
                            requestId,
                            data = stagePayload
                        });
                    },
                    ct: CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppLog.Error($"LED range visualize failed ({requestId}): {ex.Message}", ex);
            }
        });

        return Task.CompletedTask;
    };

    mqttService.LedRangeDisconnectRequested += cmd =>
    {
        var requestId = string.IsNullOrWhiteSpace(cmd.RequestId) ? Guid.NewGuid().ToString("N") : cmd.RequestId!;
        cmd.RequestId = requestId;

        firmwareUpgradeService.RequestStopLedRangeVisualization();

        _ = Task.Run(async () =>
        {
            try
            {
                await firmwareUpgradeService.DisconnectLedRangeAsync(
                    cmd,
                    report: async stagePayload =>
                    {
                        await mqttService.PublishTeleJsonAsync("led-range", new
                        {
                            name = mqttService.CurrentOptions.Name,
                            networkId = mqttService.CurrentOptions.NetworkId,
                            requestId,
                            data = stagePayload
                        });
                    },
                    ct: CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppLog.Error($"LED range disconnect failed ({requestId}): {ex.Message}", ex);
            }
        });

        return Task.CompletedTask;
    };

    mqttService.GetFirmwareManifestRequested += async cmd =>
    {
        var resp = manifestSvc.GetFirmwareManifest();

        // Optional: if you later add DetectorType filtering, do it here using cmd.DetectorType
        await mqttService.PublishFirmwareManifestAsync(resp);
    };

    mqttService.SelfUpdateRequested += async cmd =>
    {
        var requestId = string.IsNullOrWhiteSpace(cmd.RequestId) ? Guid.NewGuid().ToString("N") : cmd.RequestId!;
        var timeout = cmd.TimeoutSeconds <= 0 ? 120 : cmd.TimeoutSeconds;
        var requestedChannel = AccessAppSelfUpdater.NormalizeUpdateChannel(cmd.UpdateChannel);

        if (!string.IsNullOrWhiteSpace(cmd.UpdateChannel))
        {
            var setResult = selfUpdater.SetUpdateChannel(cmd.UpdateChannel, cmd.ChannelFilePath);
            var setOk = string.Equals(setResult.Status, "channel-set", StringComparison.OrdinalIgnoreCase);

            await mqttService.PublishTeleJsonAsync("update-channel", new
            {
                success = setOk,
                stage = "set",
                requestId,
                name = mqttService.CurrentOptions.Name,
                networkId = mqttService.CurrentOptions.NetworkId,
                time = DateTimeOffset.UtcNow,
                channel = setResult.Channel ?? requestedChannel,
                status = setResult.Status,
                message = setResult.Message
            });

            if (!setOk)
                return;
        }

        await mqttService.PublishTeleJsonAsync("self-update", new
        {
            success = true,
            stage = "started",
            requestId,
            name = mqttService.CurrentOptions.Name,
            networkId = mqttService.CurrentOptions.NetworkId,
            time = DateTimeOffset.UtcNow,
            dryRun = cmd.DryRun,
            timeoutSeconds = timeout,
            restartService = cmd.RestartService,
            channel = string.IsNullOrWhiteSpace(requestedChannel) ? null : requestedChannel
        });

        if (cmd.RestartService)
        {
            var queued = selfUpdater.TriggerServiceRestart(cmd);
            var ok = queued.Status == "restart-queued";

            await mqttService.PublishTeleJsonAsync("self-update", new
            {
                success = ok,
                stage = "restart-queued",
                requestId,
                name = mqttService.CurrentOptions.Name,
                networkId = mqttService.CurrentOptions.NetworkId,
                time = DateTimeOffset.UtcNow,
                status = queued.Status,
                message = queued.Message,
                channel = string.IsNullOrWhiteSpace(requestedChannel) ? null : requestedChannel,
                exitCode = queued.ExitCode,
                stdout = queued.StdOut,
                stderr = queued.StdErr
            });

            if (ok && !cmd.DryRun)
            {
                // Give MQTT publish a brief moment, then exit so updater/restart can replace this process.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    Environment.Exit(0);
                });
            }

            return;
        }

        cmd.TimeoutSeconds = timeout;
        var result = await selfUpdater.RunAsync(cmd, CancellationToken.None);
        var success = result.Status is "updated" or "no-update" or "dry-run" or "ok";

        await mqttService.PublishTeleJsonAsync("self-update", new
        {
            success,
            stage = "completed",
            requestId,
            name = mqttService.CurrentOptions.Name,
            networkId = mqttService.CurrentOptions.NetworkId,
            time = DateTimeOffset.UtcNow,
            status = result.Status,
            message = result.Message,
            channel = string.IsNullOrWhiteSpace(requestedChannel) ? result.Channel : requestedChannel,
            exitCode = result.ExitCode,
            stdout = result.StdOut,
            stderr = result.StdErr
        });
    };

    mqttService.SetUpdateChannelRequested += async cmd =>
    {
        var requestId = string.IsNullOrWhiteSpace(cmd.RequestId) ? Guid.NewGuid().ToString("N") : cmd.RequestId!;
        var result = selfUpdater.SetUpdateChannel(cmd.Channel, cmd.ChannelFilePath);
        var success = string.Equals(result.Status, "channel-set", StringComparison.OrdinalIgnoreCase);

        await mqttService.PublishTeleJsonAsync("update-channel", new
        {
            success,
            stage = "set",
            requestId,
            name = mqttService.CurrentOptions.Name,
            networkId = mqttService.CurrentOptions.NetworkId,
            time = DateTimeOffset.UtcNow,
            channel = result.Channel,
            status = result.Status,
            message = result.Message
        });
    };

    mqttService.RebootRequested += async cmd =>
    {
        var requestId = string.IsNullOrWhiteSpace(cmd.RequestId) ? Guid.NewGuid().ToString("N") : cmd.RequestId!;
        var delaySeconds = cmd.DelaySeconds <= 0 ? 2 : cmd.DelaySeconds;

        await mqttService.PublishTeleJsonAsync("reboot", new
        {
            success = true,
            stage = "requested",
            requestId,
            name = mqttService.CurrentOptions.Name,
            networkId = mqttService.CurrentOptions.NetworkId,
            time = DateTimeOffset.UtcNow,
            delaySeconds
        });

        var result = rebootService.QueueReboot(cmd);
        var success = string.Equals(result.Status, "reboot-queued", StringComparison.OrdinalIgnoreCase);

        await mqttService.PublishTeleJsonAsync("reboot", new
        {
            success,
            stage = success ? "queued" : "failed",
            requestId,
            name = mqttService.CurrentOptions.Name,
            networkId = mqttService.CurrentOptions.NetworkId,
            time = DateTimeOffset.UtcNow,
            status = result.Status,
            message = result.Message,
            delaySeconds = result.DelaySeconds,
            exitCode = result.ExitCode,
            stdout = result.StdOut,
            stderr = result.StdErr
        });
    };

}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Enable CORS
app.UseCors("AllowAngularApp");

//NEW: Enable static file serving for Angular frontend
app.UseStaticFiles();

//Keep existing configuration
app.UseHttpsRedirection();
app.UseAuthorization();

// Map API controllers (your existing APIs will be at /api/...)
app.MapControllers();

// NEW: Fallback to serve Angular app for client-side routing
// This ensures Angular routing works properly (e.g., /dashboard, /logs-dashboard)
app.MapFallbackToFile("index.html");

app.Lifetime.ApplicationStopping.Register(() =>
{
    using var scope = app.Services.CreateScope();
    var mqtt = scope.ServiceProvider.GetRequiredService<IMqttService>();
    _ = mqtt.StopAsync();
});

app.Run();


