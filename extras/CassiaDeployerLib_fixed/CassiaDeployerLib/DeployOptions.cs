using System.Text.Json;

namespace CassiaDeployerLib;

public sealed class DeployOptions
{
    public string Host { get; set; } = "192.168.40.1";
    public int Port { get; set; } = 20022;
    public string User { get; set; } = "cassia";
    public string Password { get; set; } = "cassia-e42b34";

    public string ProjectDir { get; set; } = @"C:\Users\PLO\source\repos\CassiaGateway\AccessAPP";
    public string ProjectFile { get; set; } = @"C:\Users\PLO\source\repos\CassiaGateway\AccessAPP\AccessAPP.csproj";

    public string LocalPublishDir { get; set; } = @"C:\Users\PLO\source\repos\CassiaGateway\AccessAPP\publish";

    public string RemoteDir { get; set; } = "/home/cassia/FWUpgrade";
    public string RemoteExeName { get; set; } = "AccessAPP";
    public string ServiceName { get; set; } = "accessapp";

    public bool SkipUnchanged { get; set; } = true;
    public bool ManageService { get; set; } = true;
    public bool InstallServiceIfMissing { get; set; } = true;
    public bool UnmaskServiceIfMasked { get; set; } = true;

    public string? ExtraChmod755Path { get; set; } =
        "/home/cassia/FWUpgrade/libBootloaderUtilMultiThread.so";

    public string RemoteManifestPath { get; set; } =
        "/home/cassia/FWUpgrade/.deploy_manifest.txt";

    public string PublishConfiguration { get; set; } = "Release";
    public string PublishRuntime { get; set; } = "linux-arm";
    public bool SelfContained { get; set; } = true;
    public bool SkipClientAppBuild { get; set; } = false;

    // ---------- Startup updater deploy ----------
    public bool InstallStartupUpdater { get; set; } = true;

    public string UpdaterProjectDir { get; set; } =
        @"C:\Users\PLO\source\repos\CassiaGateway\extras\AccessAppUpdater";

    public string UpdaterProjectFile { get; set; } =
        @"C:\Users\PLO\source\repos\CassiaGateway\extras\AccessAppUpdater\AccessAppUpdater.csproj";

    public string LocalUpdaterPublishDir { get; set; } =
        @"C:\Users\PLO\source\repos\CassiaGateway\extras\AccessAppUpdater\publish";

    public string UpdaterPublishRuntime { get; set; } = "linux-arm";
    public bool UpdaterSelfContained { get; set; } = true;
    public bool UpdaterSingleFile { get; set; } = true;

    public string UpdaterRemoteExePath { get; set; } = "/usr/local/bin/AccessAppUpdater";
    public string UpdaterRemoteConfigPath { get; set; } = "/etc/accessapp-updater.json";
    public string UpdaterManifestUrl { get; set; } = "http://prod.statistics.niko-test.nu/accessapp/{channel}/manifest.json";
    public string UpdaterChannel { get; set; } = "stable";
    public string UpdaterChannelFilePath { get; set; } = "/etc/accessapp-updater.channel";
    public string UpdaterWorkDir { get; set; } = "/tmp/accessapp-updater";
    public string UpdaterVersionFileName { get; set; } = "version.txt";
    public int UpdaterHttpTimeoutSeconds { get; set; } = 600;

    public List<string> UpdaterPreserveFiles { get; set; } = new() { "mqtt.json" };

    // ---------- SSH key login provisioning ----------
    // If enabled, the deployer will:
    //  1) Ensure your local public key is present in ~/.ssh/authorized_keys on the target.
    //  2) Ensure sshd is configured to allow public-key auth.
    //  3) Optionally disable password authentication and restart ssh.
    //
    // Defaults are set up for Peter's Windows key at:
    //   C:\Users\PLO\.ssh\id_ed25519(.pub)
    public bool EnsureSshKeyLogin { get; set; } = true;

    // Public key path (the deployer will read this file and append it if missing on target)
    public string LocalSshPublicKeyPath { get; set; } = @"C:\Users\PLO\.ssh\id_ed25519.pub";

    // If true, sets PasswordAuthentication no (recommended once key auth works)
    public bool DisablePasswordAuthentication { get; set; } = true;

    // If true, restarts ssh/sshd after updating sshd_config
    public bool RestartSshServiceAfterConfig { get; set; } = true;

    // ---------- Bulk Wi-Fi deploy (Cassia AP mode) ----------
    // If enabled, the deployer will:
    //  1) Build+publish once
    //  2) Enumerate nearby Wi-Fi networks and match SSIDs by prefix (default: "cassia-e4")
    //  3) Connect to each SSID one-by-one
    //  4) Deploy to the default Cassia AP IP (Host) using the SSID as password (unless Password is set)
    //
    // This is intended for provisioning many Cassias in AP mode quickly.
    public bool BulkWifiDeploy { get; set; } = false;

