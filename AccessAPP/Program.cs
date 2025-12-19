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

var app = builder.Build();

// Start BLE scanning when the application starts
using (var scope = app.Services.CreateScope())
{
    var serviceProvider = scope.ServiceProvider;

    var cassiaConnectService = serviceProvider.GetRequiredService<CassiaConnectService>();
    var cassiaNotificationService = serviceProvider.GetRequiredService<CassiaNotificationService>();
    cassiaNotificationService.semaphore = cassiaConnectService.semaphore;
    var scanBleDevice = serviceProvider.GetRequiredService<ScanBleDevice>();
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

app.Run();
