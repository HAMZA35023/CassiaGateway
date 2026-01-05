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
    public string? ExtraChmod755Path { get; set; } = "/home/cassia/FWUpgrade/libBootloaderUtilMultiThread.so";
    public string RemoteManifestPath { get; set; } = "/home/cassia/FWUpgrade/.deploy_manifest.txt";

    public string PublishConfiguration { get; set; } = "Release";
    public string PublishRuntime { get; set; } = "linux-arm";
    public bool SelfContained { get; set; } = true;
}
