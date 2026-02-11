// SshCassiaDeployer.cs
// Library-based deployer: dotnet publish -> SFTP sync (skip unchanged via manifest) -> chmod -> init-managed restart
// Supports: systemd, OpenRC, SysV init. Uses password-based sudo (no SSH keys needed).

using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;

namespace CassiaDeployerLib;

public sealed class SshCassiaDeployer
{
    private readonly DeployOptions _opt;
    private readonly ConsoleProgress _log;

    public SshCassiaDeployer(DeployOptions opt, ConsoleProgress log)
    {
        _opt = opt;
        _log = log;

        // For single-device deploy, allow auto-filling password from currently connected Wi-Fi SSID.
        // For bulk Wi-Fi deploy, password is set per SSID during iteration.
        if (!_opt.BulkWifiDeploy && string.IsNullOrWhiteSpace(_opt.Password))
        {
            var ssid = GetConnectedWifiSsidLower();

            if (!string.IsNullOrWhiteSpace(ssid))
            {
                _opt.Password = ssid;
                Console.WriteLine($"[INFO] Using Wi-Fi SSID as password: {ssid}");
            }
            else
            {
                Console.WriteLine("[WARN] No Wi-Fi SSID detected – password not auto-filled");
            }
        }
    }

    public static string? GetConnectedWifiSsidLower()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = "wlan show interfaces",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            return null;

        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();

            // Match: SSID : MyWifi
            if (trimmed.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("SSID name", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split(':', 2);
                if (parts.Length == 2)
                {
                    var ssid = parts[1].Trim();
                    return string.IsNullOrWhiteSpace(ssid)
                        ? null
                        : ssid.ToLowerInvariant();
                }
            }
        }

