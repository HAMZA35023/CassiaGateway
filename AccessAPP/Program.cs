using AccessAPP;
using AccessAPP.Services;
using AccessAPP.Services.BleAbstractions;
using AccessAPP.Services.LinuxBle;
#if WINDOWS
using AccessAPP.Services.WindowsBle;
#endif
using AccessAPP.Services.HelperClasses;
using AccessAPP.Models;
using AccessAPP.Logging;
using Serilog;
using System.Runtime.InteropServices;

// ── Native library resolver ────────────────────────────────────────────────
// DllImport("BootloaderUtilMultiThread") targets platform-specific binaries:
//   Windows x86: BootloaderUtilMultiThread_x86.dll
//   Windows x64: BootloaderUtilMultiThread_x64.dll
//   Linux x64  : libBootloaderUtilMultiThread_linux-x64.so
//   Linux ARM  : libBootloaderUtilMultiThread_arm.so
// SetDllImportResolver must be called from within the assembly that owns
// the DllImport (AccessAPP), which is also the executing assembly here.
NativeLibrary.SetDllImportResolver(typeof(Bootloader_Utils).Assembly,
    (libraryName, assembly, searchPath) =>
    {
        if (!libraryName.Equals("BootloaderUtilMultiThread", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero; // let default logic handle everything else

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var winArch = Environment.Is64BitProcess ? "x64" : "x86";
            var dllName = $"BootloaderUtilMultiThread_{winArch}.dll";
            var dllPath = Path.Combine(AppContext.BaseDirectory, dllName);
            if (NativeLibrary.TryLoad(dllPath, out var winHandle))
            {
                AppLog.Info($"Native library loaded: {dllName} (arch={winArch})");
                return winHandle;
            }
            AppLog.Error($"Native library load failed: {dllPath} not found.");
            return IntPtr.Zero;
        }

        var arch = RuntimeInformation.ProcessArchitecture;
        var suffix = (arch == Architecture.Arm || arch == Architecture.Arm64) ? "arm" : "linux-x64";
        var soName = $"libBootloaderUtilMultiThread_{suffix}.so";

        if (NativeLibrary.TryLoad(soName, assembly, searchPath, out var handle))
        {
            AppLog.Info($"Native library loaded: {soName} (arch={arch})");
            return handle;
        }

        AppLog.Warn($"Native library not found: {soName} (arch={arch}) — trying generic fallback");

        // Last-resort fallback: try a non-arch-suffixed name
        if (NativeLibrary.TryLoad("libBootloaderUtilMultiThread.so", assembly, searchPath, out handle))
        {
            AppLog.Info("Native library loaded: libBootloaderUtilMultiThread.so (generic fallback)");
            return handle;
        }

        AppLog.Error("Native library load failed: neither arch-specific nor generic libBootloaderUtilMultiThread.so found. Deploy directory is missing the .so file.");
        return IntPtr.Zero;
    });

var builder = WebApplication.CreateBuilder(args);

// Migrate persistent files from old base-directory location to the state directory on first run after update.
MigrateToStateDir(Path.Combine(builder.Environment.ContentRootPath, "mqtt.json"),            AccessAppPaths.MqttConfig);
MigrateToStateDir(Path.Combine(builder.Environment.ContentRootPath, "runtime.json"),         AccessAppPaths.RuntimeConfig);
MigrateToStateDir(Path.Combine(builder.Environment.ContentRootPath, "Logs", "upgrade_logs.txt"), AccessAppPaths.UpgradeLog);

LoggingBootstrapper.TryConfigureSerilog(builder);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // IFormFile with [FromForm] requires explicit multipart schema mapping.
    c.MapType<Microsoft.AspNetCore.Http.IFormFile>(() =>
        new Microsoft.OpenApi.Models.OpenApiSchema { Type = "string", Format = "binary" });
});

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

// ── Windows native BLE backend ────────────────────────────────────────────
#if WINDOWS
builder.Services.AddSingleton<WindowsBleConnectionService>();
builder.Services.AddSingleton<WindowsBleNotificationService>();
builder.Services.AddSingleton<WindowsBleReadWriteService>();
builder.Services.AddSingleton<WindowsNativeScanDevice>();
#endif

