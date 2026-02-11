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

            ApplyCommandLineOverrides(options, args);

            var log = new ConsoleProgress();

            log.Info("=== Cassia AccessAPP Deployer ===");
            log.Info($"Target        : {options.User}@{options.Host}:{options.Port}");
            log.Info($"Local publish : {options.LocalPublishDir}");
            log.Info($"Remote dir    : {options.RemoteDir}");
            log.Info($"Service       : {options.ServiceName}");
            log.Info($"Skip client build: {options.SkipClientAppBuild}");
            if (options.InstallStartupUpdater)
            {
                log.Info($"Updater exe   : {options.UpdaterRemoteExePath}");
                log.Info($"Updater feed  : {options.UpdaterManifestUrl}");
                log.Info($"Updater ch    : {options.UpdaterChannel}");
                log.Info($"Updater chfile: {options.UpdaterChannelFilePath}");
            }
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
        Console.WriteLine("Closing in 5 seconds ...");
        Thread.Sleep(TimeSpan.FromSeconds(5));
        return exitCode;
    }

    private static void ApplyCommandLineOverrides(DeployOptions opt, string[] args)
    {
        // Supported:
        //   --bulk-wifi                 Enables BulkWifiDeploy (iterate cassia-E4* SSIDs)
        //   --ssid-prefix <prefix>      Override BulkWifiSsidPrefix (case-insensitive)
        //   --max <n>                   Limit number of SSIDs processed (0 = no limit)
        //   --scan-passes <n>           Number of Wi-Fi scan passes before bulk deploy
        //   --scan-delay-ms <n>         Delay between Wi-Fi scan passes
        //   --deploy-attempts <n>       Full deploy retries per SSID
        //   --host <ip>                 Override host (default is 192.168.40.1)
        //   --port <n>                  Override port
        //   --user <name>               Override user
        //   --password <pwd>            Override password (if empty, SSID will be used)

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i].Trim();
            if (string.IsNullOrWhiteSpace(a)) continue;

            switch (a.ToLowerInvariant())
            {
                case "--bulk-wifi":
                case "/bulk-wifi":
                    opt.BulkWifiDeploy = true;
                    break;

                case "--ssid-prefix":
                case "--prefix":
                    if (i + 1 < args.Length) opt.BulkWifiSsidPrefix = args[++i];
                    break;

                case "--max":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var max)) opt.BulkWifiMaxCount = max;
                    break;

                case "--scan-passes":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var scanPasses)) opt.BulkWifiScanPasses = scanPasses;
                    break;

                case "--scan-delay-ms":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var scanDelayMs)) opt.BulkWifiScanDelayMs = scanDelayMs;
                    break;

                case "--deploy-attempts":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var deployAttempts)) opt.BulkWifiDeployAttemptsPerTarget = deployAttempts;
                    break;

                case "--host":
                    if (i + 1 < args.Length) opt.Host = args[++i];
                    break;

                case "--port":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var port)) opt.Port = port;
                    break;

                case "--user":
                    if (i + 1 < args.Length) opt.User = args[++i];
                    break;

                case "--password":
                case "--pass":
                    if (i + 1 < args.Length) opt.Password = args[++i];
                    break;
            }
        }
    }
}
