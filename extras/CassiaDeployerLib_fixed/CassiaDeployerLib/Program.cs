using CassiaDeployerLib;

internal static class Program
{
    private static int Main(string[] args)
    {
        int exitCode;

        try
        {
            var optionsPath = Path.Combine(
                AppContext.BaseDirectory,
                "deployoptions.json");

            DeployOptions options = DeployOptions.LoadOrCreate(optionsPath);

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

            exitCode = 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("DEPLOY FAILED:");
            Console.Error.WriteLine(ex);
            Console.ResetColor();

            exitCode = 1;
        }

        // Auto-close after 5 seconds
        Console.WriteLine();
        Console.WriteLine("Window will close in 5 seconds...");
        Thread.Sleep(TimeSpan.FromSeconds(5));

        return exitCode;
    }
}
