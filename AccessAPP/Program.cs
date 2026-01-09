using AccessAPP.Services;

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

    mqttService.StartUpdateRequested += async cmd =>
    {
        // TODO: replace with your real entrypoint (parallel upgrade, etc.)
        foreach (var mac in cmd.Sensors)
        {
            // Example placeholder - you likely have a method that takes mac + options
            
        }
    };

    mqttService.GetFwVersionRequested += async cmd =>
    {
        foreach (var mac in cmd.Sensors)
        {
           
            await mqttService.PublishRespAsync("FW version: DUMMY TEST");
        }
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
