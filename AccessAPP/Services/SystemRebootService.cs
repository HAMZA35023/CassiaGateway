using System.Diagnostics;

namespace AccessAPP.Services;

public sealed class SystemRebootService
{
    public SystemRebootResult QueueReboot(RebootCommand cmd)
    {
        var delaySeconds = cmd.DelaySeconds <= 0 ? 2 : Math.Min(cmd.DelaySeconds, 120);

        try
        {
            if (OperatingSystem.IsWindows())
                return QueueWindows(delaySeconds);

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return QueueUnix(delaySeconds);

            return new SystemRebootResult
            {
                Status = "failed",
                Message = $"Unsupported OS platform '{Environment.OSVersion.Platform}'.",
                DelaySeconds = delaySeconds
            };
        }
        catch (Exception ex)
        {
            return new SystemRebootResult
            {
                Status = "failed",
                Message = ex.Message,
                DelaySeconds = delaySeconds
            };
        }
    }

    private static SystemRebootResult QueueWindows(int delaySeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "shutdown",
            Arguments = $"/r /t {delaySeconds} /f",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            return new SystemRebootResult
            {
                Status = "failed",
                Message = "Could not start shutdown command.",
                DelaySeconds = delaySeconds
            };
        }

        process.WaitForExit(5000);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0)
        {
            return new SystemRebootResult
            {
                Status = "failed",
                Message = $"Failed to queue reboot (exit {process.ExitCode}).",
                DelaySeconds = delaySeconds,
                ExitCode = process.ExitCode,
                StdOut = Truncate(stdout, 1000),
                StdErr = Truncate(stderr, 1000)
            };
        }

        return new SystemRebootResult
        {
            Status = "reboot-queued",
            Message = $"System reboot queued in {delaySeconds}s.",
            DelaySeconds = delaySeconds,
            ExitCode = process.ExitCode,
            StdOut = Truncate(stdout, 1000),
            StdErr = Truncate(stderr, 1000)
        };
    }

    private static SystemRebootResult QueueUnix(int delaySeconds)
    {
        var command =
            "(sleep " + delaySeconds + "; " +
            "sync; " +
            "sudo -n reboot || " +
            "sudo -n /sbin/reboot || " +
            "/sbin/reboot || " +
            "reboot || " +
            "shutdown -r now) " +
            ">/tmp/accessapp-reboot.log 2>&1 &";

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = $"-lc \"{command}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            return new SystemRebootResult
            {
                Status = "failed",
                Message = "Could not start reboot shell command.",
                DelaySeconds = delaySeconds
            };
        }

        process.WaitForExit(5000);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0)
        {
            return new SystemRebootResult
            {
                Status = "failed",
                Message = $"Failed to queue reboot (exit {process.ExitCode}).",
                DelaySeconds = delaySeconds,
                ExitCode = process.ExitCode,
                StdOut = Truncate(stdout, 1000),
                StdErr = Truncate(stderr, 1000)
            };
        }

        return new SystemRebootResult
        {
            Status = "reboot-queued",
            Message = $"System reboot queued in {delaySeconds}s.",
            DelaySeconds = delaySeconds,
            ExitCode = process.ExitCode,
            StdOut = Truncate(stdout, 1000),
            StdErr = Truncate(stderr, 1000)
        };
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s.Substring(0, max);
    }
}

public sealed class SystemRebootResult
{
    public string Status { get; set; } = "failed";
    public string Message { get; set; } = "Unknown error";
    public int DelaySeconds { get; set; } = 2;
    public int? ExitCode { get; set; }
    public string? StdOut { get; set; }
    public string? StdErr { get; set; }
}