        return null;
    }

    private enum InitKind { Systemd, OpenRc, SysV, Unknown }

    private string ServiceUnitName => $"{_opt.ServiceName}.service";
    private string StartupUpdaterExecStartPre =>
        $"{_opt.UpdaterRemoteExePath} --config {_opt.UpdaterRemoteConfigPath}";

    public void Run()
    {
        // Build/publish all artifacts once, then deploy to one or many targets.
        BuildAllArtifacts();

        if (_opt.BulkWifiDeploy)
        {
            RunBulkWifiDeploy();
            return;
        }

        DeployToTarget(_opt.Host, _opt.Port, _opt.User, _opt.Password, label: _opt.Host);
    }

    private void DeployToTarget(string host, int port, string user, string password, string label)
    {
        // IMPORTANT:
        // Many operations (service stop/start, sshd hardening, chown, etc.) use RunSudo(),
        // which relies on _opt.Password. In single-device mode _opt.Password may be auto-filled.
        // In bulk mode we pass the per-SSID password into DeployToTarget(), so we must also
        // update _opt.Password for the duration of this deployment.
        var prevPassword = _opt.Password;
        _opt.Password = password;

        var conn = new PasswordConnectionInfo(host, port, user, password)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        using var ssh = new SshClient(conn);
        using var sftp = new SftpClient(conn);

        _log.Info($"Connecting to device ({label})...");
        ConnectSshAndSftpWithRetry(
            ssh,
            sftp,
            label,
            attempts: Math.Max(1, _opt.SshConnectAttempts),
            retryDelayMs: Math.Max(0, _opt.SshConnectRetryDelayMs));
        _log.Info("Connected.");

        try
        {
            // Provision SSH public-key login (so future logins/deploys can be password-less)
            if (_opt.EnsureSshKeyLogin)
            {
                try
                {
                    EnsureSshKeyLoginAndHardenSshd(ssh);
                }
                catch (Exception ex)
                {
                    // Do not fail deployment if hardening fails – keep current behavior.
                    _log.Error($"SSH key provisioning failed (deployment continues): {ex.Message}");
                }
            }

            EnsureRemoteDirWritable(ssh);

            var init = DetectInit(ssh);
            _log.Info($"Init system detected: {init}");

            if (_opt.ManageService && _opt.InstallServiceIfMissing)
                EnsureServiceInstalled(ssh, init);

            if (_opt.ManageService)
                StopService(ssh, init);

            UploadDirectorySftpWithManifest(ssh, sftp, _opt.LocalPublishDir, _opt.RemoteDir, _opt.RemoteManifestPath);

            // Ensure executable bit on main app
            var remoteExe = CombineRemote(_opt.RemoteDir, _opt.RemoteExeName);
            RunCommand(ssh, $"chmod +x {ShEscape(remoteExe)} || true");

            // Extra chmod requested
            if (!string.IsNullOrWhiteSpace(_opt.ExtraChmod755Path))
            {
                RunCommand(ssh, $"if [ -f {ShEscape(_opt.ExtraChmod755Path)} ]; then chmod 755 {ShEscape(_opt.ExtraChmod755Path)}; fi");
            }

            if (_opt.InstallStartupUpdater)
            {
                InstallStartupUpdater(ssh, sftp);
                EnsureSelfUpdateSudoers(ssh);
            }

            if (_opt.ManageService)
            {
                StartService(ssh, init);
                ShowServiceStatus(ssh, init);
            }
        }
        finally
        {
            if (sftp.IsConnected) sftp.Disconnect();
            if (ssh.IsConnected) ssh.Disconnect();

            // restore
            _opt.Password = prevPassword;
        }
    }

    private void ConnectSshAndSftpWithRetry(SshClient ssh, SftpClient sftp, string label, int attempts, int retryDelayMs)
    {
        Exception? lastEx = null;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (attempt > 1)
                    _log.Warn($"SSH connect retry {attempt}/{attempts} for {label}...");

                if (ssh.IsConnected) ssh.Disconnect();
                if (sftp.IsConnected) sftp.Disconnect();

                ssh.Connect();
                sftp.Connect();
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;

                try
                {
                    if (ssh.IsConnected) ssh.Disconnect();
                    if (sftp.IsConnected) sftp.Disconnect();
                }
                catch { /* ignore */ }

                if (attempt < attempts && retryDelayMs > 0)
                    Thread.Sleep(retryDelayMs);
            }
        }

        throw new InvalidOperationException($"Failed to connect via SSH to {label} after {attempts} attempts.", lastEx);
    }

    // ------------------------------------------------------------
    // BULK WI-FI DEPLOY (iterate Cassia AP SSIDs)
    // ------------------------------------------------------------
    private void RunBulkWifiDeploy()
    {
        var prefix = (_opt.BulkWifiSsidPrefix ?? "").Trim();
        if (string.IsNullOrWhiteSpace(prefix)) prefix = "cassia-e4";

        _log.Info($"Bulk Wi-Fi deploy enabled. Scanning for SSIDs with prefix '{prefix}'...");

        // Extended Wi-Fi scan: do multiple passes and union results.
        // This helps discover Cassias that appear/disappear during scanning.
        var ssids = ListWifiSsidsExtended(
                scanPasses: Math.Max(1, _opt.BulkWifiScanPasses),
                scanDelayMs: Math.Max(0, _opt.BulkWifiScanDelayMs))
            .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_opt.BulkWifiMaxCount > 0)
            ssids = ssids.Take(_opt.BulkWifiMaxCount).ToList();

        if (ssids.Count == 0)
        {
            _log.Error("No matching Cassia SSIDs found.");
            return;
        }

        _log.Info($"Found {ssids.Count} matching networks:");
        foreach (var s in ssids) _log.Info($"  - {s}");

        int ok = 0, fail = 0;
        foreach (var ssid in ssids)
        {
            try
            {
                _log.Info("");
                _log.Info($"=== [{ssid}] Connecting Wi-Fi ===");

                // Connect Wi-Fi (and optionally create a temporary profile if missing)
                // NOTE: Cassia AP password is required to be LOWERCASE, while the SSID is not.
                var ssidPasswordLower = ssid.ToLowerInvariant();
                ConnectWifiWithRetry(
                    ssid,
                    password: ssidPasswordLower,
                    autoCreateProfile: _opt.BulkWifiAutoCreateProfile,
                    timeoutSeconds: _opt.BulkWifiConnectTimeoutSeconds,
                    attempts: Math.Max(1, _opt.BulkWifiConnectAttempts),
                    retryDelayMs: Math.Max(0, _opt.BulkWifiConnectRetryDelayMs));

                // Per your existing convention: password defaults to SSID when not explicitly set.
                // In bulk mode, if no explicit password is provided, use the LOWERCASE SSID password.
                var pwd = string.IsNullOrWhiteSpace(_opt.Password) ? ssidPasswordLower : _opt.Password;

                _log.Info($"=== [{ssid}] Deploying to {_opt.Host}:{_opt.Port} ===");
                DeployToTargetWithRetry(
                    host: _opt.Host,
                    port: _opt.Port,
                    user: _opt.User,
                    password: pwd,
                    ssid: ssid,
                    attempts: Math.Max(1, _opt.BulkWifiDeployAttemptsPerTarget),
                    retryDelayMs: Math.Max(0, _opt.BulkWifiDeployRetryDelayMs),
                    connectTimeoutSeconds: _opt.BulkWifiConnectTimeoutSeconds,
                    connectAttempts: Math.Max(1, _opt.BulkWifiConnectAttempts),
                    connectRetryDelayMs: Math.Max(0, _opt.BulkWifiConnectRetryDelayMs),
                    autoCreateProfile: _opt.BulkWifiAutoCreateProfile);
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                _log.Error($"[{ssid}] FAILED: {ex.Message}");
            }
        }

        _log.Info("");
        _log.Info($"Bulk deploy finished. Success={ok}, Failed={fail}");
    }

    private void DeployToTargetWithRetry(
        string host,
        int port,
        string user,
        string password,
        string ssid,
        int attempts,
        int retryDelayMs,
        int connectTimeoutSeconds,
        int connectAttempts,
        int connectRetryDelayMs,
        bool autoCreateProfile)
    {
        Exception? lastEx = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (attempt > 1)
                {
                    _log.Warn($"[{ssid}] Full deploy retry {attempt}/{attempts}...");
                    ConnectWifiWithRetry(
                        ssid,
                        password: password,
                        autoCreateProfile: autoCreateProfile,
                        timeoutSeconds: connectTimeoutSeconds,
                        attempts: connectAttempts,
                        retryDelayMs: connectRetryDelayMs);
                }

                DeployToTarget(host, port, user, password, label: ssid);
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                if (attempt < attempts && retryDelayMs > 0)
                    Thread.Sleep(retryDelayMs);
            }
        }

        throw new InvalidOperationException($"[{ssid}] Deployment failed after {attempts} attempt(s).", lastEx);
    }

    private static List<string> ListWifiSsids()
    {
        // Requires Windows. Output example includes:
        //   SSID 1 : cassia-E4ABCD
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = "wlan show networks mode=bssid",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return new List<string>();

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);

        var ssids = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("SSID ", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("SSID name", StringComparison.OrdinalIgnoreCase)) continue;

            // "SSID 1 : ..."
            var parts = line.Split(':', 2);
            if (parts.Length != 2) continue;
            var ssid = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(ssid)) continue;
            if (ssid.Equals("<hidden>", StringComparison.OrdinalIgnoreCase)) continue;

            ssids.Add(ssid);
        }

        return ssids;
    }

    private List<string> ListWifiSsidsExtended(int scanPasses, int scanDelayMs)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int pass = 1; pass <= scanPasses; pass++)
        {
            try
            {
                _log.Info($"Wi-Fi scan pass {pass}/{scanPasses}...");
                // netsh triggers a scan; repeating it increases discovery reliability.
                var ssids = ListWifiSsids();
                foreach (var s in ssids)
                    all.Add(s);
            }
            catch
            {
                // Ignore transient scan failures
            }

            if (pass < scanPasses && scanDelayMs > 0)
                Thread.Sleep(scanDelayMs);
        }

        return all.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ConnectWifi(string ssid, string password, bool autoCreateProfile, int timeoutSeconds)
    {
        // If already connected, do nothing.
        var current = GetConnectedWifiSsidLower();
        if (!string.IsNullOrWhiteSpace(current) && current.Equals(ssid, StringComparison.OrdinalIgnoreCase))
        {
            _log.Info($"Already connected to {ssid}");
            return;
        }

        if (autoCreateProfile)
        {
            // Create a temporary WPA2-PSK profile (common for Cassia AP SSIDs)
            // Profile name = SSID
            // IMPORTANT: If a Wi-Fi profile already exists (including All User / GPO scope),
            // netsh will refuse to overwrite it. In that case we must re-use the existing profile.
            if (WifiProfileExists(ssid))
            {
                _log.Info($"Wi-Fi profile already exists for {ssid} – reusing it");
            }
            else
            {
                // Verbatim interpolated string: use "" for quotes (NOT backslash-escaped quotes).
            var profileXml = $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
  <name>{SecurityElementEscape(ssid)}</name>
  <SSIDConfig>
    <SSID>
      <name>{SecurityElementEscape(ssid)}</name>
    </SSID>
  </SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>auto</connectionMode>
  <MSM>
    <security>
      <authEncryption>
        <authentication>WPA2PSK</authentication>
        <encryption>AES</encryption>
        <useOneX>false</useOneX>
      </authEncryption>
      <sharedKey>
        <keyType>passPhrase</keyType>
        <protected>false</protected>
        <keyMaterial>{SecurityElementEscape(password)}</keyMaterial>
      </sharedKey>
    </security>
  </MSM>
</WLANProfile>";

                var tmp = Path.Combine(Path.GetTempPath(), $"cassia_{SanitizeFileName(ssid)}.xml");
                File.WriteAllText(tmp, profileXml, Encoding.UTF8);

                try
                {
                    RunLocal("netsh", $"wlan add profile filename=\"{tmp}\" user=current");
                }
                catch (Exception ex)
                {
                    // If the profile exists in another scope (All User / Group Policy), netsh refuses overwrite.
                    // In that case, continue and just connect using the existing profile.
                    var msg = ex.Message ?? string.Empty;
                    if (msg.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("cannot be overwritten", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("group policy", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("different user scope", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Info($"Wi-Fi profile already exists for {ssid} (non-overwritable scope) – reusing it");
                    }
                    else
                    {
                        throw;
                    }
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
                }
            }
        }

        RunLocal("netsh", $"wlan connect name=\"{ssid}\" ssid=\"{ssid}\"");

        var until = DateTime.UtcNow.AddSeconds(Math.Max(5, timeoutSeconds));
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(500);
            var now = GetConnectedWifiSsidLower();
            if (!string.IsNullOrWhiteSpace(now) && now.Equals(ssid, StringComparison.OrdinalIgnoreCase))
            {
                _log.Info($"Connected to {ssid}");
                return;
            }
        }

        throw new TimeoutException($"Timed out waiting to connect to Wi-Fi '{ssid}'.");
    }

    private void ConnectWifiWithRetry(string ssid, string password, bool autoCreateProfile, int timeoutSeconds, int attempts, int retryDelayMs)
    {
        Exception? lastEx = null;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (attempt > 1)
                    _log.Warn($"Wi-Fi connect retry {attempt}/{attempts} for {ssid}...");

                // Best-effort disconnect before retry to force Windows to re-evaluate profiles.
                if (attempt > 1)
                {
                    try { RunLocal("netsh", "wlan disconnect"); } catch { /* ignore */ }
                    Thread.Sleep(Math.Max(0, retryDelayMs));
                }

                ConnectWifi(ssid, password, autoCreateProfile, timeoutSeconds);
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }

        throw new InvalidOperationException($"Failed to connect to Wi-Fi '{ssid}' after {attempts} attempts.", lastEx);
    }

    private static void RunLocal(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException($"Failed to start process: {exe} {args}");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Command failed ({proc.ExitCode}): {exe} {args}\n{stdout}\n{stderr}");
    }

    private static string RunLocalCapture(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException($"Failed to start process: {exe} {args}");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Command failed ({proc.ExitCode}): {exe} {args}\n{stdout}\n{stderr}");

        return stdout + "\n" + stderr;
    }

    private bool WifiProfileExists(string ssid)
    {
        // netsh output varies depending on scope:
        //   All User Profile     : <name>
        //   User Profile         : <name>
        // Group policy / other scope can show up in sections that still include ": <name>".
        // We treat ANY profile name match as existing, regardless of scope.

        var output = RunLocalCapture("netsh", "wlan show profiles");
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Common forms: "All User Profile     : SSID", "User Profile         : SSID"
            // Less common: "Profile : SSID" (seen in some locales/versions)
            if (line.Contains(':'))
            {
                var parts = line.Split(':', 2);
                if (parts.Length == 2)
                {
                    var right = parts[1].Trim();
                    if (right.Equals(ssid, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // Fallback: some outputs list names indented without a label.
            // Avoid false positives by requiring the whole trimmed line to match.
            if (line.Equals(ssid, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string SecurityElementEscape(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.ToString();
    }

    // ------------------------------------------------------------
    // BUILD
    // ------------------------------------------------------------
    private void BuildAllArtifacts()
    {
        _log.Info("Preparing deploy artifacts...");

        BuildAndPublishAccessApp();

        if (_opt.InstallStartupUpdater)
            BuildAndPublishUpdater();

        _log.Info("All deploy artifacts are ready.");
    }

    private void BuildAndPublishAccessApp()
    {
        _log.Info("Building and publishing AccessAPP...");

        var projectFile = ResolveProjectFile();

        if (Directory.Exists(_opt.LocalPublishDir))
            Directory.Delete(_opt.LocalPublishDir, recursive: true);

        var selfContainedArg = _opt.SelfContained ? "--self-contained" : "--no-self-contained";
        var skipClientBuildArg = _opt.SkipClientAppBuild ? "-p:SkipClientAppBuild=true" : "";

        var args =
            $"publish \"{projectFile}\" " +
            $"-c {_opt.PublishConfiguration} " +
            $"-r {_opt.PublishRuntime} " +
            $"{selfContainedArg} " +
            $"{skipClientBuildArg} " +
            $"--output \"{_opt.LocalPublishDir}\"";

        _log.Info($"dotnet {args}");
        _log.Info($"WorkingDirectory: {_opt.ProjectDir}");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
            WorkingDirectory = _opt.ProjectDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet publish");

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) _log.Info(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) _log.Error(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"dotnet publish failed (exit {proc.ExitCode})");

        if (!Directory.Exists(_opt.LocalPublishDir))
            throw new InvalidOperationException($"Publish output folder not created: {_opt.LocalPublishDir}");

        WriteVersionFileIfAvailable(_opt.LocalPublishDir);

        _log.Info("Publish completed.");
    }

    private void BuildAndPublishUpdater()
    {
        _log.Info("Building and publishing startup updater...");

        if (Directory.Exists(_opt.LocalUpdaterPublishDir))
            Directory.Delete(_opt.LocalUpdaterPublishDir, recursive: true);

        var updaterProjectFile = ResolveUpdaterProjectFile();
        var selfContainedArg = _opt.UpdaterSelfContained ? "--self-contained" : "--no-self-contained";
        var singleFileArg = _opt.UpdaterSingleFile ? "-p:PublishSingleFile=true" : "";

        var args =
            $"publish \"{updaterProjectFile}\" " +
            $"-c {_opt.PublishConfiguration} " +
            $"-r {_opt.UpdaterPublishRuntime} " +
            $"{selfContainedArg} " +
            $"{singleFileArg} " +
            $"--output \"{_opt.LocalUpdaterPublishDir}\"";

        _log.Info($"dotnet {args}");
        _log.Info($"WorkingDirectory: {_opt.UpdaterProjectDir}");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
            WorkingDirectory = _opt.UpdaterProjectDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start updater dotnet publish");

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) _log.Info(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) _log.Error(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"updater dotnet publish failed (exit {proc.ExitCode})");

        if (!Directory.Exists(_opt.LocalUpdaterPublishDir))
            throw new InvalidOperationException($"Updater publish output folder not created: {_opt.LocalUpdaterPublishDir}");

        _log.Info("Startup updater publish completed.");
    }

    private string ResolveProjectFile()
    {
        if (!string.IsNullOrWhiteSpace(_opt.ProjectFile))
        {
            if (!File.Exists(_opt.ProjectFile))
                throw new FileNotFoundException($"ProjectFile not found: {_opt.ProjectFile}");
            return _opt.ProjectFile;
        }

        if (string.IsNullOrWhiteSpace(_opt.ProjectDir) || !Directory.Exists(_opt.ProjectDir))
            throw new DirectoryNotFoundException($"ProjectDir not found: {_opt.ProjectDir}");

        var csprojs = Directory.GetFiles(_opt.ProjectDir, "*.csproj", SearchOption.TopDirectoryOnly);
        if (csprojs.Length == 0)
            throw new InvalidOperationException($"No .csproj found in ProjectDir: {_opt.ProjectDir}");
        if (csprojs.Length > 1)
            throw new InvalidOperationException(
                $"Multiple .csproj found in ProjectDir. Set DeployOptions.ProjectFile explicitly.\n" +
                string.Join("\n", csprojs));

        return csprojs[0];
    }

    private string ResolveUpdaterProjectFile()
    {
        if (!string.IsNullOrWhiteSpace(_opt.UpdaterProjectFile))
        {
            if (!File.Exists(_opt.UpdaterProjectFile))
                throw new FileNotFoundException($"UpdaterProjectFile not found: {_opt.UpdaterProjectFile}");
            return _opt.UpdaterProjectFile;
        }

        if (string.IsNullOrWhiteSpace(_opt.UpdaterProjectDir) || !Directory.Exists(_opt.UpdaterProjectDir))
            throw new DirectoryNotFoundException($"UpdaterProjectDir not found: {_opt.UpdaterProjectDir}");

        var csprojs = Directory.GetFiles(_opt.UpdaterProjectDir, "*.csproj", SearchOption.TopDirectoryOnly);
        if (csprojs.Length == 0)
            throw new InvalidOperationException($"No updater .csproj found in UpdaterProjectDir: {_opt.UpdaterProjectDir}");
        if (csprojs.Length > 1)
            throw new InvalidOperationException(
                $"Multiple updater .csproj found in UpdaterProjectDir. Set DeployOptions.UpdaterProjectFile explicitly.\n" +
                string.Join("\n", csprojs));

        return csprojs[0];
    }

    private void WriteVersionFileIfAvailable(string publishDir)
    {
        try
        {
            var versionCs = Path.Combine(_opt.ProjectDir, "Version.cs");
            if (!File.Exists(versionCs))
            {
                _log.Warn($"Version source file not found, skipping version.txt write: {versionCs}");
                return;
            }

            var text = File.ReadAllText(versionCs);
            var match = Regex.Match(text, "AppVersion\\s*=\\s*\"([^\"]+)\"");
            if (!match.Success)
            {
                _log.Warn("Could not parse AppVersion from Version.cs, skipping version.txt write.");
                return;
            }

            var version = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(version))
            {
                _log.Warn("Parsed AppVersion was empty, skipping version.txt write.");
                return;
            }

            var versionFilePath = Path.Combine(publishDir, "version.txt");
            File.WriteAllText(versionFilePath, version + Environment.NewLine);
            _log.Info($"Wrote publish version file: {versionFilePath} ({version})");
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to write publish version.txt (deployment continues): {ex.Message}");
        }
    }

    // ------------------------------------------------------------
    // INIT DETECTION
    // ------------------------------------------------------------
    private InitKind DetectInit(SshClient ssh)
    {
        // systemd?
        var hasSystemctl = RunCommandCapture(ssh, "command -v systemctl >/dev/null 2>&1; echo $?").Trim() == "0";
        if (hasSystemctl)
        {
            // verify it behaves like systemd (daemon-reload probe)
            var ok = RunCommandCapture(ssh, "systemctl daemon-reload >/dev/null 2>&1; echo $?").Trim() == "0";
            if (ok) return InitKind.Systemd;
        }

        // OpenRC?
        var hasRcService = RunCommandCapture(ssh, "command -v rc-service >/dev/null 2>&1; echo $?").Trim() == "0";
        if (hasRcService) return InitKind.OpenRc;

        // SysV?
        var hasInitD = RunCommandCapture(ssh, "test -d /etc/init.d; echo $?").Trim() == "0";
        if (hasInitD) return InitKind.SysV;

        return InitKind.Unknown;
    }

    private void InstallStartupUpdater(SshClient ssh, SftpClient sftp)
    {
        var localUpdaterExe = Path.Combine(_opt.LocalUpdaterPublishDir, "AccessAppUpdater");
        if (!File.Exists(localUpdaterExe))
            throw new FileNotFoundException($"Updater binary not found: {localUpdaterExe}");

        _log.Info("Installing startup updater on target...");

        var remoteTmpPath = CombineRemote(_opt.RemoteDir, ".accessapp_updater_tmp");
        UploadStreamSftpWithRetry(sftp, () => File.OpenRead(localUpdaterExe), remoteTmpPath);

        RunSudo(ssh, $"mkdir -p {ShEscape(GetRemoteParentDir(_opt.UpdaterRemoteExePath))}");
        RunSudo(ssh, $"install -m 755 {ShEscape(remoteTmpPath)} {ShEscape(_opt.UpdaterRemoteExePath)}");
        RunCommand(ssh, $"rm -f {ShEscape(remoteTmpPath)} || true");

        var updaterConfig = BuildUpdaterConfigJson();
        var configB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(updaterConfig));

        RunSudo(ssh, $"mkdir -p {ShEscape(GetRemoteParentDir(_opt.UpdaterRemoteConfigPath))}");
        RunSudo(ssh, $"echo '{configB64}' | base64 -d > {ShEscape(_opt.UpdaterRemoteConfigPath)}");
        RunSudo(ssh, $"chmod 644 {ShEscape(_opt.UpdaterRemoteConfigPath)}");
        RunSudo(ssh, $"chown {_opt.User}:{_opt.User} {ShEscape(_opt.UpdaterRemoteConfigPath)} || true");
        EnsureUpdaterChannelFile(ssh);
        EnsureUpdaterWorkDirWritable(ssh);

        _log.Info($"Startup updater installed: {_opt.UpdaterRemoteExePath}");
        _log.Info($"Startup updater config updated: {_opt.UpdaterRemoteConfigPath}");
    }

    private void EnsureUpdaterChannelFile(SshClient ssh)
    {
        var channelFilePath = string.IsNullOrWhiteSpace(_opt.UpdaterChannelFilePath)
            ? "/etc/accessapp-updater.channel"
            : _opt.UpdaterChannelFilePath.Trim();
        var channel = NormalizeUpdaterChannel(_opt.UpdaterChannel);
        _log.Info($"Ensuring updater channel file: {channelFilePath} ({channel})");

        RunSudo(ssh, $"mkdir -p {ShEscape(GetRemoteParentDir(channelFilePath))}");
        RunSudo(ssh, $"printf '%s\\n' {ShEscape(channel)} > {ShEscape(channelFilePath)}");
        RunSudo(ssh, $"chmod 644 {ShEscape(channelFilePath)}");
        RunSudo(ssh, $"chown {_opt.User}:{_opt.User} {ShEscape(channelFilePath)} || true");
    }

    private void EnsureUpdaterWorkDirWritable(SshClient ssh)
    {
        if (string.IsNullOrWhiteSpace(_opt.UpdaterWorkDir))
            return;

        var dir = _opt.UpdaterWorkDir.Trim();
        _log.Info($"Ensuring updater work dir is writable: {dir}");

        // Make sure the directory exists and cassia user can write there.
        RunSudo(ssh, $"mkdir -p {ShEscape(dir)}");
        RunSudo(ssh, $"chown -R {_opt.User}:{_opt.User} {ShEscape(dir)} || true");
        RunSudo(ssh, $"chmod -R u+rwX {ShEscape(dir)} || true");
    }

    private void EnsureSelfUpdateSudoers(SshClient ssh)
    {
        var service = _opt.ServiceName.Replace("'", "");
        var user = _opt.User.Replace("'", "");
        var sudoersPath = $"/etc/sudoers.d/{service}-self-update";

        // Keep entries short and LF-only; some target visudo builds are sensitive to very long lines/CRLF.
        var sudoersLines = new[]
        {
            $"Defaults:{user} !requiretty",
            $"{user} ALL=(root) NOPASSWD: /bin/systemctl start {service}",
            $"{user} ALL=(root) NOPASSWD: /bin/systemctl stop {service}",
            $"{user} ALL=(root) NOPASSWD: /bin/systemctl restart {service}",
            $"{user} ALL=(root) NOPASSWD: /usr/bin/systemctl start {service}",
            $"{user} ALL=(root) NOPASSWD: /usr/bin/systemctl stop {service}",
            $"{user} ALL=(root) NOPASSWD: /usr/bin/systemctl restart {service}",
            $"{user} ALL=(root) NOPASSWD: /usr/sbin/service {service} start",
            $"{user} ALL=(root) NOPASSWD: /usr/sbin/service {service} stop",
            $"{user} ALL=(root) NOPASSWD: /usr/sbin/service {service} restart",
            $"{user} ALL=(root) NOPASSWD: /sbin/service {service} start",
            $"{user} ALL=(root) NOPASSWD: /sbin/service {service} stop",
            $"{user} ALL=(root) NOPASSWD: /sbin/service {service} restart",
            $"{user} ALL=(root) NOPASSWD: /etc/init.d/{service} start",
            $"{user} ALL=(root) NOPASSWD: /etc/init.d/{service} stop",
            $"{user} ALL=(root) NOPASSWD: /etc/init.d/{service} restart"
        };

        var sudoers = string.Join("\n", sudoersLines) + "\n";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sudoers));

        RunSudo(ssh, $"echo '{b64}' | base64 -d > {ShEscape(sudoersPath)}");
        RunSudo(ssh, $"chmod 440 {ShEscape(sudoersPath)}");
        RunSudo(ssh, $"visudo -cf {ShEscape(sudoersPath)} >/dev/null");
        _log.Info($"Installed sudoers self-update rule: {sudoersPath}");
    }

    private string BuildUpdaterConfigJson()
    {
        var chmodPaths = new List<string> { _opt.RemoteExeName };
        var channel = NormalizeUpdaterChannel(_opt.UpdaterChannel);
        var channelFilePath = string.IsNullOrWhiteSpace(_opt.UpdaterChannelFilePath)
            ? "/etc/accessapp-updater.channel"
            : _opt.UpdaterChannelFilePath.Trim();

        if (!string.IsNullOrWhiteSpace(_opt.ExtraChmod755Path))
        {
            var rel = TryMakeRelativeRemotePath(_opt.RemoteDir, _opt.ExtraChmod755Path);
            if (!string.IsNullOrWhiteSpace(rel))
                chmodPaths.Add(rel);
        }

        var payload = new
        {
            ManifestUrl = _opt.UpdaterManifestUrl,
            Channel = channel,
            ChannelFilePath = channelFilePath,
            AllowDowngrade = false,
            InstallDir = _opt.RemoteDir,
            WorkDir = _opt.UpdaterWorkDir,
            VersionFileName = _opt.UpdaterVersionFileName,
            ExecutableName = _opt.RemoteExeName,
            HttpTimeoutSeconds = Math.Max(30, _opt.UpdaterHttpTimeoutSeconds),
            PreserveFiles = _opt.UpdaterPreserveFiles,
            ExecutableRelativePathsToChmodX = chmodPaths.Distinct(StringComparer.Ordinal).ToArray()
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string NormalizeUpdaterChannel(string? value)
    {
        var channel = (value ?? string.Empty).Trim().ToLowerInvariant();
        return channel switch
        {
            "stable" => "stable",
            "test" => "test",
            "develop" => "develop",
            "dev" => "develop",
            "prod-stable" => "stable",
            "prod-test" => "test",
            "prod-develop" => "develop",
            _ => "stable"
        };
    }

    // ------------------------------------------------------------
    // SERVICE INSTALL / STOP / START
    // ------------------------------------------------------------
    private void EnsureServiceInstalled(SshClient ssh, InitKind init)
    {
        switch (init)
        {
            case InitKind.Systemd:
                EnsureSystemdUnitInstalled(ssh);
                break;

            case InitKind.OpenRc:
            case InitKind.SysV:
                EnsureInitDScriptInstalled(ssh);
                break;

            default:
                _log.Warn("Unknown init system. Skipping service install.");
                break;
        }
    }

    private void StopService(SshClient ssh, InitKind init)
    {
        switch (init)
        {
            case InitKind.Systemd:
                RunSudo(ssh, $"systemctl stop '{ServiceUnitName}' || true");
                break;

            case InitKind.OpenRc:
                RunSudo(ssh, $"rc-service '{_opt.ServiceName}' stop || true");
                break;

            case InitKind.SysV:
                RunSudo(ssh, $"/etc/init.d/{_opt.ServiceName} stop || true");
                break;

            default:
                _log.Warn("No service stop (unknown init).");
                break;
        }
    }

    private void StartService(SshClient ssh, InitKind init)
    {
        switch (init)
        {
            case InitKind.Systemd:
                // Minimal systemd handling (masked logic removed because your Cassia isn't systemd)
                RunSudo(ssh, $"systemctl enable '{ServiceUnitName}' || true");
                RunSudo(ssh, $"systemctl start '{ServiceUnitName}'");
                break;

            case InitKind.OpenRc:
                RunSudo(ssh, $"rc-service '{_opt.ServiceName}' start");
                break;

            case InitKind.SysV:
                RunSudo(ssh, $"/etc/init.d/{_opt.ServiceName} start");
                break;

            default:
                _log.Warn("No service start (unknown init).");
                break;
        }
    }

    private void ShowServiceStatus(SshClient ssh, InitKind init)
    {
        try
        {
            string txt = init switch
            {
                InitKind.Systemd => RunCommandCapture(ssh, $"systemctl --no-pager --full status '{ServiceUnitName}' | tail -n 50 || true"),
                InitKind.OpenRc => RunCommandCapture(ssh, $"rc-service '{_opt.ServiceName}' status || true"),
                InitKind.SysV => RunCommandCapture(ssh, $"/etc/init.d/{_opt.ServiceName} status || true"),
                _ => "Unknown init system; no status available."
            };

            _log.Info("Service status:");
            Console.WriteLine(txt);
        }
        catch { /* ignore */ }
    }

    private void EnsureSystemdUnitInstalled(SshClient ssh)
    {
        // Strong check via FragmentPath
        var frag = RunCommandCapture(ssh,
            $"systemctl show -p FragmentPath '{ServiceUnitName}' 2>/dev/null | sed 's/^FragmentPath=//'").Trim();

        if (!string.IsNullOrWhiteSpace(frag) && !frag.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            _log.Info($"Systemd unit exists -> updating: {frag}");
        else
            _log.Info($"Systemd unit missing -> installing: {ServiceUnitName}");

        var unitPath = $"/etc/systemd/system/{ServiceUnitName}";
        var remoteExe = CombineRemote(_opt.RemoteDir, _opt.RemoteExeName);
        var preStartLine = _opt.InstallStartupUpdater
            ? $"ExecStartPre=-{StartupUpdaterExecStartPre}"
            : string.Empty;

        var unit = $"""
[Unit]
Description=AccessAPP (deployed to {_opt.RemoteDir})
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User={_opt.User}
WorkingDirectory={_opt.RemoteDir}
{preStartLine}
ExecStart={remoteExe}
Restart=always
RestartSec=2

[Install]
WantedBy=multi-user.target
""";

        // Write as root (one-liner) to avoid CRLF/multiline issues
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(unit));
        RunSudo(ssh, $"echo '{b64}' | base64 -d > '{unitPath}'");
        RunSudo(ssh, $"chmod 644 '{unitPath}'");
        RunSudo(ssh, "systemctl daemon-reload");
        RunSudo(ssh, $"systemctl enable '{ServiceUnitName}' || true");
    }

    private void EnsureInitDScriptInstalled(SshClient ssh)
    {
        var scriptPath = $"/etc/init.d/{_opt.ServiceName}";
        var remoteExe = CombineRemote(_opt.RemoteDir, _opt.RemoteExeName);
        var updaterExe = _opt.UpdaterRemoteExePath.Replace("'", "");
        var updaterCfg = _opt.UpdaterRemoteConfigPath.Replace("'", "");
        var updaterStartBlock = _opt.InstallStartupUpdater
            ? @"  if [ -x ""$UPDATER_EXE"" ]; then
    echo ""Running startup updater...""
    run_as_app_user ""'$UPDATER_EXE' --config '$UPDATER_CFG'"" || echo ""[WARN] startup updater failed; continuing with current app""
  fi
"
            : string.Empty;

        var exists = RunCommandCapture(ssh, $"test -f '{scriptPath}'; echo $?").Trim() == "0";
        if (exists)
            _log.Info($"Init script exists -> updating: {scriptPath}");
        else
            _log.Info($"Installing init script: {scriptPath}");

        // IMPORTANT: Use real newlines in the script content (no \\n sequences).
        // Also remove any accidental CRLF by running sed after write.
        var script = $@"#!/bin/sh
### BEGIN INIT INFO
# Provides:          {_opt.ServiceName}
# Required-Start:    $network
# Required-Stop:     $network
# Default-Start:     2 3 4 5
# Default-Stop:      0 1 6
# Short-Description: AccessAPP
### END INIT INFO

APP_DIR='{_opt.RemoteDir.Replace("'", "")}'
APP_EXE='{remoteExe.Replace("'", "")}'
UPDATER_EXE='{updaterExe}'
UPDATER_CFG='{updaterCfg}'
APP_USER='{_opt.User.Replace("'", "")}'
PIDFILE='{_opt.RemoteDir.Replace("'", "")}/.{_opt.ServiceName}.pid'
LOGFILE='{_opt.RemoteDir.Replace("'", "")}/accessapp.log'
MAX_LOG_SIZE=$((10 * 1024 * 1024))  # 10 MB

run_as_app_user() {{
  CMD=""$1""
  if [ ""$(id -un 2>/dev/null || true)"" = ""$APP_USER"" ]; then
    sh -lc ""$CMD""
    return $?
  fi
  if command -v su >/dev/null 2>&1; then
    su -s /bin/sh -c ""$CMD"" ""$APP_USER""
    return $?
  fi
  if command -v runuser >/dev/null 2>&1; then
    runuser -u ""$APP_USER"" -- sh -lc ""$CMD""
    return $?
  fi
  sh -lc ""$CMD""
}}

truncate_log_if_needed() {{
  if [ -f ""$LOGFILE"" ]; then
    SIZE=$(wc -c < ""$LOGFILE"" 2>/dev/null || echo 0)
    if [ ""$SIZE"" -ge ""$MAX_LOG_SIZE"" ]; then
      : > ""$LOGFILE""
    fi
  fi
}}

start() {{
  echo ""Starting accessapp...""
  mkdir -p ""$APP_DIR"" 2>/dev/null || true
  cd ""$APP_DIR"" || exit 1
  touch ""$LOGFILE"" 2>/dev/null || true
  chown ""$APP_USER"":""$APP_USER"" ""$APP_DIR"" ""$LOGFILE"" 2>/dev/null || true

{updaterStartBlock}

  truncate_log_if_needed

  run_as_app_user ""nohup '$APP_EXE' >> '$LOGFILE' 2>&1 & echo \\$! > '$PIDFILE'""
  PID=$(cat ""$PIDFILE"" 2>/dev/null || true)

  sleep 1
  if [ -n ""$PID"" ] && kill -0 $PID 2>/dev/null; then
    echo ""Started (pid=$PID)""
    exit 0
  fi

  echo ""FAILED: process exited immediately (pid=$PID)""
  echo ""--- last 50 log lines ($LOGFILE) ---""
  tail -n 50 ""$LOGFILE"" 2>/dev/null || true
  exit 1
}}

stop() {{
  echo ""Stopping {_opt.ServiceName}...""
  if [ -f ""$PIDFILE"" ]; then
    PID=$(cat ""$PIDFILE"")
    kill ""$PID"" 2>/dev/null || true
    sleep 1
    kill -9 ""$PID"" 2>/dev/null || true
    rm -f ""$PIDFILE""
  else
    pkill -f ""$APP_EXE"" 2>/dev/null || true
  fi
}}

status() {{
  if [ -f ""$PIDFILE"" ] && kill -0 $(cat ""$PIDFILE"") 2>/dev/null; then
    echo ""{_opt.ServiceName} is running (pid=$(cat ""$PIDFILE""))""
    exit 0
  fi
  echo ""{_opt.ServiceName} is not running""
  exit 3
}}

case ""$1"" in
  start) start ;;
  stop) stop ;;
  restart) stop; start ;;
  status) status ;;
  log) tail -n 200 ""$LOGFILE"" 2>/dev/null || true ;;
  *) echo ""Usage: $0 {{start|stop|restart|status|log}}""; exit 2 ;;
