using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static System.Net.WebRequestMethods;
using File = System.IO.File;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var configPath = GetArgValue(args, "--config")
                ?? "/etc/accessapp-updater.json";

            var dryRun = HasArg(args, "--dry-run");
            var forceSetup = HasArg(args, "--setup");

            var cfg = EnsureConfig(configPath, forceSetup);

            var effectiveTimeoutSeconds = Math.Max(600, cfg.HttpTimeoutSeconds);
            var effectiveChannel = ResolveEffectiveChannel(cfg);
            var manifestUrl = ResolveManifestUrl(cfg.ManifestUrl, effectiveChannel);

            var runtimeTag = ResolveEffectiveRuntimeTag(cfg);

            var workDir = ResolveWritableWorkDir(cfg.WorkDir);

            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(effectiveTimeoutSeconds)
            };

            Log($"Channel: {effectiveChannel}");
            Log($"System: {runtimeTag}");
            Log($"Fetching manifest: {manifestUrl}");
            Log($"HTTP timeout: {effectiveTimeoutSeconds}s");

            var manifest = await http.GetFromJsonAsync<UpdateManifest>(manifestUrl)
                ?? throw new InvalidOperationException("Manifest response was empty.");

            var release = SelectTargetRelease(manifest, cfg)
                ?? throw new InvalidOperationException("No release found in manifest.");

            var artifact = SelectArtifactForRuntime(release, runtimeTag)
                ?? throw new InvalidOperationException($"No compatible build found for runtime '{runtimeTag}' in version '{release.Version}'.");

            var currentVersion = ReadCurrentVersion(cfg);
            if (!cfg.AllowDowngrade && CompareVersions(release.Version, currentVersion) <= 0)
            {
                Log($"No update needed. current={currentVersion}, target={release.Version}");
                return 0;
            }

            Log($"Update found. current={currentVersion}, target={release.Version}");
            Log($"Selected build: {runtimeTag} -> {artifact.Url}");

            var zipFile = Path.Combine(workDir, Path.GetFileName(new Uri(artifact.Url).AbsolutePath));
            var stageDir = Path.Combine(workDir, $"stage-{Guid.NewGuid():N}");

            try
            {
                if (!dryRun)
                {
                    await DownloadFileWithRetryAsync(http, artifact.Url, zipFile, maxAttempts: 3);
                    ValidateDownloadedArtifact(zipFile, artifact);

                    Directory.CreateDirectory(stageDir);
                    ExtractZipSafe(zipFile, stageDir);

                    EnsureExpectedExecutableExists(stageDir, cfg.ExecutableName);
                    PreserveFiles(cfg.InstallDir, stageDir, cfg.PreserveFiles);

                    InstallAtomically(cfg.InstallDir, stageDir);
                    stageDir = string.Empty;

                    // Always make the primary executable runnable regardless of config.
                    TryChmodX(Path.Combine(cfg.InstallDir, cfg.ExecutableName));

                    // Also chmod any additional configured paths (e.g. shared libraries).
                    if (cfg.ExecutableRelativePathsToChmodX?.Count > 0)
                    {
                        foreach (var rel in cfg.ExecutableRelativePathsToChmodX)
                        {
                            var full = Path.Combine(cfg.InstallDir, rel);
                            TryChmodX(full);
                        }
                    }

                    WriteCurrentVersion(cfg, release.Version);
                }
                else
                {
                    Log("[DRY-RUN] Skipping download/install.");
                }
            }
            finally
            {
                SafeDeleteDirectory(stageDir);
                SafeDeleteFile(zipFile);
            }

            Log($"Update completed. version={release.Version}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[updater] FAILED: {ex}");
            return 1;
        }
    }

    private static UpdaterConfig EnsureConfig(string configPath, bool forceSetup)
    {
        if (!forceSetup && File.Exists(configPath))
            return LoadConfig(configPath);

        // First-run interactive setup.
        if (Console.IsInputRedirected)
            throw new InvalidOperationException($"Config file not found and stdin is redirected. Create config at: {configPath}");

        Log($"First-run setup (config missing or --setup). Target config: {configPath}");

        var cfg = new UpdaterConfig();

        // Channel
        var defaultChannel = string.IsNullOrWhiteSpace(cfg.Channel) ? "stable" : cfg.Channel;
        var channel = Prompt("Choose channel [stable/test/develop]", defaultChannel).Trim().ToLowerInvariant();
        channel = NormalizeChannelToken(channel);
        cfg.Channel = channel;

        // System/runtime
        var detected = DetermineRuntimeTag();
        var system = Prompt("Choose system [linux-arm/linux-64]", detected).Trim().ToLowerInvariant();
        system = NormalizeRuntimeTag(system);
        cfg.System = system; 
        // Persist channel + system files (handy for scripts/ops)
        TryWriteTextFile(cfg.ChannelFilePath, cfg.Channel + "\n");
        TryWriteTextFile(cfg.SystemFilePath, cfg.System + "\n");

        // Write config
        WriteConfig(configPath, cfg);

        // Install init.d script and enable autostart
        InstallInitDScript(cfg, configPath);

        Log("Setup completed.");
        Log("Next: /etc/init.d/accessapp start");
        return cfg;
    }

    private static void WriteConfig(string path, UpdaterConfig cfg)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        Log($"Wrote config: {path}");
    }

    private static void InstallInitDScript(UpdaterConfig cfg, string configPath)
    {
        // Write /etc/init.d/accessapp script that:
        // - Always runs updater before starting AccessAPP (so service restarts/boots can update too).
        // - Uses an explicit PATH so start-stop-daemon is found even with a restricted root PATH.
        var initPath = "/etc/init.d/accessapp";
        var updaterPath = Environment.ProcessPath ?? "/usr/local/bin/AccessAppUpdater";
        var appPath = Path.Combine(cfg.InstallDir, cfg.ExecutableName);

        var script = $@"#!/bin/sh
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
### BEGIN INIT INFO
# Provides:          AccessApp
# Required-Start:    $remote_fs $syslog $network
# Required-Stop:     $remote_fs $syslog $network
# Default-Start:     2 3 4 5
# Default-Stop:      0 1 6
# Short-Description: AccessAPP with auto-update before start
### END INIT INFO

NAME=AccessApp
APP=""{appPath}""
UPDATER=""{updaterPath}""
CONFIG=""{configPath}""
PIDFILE=/var/run/accessapp.pid

start_app() {{
  echo ""[init] Running updater...""
  ""$UPDATER"" --config ""$CONFIG"" || exit 1

  echo ""[init] Starting AccessAPP...""
  start-stop-daemon --start --background --make-pidfile --pidfile ""$PIDFILE"" --chdir ""{cfg.InstallDir}"" --exec ""$APP""
}}

stop_app() {{
  echo ""[init] Stopping AccessAPP...""
  start-stop-daemon --stop --pidfile ""$PIDFILE"" --retry=TERM/10/KILL/5
  rm -f ""$PIDFILE""
}}

status_app() {{
  if [ -f ""$PIDFILE"" ] && kill -0 $(cat ""$PIDFILE"") 2>/dev/null; then
    echo ""AccessAPP running (pid $(cat ""$PIDFILE""))""
    exit 0
  fi
  echo ""AccessAPP not running""
  exit 3
}}

case ""$1"" in
  start)
    start_app
    ;;
  stop)
    stop_app
    ;;
  restart)
    stop_app
    start_app
    ;;
  status)
    status_app
    ;;
  *)
    echo ""Usage: /etc/init.d/accessapp {{start|stop|restart|status}}""
    exit 1
    ;;
