using System;
using System.Runtime.CompilerServices;
using Serilog;
using Serilog.Events;

namespace AccessAPP.Logging;

/// <summary>
/// Small logging facade:
/// - If Serilog is enabled: writes to Serilog (console + file sinks as configured).
/// - If Serilog is disabled: falls back to Console.WriteLine.
///
/// Includes caller file/member/line so logs show where they came from.
/// </summary>
public static class AppLog
{
    internal static bool SerilogEnabled { get; set; }

    // Minimum level (used for the Console fallback and for quick checks).
    internal static LogEventLevel MinimumLevel { get; set; } = LogEventLevel.Information;

    public static bool IsVerboseEnabled => MinimumLevel <= LogEventLevel.Verbose;
    public static bool IsDebugEnabled => MinimumLevel <= LogEventLevel.Debug;

    private static readonly object LastErrorGate = new();
    private static string _lastErrorMessage = "-";
    private static DateTimeOffset? _lastErrorAtUtc;

    // Use a dedicated property to avoid anything else overwriting "SourceFile".
    private const string SourceFileNameProperty = "SourceFileName";

    public static void Info(string message,
        [CallerFilePath] string file = "",
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
        => Write(LogEventLevel.Information, message, null, file, member, line);

    public static void Verbose(string message,
        [CallerFilePath] string file = "",
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
        => Write(LogEventLevel.Verbose, message, null, file, member, line);

    public static void Warn(string message,
        [CallerFilePath] string file = "",
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
        => Write(LogEventLevel.Warning, message, null, file, member, line);

    public static void Error(string message, Exception? ex = null,
        [CallerFilePath] string file = "",
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
        => Write(LogEventLevel.Error, message, ex, file, member, line);

    public static void Fatal(string message, Exception? ex = null,
        [CallerFilePath] string file = "",
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
        => Write(LogEventLevel.Fatal, message, ex, file, member, line);

    public static void Debug(string message,
        [CallerFilePath] string file = "",
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
        => Write(LogEventLevel.Debug, message, null, file, member, line);

    public static (string Message, DateTimeOffset? AtUtc) GetLastError()
    {
        lock (LastErrorGate)
            return (_lastErrorMessage, _lastErrorAtUtc);
    }

    private static void Write(LogEventLevel level, string message, Exception? ex, string file, string member, int line)
    {
        var fileName = TryGetFileName(file);

        if (level >= LogEventLevel.Error)
            RecordLastError(message, ex);

        // Fast level filter (mainly relevant for non-Serilog fallback).
        if (level < MinimumLevel)
            return;

        if (SerilogEnabled)
        {
            // Force SourceFile to be filename only, regardless of what caller provides.
            var logger = Log.ForContext("SourceFileName", fileName)
                            .ForContext("SourceMember", member)
                            .ForContext("SourceLine", line);

            if (ex != null)
                logger.Write(level, ex, "{Message}", message);
            else
                logger.Write(level, "{Message}", message);
        }
        else
        {
            var prefix = $"{DateTime.Now:HH:mm:ss} [{level}] ({fileName}:{line} {member}) ";
            if (ex != null)
                Console.WriteLine(prefix + message + " :: " + ex);
            else
                Console.WriteLine(prefix + message);
        }
    }

    private static string TryGetFileName(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        try
        {
            // Fast manual trim (handles Windows + Linux paths, even malformed ones)
            var slash = path.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0 && slash < path.Length - 1)
                return path.Substring(slash + 1);

            return path;
        }
        catch
        {
            return path;
        }
    }

    private static void RecordLastError(string message, Exception? ex)
    {
        var msg = string.IsNullOrWhiteSpace(message) ? "Unexpected error" : message.Trim();
        if (ex != null && !string.IsNullOrWhiteSpace(ex.Message))
            msg = $"{msg} ({ex.Message.Trim()})";

        lock (LastErrorGate)
        {
            _lastErrorMessage = msg;
            _lastErrorAtUtc = DateTimeOffset.UtcNow;
        }
    }

}
