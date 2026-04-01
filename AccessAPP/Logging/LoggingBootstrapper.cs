using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AccessAPP;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;
using VersionInfo = AccessAPP.Version;

namespace AccessAPP.Logging;

public static class LoggingBootstrapper
{
    private static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Warning);

    /// <summary>
    /// Configures Serilog (console + rolling file + OTLP to Grafana Cloud).
    /// Default minimum level: Warning (adjustable via RuntimeVariables.LOG_MIN_LEVEL).
    /// </summary>
    public static bool TryConfigureSerilog(WebApplicationBuilder builder)
    {
        // Load runtime.json early so LOG_MIN_LEVEL can influence Serilog.
        LoadRuntimeVariablesEarly(builder);

        var minLevel = ResolveMinimumLevel();
        LevelSwitch.MinimumLevel = minLevel;
        AppLog.MinimumLevel = minLevel;

        var logDir = ResolveLogDirectory(builder.Environment.ContentRootPath);
        Directory.CreateDirectory(logDir);
        CleanupOldLogs(logDir, TimeSpan.FromDays(3));
        EnableSerilogSelfLog(logDir);

        var otlpResources = BuildOtlpResourceAttributes(builder);

        // Keep console output readable, include caller context (SourceFile/Member/Line)
        var consoleTemplate = "{Timestamp:HH:mm:ss} [{Level}] ({SourceFileName}:{SourceLine} {SourceMember}) {Message:lj}{NewLine}{Exception}";
        var fileTemplate    = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] ({SourceFileName}:{SourceLine} {SourceMember}) {Message:lj}{NewLine}{Exception}";

        builder.Host.UseSerilog((ctx, services, cfg) =>
        {
            cfg.MinimumLevel.ControlledBy(LevelSwitch)
               .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
               .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
               .Enrich.FromLogContext()
               .Enrich.WithEnvironmentName()
               .Enrich.WithProcessId()
               .Enrich.WithThreadId()
               .WriteTo.Console(outputTemplate: consoleTemplate)
               .WriteTo.File(
                    path: Path.Combine(logDir, "accessapp-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 10,
                    fileSizeLimitBytes: 15 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    outputTemplate: fileTemplate
                );

            ConfigureOtlpSink(cfg, otlpResources);
        });

        AppLog.SerilogEnabled = true;
        Log.Information("Serilog enabled. MinLevel={MinLevel}; LogDir={LogDir}; OTLP endpoint={OtlpEndpoint}; service.name={ServiceName}",
            minLevel, logDir, OtlpEndpoint, otlpResources.GetValueOrDefault("service.name"));
        return true;
    }

    private const string OtlpEndpoint = "https://otlp-gateway-prod-eu-north-0.grafana.net/otlp/v1/logs";
    // Grafana Cloud OTLP: Basic auth = base64("1528426:glc_...")  (stack id first, then token)
    private const string OtlpHeaders = "Authorization=Basic MTUyODQyNjpnbGNfZXlKdklqb2lNVFkzTVRVNE9TSXNJbTRpT2lKaFkyTmxjM05oY0hBdFkyRnpjMmxoSWl3aWF5STZJbk52YldnMFJqRTVaRmcxTURVMU1sYzNXamhHUjBoWFppSXNJbTBpT25zaWNpSTZJbkJ5YjJRdFpYVXRibTl5ZEdndE1DSjlmUT09";

    private static void ConfigureOtlpSink(LoggerConfiguration cfg, IDictionary<string, object> resourceAttributes)
    {
        try
        {
            var normalizedHeaders = NormalizeOtlpHeaders(OtlpHeaders);

            cfg.WriteTo.OpenTelemetry(otel =>
            {
                otel.Endpoint = OtlpEndpoint;
                otel.Protocol = OtlpProtocol.HttpProtobuf;
                if (normalizedHeaders != null && normalizedHeaders.Count > 0)
                    otel.Headers = normalizedHeaders;
                if (resourceAttributes.Count > 0)
                    otel.ResourceAttributes = resourceAttributes;
            });

            Log.Information("OTLP sink configured: endpoint={Endpoint}, headers={HeaderCount}, resources={ResourceCount}",
                OtlpEndpoint, normalizedHeaders?.Count ?? 0, resourceAttributes.Count);
        }
        catch (Exception ex)
        {
            // Keep app alive even if OTLP misconfiguration occurs.
            Log.Warning("Failed to configure OTLP sink: {Error}", ex.Message);
        }
    }

    private static Dictionary<string, object> BuildOtlpResourceAttributes(WebApplicationBuilder builder)
    {
        var (mqttName, mqttNetwork) = TryReadMqttIdentity(builder);

        var attrs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["service.name"] = mqttName ?? ResolveServiceName(builder),
            ["service.namespace"] = "accessapp",
            ["service.version"] = VersionInfo.AppVersion,
            ["deployment.environment"] = builder.Environment.EnvironmentName,
            ["host.name"] = Environment.MachineName
        };

        if (!string.IsNullOrWhiteSpace(mqttNetwork))
            attrs["cassia.network"] = mqttNetwork;

        var extra = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");
        if (!string.IsNullOrWhiteSpace(extra))
        {
            foreach (var pair in extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = pair.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length == 2 && !string.IsNullOrWhiteSpace(kv[0]))
                    attrs[kv[0]] = kv[1];
            }
        }

        return attrs;
    }

    private static string ResolveServiceName(WebApplicationBuilder builder)
    {
        var envValue = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
        if (!string.IsNullOrWhiteSpace(envValue))
            return envValue.Trim();

        if (!string.IsNullOrWhiteSpace(builder.Environment.ApplicationName))
            return builder.Environment.ApplicationName!;

        return "accessapp";
    }

    private static IDictionary<string, string>? NormalizeOtlpHeaders(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        try
        {
            // Handles %20 (spaces) and other URL-encoded chars commonly used in env vars
            trimmed = Uri.UnescapeDataString(trimmed);
        }
        catch
        {
            trimmed = trimmed.Replace("%20", " ");
        }

        // Accept either a single header (key=value) or multiple separated by commas.
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in trimmed.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2 && !string.IsNullOrWhiteSpace(kv[0]))
                dict[kv[0]] = kv[1];
        }

        return dict;
    }

    private const long SelfLogMaxBytes = 15 * 1024 * 1024;

    private static void EnableSerilogSelfLog(string logDir)
    {
        try
        {
            var selfLogPath = Path.Combine(logDir, "serilog-selflog.txt");
            var writer = new CappedFileWriter(selfLogPath, SelfLogMaxBytes);
            Serilog.Debugging.SelfLog.Enable(TextWriter.Synchronized(writer));
            Console.WriteLine($"[info] Serilog SelfLog writing to {selfLogPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[warn] Failed to enable Serilog SelfLog: {ex.Message}");
        }
    }

    /// <summary>
    /// A TextWriter that appends to a file and truncates it (starts fresh) once it exceeds
    /// <paramref name="maxBytes"/>, ensuring the file never grows beyond the cap at runtime.
    /// </summary>
    private sealed class CappedFileWriter : TextWriter
    {
        private readonly string _path;
        private readonly long _maxBytes;

        public CappedFileWriter(string path, long maxBytes)
        {
            _path = path;
            _maxBytes = maxBytes;
        }

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            try
            {
                var mode = File.Exists(_path) && new FileInfo(_path).Length > _maxBytes
                    ? FileMode.Create   // truncate: exceeded cap
                    : FileMode.Append;

                using var fs = new FileStream(_path, mode, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs, System.Text.Encoding.UTF8) { AutoFlush = true };
                sw.WriteLine(value);
            }
            catch { /* best-effort */ }
        }
    }

    private static (string? name, string? networkId) TryReadMqttIdentity(WebApplicationBuilder builder)
    {
        try
        {
            var path = builder.Configuration.GetValue<string>("Mqtt:ConfigPath");
            if (string.IsNullOrWhiteSpace(path))
                path = AccessAppPaths.MqttConfig;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(builder.Environment.ContentRootPath, path);

            if (!File.Exists(path))
                return (null, null);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            string? name = null;
            string? networkId = null;

            if (root.TryGetProperty("name", out var pName) && pName.ValueKind == JsonValueKind.String)
                name = pName.GetString();
            if (root.TryGetProperty("networkId", out var pNet) && pNet.ValueKind == JsonValueKind.String)
                networkId = pNet.GetString();

            return (name, networkId);
        }
        catch
        {
            return (null, null);
        }
    }

    private static void LoadRuntimeVariablesEarly(WebApplicationBuilder builder)
    {
        try
        {
            var store = new Services.RuntimeVariablesStore(builder.Configuration, builder.Environment);
            store.LoadFromDisk();
        }
        catch (Exception ex)
        {
            // Use Console because Serilog not configured yet.
            Console.WriteLine($"[warn] RuntimeVariables preload failed: {ex.Message}");
        }
    }

    private static LogEventLevel ResolveMinimumLevel()
    {
        var runtimeLevel = ParseLogLevel(RuntimeVariables.LOG_MIN_LEVEL, null);
        if (runtimeLevel.HasValue)
            return runtimeLevel.Value;

        var vVerbose = Environment.GetEnvironmentVariable("ACCESSAPP_VERBOSE")?.Trim();
        if (IsTruthy(vVerbose))
            return LogEventLevel.Verbose;

        var vDebug = Environment.GetEnvironmentVariable("ACCESSAPP_DEBUG")?.Trim();
        if (IsTruthy(vDebug))
            return LogEventLevel.Debug;

        var v = Environment.GetEnvironmentVariable("ACCESSAPP_LOG_LEVEL");
        var envLevel = ParseLogLevel(v, null);
        if (envLevel.HasValue)
            return envLevel.Value;

        return LogEventLevel.Warning;
    }

    public static void RefreshLogLevelFromRuntime()
    {
        var level = ResolveMinimumLevel();
        LevelSwitch.MinimumLevel = level;
        AppLog.MinimumLevel = level;
        Log.Information("Runtime log level updated to {LogLevel}", level);
    }

    private static LogEventLevel? ParseLogLevel(string? raw, LogEventLevel? fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "verbose":
            case "vrb":
            case "trace":
                return LogEventLevel.Verbose;
            case "debug":
            case "dbg":
                return LogEventLevel.Debug;
            case "info":
            case "information":
            case "inf":
                return LogEventLevel.Information;
            case "warn":
            case "warning":
            case "wrn":
                return LogEventLevel.Warning;
            case "error":
            case "err":
                return LogEventLevel.Error;
            case "fatal":
            case "ftl":
                return LogEventLevel.Fatal;
            default:
                return fallback;
        }
    }

    private static bool IsEnabled()
    {
        var v = Environment.GetEnvironmentVariable("ACCESSAPP_USE_SERILOG");
        if (string.IsNullOrWhiteSpace(v)) return false;
        v = v.Trim();
        return v.Equals("1") || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTruthy(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        v = v.Trim();
        return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLogDirectory(string contentRoot)
    {
        var env = Environment.GetEnvironmentVariable("ACCESSAPP_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        // Default: ./logs (next to app)
        return Path.Combine(contentRoot, "logs");
    }

    private static void CleanupOldLogs(string logDir, TimeSpan maxAge)
    {
        try
        {
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var file in Directory.EnumerateFiles(logDir, "*.log", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if (lastWrite < cutoff)
                        File.Delete(file);
                }
                catch { /* ignore individual file issues */ }
            }
        }
        catch { /* ignore cleanup issues */ }
    }
}
