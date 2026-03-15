using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace AccessAppMqttWpf.Services;

/// <summary>
/// Lightweight file logger for the WPF app.
/// Writes to %LOCALAPPDATA%\AccessAppMqttWpf\logs\wpf-YYYY-MM-DD.log (one file per day).
/// Thread-safe via locking on <see cref="_gate"/>.
/// Call <see cref="Init"/> once at startup.
/// </summary>
public static class AppLog
{
    private static readonly object _gate = new();
    private static string _logDir = "";

    /// <summary>Must be called once at app startup before any logging.</summary>
    public static void Init()
    {
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessAppMqttWpf", "logs");
        Directory.CreateDirectory(_logDir);
    }

    public static void Info(string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        => Write("INFO", message, member, file, line);

    public static void Warn(string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        => Write("WARN", message, member, file, line);

    public static void Error(string message, Exception? ex = null,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        => Write("ERROR", ex != null ? $"{message} :: {ex.Message}" : message, member, file, line);

    private static void Write(string level, string message, string member, string file, int line)
    {
        var fileName = TryGetFileName(file);
        var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] ({fileName}:{line} {member}) {message}";

        System.Diagnostics.Debug.WriteLine(entry);

        if (string.IsNullOrEmpty(_logDir)) return;

        try
        {
            var logPath = Path.Combine(_logDir, $"wpf-{DateTime.Today:yyyy-MM-dd}.log");
            lock (_gate)
                File.AppendAllText(logPath, entry + Environment.NewLine);
        }
        catch { /* never throw from logger */ }
    }

    private static string TryGetFileName(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var slash = path.LastIndexOfAny(new[] { '\\', '/' });
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }
}
