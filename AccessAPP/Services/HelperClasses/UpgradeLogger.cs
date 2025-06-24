using System.Text;

public static class UpgradeLogger
{
    private static readonly object _lock = new object();
    private const string LogFilePath = "Logs/upgrade_logs.txt";

    public static void Log(string logId, string mac, string stage, string status, string fwVersion = null)
    {
        lock (_lock)
        {
            Directory.CreateDirectory("Logs");

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logEntry = $"[logId={logId}] stage={stage} time={timestamp}";

            if (!string.IsNullOrEmpty(mac))
                logEntry += $" mac={mac}";

            if (!string.IsNullOrEmpty(fwVersion))
                logEntry += $" fw={fwVersion}";

            if (!string.IsNullOrEmpty(status))
                logEntry += $" status={status}";

            File.AppendAllText(LogFilePath, logEntry + Environment.NewLine, Encoding.UTF8);
        }
    }
}
