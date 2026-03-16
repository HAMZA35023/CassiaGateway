using AccessAppMqttWpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AccessAppMqttWpf.ViewModels;

public partial class LocalServerSettingsViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly SettingsStore _store = new();
    private readonly bool _launchAfterDownload;
    private CancellationTokenSource? _downloadCts;

    /// <summary>Raised when the VM wants to close its window (e.g. after download+launch).</summary>
    public event Action? RequestClose;

    // ── Settings ─────────────────────────────────────────────────────────────

    [ObservableProperty] private int mqttPort = 1883;
    [ObservableProperty] private string accessAppChannel = "develop";
    [ObservableProperty] private string manifestBaseUrl = "https://prod.statistics.niko-test.nu/accessapp";
    [ObservableProperty] private string selectedRuntime = "windows-x64";
    [ObservableProperty] private string localAccessAppPath = "";
    [ObservableProperty] private bool autoStartLocalServer;
    [ObservableProperty] private bool autoStartAccessApp;
    [ObservableProperty] private bool useSharedNetworkId = true;
    /// <summary>Comma-separated list of known Cassia WAN IPs for unicast discovery.</summary>
    [ObservableProperty] private string gatewayIpsText = "";
    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private double scanProgress;

    public string[] RuntimeOptions { get; } = new[]
    {
        "windows-x64", "windows-x86", "linux-64", "linux-arm"
    };

    // ── Status ────────────────────────────────────────────────────────────────

    [ObservableProperty] private string installedVersion = "Not installed";
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private bool isDownloading;
    [ObservableProperty] private double downloadProgress;

    public bool IsLocalServerRunning => _main.IsLocalServerRunning;
    public string LocalServerStatus => _main.LocalServerStatus;
    public bool IsAccessAppRunning => _main.IsAccessAppRunning;
    public string AccessAppProcessStatus => _main.AccessAppProcessStatus;

    public string StartStopServerLabel => IsLocalServerRunning ? "Stop server" : "Start server";
    public string StartStopAccessAppLabel => IsAccessAppRunning ? "Stop AccessApp" : "Start AccessApp";

    // ── Constructor ───────────────────────────────────────────────────────────

    public LocalServerSettingsViewModel(MainViewModel main, bool launchAfterDownload = false)
    {
        _main = main;
        _launchAfterDownload = launchAfterDownload;

        var s = _store.Load().localServer;
        MqttPort = s.mqttPort;
        AccessAppChannel = s.accessAppChannel;
        ManifestBaseUrl = s.manifestBaseUrl;
        SelectedRuntime = s.accessAppRuntime;
        LocalAccessAppPath = s.localAccessAppPath;
        AutoStartLocalServer = s.autoStartLocalServer;
        AutoStartAccessApp = s.autoStartAccessApp;
        UseSharedNetworkId = s.useSharedNetworkId;
        GatewayIpsText = string.Join(", ", s.gatewayIps);

        RefreshInstalledVersion();

        _main.PropertyChanged += OnMainPropertyChanged;
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsLocalServerRunning))
        {
            OnPropertyChanged(nameof(IsLocalServerRunning));
            OnPropertyChanged(nameof(LocalServerStatus));
            OnPropertyChanged(nameof(StartStopServerLabel));
        }
        if (e.PropertyName == nameof(MainViewModel.IsAccessAppRunning))
        {
            OnPropertyChanged(nameof(IsAccessAppRunning));
            OnPropertyChanged(nameof(AccessAppProcessStatus));
            OnPropertyChanged(nameof(StartStopAccessAppLabel));
        }
        if (e.PropertyName == nameof(MainViewModel.LocalServerStatus))
            OnPropertyChanged(nameof(LocalServerStatus));
        if (e.PropertyName == nameof(MainViewModel.AccessAppProcessStatus))
            OnPropertyChanged(nameof(AccessAppProcessStatus));
    }

    private void RefreshInstalledVersion()
    {
        var localPath = string.IsNullOrWhiteSpace(LocalAccessAppPath) ? null : LocalAccessAppPath;
        InstalledVersion = AccessAppLauncherService.GetInstalledVersion(localPath);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleServerAsync()
    {
        try
        {
            if (_main.IsLocalServerRunning)
            {
                await _main.LocalMqttServer.StopAsync();
                StatusText = "Local MQTT server stopped.";
            }
            else
            {
                await _main.LocalMqttServer.StartAsync(MqttPort);
                StatusText = $"Local MQTT server started on port {MqttPort}.";

                if (AutoStartAccessApp)
                    await StartAccessAppInternalAsync();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ToggleAccessAppAsync()
    {
        if (IsAccessAppRunning)
        {
            try
            {
                var proc = _main.GetAccessAppProcess();
                if (proc != null && !proc.HasExited)
                    proc.Kill(entireProcessTree: true);
                StatusText = "AccessApp stopped.";
            }
            catch (Exception ex)
            {
                StatusText = $"Stop failed: {ex.Message}";
            }
        }
        else
        {
            await StartAccessAppInternalAsync();
        }
    }

    private async Task StartAccessAppInternalAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var localPath = string.IsNullOrWhiteSpace(LocalAccessAppPath) ? null : LocalAccessAppPath;
                var exe = AccessAppLauncherService.FindExecutable(localPath);
                if (exe == null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                        StatusText = "AccessApp executable not found. Download it first or set a local path.");
                    return;
                }

                var proc = AccessAppLauncherService.StartAccessApp(exe, _main.LocalMqttServer.Port, _main.NetworkId,
                    cassia: Environment.MachineName.ToLower(),
                    username: "local", password: LocalMqttServerService.LocalToken);
                _main.SetAccessAppProcess(proc);

                Application.Current.Dispatcher.Invoke(() =>
                    StatusText = proc != null
                        ? $"AccessApp started (PID {proc.Id})."
                        : "Failed to start AccessApp.");
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                    StatusText = $"Start AccessApp failed: {ex.Message}");
            }
        });
    }

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (IsDownloading)
        {
            _downloadCts?.Cancel();
            return;
        }

        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        DownloadProgress = 0;
        StatusText = "Downloading…";

        try
        {
            var progress = new Progress<double>(p =>
            {
                Application.Current.Dispatcher.Invoke(() => DownloadProgress = p);
            });

            var (success, message, version) = await AccessAppLauncherService.DownloadAndExtractAsync(
                ManifestBaseUrl, AccessAppChannel, SelectedRuntime, progress, _downloadCts.Token);

            StatusText = message;
            if (success)
            {
                RefreshInstalledVersion();

                if (_launchAfterDownload)
                {
                    await StartAccessAppInternalAsync();

                    var close = Application.Current.Dispatcher.Invoke(() =>
                        MessageBox.Show(
                            "AccessApp has been updated and launched.\n\nClose this settings window?",
                            "AccessApp started",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question));

                    if (close == MessageBoxResult.Yes)
                        Application.Current.Dispatcher.Invoke(() => RequestClose?.Invoke());
                }
            }
        }
        finally
        {
            IsDownloading = false;
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private void BrowseLocalPath()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select AccessAPP executable",
            Filter = "Executable|AccessAPP.exe;AccessAPP|All files|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            LocalAccessAppPath = System.IO.Path.GetDirectoryName(dlg.FileName) ?? "";
            RefreshInstalledVersion();
        }
    }

    [RelayCommand]
    private void ClearLocalPath()
    {
        LocalAccessAppPath = "";
        RefreshInstalledVersion();
    }

    [RelayCommand]
    private void SaveSettings()
    {
        var all = _store.Load();
        all.localServer.mqttPort = MqttPort;
        all.localServer.accessAppChannel = AccessAppChannel;
        all.localServer.manifestBaseUrl = ManifestBaseUrl;
        all.localServer.accessAppRuntime = SelectedRuntime;
        all.localServer.localAccessAppPath = LocalAccessAppPath;
        all.localServer.autoStartLocalServer = AutoStartLocalServer;
        all.localServer.autoStartAccessApp = AutoStartAccessApp;
        all.localServer.useSharedNetworkId = UseSharedNetworkId;
        all.localServer.gatewayIps = ParseGatewayIps(GatewayIpsText);
        _store.Save(all);
        StatusText = "Settings saved.";
    }

    [RelayCommand]
    private async Task ScanForGatewaysAsync()
    {
        if (IsScanning) return;
        IsScanning    = true;
        ScanProgress  = 0;
        StatusText    = "Scanning subnet for AccessApp instances…";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var progress  = new Progress<(int done, int total)>(p =>
                Application.Current.Dispatcher.Invoke(() =>
                    ScanProgress = p.total > 0 ? (double)p.done / p.total : 0));

            var found = await GatewayScannerService.ScanAsync(progress, cts.Token);

            if (found.Count == 0)
            {
                StatusText = "Scan complete — no AccessApp instances found.";
                return;
            }

            // Merge into existing IPs
            var existing = ParseGatewayIps(GatewayIpsText);
            foreach (var ip in found)
                if (!existing.Contains(ip, StringComparer.OrdinalIgnoreCase))
                    existing.Add(ip);

            GatewayIpsText = string.Join(", ", existing);
            StatusText = $"Scan complete — found: {string.Join(", ", found)}";

            // Auto-save
            SaveSettings();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan timed out.";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private static List<string> ParseGatewayIps(string text) =>
        new(text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0));

    public void Dispose()
    {
        _main.PropertyChanged -= OnMainPropertyChanged;
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
    }
}
