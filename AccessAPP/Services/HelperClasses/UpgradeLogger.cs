using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

public interface IUpgradeMqttPublisher
{
    /// <summary>
    /// Publish a UTF-8 payload to a topic. Implementation can wrap MQTTnet, your MqttClientService, etc.
    /// </summary>
    Task PublishAsync(string topic, string payload, bool retain = false, int qos = 0, CancellationToken ct = default);
}

public static class UpgradeLogger
{
    public static Action<string>? DebugLog { get; set; }
    private static void D(string msg) => DebugLog?.Invoke("[UpgradeLogger] " + msg);

    private const int UpgradeLogQos = 1; // deliver-at-least-once for upgrade-log messages

    private static readonly object _lock = new object();
    private static readonly string LogDir = "/etc/accessapp/logs";
    private static readonly string LogFilePath = Path.Combine(LogDir, "upgrade_logs.txt");

    // ---- MQTT wiring (set these from your app at startup) ----
    public static IUpgradeMqttPublisher? Mqtt { get; set; }
    public static Func<string, string>? TopicResolver { get; set; } // takes networkId -> topic
    public static string NetworkId { get; set; } = "dk-lab";

    // ---- Background publish queue ----
    private static readonly Channel<MqttLogItem> _mqttQueue =
        Channel.CreateBounded<MqttLogItem>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // don't blow memory if broker is down
            SingleReader = true,
            SingleWriter = false
        });

    private static int _publisherStarted = 0;

    /// <summary>
    /// Call once at app startup (or just set Mqtt; the first Log() also auto-starts).
    /// </summary>
    public static void StartMqttPublisher(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _publisherStarted, 1) == 1) return;
        _ = Task.Run(() => PublisherLoopAsync(ct), ct);
    }

    public static void Log(string logId, string mac, string stage, string status, string fwVersion = null, string deviceName = null)
    {
        // Ensure publisher loop is running (safe if called multiple times)
        if (_publisherStarted == 0) StartMqttPublisher();

        // ---- Build line and write to file (synchronous, protected) ----
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string logEntry;

        lock (_lock)
        {
            Directory.CreateDirectory(LogDir);

            logEntry = $"[logId={logId}] stage={stage} time={timestamp}";

            if (!string.IsNullOrEmpty(mac))
                logEntry += $" mac={mac}";

            if (!string.IsNullOrEmpty(deviceName))
                logEntry += $" name={deviceName}";

            if (!string.IsNullOrEmpty(fwVersion))
                logEntry += $" fw={fwVersion}";

            if (!string.IsNullOrEmpty(status))
                logEntry += $" status={status}";

            File.AppendAllText(LogFilePath, logEntry + Environment.NewLine, Encoding.UTF8);
        }

        // ---- Enqueue MQTT publish (NO lock, NO blocking) ----
        var item = new MqttLogItem
        {
            LogId = logId,
            Mac = mac,
            Stage = stage,
            Status = status,
            Fw = fwVersion,
            Name = deviceName,
            Detector = deviceName,
            TimeLocal = timestamp,
            Line = logEntry,
            NetworkId = NetworkId
        };

        _mqttQueue.Writer.TryWrite(item);
    }

    private static async Task PublisherLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var item = await _mqttQueue.Reader.ReadAsync(ct).ConfigureAwait(false);

                var mqtt = Mqtt;
                if (mqtt == null)
                    continue; // no publisher configured

                var topic = TopicResolver?.Invoke(item.NetworkId)
                           ?? $"accessapp/{item.NetworkId}/tele/upgrade-log";

                // JSON payload is usually nicer than the raw line (we include both)
                var payload = JsonSerializer.Serialize(new
                {
                    logId = item.LogId,
                    mac = item.Mac,
                    stage = item.Stage,
                    status = item.Status,
                    name = item.Name,
                    detector = item.Detector,
                    fw = item.Fw,
                    timeLocal = item.TimeLocal,
                    line = item.Line
                });

                await mqtt.PublishAsync(topic, payload, retain: false, qos: UpgradeLogQos, ct: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Swallow to keep logger resilient. If you want, write to a separate internal debug file here.
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }


    }

    // Publish saved log file (optionally filtered) to MQTT as one compressed payload.
    public static async Task PublishSavedLogAsync(
        string? logIdFilter = null,
        int maxLines = 5000,
        CancellationToken ct = default)
    {
        try
        {
            var mqtt = Mqtt;
            if (mqtt == null)
            {
                D("PublishSavedLogAsync: MQTT publisher is NULL (UpgradeLogger.Mqtt not configured).");
                return;
            }

            D($"PublishSavedLogAsync: LogFilePath='{LogFilePath}' exists={File.Exists(LogFilePath)}");

            if (!File.Exists(LogFilePath))
                return;

            string[] lines;
            lock (_lock)
            {
                lines = File.ReadAllLines(LogFilePath, Encoding.UTF8);
            }

            D($"PublishSavedLogAsync: read {lines.Length} line(s) from file.");

            // Compressed transport always sends the full saved log file.
            if (!string.IsNullOrWhiteSpace(logIdFilter) || maxLines != 5000)
            {
                D("PublishSavedLogAsync: logIdFilter/maxLines ignored for compressed mode (sending full file).");
            }

            var topic = TopicResolver?.Invoke(NetworkId)
                       ?? $"accessapp/{NetworkId}/tele/upgrade-log";

            D($"PublishSavedLogAsync: publishing compressed payload to topic='{topic}'.");

            var fullText = string.Join(Environment.NewLine, lines);
            var sourceBytes = Encoding.UTF8.GetBytes(fullText);
            var compressedBytes = GzipCompress(sourceBytes);
            var compressedBase64 = Convert.ToBase64String(compressedBytes);

            await mqtt.PublishAsync(topic, JsonSerializer.Serialize(new
            {
                type = "saved-log-gzip",
                logId = logIdFilter,
                encoding = "gzip+base64+utf8",
                totalLines = lines.Length,
                originalBytes = sourceBytes.Length,
                compressedBytes = compressedBytes.Length,
                data = compressedBase64,
                timeLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            }), retain: false, qos: UpgradeLogQos, ct: ct).ConfigureAwait(false);

            D($"PublishSavedLogAsync: done. totalLines={lines.Length}, originalBytes={sourceBytes.Length}, compressedBytes={compressedBytes.Length}.");
        }
        catch (Exception ex)
        {
            D("PublishSavedLogAsync ERROR: " + ex);
            throw; // so caller can log too
        }
    }

    private static byte[] GzipCompress(byte[] source)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(source, 0, source.Length);
        }

        return output.ToArray();
    }


    private sealed class MqttLogItem
    {
        public string NetworkId { get; set; } = "default";
        public string LogId { get; set; } = "";
        public string Mac { get; set; } = "";
        public string Stage { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Name { get; set; }
        public string? Detector { get; set; }
        public string? Fw { get; set; }
        public string TimeLocal { get; set; } = "";
        public string Line { get; set; } = "";
    }
}