esac

exit 0
";
        TryWriteTextFile(initPath, script);
        TryChmodX(initPath);

        // Enable autostart (Debian/Ubuntu). Ignore errors if unavailable.
        TryRun("update-rc.d", "accessapp defaults");
        TryRun("systemctl", "daemon-reload");
        Log($"Installed init.d script: {initPath}");
    }

    private static string Prompt(string prompt, string defaultValue)
    {
        Console.Write($"{prompt} [{defaultValue}]: ");
        var line = Console.ReadLine();
        return string.IsNullOrWhiteSpace(line) ? defaultValue : line;
    }

    private static void TryWriteTextFile(string path, string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, content);
        }
        catch (Exception ex)
        {
            Log($"[WARN] Could not write '{path}': {ex.Message}");
        }
    }

    private static UpdaterConfig LoadConfig(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<UpdaterConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException($"Could not parse config: {path}");
    }

    private static string ResolveWritableWorkDir(string configured)
    {
        var d = string.IsNullOrWhiteSpace(configured) ? "/tmp/accessapp-updater" : configured.Trim();
        try
        {
            Directory.CreateDirectory(d);
            var test = Path.Combine(d, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(test, "ok");
            File.Delete(test);
            return d;
        }
        catch
        {
            var fallback = "/tmp/accessapp-updater";
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static string ResolveEffectiveChannel(UpdaterConfig cfg)
    {
        // Prefer the channel file if present (lets ops switch channel without editing JSON).
        var token = cfg.Channel;

        if (!string.IsNullOrWhiteSpace(cfg.ChannelFilePath) && File.Exists(cfg.ChannelFilePath))
        {
            try
            {
                var t = File.ReadAllText(cfg.ChannelFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(t))
                    token = t;
            }
            catch { /* ignore */ }
        }

        return NormalizeChannelToken(token);
    }

    private static string ResolveEffectiveRuntimeTag(UpdaterConfig cfg)
    {
        var token = cfg.System;

        if (!string.IsNullOrWhiteSpace(cfg.SystemFilePath) && File.Exists(cfg.SystemFilePath))
        {
            try
            {
                var t = File.ReadAllText(cfg.SystemFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(t))
                    token = t;
            }
            catch { /* ignore */ }
        }

        token = string.IsNullOrWhiteSpace(token) ? DetermineRuntimeTag() : token;
        return NormalizeRuntimeTag(token);
    }

    private static string ResolveManifestUrl(string manifestUrl, string channel)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
            throw new InvalidOperationException("ManifestUrl is required.");

        if (manifestUrl.Contains("{channel}", StringComparison.OrdinalIgnoreCase))
            return manifestUrl.Replace("{channel}", channel, StringComparison.OrdinalIgnoreCase);

        return manifestUrl;
    }

    private static string NormalizeChannelToken(string? channel)
    {
        var token = (channel ?? string.Empty).Trim().ToLowerInvariant();
        return token switch
        {
            "prod" => "stable",
            "production" => "stable",
            "main" => "stable",
            "" => "stable",
            _ => token
        };
    }

    private static string NormalizeRuntimeTag(string? runtime)
    {
        var token = (runtime ?? string.Empty).Trim().ToLowerInvariant();
        return token switch
        {
            "x64" => "linux-64",
            "linux-x64" => "linux-64",
            "linux64" => "linux-64",
            "amd64" => "linux-64",
            "arm" => "linux-arm",
            "arm64" => "linux-arm",
            "aarch64" => "linux-arm",
            "" => DetermineRuntimeTag(),
            _ => token
        };
    }

    private static string DetermineRuntimeTag()
    {
        try
        {
            var arch = RuntimeInformation.OSArchitecture;
            return arch switch
            {
                Architecture.Arm or Architecture.Arm64 => "linux-arm",
                Architecture.X64 => "linux-64",
                _ => "linux-64"
            };
        }
        catch
        {
            return "linux-64";
        }
    }

    private static Release? SelectTargetRelease(UpdateManifest manifest, UpdaterConfig cfg)
    {
        IEnumerable<Release> releases = new List<Release>();

        if (manifest.Releases is { Count: > 0 })
            releases = manifest.Releases;


        if (manifest.Latest is not null)
            return manifest.Latest;

        if (!string.IsNullOrWhiteSpace(cfg.Channel))
        {
            releases = releases
                .Where(r => string.Equals(r.Channel ?? manifest.Channel, cfg.Channel, StringComparison.OrdinalIgnoreCase));
        }

        return releases
            .OrderByDescending(r => ParseVersionLoose(r.Version))
            .ThenByDescending(r => r.PublishedAtUtc)
            .FirstOrDefault();
    }

    private static BuildArtifact? SelectArtifactForRuntime(Release release, string runtimeTag)
    {
        runtimeTag = NormalizeRuntimeTag(runtimeTag);

        // New schema: builds dictionary
        if (release.Builds is { Count: > 0 })
        {
            if (release.Builds.TryGetValue(runtimeTag, out var a) && a is not null)
                return a;

            // Alias support
            if (runtimeTag == "linux-64" && release.Builds.TryGetValue("linux-x64", out a) && a is not null)
                return a;
            if (runtimeTag == "linux-arm" && release.Builds.TryGetValue("linux-arm64", out a) && a is not null)
                return a;
        }

        // Legacy schema: single Url/Sha256/SizeBytes without builds -> assume linux-arm (backwards compatible)
        if (!string.IsNullOrWhiteSpace(release.Url))
        {
            return new BuildArtifact
            {
                Runtime = "linux-arm",
                Url = release.Url,
                Sha256 = release.Sha256,
                SizeBytes = release.SizeBytes,
                PublishedAtUtc = release.PublishedAtUtc
            };
        }

        return null;
    }

    private static async Task DownloadFileWithRetryAsync(HttpClient http, string url, string destPath, int maxAttempts)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await DownloadFileAsync(http, url, destPath);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Log($"Download attempt {attempt}/{maxAttempts} failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)));
            }
        }

        throw new InvalidOperationException($"Download failed after {maxAttempts} attempts.", last);
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string destPath)
    {
        using var res = await http.GetAsync(url);
        res.EnsureSuccessStatusCode();

        await using var fs = File.Create(destPath);
        await res.Content.CopyToAsync(fs);
    }

    private static void ValidateDownloadedArtifact(string path, BuildArtifact artifact)
    {
        var info = new FileInfo(path);
        if (artifact.SizeBytes > 0 && info.Length != artifact.SizeBytes)
            throw new InvalidOperationException($"Size mismatch. expected={artifact.SizeBytes}, actual={info.Length}");

        if (!string.IsNullOrWhiteSpace(artifact.Sha256))
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            var hash = sha.ComputeHash(fs);
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            var expected = artifact.Sha256.Trim().ToLowerInvariant();
            if (!string.Equals(hex, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"SHA256 mismatch. expected={expected}, actual={hex}");
        }
    }

    private static void ExtractZipSafe(string zipPath, string destDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            var entryPath = Path.Combine(destDir, entry.FullName);

            var fullDest = Path.GetFullPath(destDir);
            var fullEntry = Path.GetFullPath(entryPath);
            if (!fullEntry.StartsWith(fullDest, StringComparison.Ordinal))
                throw new InvalidOperationException($"Zip traversal detected: {entry.FullName}");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(entryPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
            entry.ExtractToFile(entryPath, overwrite: true);
        }
    }

    private static void EnsureExpectedExecutableExists(string stageDir, string exeName)
    {
        var full = Path.Combine(stageDir, exeName);
        if (!File.Exists(full))
            throw new InvalidOperationException($"Expected executable not found in package: {exeName}");
    }

    private static void PreserveFiles(string currentInstallDir, string stageDir, List<string>? preserveFiles)
    {
        if (preserveFiles is null || preserveFiles.Count == 0)
            return;

        foreach (var f in preserveFiles)
        {
            try
            {
                var src = Path.Combine(currentInstallDir, f);
                var dst = Path.Combine(stageDir, f);
                if (File.Exists(src))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(src, dst, overwrite: true);
                    Log($"Preserved file: {f}");
                }
            }
            catch (Exception ex)
            {
                Log($"[WARN] Preserve file '{f}' failed: {ex.Message}");
            }
        }
    }

    private static void InstallAtomically(string installDir, string stageDir)
    {
        Directory.CreateDirectory(installDir);

        var parent = Path.GetDirectoryName(installDir.TrimEnd(Path.DirectorySeparatorChar)) ?? "/";
        var backup = Path.Combine(parent, $"{Path.GetFileName(installDir)}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}");
        var tempTarget = Path.Combine(parent, $"{Path.GetFileName(installDir)}.new-{Guid.NewGuid():N}");

        // Move stage into tempTarget then swap
        if (Directory.Exists(tempTarget))
            Directory.Delete(tempTarget, recursive: true);

        Directory.Move(stageDir, tempTarget);

        if (Directory.Exists(installDir))
            Directory.Move(installDir, backup);

        Directory.Move(tempTarget, installDir);

        // Best effort cleanup of backup
        SafeDeleteDirectory(backup);
    }

    private static string ReadCurrentVersion(UpdaterConfig cfg)
    {
        try
        {
            var path = Path.Combine(cfg.InstallDir, cfg.VersionFileName);
            if (!File.Exists(path))
                return "0.0.0";
            return File.ReadAllText(path).Trim();
        }
        catch
        {
            return "0.0.0";
        }
    }

    private static void WriteCurrentVersion(UpdaterConfig cfg, string version)
    {
        try
        {
            var path = Path.Combine(cfg.InstallDir, cfg.VersionFileName);
            File.WriteAllText(path, version.Trim());
        }
        catch (Exception ex)
        {
            Log($"[WARN] Could not write version file: {ex.Message}");
        }
    }

    private static Version ParseVersionLoose(string version)
    {
        if (Version.TryParse(version, out var v))
            return v;

        // Strip any suffix like "-beta"
        var cleaned = version.Split('-', '+')[0];
        return Version.TryParse(cleaned, out v) ? v : new Version(0, 0, 0, 0);
    }

    private static int CompareVersions(string a, string b)
        => ParseVersionLoose(a).CompareTo(ParseVersionLoose(b));

    private static void SafeDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { /* ignore */ }
    }

    private static void SafeDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* ignore */ }
    }

    private static void TryChmodX(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            var current = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, current | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            Log($"chmod +x: {path}");
        }
        catch (Exception ex)
        {
            Log($"[WARN] chmod +x '{path}' failed: {ex.Message}");
        }
    }

    private static void TryRun(string fileName, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            if (p is null) return;
            p.WaitForExit(5000);
        }
        catch
        {
            // ignore (platform differences)
        }
    }

    private static string? GetArgValue(string[] args, string key)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                continue;

            if (i + 1 >= args.Length)
                return null;

            return args[i + 1];
        }

        return null;
    }

    private static bool HasArg(string[] args, string key)
        => args.Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));

    private static void Log(string msg)
        => Console.WriteLine($"[updater] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {msg}");
}

