using CassiaDeployerLib;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var optionsPath = Path.Combine(
                AppContext.BaseDirectory,
                "deployoptions.json");

            DeployOptions options = DeployOptions.LoadOrCreate(optionsPath);

            // use options.Host, options.RemoteDir, etc.
            var log = new ConsoleProgress();

            log.Info("=== Cassia AccessAPP Deployer ===");
            log.Info($"Target        : {options.User}@{options.Host}:{options.Port}");
            log.Info($"Local publish : {options.LocalPublishDir}");
            log.Info($"Remote dir    : {options.RemoteDir}");
            log.Info($"Service       : {options.ServiceName}");
            log.Info("");

            var deployer = new SshCassiaDeployer(options, log);
            deployer.Run();

            log.Info("");
            log.Info("Deployment finished successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("DEPLOY FAILED:");
            Console.Error.WriteLine(ex);
            Console.ResetColor();
            return 1;
        }
    }
}