esac
exit 0
";

        // Normalize line endings to LF before base64
        script = script.Replace("\r\n", "\n").Replace("\r", "\n");

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));

        RunSudo(ssh, $"echo '{b64}' | base64 -d > '{scriptPath}'");
        RunSudo(ssh, $"chmod 755 '{scriptPath}'");
        RunSudo(ssh, $"sed -i 's/\\r$//' '{scriptPath}' || true"); // ensure LF-only

        // Quick sanity: show first line (debug)
        var head = RunCommandCapture(ssh, $"head -n 1 '{scriptPath}' 2>/dev/null || true").Trim();
        _log.Info($"Init script shebang: {head}");

        // Enable on boot (best-effort)
        var hasRcUpdate = RunCommandCapture(ssh, "command -v rc-update >/dev/null 2>&1; echo $?").Trim() == "0";
        var hasUpdateRc = RunCommandCapture(ssh, "command -v update-rc.d >/dev/null 2>&1; echo $?").Trim() == "0";

        if (hasRcUpdate)
            RunSudo(ssh, $"rc-update add '{_opt.ServiceName}' default || true");
        else if (hasUpdateRc)
            RunSudo(ssh, $"update-rc.d '{_opt.ServiceName}' defaults || true");

        _log.Info("Init script installed.");
    }

    // ------------------------------------------------------------
    // REMOTE DIR + PERMISSIONS
    // ------------------------------------------------------------
    private void EnsureRemoteDirWritable(SshClient ssh)
    {
        _log.Info($"Ensuring remote dir exists & is writable: {_opt.RemoteDir}");

        RunCommand(ssh, $"mkdir -p {ShEscape(_opt.RemoteDir)}");
        RunCommand(ssh, $"mkdir -p {ShEscape(_opt.RemoteDir + ".prev")} || true");

        // chown via sudo (one-liner)
        RunSudo(ssh, $"chown -R {_opt.User}:{_opt.User} {ShEscape(_opt.RemoteDir)} || true");
        RunSudo(ssh, $"if [ -d {ShEscape(_opt.RemoteDir + ".prev")} ]; then chown -R {_opt.User}:{_opt.User} {ShEscape(_opt.RemoteDir + ".prev")}; fi || true");

        // chmod without sudo (should be fine after chown)
        RunCommand(ssh, $"chmod -R u+rwX {ShEscape(_opt.RemoteDir)} || true");
        RunCommand(ssh, $"if [ -d {ShEscape(_opt.RemoteDir + ".prev")} ]; then chmod -R u+rwX {ShEscape(_opt.RemoteDir + ".prev")}; fi || true");
    }

    // ------------------------------------------------------------
    // SSH KEY PROVISIONING (authorized_keys + sshd_config)
    // ------------------------------------------------------------
    private void EnsureSshKeyLoginAndHardenSshd(SshClient ssh)
    {
        if (string.IsNullOrWhiteSpace(_opt.LocalSshPublicKeyPath))
        {
            _log.Warn("EnsureSshKeyLogin enabled, but LocalSshPublicKeyPath is empty. Skipping.");
            return;
        }

        if (!File.Exists(_opt.LocalSshPublicKeyPath))
        {
            _log.Warn($"SSH public key not found: {_opt.LocalSshPublicKeyPath}. Skipping SSH key provisioning.");
            return;
        }

        var pubKey = File.ReadAllText(_opt.LocalSshPublicKeyPath).Trim();
        if (string.IsNullOrWhiteSpace(pubKey) || !pubKey.StartsWith("ssh-", StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn($"SSH public key file does not look valid: {_opt.LocalSshPublicKeyPath}. Skipping.");
            return;
        }

        _log.Info("Ensuring SSH public key is installed on target (~/.ssh/authorized_keys)...");

        // 1) Ensure ~/.ssh exists and has sane permissions
        RunCommand(ssh, "mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys");

        // 2) Append key if missing (safe quoting via base64)
        var keyB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(pubKey));
        var addKeyCmd =
            $"KEY=$(echo '{keyB64}' | base64 -d); " +
            "grep -qxF \"$KEY\" ~/.ssh/authorized_keys || echo \"$KEY\" >> ~/.ssh/authorized_keys";
        RunCommand(ssh, $"sh -lc '{addKeyCmd.Replace("'", "'\"'\"'")}'");

        // 3) Ensure sshd allows pubkey auth and (optionally) disables password auth
        _log.Info("Ensuring sshd_config enables public-key authentication...");

        // Helper to set or append config lines (works whether commented or not)
        static string SetOrAppend(string key, string value)
        {
            // Uses sed to replace: ^#?Key.*  -> Key value
            // If not found, appends.
            var escapedValue = value.Replace("'", "'\"'\"'");
            return
                $"if grep -qE '^\\s*#?\\s*{key}\\b' /etc/ssh/sshd_config; then " +
                $"sed -i -E 's/^\\s*#?\\s*{key}\\b.*/{key} {escapedValue}/' /etc/ssh/sshd_config; " +
                "else " +
                $"echo '{key} {escapedValue}' >> /etc/ssh/sshd_config; " +
                "fi";
        }

        var cfgCmds = new List<string>
        {
            SetOrAppend("PubkeyAuthentication", "yes"),
            SetOrAppend("AuthorizedKeysFile", ".ssh/authorized_keys"),
            SetOrAppend("ChallengeResponseAuthentication", "no"),
            SetOrAppend("KbdInteractiveAuthentication", "no")
        };

        if (_opt.DisablePasswordAuthentication)
            cfgCmds.Add(SetOrAppend("PasswordAuthentication", "no"));
        else
            cfgCmds.Add(SetOrAppend("PasswordAuthentication", "yes"));

        // Run as sudo
        RunSudo(ssh, string.Join("; ", cfgCmds));

        if (_opt.RestartSshServiceAfterConfig)
        {
            _log.Info("Restarting SSH service to apply config...");
            RunSudo(ssh, "systemctl restart ssh || systemctl restart sshd || service ssh restart || service sshd restart || true");
        }

        _log.Info("SSH key provisioning completed.");
    }

    // ------------------------------------------------------------
    // SFTP SYNC (manifest-based skip)
    // ------------------------------------------------------------
    private void UploadDirectorySftpWithManifest(
        SshClient ssh,
        SftpClient sftp,
        string localDir,
        string remoteDir,
        string remoteManifestPath)
    {
        localDir = Path.GetFullPath(localDir);
        if (!Directory.Exists(localDir))
            throw new DirectoryNotFoundException(localDir);

        remoteDir = NormalizeRemote(remoteDir);
        remoteManifestPath = NormalizeRemote(remoteManifestPath);

        EnsureRemoteDirectorySftp(sftp, remoteDir);

        _log.Info($"Loading remote manifest: {remoteManifestPath}");
        var remoteMap = LoadRemoteManifestSftp(sftp, remoteManifestPath);
        _log.Info($"Remote manifest entries: {remoteMap.Count}");

        // Build local entries
        var localEntries = new List<(string rel, long size, long mtimeUtcSeconds, string fullPath)>();
        foreach (var file in Directory.EnumerateFiles(localDir, "*", SearchOption.AllDirectories))
        {
            var rel = NormalizeRel(Path.GetRelativePath(localDir, file));

            // SAFETY: Never deploy (overwrite/replace) mqtt.json.
            // This file is device-specific configuration and must be preserved on the target.
            // By excluding it from the sync set, we guarantee it is never uploaded nor recorded in the manifest.
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, "mqtt.json", StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn($"Skipping protected file: {rel}");
                continue;
            }

            var fi = new FileInfo(file);
            var mtimeSec = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
            localEntries.Add((rel, fi.Length, mtimeSec, file));
        }

        int uploaded = 0, skipped = 0, checkedCount = 0;
        int debugMismatchesShown = 0;

        _log.Info("Uploading files via SFTP (skip-unchanged enabled)...");

        foreach (var e in localEntries)
        {
            checkedCount++;

            if (_opt.SkipUnchanged && remoteMap.TryGetValue(e.rel, out var r))
            {
                if (r.size == e.size && r.mtimeUtcSeconds == e.mtimeUtcSeconds)
                {
                    skipped++;
                    continue;
                }

                if (debugMismatchesShown < 10)
                {
                    _log.Warn($"Reupload reason: {e.rel}");
                    _log.Warn($"  local  size={e.size} mtime={e.mtimeUtcSeconds}");
                    _log.Warn($"  remote size={r.size} mtime={r.mtimeUtcSeconds}");
                    debugMismatchesShown++;
                }
            }

            var remotePath = CombineRemote(remoteDir, e.rel);
            var remoteParent = GetRemoteParentDir(remotePath);
            EnsureRemoteDirectorySftp(sftp, remoteParent);

            UploadStreamSftpWithRetry(sftp, () => File.OpenRead(e.fullPath), remotePath);

            // Set remote mtime to local mtime (critical for stable comparisons)
            var attrs = sftp.GetAttributes(remotePath);
            attrs.LastWriteTimeUtc = DateTimeOffset.FromUnixTimeSeconds(e.mtimeUtcSeconds).UtcDateTime;
            sftp.SetAttributes(remotePath, attrs);

            uploaded++;
            _log.Info($"Uploaded: {e.rel}");
        }

        _log.Info($"Sync summary: Checked={checkedCount}, Uploaded={uploaded}, Skipped={skipped}");

        // Write updated manifest via SFTP so it’s guaranteed to persist
        _log.Info("Writing remote manifest via SFTP...");
        WriteRemoteManifestSftp(sftp, remoteManifestPath, localEntries);
        _log.Info("Manifest updated.");
    }

    private Dictionary<string, (long size, long mtimeUtcSeconds)> LoadRemoteManifestSftp(SftpClient sftp, string remoteManifestPath)
    {
        var dict = new Dictionary<string, (long size, long mtimeUtcSeconds)>(StringComparer.Ordinal);

        if (!sftp.Exists(remoteManifestPath))
            return dict;

        using var ms = new MemoryStream();
        sftp.DownloadFile(remoteManifestPath, ms);
        ms.Position = 0;

        using var sr = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        while (!sr.EndOfStream)
        {
            var line = sr.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // <size>\t<mtimeSec>\t<relpath>
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;

            if (!long.TryParse(parts[0], out var size)) continue;
            if (!long.TryParse(parts[1], out var mtime)) continue;

            var rel = NormalizeRel(parts[2]);
            if (string.IsNullOrWhiteSpace(rel)) continue;

            dict[rel] = (size, mtime);
        }

        return dict;
    }

    private void WriteRemoteManifestSftp(
        SftpClient sftp,
        string remoteManifestPath,
        List<(string rel, long size, long mtimeUtcSeconds, string fullPath)> entries)
    {
        EnsureRemoteDirectorySftp(sftp, GetRemoteParentDir(remoteManifestPath));

        using var ms = new MemoryStream();
        using (var sw = new StreamWriter(ms, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true))
        {
            foreach (var e in entries.OrderBy(x => x.rel, StringComparer.Ordinal))
                sw.WriteLine($"{e.size}\t{e.mtimeUtcSeconds}\t{e.rel}");
        }

        var manifestBytes = ms.ToArray();
        UploadStreamSftpWithRetry(sftp, () => new MemoryStream(manifestBytes, writable: false), remoteManifestPath);

        // best effort perms
        try
        {
            var a = sftp.GetAttributes(remoteManifestPath);
            a.SetPermissions(0x180); // 0600
            sftp.SetAttributes(remoteManifestPath, a);
        }
        catch { /* ignore */ }
    }

    private static string NormalizeRel(string rel)
    {
        rel = rel.Replace('\\', '/');
        while (rel.StartsWith("./", StringComparison.Ordinal)) rel = rel.Substring(2);
        rel = rel.TrimStart('/');
        return rel;
    }

    private void EnsureRemoteDirectorySftp(SftpClient sftp, string path)
    {
        path = NormalizeRemote(path);

        if (path == "/")
            return;

        if (sftp.Exists(path))
            return;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "/";

        foreach (var p in parts)
        {
            current = current.EndsWith("/") ? current + p : current + "/" + p;
            if (!sftp.Exists(current))
                sftp.CreateDirectory(current);
        }
    }

    private void UploadStreamSftpWithRetry(
        SftpClient sftp,
        Func<Stream> openInput,
        string remotePath,
        int attempts = 3,
        int retryDelayMs = 300)
    {
        remotePath = NormalizeRemote(remotePath);
        EnsureRemoteDirectorySftp(sftp, GetRemoteParentDir(remotePath));

        Exception? lastEx = null;
        var pid = Process.GetCurrentProcess().Id;

        for (int attempt = 1; attempt <= Math.Max(1, attempts); attempt++)
        {
            var tempPath = $"{remotePath}.upload-{pid}-{Guid.NewGuid():N}.tmp";

            try
            {
                using (var input = openInput())
                {
                    if (input.CanSeek)
                        input.Position = 0;

                    sftp.UploadFile(input, tempPath, true);
                }

                if (sftp.Exists(remotePath))
                {
                    var existing = sftp.GetAttributes(remotePath);
                    if (existing.IsDirectory)
                        throw new IOException($"Remote upload target is a directory, expected file: {remotePath}");

                    sftp.DeleteFile(remotePath);
                }

                sftp.RenameFile(tempPath, remotePath);
                return;
            }
            catch (Exception ex) when (
                ex is SshException ||
                ex is SftpPermissionDeniedException ||
                ex is SftpPathNotFoundException ||
                ex is IOException)
            {
                lastEx = ex;

                try
                {
                    if (sftp.Exists(tempPath))
                        sftp.DeleteFile(tempPath);
                }
                catch
                {
                    // ignore cleanup errors
                }

                if (attempt < attempts)
                {
                    _log.Warn($"SFTP upload retry {attempt}/{attempts} failed for {remotePath}: {ex.Message}");
                    if (retryDelayMs > 0)
                        Thread.Sleep(retryDelayMs);
                    continue;
                }
            }
        }

        throw new InvalidOperationException(
            $"SFTP upload failed for {remotePath} after {attempts} attempts.",
            lastEx);
    }

    private static string GetRemoteParentDir(string remotePath)
    {
        remotePath = remotePath.Replace('\\', '/');
        var idx = remotePath.LastIndexOf('/');
        if (idx <= 0) return "/";
        return remotePath.Substring(0, idx);
    }

    // ------------------------------------------------------------
    // SSH HELPERS
    // ------------------------------------------------------------
    private void RunCommand(SshClient ssh, string cmd)
    {
        _log.Info($"SSH: {cmd}");
        using var c = ssh.CreateCommand(cmd);
        c.CommandTimeout = TimeSpan.FromSeconds(180);
        var stdout = c.Execute() ?? "";

        if (c.ExitStatus != 0)
            throw new Exception($"Command failed (exit {c.ExitStatus}): {cmd}\nSTDOUT:\n{stdout}\nSTDERR:\n{c.Error}");
    }

    private string RunCommandCapture(SshClient ssh, string cmd)
    {
        using var c = ssh.CreateCommand(cmd);
        c.CommandTimeout = TimeSpan.FromSeconds(60);
        var stdout = c.Execute() ?? "";
        if (!string.IsNullOrWhiteSpace(c.Error))
            stdout += "\n" + c.Error;
        return stdout;
    }

    private void RunSudo(SshClient ssh, string command)
    {
        // One-liner only. Execute through sh -lc so pipes/redirects work.
        var escaped = command.Replace("'", "'\"'\"'");

        // IMPORTANT:
        // - Bulk deploy sets the per-SSID password via DeployToTarget() -> _opt.Password
        // - Quote the password so special chars can't break the shell
        // - Use -p '' so sudo doesn't print interactive prompts into STDERR
        var pwd = _opt.Password ?? "";
        RunCommand(ssh, $"printf '%s\\n' {ShEscape(pwd)} | sudo -S -p '' sh -lc '{escaped}'");
    }

    // ------------------------------------------------------------
    // PATH HELPERS (remote linux paths)
    // ------------------------------------------------------------
    private static string NormalizeRemote(string path)
    {
        path = path.Replace('\\', '/');
        if (!path.StartsWith("/")) path = "/" + path;
        if (path.Length > 1 && path.EndsWith("/")) path = path.TrimEnd('/');
        return path;
    }

    private static string CombineRemote(string baseDir, string relative)
    {
        baseDir = NormalizeRemote(baseDir);
        relative = relative.Replace('\\', '/').TrimStart('/');
        return baseDir + "/" + relative;
    }

    private static string? TryMakeRelativeRemotePath(string baseDir, string fullPath)
    {
        baseDir = NormalizeRemote(baseDir).TrimEnd('/') + "/";
        fullPath = NormalizeRemote(fullPath);

        if (!fullPath.StartsWith(baseDir, StringComparison.Ordinal))
            return null;

        return fullPath.Substring(baseDir.Length);
    }

    private static string ShEscape(string s)
        => "'" + s.Replace("'", "'\"'\"'") + "'";
}
