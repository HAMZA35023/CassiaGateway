using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace AccessAppMqttWpf.ViewModels;

public partial class ShellTerminalViewModel : ObservableObject, IDisposable
{
    // Must match ShellExecToken in AccessApp MqttService.cs.
    private const string ShellSecret = "Cassia@Shell#2025";

    private readonly MainViewModel _main;
    private readonly string _cassiaName;

    private readonly List<string> _history = new();
    private int _historyIndex = -1; // -1 = not browsing

    [ObservableProperty] private string inputText = "";
    [ObservableProperty] private string outputText = "";

    public string WindowTitle => $"Local Terminal — {_cassiaName}";
    public string HeaderText  => $"Shell  ·  {_cassiaName}";

    // Raised whenever new output is appended; code-behind uses this to scroll to end.
    public event Action? OutputAppended;

    public ShellTerminalViewModel(MainViewModel main, string cassiaName)
    {
        _main = main;
        _cassiaName = cassiaName;
        _main.ShellResponseReceived += OnShellResponse;

        AppendLine($"# Terminal connected to '{_cassiaName}'");
        AppendLine("# Commands are executed on the remote device. 30 s timeout.");
        AppendLine("");
    }

    /// <summary>Navigate command history. direction: -1 = older (Up), +1 = newer (Down).</summary>
    public void NavigateHistory(int direction)
    {
        if (_history.Count == 0) return;

        if (_historyIndex == -1)
        {
            // Start browsing from the most recent entry.
            if (direction == -1) _historyIndex = _history.Count - 1;
            else return;
        }
        else
        {
            _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count - 1);
        }

        InputText = _history[_historyIndex];
    }

    [RelayCommand]
    private async Task SendCommandAsync()
    {
        var cmd = InputText.Trim();
        if (string.IsNullOrWhiteSpace(cmd)) return;

        _history.Add(cmd);
        _historyIndex = -1;

        InputText = "";
        AppendLine($"$ {cmd}");

        var ts   = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hmac = ComputeHmac(cmd, ts);

        await _main.SendShellCommandAsync(_cassiaName, new { cmd, ts, hmac }).ConfigureAwait(false);
    }

    private void OnShellResponse(string cassiaName, string payload)
    {
        if (!string.Equals(cassiaName, _cassiaName, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            var exitCode = root.TryGetProperty("exitCode", out var ec) ? ec.GetInt32() : -1;
            var stdout   = root.TryGetProperty("stdout",   out var so) ? so.GetString() ?? "" : "";
            var stderr   = root.TryGetProperty("stderr",   out var se) ? se.GetString() ?? "" : "";
            var timedOut = root.TryGetProperty("timedOut", out var to) && to.GetBoolean();
            var message  = root.TryGetProperty("message",  out var msg) ? msg.GetString() : null;

            if (!success && message != null)
            {
                AppendLine($"[error] {message}");
            }
            else
            {
                if (!string.IsNullOrEmpty(stdout)) AppendLine(stdout.TrimEnd());
                if (!string.IsNullOrEmpty(stderr)) AppendLine($"[stderr] {stderr.TrimEnd()}");
                if (timedOut)   AppendLine("[timed out after 30 s]");
                else if (exitCode != 0) AppendLine($"[exit {exitCode}]");
            }
        }
        catch
        {
            AppendLine(payload);
        }

        AppendLine("");
    }

    private void AppendLine(string text)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            OutputText += text + "\n";
            OutputAppended?.Invoke();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private string ComputeHmac(string cmd, long ts)
    {
        // Key = secret + cassiaName (device-specific; matches AccessApp ComputeShellHmac).
        var key  = Encoding.UTF8.GetBytes(ShellSecret + _cassiaName);
        var data = Encoding.UTF8.GetBytes(cmd + ts.ToString());
        return Convert.ToHexString(HMACSHA256.HashData(key, data)).ToLowerInvariant();
    }

    public void Dispose()
    {
        _main.ShellResponseReceived -= OnShellResponse;
    }
}