    // SSID prefix to match (case-insensitive). For "cassia-E4*", set "cassia-e4".
    public string BulkWifiSsidPrefix { get; set; } = "cassia-e4";

    // Optional: limit how many SSIDs to process (0 = no limit)
    public int BulkWifiMaxCount { get; set; } = 0;

    // How long to wait for Windows to report we're connected to the target SSID
    public int BulkWifiConnectTimeoutSeconds { get; set; } = 25;

    // How many times to attempt Wi-Fi connect per SSID (at least 3 recommended)
    public int BulkWifiConnectAttempts { get; set; } = 3;

    // Delay between Wi-Fi connect attempts
    public int BulkWifiConnectRetryDelayMs { get; set; } = 1500;

    // If true, attempts to create a temporary Wi-Fi profile for each SSID before connecting
    // (useful when the profile doesn't already exist).
    public bool BulkWifiAutoCreateProfile { get; set; } = true;

    // How many Wi-Fi scan passes to perform to find as many Cassias as possible
    public int BulkWifiScanPasses { get; set; } = 8;

    // Delay between Wi-Fi scan passes
    public int BulkWifiScanDelayMs { get; set; } = 2000;

    // How many times to retry full deployment per SSID (reconnect Wi-Fi + SSH each retry)
    public int BulkWifiDeployAttemptsPerTarget { get; set; } = 2;

    // Delay between full deployment retries per SSID
    public int BulkWifiDeployRetryDelayMs { get; set; } = 3000;

    // How many times to attempt SSH connect per target (at least 3 recommended)
    public int SshConnectAttempts { get; set; } = 3;

    // Delay between SSH connect attempts
    public int SshConnectRetryDelayMs { get; set; } = 1500;

    // ---------- JSON loading ----------

    public static DeployOptions Load(string path)
    {
        var json = File.ReadAllText(path);
        var options = JsonSerializer.Deserialize<DeployOptions>(
                   json,
                   new JsonSerializerOptions
                   {
                       PropertyNameCaseInsensitive = true
                   }
               )
               ?? throw new InvalidOperationException(
                   "Failed to deserialize DeployOptions");

        options.NormalizeLocalPaths(Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory());
        return options;
    }

    public static DeployOptions LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new DeployOptions();
            defaults.Save(path);
            return defaults;
        }

        return Load(path);
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(
            this,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        File.WriteAllText(path, json);
    }

    private void NormalizeLocalPaths(string baseDir)
    {
        ProjectDir = ResolveMaybeRelative(ProjectDir, baseDir, mustExist: true, expectDirectory: true);
        ProjectFile = ResolveMaybeRelative(ProjectFile, baseDir, mustExist: true, expectDirectory: false);
        LocalPublishDir = ResolveMaybeRelative(LocalPublishDir, baseDir, mustExist: false, expectDirectory: true);
        UpdaterProjectDir = ResolveMaybeRelative(UpdaterProjectDir, baseDir, mustExist: true, expectDirectory: true);
        UpdaterProjectFile = ResolveMaybeRelative(UpdaterProjectFile, baseDir, mustExist: true, expectDirectory: false);
        LocalUpdaterPublishDir = ResolveMaybeRelative(LocalUpdaterPublishDir, baseDir, mustExist: false, expectDirectory: true);
        LocalSshPublicKeyPath = ResolveMaybeRelative(LocalSshPublicKeyPath, baseDir, mustExist: false, expectDirectory: false);
    }

    private static string ResolveMaybeRelative(string path, string baseDir, bool mustExist, bool expectDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        var candidates = new List<string>();

        void AddCandidate(string p)
        {
            var full = Path.GetFullPath(p);
            if (!candidates.Contains(full, StringComparer.OrdinalIgnoreCase))
                candidates.Add(full);
        }

        // 1) Relative to config file location
        AddCandidate(Path.Combine(baseDir, path));

        // 2) Relative to process working directory
        AddCandidate(Path.Combine(Directory.GetCurrentDirectory(), path));

        // 3) Relative to any ancestor of config location (helps when config is copied to bin/)
        var cursor = new DirectoryInfo(baseDir);
        while (cursor != null)
        {
            AddCandidate(Path.Combine(cursor.FullName, path));
            cursor = cursor.Parent;
        }

        if (!mustExist)
            return candidates[0];

        foreach (var c in candidates)
        {
            if (expectDirectory)
            {
                if (Directory.Exists(c)) return c;
            }
            else
            {
                if (File.Exists(c)) return c;
            }
        }

        // Fall back to first candidate to preserve a deterministic error path later.
        return candidates[0];
    }
}
