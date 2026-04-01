using System;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AccessAppMqttWpf.Services;

/// <summary>
/// Non-blocking perf / stats logger.
/// All file I/O runs on a background task — callers are never blocked.
/// Writes to %LOCALAPPDATA%\AccessAppMqttWpf\logs\perf-YYYY-MM-DD.log.
/// Call <see cref="Init"/> once at startup alongside <see cref="AppLog.Init"/>.
/// </summary>
public static class PerfLog
{
    private static readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    private static string _logDir = "";

    /// <summary>Set to true to enable logging (tied to developer mode).</summary>
    public static bool Enabled { get; set; }

    public static void Init()
    {
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessAppMqttWpf", "logs");
        Directory.CreateDirectory(_logDir);
        Task.Run(RunWriterAsync);
    }

    /// <summary>MQTT message arrived (called from background MQTT thread).</summary>
    public static void Mqtt(string leaf, int payloadBytes)
        => Enqueue($"MQTT  {leaf,-26} {payloadBytes,7}B");

    /// <summary>
    /// A Dispatcher work item finished.
    /// <paramref name="lagMs"/>  = time from InvokeAsync to lambda start (queue wait).
    /// <paramref name="workMs"/> = time the lambda itself ran on the UI thread.
    /// </summary>
    public static void UiWork(string op, long lagMs, long workMs)
        => Enqueue($"UI    {op,-26} lag={lagMs,5}ms  work={workMs,5}ms");

    /// <summary>A one-shot UI measurement (e.g. CollectionView.Refresh duration).</summary>
    public static void Measure(string op, long ms)
        => Enqueue($"MS    {op,-26} {ms,5}ms");

    private static void Enqueue(string body)
    {
        if (!Enabled || string.IsNullOrEmpty(_logDir)) return;
        _channel.Writer.TryWrite($"{DateTime.Now:HH:mm:ss.fff}  {body}");
    }

    private static async Task RunWriterAsync()
    {
        StreamWriter? writer = null;
        string? currentPath = null;

        try
        {
            await foreach (var line in _channel.Reader.ReadAllAsync())
            {
                try
                {
                    var path = Path.Combine(_logDir, $"perf-{DateTime.Today:yyyy-MM-dd}.log");
                    if (path != currentPath)
                    {
                        writer?.Dispose();
                        writer = new StreamWriter(path, append: true) { AutoFlush = true };
                        currentPath = path;
                    }
                    writer!.WriteLine(line);
                }
                catch { /* never throw from logger */ }
            }
        }
        catch { /* never throw from logger */ }
        finally { writer?.Dispose(); }
    }
}
