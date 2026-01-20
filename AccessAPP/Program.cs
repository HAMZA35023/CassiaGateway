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
        foreach (var mac in cmd.Sensors)
        {
           
            await mqttService.PublishRespAsync("FW version: DUMMY TEST");
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
