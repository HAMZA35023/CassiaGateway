// SshCassiaDeployer.cs
// Library-based deployer: dotnet publish -> SFTP sync (skip unchanged via manifest) -> chmod -> init-managed restart
// Supports: systemd, OpenRC, SysV init. Uses password-based sudo (no SSH keys needed).

using Renci.SshNet;
using Renci.SshNet.Sftp;
using System.Diagnostics;
using System.Text;

namespace CassiaDeployerLib;

public sealed class SshCassiaDeployer
{
    private readonly DeployOptions _opt;
    private readonly ConsoleProgress _log;

    public SshCassiaDeployer(DeployOptions opt, ConsoleProgress log)
    {
        _opt = opt;
        _log = log;
    }

    private enum InitKind { Systemd, OpenRc, SysV, Unknown }

    private string ServiceUnitName => $"{_opt.ServiceName}.service";

    public void Run()
    {
        BuildAndPublish();

        var conn = new PasswordConnectionInfo(_opt.Host, _opt.Port, _opt.User, _opt.Password)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        using var ssh = new SshClient(conn);
        using var sftp = new SftpClient(conn);

        _log.Info("Connecting to device...");
        ssh.Connect();
        sftp.Connect();
        _log.Info("Connected.");

        try
        {
            EnsureRemoteDirWritable(ssh);

            var init = DetectInit(ssh);
            _log.Info($"Init system detected: {init}");

            if (_opt.ManageService)
            {
                if (_opt.InstallServiceIfMissing)
                    EnsureServiceInstalled(ssh, init);

                StopService(ssh, init);
            }

            UploadDirectorySftpWithManifest(ssh, sftp, _opt.LocalPublishDir, _opt.RemoteDir, _opt.RemoteManifestPath);

            // Ensure executable bit on main app
            var remoteExe = CombineRemote(_opt.RemoteDir, _opt.RemoteExeName);
            RunCommand(ssh, $"chmod +x {ShEscape(remoteExe)} || true");

            // Extra chmod requested
            if (!string.IsNullOrWhiteSpace(_opt.ExtraChmod755Path))
            {
                RunCommand(ssh, $"if [ -f {ShEscape(_opt.ExtraChmod755Path)} ]; then chmod 755 {ShEscape(_opt.ExtraChmod755Path)}; fi");
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
        }
    }

    // ------------------------------------------------------------
    // BUILD
    // ------------------------------------------------------------
    private void BuildAndPublish()
    {
        _log.Info("Building and publishing AccessAPP...");

        var projectFile = ResolveProjectFile();

        if (Directory.Exists(_opt.LocalPublishDir))
            Directory.Delete(_opt.LocalPublishDir, recursive: true);

        var selfContainedArg = _opt.SelfContained ? "--self-contained" : "--no-self-contained";

        var args =
            $"publish \"{projectFile}\" " +
            $"-c {_opt.PublishConfiguration} " +
            $"-r {_opt.PublishRuntime} " +
            $"{selfContainedArg} " +
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

        _log.Info("Publish completed.");
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
        {
            _log.Info($"Systemd unit exists: {frag}");
            return;
        }

        _log.Info($"Systemd unit missing -> installing: {ServiceUnitName}");

        var unitPath = $"/etc/systemd/system/{ServiceUnitName}";
        var remoteExe = CombineRemote(_opt.RemoteDir, _opt.RemoteExeName);

        var unit = $"""
[Unit]
Description=AccessAPP (deployed to {_opt.RemoteDir})
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User={_opt.User}
WorkingDirectory={_opt.RemoteDir}
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

        var exists = RunCommandCapture(ssh, $"test -f '{scriptPath}'; echo $?").Trim() == "0";
        if (exists)
        {
            _log.Info($"Init script exists: {scriptPath}");

            // Still fix CRLF / bad shebang issues if any (best effort)
            RunSudo(ssh, $"sed -i 's/\\r$//' '{scriptPath}' || true");
            return;
        }

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
PIDFILE='/var/run/{_opt.ServiceName}.pid'
LOGFILE='{_opt.RemoteDir.Replace("'", "")}/accessapp.log'
MAX_LOG_SIZE=$((10 * 1024 * 1024))  # 10 MB

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

  truncate_log_if_needed

  nohup ""$APP_EXE"" >> ""$LOGFILE"" 2>&1 &
  PID=$!
  echo $PID > ""$PIDFILE""

  sleep 1
  if kill -0 $PID 2>/dev/null; then
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

        // chown via sudo (one-liner)
        RunSudo(ssh, $"chown -R {_opt.User}:{_opt.User} {ShEscape(_opt.RemoteDir)} || true");

        // chmod without sudo (should be fine after chown)
        RunCommand(ssh, $"chmod -R u+rwX {ShEscape(_opt.RemoteDir)} || true");
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

            using var fs = File.OpenRead(e.fullPath);
            sftp.UploadFile(fs, remotePath, true);

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

        ms.Position = 0;
        sftp.UploadFile(ms, remoteManifestPath, true);

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
        RunCommand(ssh, $"echo {_opt.Password} | sudo -S sh -lc '{escaped}'");
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

    private static string ShEscape(string s)
        => "'" + s.Replace("'", "'\"'\"'") + "'";
}
