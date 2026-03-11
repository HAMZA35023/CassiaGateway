using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAppMqttWpf.Services;

public sealed class AccessAppLauncherService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private static string GetDefaultDeployDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessAppMqttWpf", "AccessApp");

    private static string GetVersionFile(string deployDir) =>
        Path.Combine(deployDir, ".version");

    public static string GetInstalledVersion(string? localPath = null)
    {
        var dir = string.IsNullOrWhiteSpace(localPath) ? GetDefaultDeployDir() : localPath;
        var versionFile = GetVersionFile(dir);
        if (!File.Exists(versionFile)) return "Not installed";
        try { return File.ReadAllText(versionFile).Trim(); } catch { return "Unknown"; }
    }

    /// <summary>
    /// Fetches the manifest, downloads the AccessApp zip for the given runtime,
    /// verifies the SHA-256, then extracts to the deploy dir.
    /// </summary>
    public static async Task<(bool success, string message, string version)> DownloadAndExtractAsync(
        string manifestBaseUrl,
        string channel,
        string runtime,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            // 1. Fetch manifest
            var manifestUrl = $"{manifestBaseUrl.TrimEnd('/')}/{channel}/manifest.json";
            var manifestJson = await _http.GetStringAsync(manifestUrl, ct);
            var manifest = JsonNode.Parse(manifestJson);

            var builds = manifest?["latest"]?["builds"];
            var build = builds?[runtime];
            if (build == null)
                return (false, $"Runtime '{runtime}' not found in manifest.", "");

            var url = build["url"]?.GetValue<string>() ?? "";
            var sha256Expected = build["sha256"]?.GetValue<string>() ?? "";
            var version = manifest?["latest"]?["version"]?.GetValue<string>() ?? "unknown";

            if (string.IsNullOrWhiteSpace(url))
                return (false, "No URL in manifest build entry.", "");

            // 2. Download zip
            progress?.Report(0.01);
            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var tempZip = Path.GetTempFileName();
            try
            {
                using (var fs = File.Create(tempZip))
                using (var stream = await response.Content.ReadAsStreamAsync(ct))
                {
                    var buffer = new byte[81920];
                    long downloaded = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                        downloaded += read;
                        if (totalBytes > 0)
                            progress?.Report(0.01 + 0.79 * ((double)downloaded / totalBytes));
                    }
                }

                // 3. Verify SHA-256
                if (!string.IsNullOrWhiteSpace(sha256Expected))
                {
                    progress?.Report(0.80);
                    using var fs = File.OpenRead(tempZip);
                    var hash = await Task.Run(() =>
                    {
                        using var sha = SHA256.Create();
                        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
                    }, ct);

                    if (!hash.Equals(sha256Expected.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                        return (false, $"SHA-256 mismatch. Expected: {sha256Expected}, got: {hash}", "");
                }

                // 4. Extract
                progress?.Report(0.82);
                var deployDir = GetDefaultDeployDir();
                if (Directory.Exists(deployDir))
                    Directory.Delete(deployDir, recursive: true);
                Directory.CreateDirectory(deployDir);

                await Task.Run(() => ZipFile.ExtractToDirectory(tempZip, deployDir), ct);

                // 5. Write version marker
                File.WriteAllText(GetVersionFile(deployDir), version);

                // On Linux/Mac set executable bit — no-op on Windows
                TrySetExecutableBit(deployDir);

                progress?.Report(1.0);
                return (true, $"Installed version {version} to {deployDir}", version);
            }
            finally
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            return (false, "Download cancelled.", "");
        }
        catch (Exception ex)
        {
            return (false, $"Download failed: {ex.Message}", "");
        }
    }

    /// <summary>
    /// Finds the AccessAPP executable in the given directory (or default deploy dir).
    /// </summary>
    public static string? FindExecutable(string? localPath = null)
    {
        var dir = string.IsNullOrWhiteSpace(localPath) ? GetDefaultDeployDir() : localPath;
        if (!Directory.Exists(dir)) return null;

        foreach (var candidate in new[] { "AccessAPP.exe", "AccessAPP", "accessapp", "accessapp.exe" })
        {
            var path = Path.Combine(dir, candidate);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>
    /// Writes a local mqtt.json and starts the AccessAPP process pointing to the local MQTT server.
    /// Returns the started Process, or null on failure.
    /// </summary>
    public static Process? StartAccessApp(
        string executablePath,
        int mqttPort,
        string networkId,
        string cassia = "local-cassia")
    {
        try
        {
            // Write mqtt.json next to the executable so it can be found without interfering with the global state dir
            var exeDir = Path.GetDirectoryName(executablePath) ?? "";
            var mqttConfigPath = Path.Combine(exeDir, "local-mqtt.json");

            var config = new
            {
                name = cassia,
                networkId = networkId,
                host = "127.0.0.1",
                port = mqttPort,
                useTls = false,
                username = "",
                password = "",
                baseTopic = "accessapp",
                keepAliveSeconds = 30,
                reconnectDelaySeconds = 10,
                subscribeToAllTarget = true
            };

            File.WriteAllText(mqttConfigPath,
                JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"--Mqtt:ConfigPath=\"{mqttConfigPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = exeDir
            };

            return Process.Start(psi);
        }
        catch
        {
            return null;
        }
    }

    private static void TrySetExecutableBit(string dir)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "AccessAPP*", SearchOption.AllDirectories))
            {
                try
                {
                    var psi = new ProcessStartInfo("chmod", $"+x \"{f}\"") { UseShellExecute = false };
                    Process.Start(psi)?.WaitForExit(2000);
                }
                catch { }
            }
        }
        catch { }
    }
}
