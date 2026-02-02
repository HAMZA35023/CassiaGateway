using Serilog;
using Serilog.Events;

namespace AccessAPP.Logging;

public static class LoggingBootstrapper
{
    /// <summary>
    /// Enables Serilog if ACCESSAPP_USE_SERILOG is true/1/yes.
    /// Writes to console and to a rolling file in LOG_DIR (default ./logs).
    /// Also deletes log files older than 3 days on startup.
    /// </summary>
    public static bool TryConfigureSerilog(WebApplicationBuilder builder)
    {
    
        // Minimum log level (default: Information)
        // Supported env vars:
        //  - ACCESSAPP_LOG_LEVEL=verbose|debug|info|warn|warning|error|fatal
        //  - ACCESSAPP_VERBOSE=1  (shortcut for verbose)
        //  - ACCESSAPP_DEBUG=1    (shortcut for debug)
        var minLevel = ResolveMinimumLevel();
        
        minLevel = LogEventLevel.Debug;
        
        AppLog.MinimumLevel = minLevel;

        var logDir = ResolveLogDirectory(builder.Environment.ContentRootPath);
        Directory.CreateDirectory(logDir);
        CleanupOldLogs(logDir, TimeSpan.FromDays(3));

        // Keep console output readable, include caller context (SourceFile/Member/Line)
        var consoleTemplate = "{Timestamp:HH:mm:ss} [{Level}] ({SourceFileName}:{SourceLine} {SourceMember}) {Message:lj}{NewLine}{Exception}";
        var fileTemplate    = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] ({SourceFileName}:{SourceLine} {SourceMember}) {Message:lj}{NewLine}{Exception}";

        builder.Host.UseSerilog((ctx, services, cfg) =>
        {
            cfg.MinimumLevel.Is(minLevel)
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
        });

        AppLog.SerilogEnabled = true;
        Log.Information("Serilog enabled. LogDir={LogDir}", logDir);
        return true;
    }

    private static LogEventLevel ResolveMinimumLevel()
    {
        var vVerbose = Environment.GetEnvironmentVariable("ACCESSAPP_VERBOSE")?.Trim();
        if (IsTruthy(vVerbose))
            return LogEventLevel.Verbose;

        var vDebug = Environment.GetEnvironmentVariable("ACCESSAPP_DEBUG")?.Trim();
        if (IsTruthy(vDebug))
            return LogEventLevel.Debug;

        var v = Environment.GetEnvironmentVariable("ACCESSAPP_LOG_LEVEL");
        if (string.IsNullOrWhiteSpace(v))
            return LogEventLevel.Information;

        v = v.Trim().ToLowerInvariant();
        return v switch
        {
            "verbose" or "vrb" or "trace" => LogEventLevel.Verbose,
            "debug" or "dbg" => LogEventLevel.Debug,
            "info" or "information" or "inf" => LogEventLevel.Information,
            "warn" or "warning" or "wrn" => LogEventLevel.Warning,
            "error" or "err" => LogEventLevel.Error,
            "fatal" or "ftl" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
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
