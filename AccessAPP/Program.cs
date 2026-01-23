using AccessAPP.Services;

const string VERSION = "0.2.0";

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register HttpClient
builder.Services.AddHttpClient();

// Register services
builder.Services.AddSingleton<CassiaScanService>();
builder.Services.AddSingleton<ScanBleDevice>();
builder.Services.AddSingleton<CassiaConnectService>();
builder.Services.AddSingleton<CassiaPinCodeService>();
builder.Services.AddSingleton<DeviceStorageService>();
builder.Services.AddSingleton<CassiaNotificationService>();
builder.Services.AddSingleton<CassiaFirmwareUpgradeService>();
builder.Services.AddScoped<FirmwareUploadService>();
builder.Services.AddSingleton<FirmwareManifestService>();

// ✅ Add CORS policy
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

    // Put in appsettings.json if you want, fallback is fine on Cassia
    var path = cfg.GetValue<string>("Mqtt:ConfigPath") ?? "/home/cassia/FWUpgrade/mqtt.json";

    return new MqttConfigStore(path);
});

builder.Services.AddSingleton<IMqttService, MqttService>();

var app = builder.Build();

// Start BLE scanning when the application starts
using (var scope = app.Services.CreateScope())
{
    var serviceProvider = scope.ServiceProvider;

    var cassiaConnectService = serviceProvider.GetRequiredService<CassiaConnectService>();
    var cassiaNotificationService = serviceProvider.GetRequiredService<CassiaNotificationService>();
    cassiaNotificationService.semaphore = cassiaConnectService.semaphore;
    var scanBleDevice = serviceProvider.GetRequiredService<ScanBleDevice>();

    // Start MQTT service
    var mqttService = serviceProvider.GetRequiredService<IMqttService>();
     _ = mqttService.StartAsync();

    // Hook incoming MQTT commands to your services
    var firmwareUpgradeService = serviceProvider.GetRequiredService<CassiaFirmwareUpgradeService>();
    var deviceStorageService = serviceProvider.GetRequiredService<DeviceStorageService>();
    var manifestSvc = app.Services.GetRequiredService<FirmwareManifestService>();

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
                FirmwareVersion = r.FirmwareVersion ?? ""
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

    mqttService.GetFirmwareManifestRequested += async cmd =>
    {
        var resp = manifestSvc.GetFirmwareManifest();

        // Optional: if you later add DetectorType filtering, do it here using cmd.DetectorType
        await mqttService.PublishFirmwareManifestAsync(resp);
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