// ── BLE interface → concrete mapping (resolved lazily after runtime.json) ─
// Factories run on first resolution, which happens AFTER LoadFromDisk() below,
// so BLE_BACKEND already reflects any override from runtime.json.
builder.Services.AddSingleton<IBleConnectionService>(sp =>
    RuntimeVariables.BLE_BACKEND switch
    {
        "linux-native" => sp.GetRequiredService<LinuxBleConnectionService>(),
#if WINDOWS
        "windows-native" => sp.GetRequiredService<WindowsBleConnectionService>(),
#endif
        _ => (IBleConnectionService)sp.GetRequiredService<CassiaConnectService>()
    });

builder.Services.AddSingleton<IBleNotificationService>(sp =>
    RuntimeVariables.BLE_BACKEND switch
    {
        "linux-native" => sp.GetRequiredService<LinuxBleNotificationService>(),
#if WINDOWS
        "windows-native" => sp.GetRequiredService<WindowsBleNotificationService>(),
#endif
        _ => (IBleNotificationService)sp.GetRequiredService<CassiaNotificationService>()
    });

builder.Services.AddSingleton<IBleReadWriteService>(sp =>
    RuntimeVariables.BLE_BACKEND switch
    {
        "linux-native" => sp.GetRequiredService<LinuxBleReadWriteService>(),
#if WINDOWS
        "windows-native" => sp.GetRequiredService<WindowsBleReadWriteService>(),
#endif
        _ => (IBleReadWriteService)sp.GetRequiredService<CassiaReadWriteService>()
    });

// ── Shared / always-on services ────────────────────────────────────────────
builder.Services.AddSingleton<CassiaScanService>();
builder.Services.AddSingleton<ScanBleDevice>();
builder.Services.AddSingleton<CassiaPinCodeService>();
builder.Services.AddSingleton<DeviceStorageService>();
builder.Services.AddSingleton<CassiaFirmwareUpgradeService>();
builder.Services.AddSingleton<DaliDbService>();
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

    var path = cfg.GetValue<string>("Mqtt:ConfigPath");
    if (string.IsNullOrWhiteSpace(path))
        path = AccessAppPaths.MqttConfig;
    if (!Path.IsPathRooted(path))
        path = Path.Combine(env.ContentRootPath, path);

    return new MqttConfigStore(path);
});


// --- Runtime variables (optional persistence via runtime.json) ---
builder.Services.AddSingleton<RuntimeVariablesStore>();

builder.Services.AddSingleton<IMqttService, MqttService>();
builder.Services.AddSingleton<LocalBrokerDiscoveryService>();
builder.Services.AddSingleton<AccessAppBeaconService>();

var app = builder.Build();

