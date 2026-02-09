using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var configPath = GetArgValue(args, "--config")
                ?? "/etc/accessapp-updater.json";

            var dryRun = HasArg(args, "--dry-run");
            var cfg = LoadConfig(configPath);
            var effectiveTimeoutSeconds = Math.Max(600, cfg.HttpTimeoutSeconds);

            var workDir = ResolveWritableWorkDir(cfg.WorkDir);

            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(effectiveTimeoutSeconds)
            };

            Log($"Fetching manifest: {cfg.ManifestUrl}");
            Log($"HTTP timeout: {effectiveTimeoutSeconds}s");
            var manifest = await http.GetFromJsonAsync<UpdateManifest>(cfg.ManifestUrl)
                ?? throw new InvalidOperationException("Manifest response was empty.");

            var target = SelectTargetRelease(manifest, cfg)
                ?? throw new InvalidOperationException("No release found in manifest.");

            var currentVersion = ReadCurrentVersion(cfg);
            if (!cfg.AllowDowngrade && CompareVersions(target.Version, currentVersion) <= 0)
            {
                Log($"No update needed. current={currentVersion}, target={target.Version}");
                return 0;
            }

            Log($"Update found. current={currentVersion}, target={target.Version}");

            var zipFile = Path.Combine(workDir, Path.GetFileName(new Uri(target.Url).AbsolutePath));
            var stageDir = Path.Combine(workDir, $"stage-{Guid.NewGuid():N}");

            try
            {
                if (!dryRun)
                {
                    await DownloadFileWithRetryAsync(http, target.Url, zipFile, maxAttempts: 3);
                    ValidateDownloadedArtifact(zipFile, target);

                    Directory.CreateDirectory(stageDir);
                    ExtractZipSafe(zipFile, stageDir);

                    EnsureExpectedExecutableExists(stageDir, cfg.ExecutableName);
                    PreserveFiles(cfg.InstallDir, stageDir, cfg.PreserveFiles);

                    InstallAtomically(cfg.InstallDir, stageDir);
                    stageDir = string.Empty;

                    if (cfg.ExecutableRelativePathsToChmodX?.Count > 0)
                    {
                        foreach (var rel in cfg.ExecutableRelativePathsToChmodX)
                        {
                            var full = Path.Combine(cfg.InstallDir, rel);
                            TryChmodX(full);
                        }
                    }

                    WriteCurrentVersion(cfg, target.Version);
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

            Log($"Update completed. version={target.Version}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[updater] FAILED: {ex}");
            return 1;
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

    private static string ReadCurrentVersion(UpdaterConfig cfg)
    {
        var path = Path.Combine(cfg.InstallDir, cfg.VersionFileName);
        if (!File.Exists(path))
            return "0.0.0";

        return (File.ReadAllText(path) ?? string.Empty).Trim();
    }

    private static void WriteCurrentVersion(UpdaterConfig cfg, string version)
    {
        var path = Path.Combine(cfg.InstallDir, cfg.VersionFileName);
        File.WriteAllText(path, $"{version}\n");
    }

    private static Release? SelectTargetRelease(UpdateManifest manifest, UpdaterConfig cfg)
    {
        if (manifest.Latest is not null)
            return manifest.Latest;

        var releases = manifest.Releases ?? new List<Release>();
        if (!string.IsNullOrWhiteSpace(cfg.Channel))
        {
            releases = releases
                .Where(r => string.Equals(r.Channel ?? manifest.Channel, cfg.Channel, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return releases
            .OrderByDescending(r => ParseVersionLoose(r.Version))
            .ThenByDescending(r => r.PublishedAtUtc)
            .FirstOrDefault();
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string destPath)
    {
        using var res = await http.GetAsync(url);
        res.EnsureSuccessStatusCode();

        await using var fs = File.Create(destPath);
        await res.Content.CopyToAsync(fs);
    }

    private static async Task DownloadFileWithRetryAsync(HttpClient http, string url, string destPath, int maxAttempts)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= Math.Max(1, maxAttempts); attempt++)
        {
            try
            {
                if (File.Exists(destPath))
                    File.Delete(destPath);

                if (attempt > 1)
                    Log($"Retrying download ({attempt}/{maxAttempts})...");

                await DownloadFileAsync(http, url, destPath);
                return;
            }
            catch (Exception ex) when (IsTransientDownloadError(ex))
            {
                last = ex;
                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }

        throw new InvalidOperationException($"Failed to download update package after {maxAttempts} attempts.", last);
    }

    private static bool IsTransientDownloadError(Exception ex) =>
        ex is TaskCanceledException ||
        ex is TimeoutException ||
        ex is HttpRequestException ||
        ex.InnerException is TaskCanceledException ||
        ex.InnerException is TimeoutException ||
        ex.InnerException is HttpRequestException;

    private static void ValidateDownloadedArtifact(string zipPath, Release rel)
    {
        var fi = new FileInfo(zipPath);
        if (rel.SizeBytes > 0 && fi.Length != rel.SizeBytes)
            throw new InvalidOperationException($"Size mismatch for {zipPath}: actual={fi.Length}, expected={rel.SizeBytes}");

        if (!string.IsNullOrWhiteSpace(rel.Sha256))
        {
            var actualHash = ComputeSha256Hex(zipPath);
            if (!actualHash.Equals(rel.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"SHA256 mismatch for {zipPath}: actual={actualHash}, expected={rel.Sha256}");
            }
        }
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ExtractZipSafe(string zipPath, string destinationDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(destinationDir) + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            var normalizedEntryPath = entry.FullName
                .Replace('\\', '/')
                .TrimStart('/');

            var isDirectoryEntry = normalizedEntryPath.EndsWith("/", StringComparison.Ordinal);
            if (isDirectoryEntry)
                normalizedEntryPath = normalizedEntryPath.TrimEnd('/');

            var targetPath = Path.GetFullPath(Path.Combine(destinationDir, normalizedEntryPath));
            if (!targetPath.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unsafe zip entry path detected: {entry.FullName}");

            if (isDirectoryEntry)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDir))
                Directory.CreateDirectory(targetDir);

            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static void PreserveFiles(string currentInstallDir, string stagedInstallDir, IReadOnlyList<string> filesToPreserve)
    {
        foreach (var rel in filesToPreserve)
        {
            var src = Path.GetFullPath(Path.Combine(currentInstallDir, rel));
            var dst = Path.GetFullPath(Path.Combine(stagedInstallDir, rel));

            if (!File.Exists(src))
                continue;

            var stagedRoot = Path.GetFullPath(stagedInstallDir) + Path.DirectorySeparatorChar;
            if (!dst.StartsWith(stagedRoot, StringComparison.Ordinal))
                throw new InvalidOperationException($"Invalid preserve file path: {rel}");

            var parent = Path.GetDirectoryName(dst);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            File.Copy(src, dst, overwrite: true);
            Log($"Preserved file: {rel}");
        }
    }

    private static void EnsureExpectedExecutableExists(string stageDir, string executableName)
    {
        var path = Path.Combine(stageDir, executableName);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Expected executable not found in package: {path}");
    }

    private static void InstallAtomically(string installDir, string stageDir)
    {
        var backupDir = PrepareBackupDirPath(installDir);

        if (Directory.Exists(installDir))
            Directory.Move(installDir, backupDir);

        try
        {
            Directory.Move(stageDir, installDir);
            Log($"Installed update to {installDir}");
        }
        catch
        {
            SafeDeleteDirectory(installDir);
            if (Directory.Exists(backupDir))
                Directory.Move(backupDir, installDir);
            throw;
        }
    }

    private static string PrepareBackupDirPath(string installDir)
    {
        var preferred = installDir + ".prev";
        SafeDeleteDirectory(preferred);
        if (Directory.Exists(preferred))
            throw new IOException($"Cannot clear backup dir '{preferred}'.");
        return preferred;
    }

    private static void TryChmodX(string path)
    {
        if (!File.Exists(path))
            return;

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        try
        {
            var mode = File.GetUnixFileMode(path);
            mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
            Log($"chmod +x: {path}");
        }
        catch (Exception ex)
        {
            Log($"chmod +x failed for {path}: {ex.Message}");
        }
    }

    private static int CompareVersions(string a, string b)
    {
        var va = ParseVersionLoose(a);
        var vb = ParseVersionLoose(b);
        return va.CompareTo(vb);
    }

    private static Version ParseVersionLoose(string version)
    {
        var core = (version ?? string.Empty).Split('-', 2)[0];
        return Version.TryParse(core, out var v) ? v : new Version(0, 0, 0);
    }

    private static string? GetArgValue(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static bool HasArg(string[] args, string key) =>
        args.Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    private static void Log(string message) =>
        Console.WriteLine($"[updater] {message}");

    private static string ResolveWritableWorkDir(string preferred)
    {
        if (TryEnsureWritableDirectory(preferred, out var resolvedPreferred))
        {
            Log($"Work dir: {resolvedPreferred}");
            return resolvedPreferred;
        }

        var fallback = Path.Combine(Path.GetTempPath(), $"accessapp-updater-{SanitizeFileName(Environment.UserName ?? "user")}");
        if (TryEnsureWritableDirectory(fallback, out var resolvedFallback))
        {
            Log($"Work dir fallback: {resolvedFallback}");
            return resolvedFallback;
        }

        throw new UnauthorizedAccessException($"No writable work directory available. preferred='{preferred}', fallback='{fallback}'");
    }

    private static bool TryEnsureWritableDirectory(string path, out string resolvedPath)
    {
        resolvedPath = Path.GetFullPath(path);
        try
        {
            Directory.CreateDirectory(resolvedPath);
            var probe = Path.Combine(resolvedPath, $".probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}

internal sealed class UpdaterConfig
{
    public string ManifestUrl { get; set; } = "https://updates.example.com/accessapp/manifest.json";
    public string Channel { get; set; } = "stable";
    public bool AllowDowngrade { get; set; }
    public string InstallDir { get; set; } = "/home/cassia/FWUpgrade";
    public string WorkDir { get; set; } = "/tmp/accessapp-updater";
    public string VersionFileName { get; set; } = "version.txt";
    public string ExecutableName { get; set; } = "AccessAPP";
    public int HttpTimeoutSeconds { get; set; } = 600;
    public List<string> PreserveFiles { get; set; } = new() { "mqtt.json" };
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
    public string Url { get; set; } = string.Empty;
    public string? Channel { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset PublishedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
