using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AccessAPP.Services;

public sealed class AccessAppSelfUpdater
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public const string DefaultChannelFilePath = "/etc/accessapp-updater.channel";

    public async Task<SelfUpdateRunResult> RunAsync(SelfUpdateCommand cmd, CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct))
        {
            return new SelfUpdateRunResult
            {
                Status = "busy",
                Message = "Updater is already running."
            };
        }

        try
        {
            var updaterPath = string.IsNullOrWhiteSpace(cmd.UpdaterPath)
                ? "/usr/local/bin/AccessAppUpdater"
                : cmd.UpdaterPath!;

            var configPath = string.IsNullOrWhiteSpace(cmd.ConfigPath)
                ? "/etc/accessapp-updater.json"
                : cmd.ConfigPath!;

            var timeoutSeconds = cmd.TimeoutSeconds <= 0 ? 120 : cmd.TimeoutSeconds;
            var args = $"--config \"{configPath}\"";
            if (cmd.DryRun) args += " --dry-run";

            var psi = new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return new SelfUpdateRunResult
                {
                    Status = "failed",
                    Message = $"Could not start updater process: {updaterPath}"
                };
            }

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new SelfUpdateRunResult
                {
                    Status = "timeout",
                    Message = $"Updater timed out after {timeoutSeconds}s."
                };
            }

            var stdout = await stdOutTask;
            var stderr = await stdErrTask;
            var combined = $"{stdout}\n{stderr}";

            var status = "failed";
            var message = $"Updater exited with code {process.ExitCode}.";

            if (process.ExitCode == 0)
            {
                if (combined.Contains("No update needed", StringComparison.OrdinalIgnoreCase))
                {
                    status = "no-update";
                    message = "No newer update available.";
                }
                else if (combined.Contains("Update completed", StringComparison.OrdinalIgnoreCase))
                {
                    status = "updated";
                    message = "Update was downloaded and installed. Restart service to run the new version.";
                }
                else if (cmd.DryRun)
                {
                    status = "dry-run";
                    message = "Dry-run completed.";
                }
                else
                {
                    status = "ok";
                    message = "Updater completed.";
                }
            }

            return new SelfUpdateRunResult
            {
                Status = status,
                Message = message,
                ExitCode = process.ExitCode,
                StdOut = Truncate(stdout, 4000),
                StdErr = Truncate(stderr, 2000)
            };
        }
        catch (Exception ex)
        {
            return new SelfUpdateRunResult
            {
                Status = "failed",
                Message = ex.Message
            };
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s.Substring(0, max);
    }

    public SelfUpdateRunResult TriggerServiceRestart(SelfUpdateCommand cmd)
    {
        try
        {
            var serviceName = string.IsNullOrWhiteSpace(cmd.ServiceName) ? "accessapp" : cmd.ServiceName.Trim();
            var updaterPath = string.IsNullOrWhiteSpace(cmd.UpdaterPath)
                ? "/usr/local/bin/AccessAppUpdater"
                : cmd.UpdaterPath!.Trim();
            var configPath = string.IsNullOrWhiteSpace(cmd.ConfigPath)
                ? "/etc/accessapp-updater.json"
                : cmd.ConfigPath!.Trim();

            if (!Regex.IsMatch(serviceName, "^[a-zA-Z0-9_-]+$"))
            {
                return new SelfUpdateRunResult
                {
                    Status = "failed",
                    Message = $"Invalid service name: {serviceName}"
                };
            }

            var shell = "/bin/sh";
            var updaterCmd = $"'{updaterPath}' --config '{configPath}'";
            if (cmd.DryRun)
                updaterCmd += " --dry-run";

            var restartCmd =
                "(sleep 1; " +
                $"{updaterCmd}; " +
                $"sudo -n systemctl restart '{serviceName}' || " +
                $"sudo -n service '{serviceName}' restart || " +
                $"sudo -n /etc/init.d/{serviceName} restart || " +
                $"systemctl restart '{serviceName}' || " +
                $"service '{serviceName}' restart || " +
                $"/etc/init.d/{serviceName} restart) " +
                ">/tmp/accessapp-self-update-restart.log 2>&1 &";

            var psi = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = $"-lc \"{restartCmd}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return new SelfUpdateRunResult
                {
                    Status = "failed",
                    Message = $"Could not start shell: {shell}"
                };
            }

            process.WaitForExit(5000);

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0)
            {
                return new SelfUpdateRunResult
                {
                    Status = "failed",
                    Message = $"Failed to queue service restart (exit {process.ExitCode}).",
                    ExitCode = process.ExitCode,
                    StdOut = Truncate(stdout, 1000),
                    StdErr = Truncate(stderr, 1000)
                };
            }

            return new SelfUpdateRunResult
            {
                Status = "restart-queued",
                Message = $"Service restart queued for '{serviceName}'.",
                ExitCode = 0,
                StdOut = Truncate(stdout, 1000),
                StdErr = Truncate(stderr, 1000)
            };
        }
        catch (Exception ex)
        {
            return new SelfUpdateRunResult
            {
                Status = "failed",
                Message = ex.Message
            };
        }
    }

    public SelfUpdateRunResult SetUpdateChannel(string? channel, string? channelFilePath = null)
    {
        var normalized = NormalizeUpdateChannel(channel);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new SelfUpdateRunResult
            {
                Status = "failed",
                Message = "Invalid channel. Allowed values: stable, test, develop."
            };
        }

        var path = string.IsNullOrWhiteSpace(channelFilePath)
            ? DefaultChannelFilePath
            : channelFilePath!.Trim();

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, normalized + Environment.NewLine, Encoding.UTF8);

            return new SelfUpdateRunResult
            {
                Status = "channel-set",
                Message = $"Update channel set to '{normalized}'.",
                Channel = normalized
            };
        }
        catch (Exception ex)
        {
            return new SelfUpdateRunResult
            {
                Status = "failed",
                Message = $"Failed to write update channel file '{path}': {ex.Message}"
            };
        }
    }

    public static string NormalizeUpdateChannel(string? channel)
    {
        var value = (channel ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "stable" => "stable",
            "test" => "test",
            "develop" => "develop",
            "dev" => "develop",
            "prod-stable" => "stable",
            "prod-test" => "test",
            "prod-develop" => "develop",
            _ => string.Empty
        };
    }
}

public sealed class SelfUpdateRunResult
{
    public string Status { get; set; } = "failed";
    public string Message { get; set; } = "Unknown error";
    public string? Channel { get; set; }
    public int? ExitCode { get; set; }
    public string? StdOut { get; set; }
    public string? StdErr { get; set; }
}