// Start BLE scanning when the application starts
using (var scope = app.Services.CreateScope())
{
    var serviceProvider = scope.ServiceProvider;

    // Load runtime variable overrides BEFORE constructing services that read them on init.
    var runtimeStore = serviceProvider.GetRequiredService<RuntimeVariablesStore>();
    var loadResult = runtimeStore.LoadFromDisk();
    if (loadResult.applied.Count > 0)
        AppLog.Info($"Runtime variables loaded: {loadResult.applied.Count} applied from {runtimeStore.FilePath} ({string.Join(", ", loadResult.applied)})");
    if (loadResult.errors.Count > 0)
        AppLog.Warn($"Runtime variables load errors: {string.Join(", ", loadResult.errors.Select(kv => $"{kv.Key}={kv.Value}"))}");
    AppLog.Info($"[Startup] LOCAL_MQTT_HOST='{RuntimeVariables.LOCAL_MQTT_HOST}', LOCAL_MQTT_PORT={RuntimeVariables.LOCAL_MQTT_PORT}");

    // Resolve "auto" BLE_BACKEND: probe Cassia HTTP API → windows-native → linux-native.
    if (RuntimeVariables.BLE_BACKEND.Equals("auto", StringComparison.OrdinalIgnoreCase))
    {
        var cassiaIp   = app.Configuration.GetValue<string>("GatewayConfiguration:IpAddress") ?? string.Empty;
        var cassiaPort = app.Configuration.GetValue<int>("GatewayConfiguration:Port", 80);
        var probeUrl   = $"http://{cassiaIp}:{cassiaPort}/gap/nodes?connection_state=connected";

        bool hasCassiaGateway = false;
        try
        {
            using var probeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await probeClient.GetAsync(probeUrl).ConfigureAwait(false);
            hasCassiaGateway = true; // any HTTP response means the gateway answered
        }
        catch (Exception ex)
        {
            AppLog.Info($"BLE_BACKEND auto: Cassia probe failed ({ex.GetType().Name}), falling back to platform detection.");
        }

        RuntimeVariables.BLE_BACKEND =
            hasCassiaGateway ? "cassia"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows-native"
            : "linux-native";
        AppLog.Info($"BLE_BACKEND auto-detected: {RuntimeVariables.BLE_BACKEND} (probe={probeUrl}, reachable={hasCassiaGateway})");
    }

    var isLinuxNativeBackend = RuntimeVariables.BLE_BACKEND.Equals("linux-native", StringComparison.OrdinalIgnoreCase);
    var isWindowsNativeBackend = RuntimeVariables.BLE_BACKEND.Equals("windows-native", StringComparison.OrdinalIgnoreCase);
    var linuxAdapters = RuntimeVariables.GetLinuxBleAdapterList()
        .Where(a => !string.IsNullOrWhiteSpace(a))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (linuxAdapters.Length == 0)
        linuxAdapters = [RuntimeVariables.LINUX_BLE_ADAPTER];

    var startupWorkersRequested = isLinuxNativeBackend
        ? Math.Max(1, linuxAdapters.Length)
        : isWindowsNativeBackend ? 1
        : 2;
    var startupWorkersValue = CassiaFirmwareUpgradeService.SetParallelProgrammers(startupWorkersRequested);
    AppLog.Info(
        isLinuxNativeBackend
            ? $"Startup workers auto-set to {startupWorkersValue} (linux-native adapters={string.Join(",", linuxAdapters)})."
            : $"Startup workers auto-set to {startupWorkersValue} (cassia default).");

    // Start the BLE backend chosen by BLE_BACKEND (evaluated after runtime.json is loaded).
#if WINDOWS
    if (isWindowsNativeBackend)
    {
        AppLog.Info("BLE backend: windows-native (Windows.Devices.Bluetooth WinRT)");
        // Constructing the singleton starts the Windows BLE advertisement watcher.
        serviceProvider.GetRequiredService<WindowsNativeScanDevice>();
    }
    else
#endif
    if (isLinuxNativeBackend)
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
    AppLog.Info($"[Startup] Primary MQTT broker: {mqttService.CurrentOptions.Host}:{mqttService.CurrentOptions.Port}, name={mqttService.CurrentOptions.Name}, networkId={mqttService.CurrentOptions.NetworkId}");
    _ = mqttService.StartAsync();

    // Start LAN discovery — connects to any WPF client's local MQTT broker found via UDP beacon
    var brokerDiscovery = serviceProvider.GetRequiredService<LocalBrokerDiscoveryService>();
    brokerDiscovery.Start();

    // Announce this AccessApp instance on the LAN so WPF clients can discover and configure it
    var accessAppBeacon = serviceProvider.GetRequiredService<AccessAppBeaconService>();
    accessAppBeacon.Start();
    _ = Task.Run(async () =>
    {
        try
        {
            await mqttService.PublishTeleJsonAsync("parallel-programmers", new
            {
                success = true,
                message = "Parallel programmers auto-set at startup.",
                name = mqttService.CurrentOptions.Name,
                networkId = mqttService.CurrentOptions.NetworkId,
                time = DateTimeOffset.UtcNow,
                source = "startup-auto",
                backend = RuntimeVariables.BLE_BACKEND,
                linuxAdapters = isLinuxNativeBackend ? linuxAdapters : Array.Empty<string>(),
                windowsNative = isWindowsNativeBackend,
                requested = startupWorkersRequested,
                value = startupWorkersValue
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Startup workers MQTT announce failed: {ex.Message}");
        }
    });

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
                ForceUpdate = r.ForceUpdate ?? false,
                PostUpdateSettings = r.DetectorSettings?.CloneNormalized(),
                RunDaliAddressAllToZone1AfterUpdate = r.RunDaliAddressAllToZone1AfterUpdate ?? false,
                RunDali102TotalNewScanAfterUpdate = r.RunDali102TotalNewScanAfterUpdate ?? false,
                RunDali103TotalNewScanAfterUpdate = r.RunDali103TotalNewScanAfterUpdate ?? false
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

    // Dedup dictionary shared by get/set detector-settings handlers to discard duplicate MQTT
    // deliveries (same requestId fired multiple times when subscribed to multiple brokers).
    var detectorSettingsRecentIds = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

    static bool DetectorSettingsDedup(System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> seen, string requestId)
    {
        if (!seen.TryAdd(requestId, DateTimeOffset.UtcNow))
            return false; // duplicate
        // Prune entries older than 5 minutes to prevent unbounded growth.
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        foreach (var kvp in seen)
            if (kvp.Value < cutoff) seen.TryRemove(kvp.Key, out _);
        return true;
    }

    mqttService.GetDetectorSettingsRequested += async cmd =>
    {
        var requestId = string.IsNullOrWhiteSpace(cmd.RequestId) ? Guid.NewGuid().ToString("N") : cmd.RequestId!;
        if (!DetectorSettingsDedup(detectorSettingsRecentIds, requestId))
        {
            AppLog.Debug($"[DetectorSettings] get duplicate requestId={requestId} — skipped");
            return;
        }
        var requested = (cmd.Sensors ?? new List<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requested.Count == 0)
        {
            await mqttService.PublishTeleJsonAsync("detector-settings", new
            {
                success = false,
                action = "get",
                requestId,
                message = "No sensors/mac addresses provided.",
                name = mqttService.CurrentOptions.Name,
                networkId = mqttService.CurrentOptions.NetworkId,
                time = DateTimeOffset.UtcNow
            });
            return;
        }

        var knownByMac = DeviceStorageService.GetDeviceListSnapshot()
            .Where(x => !string.IsNullOrWhiteSpace(x.MacAddress))
            .ToDictionary(x => x.MacAddress.Trim(), x => x, StringComparer.OrdinalIgnoreCase);

        var pincode = cmd.Pincode ?? "";
        var defaultDetector = NormalizeDetectorType(cmd.DetectorType);
        var defaultFw = (cmd.FirmwareVersion ?? "").Trim();
        var results = new List<object>();
        var failed = new List<object>();

        foreach (var mac in requested)
        {
            var detectorType = defaultDetector;
            if (string.IsNullOrWhiteSpace(detectorType) && knownByMac.TryGetValue(mac, out var known))
                detectorType = NormalizeDetectorType(known.DetectorType);

            if (string.IsNullOrWhiteSpace(detectorType))
                detectorType = "P48";

            var fw = defaultFw;

            try
            {
                var cl = await firmwareUpgradeService.ConnectAndLoginWithRetryForPipelineAsync(
                    firmwareUpgradeService.GatewayIpAddress,
                    firmwareUpgradeService.GatewayPort,
                    mac,
                    pincode,
                    logId: requestId,
                    firmwareVersion: fw,
                    maxAttempts: Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS),
                    delayBetweenAttemptsMs: 2000,
                    bootModeIsRetryable: true).ConfigureAwait(false);

                if (!cl.Success)
                {
                    failed.Add(new
                    {
                        mac,
                        detectorType,
                        success = false,
                        message = $"Connect+login failed: {cl.Message}"
                    });
                    continue;
                }

                var snapshot = await firmwareUpgradeService.SettingsBackupService
                    .CaptureSnapshotAsync(mac, detectorType, fw).ConfigureAwait(false);

                results.Add(new
                {
                    mac,
                    detectorType,
                    firmwareVersion = fw,
                    success = true,
                    settings = DetectorSettingsPatch.FromSnapshot(snapshot).CloneNormalized()
                });
            }
            catch (Exception ex)
            {
                failed.Add(new
                {
                    mac,
                    detectorType,
                    success = false,
                    message = ex.Message
                });
            }
            finally
            {
                try { await firmwareUpgradeService.DisconnectDeviceAsync(mac).ConfigureAwait(false); } catch { }
            }
        }

        await mqttService.PublishTeleJsonAsync("detector-settings", new
        {
            success = failed.Count == 0,
            action = "get",
            requestId,
            message = failed.Count == 0
                ? $"Detector settings loaded for {results.Count} sensor(s)."
                : $"Detector settings loaded with failures. Success={results.Count}, Failed={failed.Count}.",
            name = mqttService.CurrentOptions.Name,
            networkId = mqttService.CurrentOptions.NetworkId,
            time = DateTimeOffset.UtcNow,
            requested,
            results,
            failed
        });
    };

    mqttService.SetDetectorSettingsRequested += async cmd =>
    {
        var requestId = string.IsNullOrWhiteSpace(cmd.RequestId) ? Guid.NewGuid().ToString("N") : cmd.RequestId!;
        if (!DetectorSettingsDedup(detectorSettingsRecentIds, requestId))
        {
            AppLog.Debug($"[DetectorSettings] set duplicate requestId={requestId} — skipped");
            return;
        }
        var requested = (cmd.Sensors ?? new List<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var patch = cmd.Settings?.CloneNormalized();
        var writeOnlyChanged = cmd.WriteOnlyChanged ?? true;

        if (requested.Count == 0)
        {
            await mqttService.PublishTeleJsonAsync("detector-settings", new
            {
                success = false,
                action = "set",
                requestId,
                message = "No sensors/mac addresses provided.",
                name = mqttService.CurrentOptions.Name,
                networkId = mqttService.CurrentOptions.NetworkId,
                time = DateTimeOffset.UtcNow
            });
            return;
        }

        if (patch == null || !patch.HasAnyValue())
        {
            await mqttService.PublishTeleJsonAsync("detector-settings", new
            {
                success = false,
                action = "set",
                requestId,
                message = "No detector setting changes provided. Send payload with settings.{userConfigHex|pushButtonsHex|daliPushButtonsHex|daliDeviceCommonParamHex|blePushButtonsHex|tunableWhiteListHex|tunableWhitePresetHex|tunableWhiteDefaultKelvinHex} (optionally with matching *MaskHex fields for section-based settings).",
                name = mqttService.CurrentOptions.Name,
                networkId = mqttService.CurrentOptions.NetworkId,
                time = DateTimeOffset.UtcNow
            });
            return;
        }

        var knownByMac = DeviceStorageService.GetDeviceListSnapshot()
            .Where(x => !string.IsNullOrWhiteSpace(x.MacAddress))
            .ToDictionary(x => x.MacAddress.Trim(), x => x, StringComparer.OrdinalIgnoreCase);

        var pincode = cmd.Pincode ?? "";
        var defaultDetector = NormalizeDetectorType(cmd.DetectorType);
        var defaultFw = (cmd.FirmwareVersion ?? "").Trim();
        var results = new List<object>();
        var failed = new List<object>();

        foreach (var mac in requested)
        {
            var detectorType = defaultDetector;
            if (string.IsNullOrWhiteSpace(detectorType) && knownByMac.TryGetValue(mac, out var known))
                detectorType = NormalizeDetectorType(known.DetectorType);
            if (string.IsNullOrWhiteSpace(detectorType))
                detectorType = "P48";

            var fw = defaultFw;

            try
            {
                var cl = await firmwareUpgradeService.ConnectAndLoginWithRetryForPipelineAsync(
                    firmwareUpgradeService.GatewayIpAddress,
                    firmwareUpgradeService.GatewayPort,
                    mac,
                    pincode,
                    logId: requestId,
                    firmwareVersion: fw,
                    maxAttempts: Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS),
                    delayBetweenAttemptsMs: 2000,
                    bootModeIsRetryable: true).ConfigureAwait(false);

                if (!cl.Success)
                {
                    failed.Add(new
                    {
                        mac,
                        detectorType,
                        success = false,
                        message = $"Connect+login failed: {cl.Message}"
                    });
                    continue;
                }

                var apply = await firmwareUpgradeService.SettingsBackupService
                    .ApplyOverridesAsync(mac, detectorType, fw, patch, writeOnlyChanged).ConfigureAwait(false);

                var current = await firmwareUpgradeService.SettingsBackupService
                    .CaptureSnapshotAsync(mac, detectorType, fw).ConfigureAwait(false);

                var row = new
                {
                    mac,
                    detectorType,
                    firmwareVersion = fw,
                    success = apply.Success,
                    statusCode = apply.StatusCode,
                    message = apply.Message,
                    settings = DetectorSettingsPatch.FromSnapshot(current).CloneNormalized()
                };

                if (apply.Success)
                    results.Add(row);
                else
                    failed.Add(row);
            }
            catch (Exception ex)
            {
                failed.Add(new
                {
                    mac,
                    detectorType,
                    success = false,
                    message = ex.Message
                });
            }
            finally
            {
                try { await firmwareUpgradeService.DisconnectDeviceAsync(mac).ConfigureAwait(false); } catch { }
            }
        }

        await mqttService.PublishTeleJsonAsync("detector-settings", new
        {
            success = failed.Count == 0,
            action = "set",
            requestId,
            message = failed.Count == 0
                ? $"Detector settings applied for {results.Count} sensor(s)."
                : $"Detector settings apply finished with failures. Success={results.Count}, Failed={failed.Count}.",
            name = mqttService.CurrentOptions.Name,
            networkId = mqttService.CurrentOptions.NetworkId,
            time = DateTimeOffset.UtcNow,
            requested,
            writeOnlyChanged,
            requestedSettings = patch,
            results,
            failed
        });
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

    static string NormalizeDetectorType(string? value)
    {
        var s = (value ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(s))
            return "";

        if (s.Length >= 3 && (s[0] == 'P' || s[0] == 'M') && char.IsDigit(s[1]) && char.IsDigit(s[2]))
            s = $"{s[0]}{s[1]}{s[2]}";

        if (s.StartsWith("M", StringComparison.Ordinal))
            s = "P" + s[1..];
        if (s == "P49")
            s = "P46";

        return s;
    }

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

    // Peer backup sharing: respond to other gateways asking if we have a settings backup for a MAC.
    mqttService.GetDeviceBackupRequested += async cmd =>
    {
        if (string.IsNullOrWhiteSpace(cmd.MacAddress) ||
            string.IsNullOrWhiteSpace(cmd.RequesterName) ||
            string.IsNullOrWhiteSpace(cmd.RequestId))
            return;

        // Don't respond to our own broadcasts.
        if (string.Equals(cmd.RequesterName, mqttService.CurrentOptions.Name, StringComparison.OrdinalIgnoreCase))
            return;

        var snapshot = await firmwareUpgradeService.SettingsBackupService.TryGetSnapshotAsync(cmd.MacAddress).ConfigureAwait(false);
        if (snapshot is null) return;

        AppLog.Info($"[PeerBackup] Responding to {cmd.RequesterName} with backup for {cmd.MacAddress}");
        await mqttService.PublishDeviceBackupResponseAsync(cmd.RequesterName, new DeviceBackupResponseCommand
        {
            RequestId = cmd.RequestId,
            MacAddress = cmd.MacAddress,
            Snapshot = snapshot
        }).ConfigureAwait(false);
    };

    // ── DALI Database read/write ──────────────────────────────────────────────
    var daliDbService = serviceProvider.GetRequiredService<DaliDbService>();

    mqttService.DaliDbReadRequested += async cmd =>
    {
        var mac = (cmd.Sensor ?? "").Trim();
        if (string.IsNullOrWhiteSpace(mac))
        {
            await mqttService.PublishTeleJsonAsync("dali-db",
                new { mac = "", status = "error", error = "sensor MAC required" }).ConfigureAwait(false);
            return;
        }

        AppLog.Info($"[DaliDb] dali-db-read requested for {mac}");
        try
        {
            var db = await daliDbService.ReadAsync(mac).ConfigureAwait(false);
            await mqttService.PublishTeleJsonAsync("dali-db",
                new { mac, status = "ok", requestId = cmd.RequestId, db }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[DaliDb] Read failed for {mac}: {ex.Message}");
            await mqttService.PublishTeleJsonAsync("dali-db",
                new { mac, status = "error", requestId = cmd.RequestId, error = ex.Message }).ConfigureAwait(false);
        }
    };

    mqttService.DaliDbWriteRequested += async cmd =>
    {
        var mac = (cmd.Sensor ?? "").Trim();
        if (string.IsNullOrWhiteSpace(mac) || cmd.Db is null)
        {
            await mqttService.PublishTeleJsonAsync("dali-db",
                new { mac, status = "error", error = "sensor MAC and db payload required" }).ConfigureAwait(false);
            return;
        }

        AppLog.Info($"[DaliDb] dali-db-write requested for {mac}");
        try
        {
            await daliDbService.WriteAsync(mac, cmd.Db).ConfigureAwait(false);
            await mqttService.PublishTeleJsonAsync("dali-db",
                new { mac, status = "write-ok", requestId = cmd.RequestId }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[DaliDb] Write failed for {mac}: {ex.Message}");
            await mqttService.PublishTeleJsonAsync("dali-db",
                new { mac, status = "error", requestId = cmd.RequestId, error = ex.Message }).ConfigureAwait(false);
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

    var discovery = scope.ServiceProvider.GetRequiredService<LocalBrokerDiscoveryService>();
    discovery.Stop();
});

app.Run();

static void MigrateToStateDir(string oldPath, string newPath)
{
    if (!File.Exists(oldPath) || File.Exists(newPath))
        return;
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.Move(oldPath, newPath);
        Console.WriteLine($"[info] Migrated {oldPath} -> {newPath}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[warn] Could not migrate {oldPath}: {ex.Message}");
    }
}

