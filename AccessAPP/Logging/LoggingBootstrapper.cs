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
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    outputTemplate: fileTemplate
                );

            ConfigureOtlpSink(cfg, otlpResources);
        });

        AppLog.SerilogEnabled = true;
        Log.Information("Serilog enabled. MinLevel={MinLevel}; LogDir={LogDir}; OTLP endpoint={OtlpEndpoint}",
            minLevel, logDir, OtlpEndpoint);
        return true;
    }

    private const string OtlpEndpoint = "https://otlp-gateway-prod-eu-north-0.grafana.net/otlp/v1/logs";
    private const string OtlpHeaders = "Authorization=Basic Z2xjX2V5SnZJam9pTVRZM01UVTRPU0lzSW00aU9pSmhZMk5sYzNOaGNIQWlMQ0pySWpvaWFVZHplVWN5TkRsMU1qRXpRbU0zTTJ4R1NUWkNUazB5SWl3aWJTSTZleUp5SWpvaWNISnZaQzFsZFMxdWIzSjBhQzB3SW4xOToxNTI4NDI2";

    private static void ConfigureOtlpSink(LoggerConfiguration cfg, IReadOnlyDictionary<string, object> resourceAttributes)
    {
        try
        {
            var normalizedHeaders = NormalizeOtlpHeaders(OtlpHeaders);

            cfg.WriteTo.OpenTelemetry(otel =>
            {
                otel.Endpoint = OtlpEndpoint;
                otel.Protocol = OtlpProtocol.HttpProtobuf;
                if (!string.IsNullOrWhiteSpace(normalizedHeaders))
                    otel.Headers = normalizedHeaders;
                if (resourceAttributes.Count > 0)
                    otel.ResourceAttributes = resourceAttributes;
            });
        }
        catch (Exception ex)
        {
            // Keep app alive even if OTLP misconfiguration occurs.
            Log.Warning("Failed to configure OTLP sink: {Error}", ex.Message);
        }
    }

    private static IReadOnlyDictionary<string, object> BuildOtlpResourceAttributes(WebApplicationBuilder builder)
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

    private static string? NormalizeOtlpHeaders(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        try
        {
            // Handles %20 (spaces) and other URL-encoded chars commonly used in env vars
            return Uri.UnescapeDataString(trimmed);
        }
        catch
        {
            return trimmed.Replace("%20", " ");
        }
    }

    private static (string? name, string? networkId) TryReadMqttIdentity(WebApplicationBuilder builder)
    {
        try
        {
            var path = builder.Configuration.GetValue<string>("Mqtt:ConfigPath");
            if (string.IsNullOrWhiteSpace(path))
                path = "mqtt.json";
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
