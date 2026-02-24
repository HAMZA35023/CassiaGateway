namespace AccessAPP;

/// <summary>
/// Persistent state paths that survive application updates.
/// All mutable config and logs live under $HOME/.local/state/accessapp/
/// </summary>
public static class AccessAppPaths
{
    public static readonly string StateDir = Path.Combine(
        Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "state", "accessapp");

    public static string MqttConfig    => Path.Combine(StateDir, "mqtt.json");
    public static string RuntimeConfig => Path.Combine(StateDir, "runtime.json");
    public static string UpgradeLogDir => Path.Combine(StateDir, "logs");
    public static string UpgradeLog    => Path.Combine(UpgradeLogDir, "upgrade_logs.txt");
}
