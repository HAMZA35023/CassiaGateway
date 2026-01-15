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
    public bool DisablePasswordAuthentication { get; set; } = false;

    // If true, restarts ssh/sshd after updating sshd_config
    public bool RestartSshServiceAfterConfig { get; set; } = true;

    // ---------- JSON loading ----------

    public static DeployOptions Load(string path)
    {
        var json = File.ReadAllText(path);

        return JsonSerializer.Deserialize<DeployOptions>(
                   json,
                   new JsonSerializerOptions
                   {
                       PropertyNameCaseInsensitive = true
                   }
               )
               ?? throw new InvalidOperationException(
                   "Failed to deserialize DeployOptions");
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
}