internal sealed class UpdaterConfig
{
    public string ManifestUrl { get; set; } = "http://prod.statistics.niko-test.nu/accessapp/{channel}/manifest.json";

    // Channel selection (can be overridden by ChannelFilePath content)
    public string Channel { get; set; } = "stable";
    public string ChannelFilePath { get; set; } = "/etc/accessapp-updater.channel";

    // System selection (linux-arm or linux-64; can be overridden by SystemFilePath content)
    public string System { get; set; } = "";
    public string SystemFilePath { get; set; } = "/etc/accessapp-updater.system"; 
    public bool AllowDowngrade { get; set; }

    public string InstallDir { get; set; } = "/home/cassia/FWUpgrade";
    public string WorkDir { get; set; } = "/tmp/accessapp-updater";

    public string VersionFileName { get; set; } = "version.txt";
    public string ExecutableName { get; set; } = "AccessAPP";

    public int HttpTimeoutSeconds { get; set; } = 600;

    public List<string> PreserveFiles { get; set; } = new();

    public List<string> ExecutableRelativePathsToChmodX { get; set; } = new() { "AccessAPP", "libBootloaderUtilMultiThread.so" };
}

internal sealed class UpdateManifest
{
    public string? App { get; set; }
    public string? Channel { get; set; }
    public DateTimeOffset? GeneratedAtUtc { get; set; }
    public Release? Latest { get; set; }
    public List<Release>? Releases { get; set; }
}

internal sealed class Release
{
    public string Version { get; set; } = "0.0.0";

    // Legacy single-build fields (kept for backwards compatibility)
    public string Url { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset PublishedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string? Channel { get; set; }

    // New multi-build schema
    public Dictionary<string, BuildArtifact>? Builds { get; set; }
}

internal sealed class BuildArtifact
{
    public string Runtime { get; set; } = "linux-arm";
    public string Url { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset PublishedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
