using AccessAppMqttWpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public readonly LocalMqttServerService LocalMqttServer = new();

    private Process? _accessAppProcess;

    // True while the MQTT client is connected to the local broker (runtime override, never persisted)
    private bool _localMqttActive;

    [ObservableProperty] private bool isLocalServerRunning;
    [ObservableProperty] private bool isAccessAppRunning;
    [ObservableProperty] private string localServerStatus = "Stopped";
    [ObservableProperty] private string accessAppProcessStatus = "Not running";

    // Labels used in the main menu
    public string LocalServerToggleLabel => IsLocalServerRunning ? "Stop local server" : "Start local server";
    public string AccessAppToggleLabel => IsAccessAppRunning ? "Stop AccessApp" : "Start AccessApp";

    partial void OnIsLocalServerRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(LocalServerToggleLabel));
    }

    partial void OnIsAccessAppRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(AccessAppToggleLabel));
    }

    public void InitLocalServer()
    {
        LocalMqttServer.StatusChanged += async (running, msg) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsLocalServerRunning = running;
                LocalServerStatus = msg;
            });

            // Switch the client's MQTT connection to/from localhost when the server starts/stops
            if (running)
                await SwitchMqttToLocalAsync();
            else
                await SwitchMqttToPublicAsync();
        };

        // Auto-start on startup (deferred after UI is up)
        _ = AutoStartLocalServicesAsync();

        // Startup version check (deferred)
        _ = StartupVersionCheckAsync();
    }

    private async Task AutoStartLocalServicesAsync()
    {
        // Short delay so the UI is fully rendered first
        await Task.Delay(1500);

        AppSettings s;
        try { s = _store.Load(); } catch { return; }

        if (!s.localServer.autoStartLocalServer) return;

        try
        {
            await LocalMqttServer.StartAsync(s.localServer.mqttPort);
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
                ConnectionStatus = "Auto-start local MQTT failed: " + ex.Message);
            return;
        }

        if (s.localServer.autoStartAccessApp)
            await LaunchAccessAppFromSettingsAsync(s.localServer);
    }

    private async Task StartupVersionCheckAsync()
    {
        // Delay so we don't block startup
        await Task.Delay(4000);

        AppSettings s;
        try { s = _store.Load(); } catch { return; }

        // Only check when using the auto-download dir (not a local path override)
        if (!string.IsNullOrWhiteSpace(s.localServer.localAccessAppPath)) return;

        var installed = AccessAppLauncherService.GetInstalledVersion();
        if (installed == "Not installed") return;

        try
        {
            var manifestUrl = $"{s.localServer.manifestBaseUrl.TrimEnd('/')}/{s.localServer.accessAppChannel}/manifest.json";
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await http.GetStringAsync(manifestUrl);
            var manifest = System.Text.Json.JsonDocument.Parse(json);
            var latest = manifest.RootElement.TryGetProperty("latest", out var l)
                && l.TryGetProperty("version", out var v)
                ? (v.GetString() ?? "") : "";

            if (string.IsNullOrWhiteSpace(latest) || latest == installed) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                var result = MessageBox.Show(
                    $"A new AccessApp version is available.\n\nInstalled: {installed}\nLatest ({s.localServer.accessAppChannel}): {latest}\n\nOpen Local Server Settings to update?",
                    "AccessApp update available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                    OpenLocalServerSettingsCommand.Execute(null);
            });
        }
        catch { /* best-effort */ }
    }

    /// <summary>Reconnect the WPF MQTT client to the local broker. Stored host/port is NOT changed.</summary>
    private async Task SwitchMqttToLocalAsync()
    {
        _localMqttActive = true;
        ConnectionStatus = $"Switching to local MQTT (127.0.0.1:{LocalMqttServer.Port})…";
        try
        {
            if (IsConnected)
            {
                _manualDisconnectRequested = false;
                await _mqtt.DisconnectAsync();
            }
            await ConnectWithEffectiveParamsAsync();
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Switch to local MQTT failed: " + ex.Message;
        }
    }

    /// <summary>Reconnect the WPF MQTT client back to the public/stored broker.</summary>
    private async Task SwitchMqttToPublicAsync()
    {
        _localMqttActive = false;
        ConnectionStatus = "Switching back to public MQTT…";
        try
        {
            if (IsConnected)
            {
                _manualDisconnectRequested = false;
                await _mqtt.DisconnectAsync();
            }
            await ConnectWithEffectiveParamsAsync();
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Switch to public MQTT failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Connect using either local or stored MQTT parameters, without touching stored properties.
    /// </summary>
    internal async Task ConnectWithEffectiveParamsAsync()
    {
        string host;
        int port;
        string user;
        string pass;
        bool tls;
        bool ignoreTls;

        if (_localMqttActive && LocalMqttServer.IsRunning)
        {
            host = "127.0.0.1";
            port = LocalMqttServer.Port;
            user = "";
            pass = "";
            tls = false;
            ignoreTls = false;
        }
        else
        {
            host = MqttHost;
            port = MqttPort;
            user = MqttUser;
            pass = MqttPassword ?? "";
            tls = UseTls;
            ignoreTls = IgnoreTlsErrors;
        }

        _manualDisconnectRequested = false;
        _autoReconnectEnabled = true;
        _isConnecting = true;
        try
        {
            await _mqtt.ConnectAsync(host, port, user, pass, tls, ignoreTls, MqttTopic, _appCts.Token);
        }
        finally
        {
            _isConnecting = false;
        }

        await ResyncCoreAsync(false, clearUi: false).ConfigureAwait(false);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleLocalServer()
    {
        try
        {
            if (LocalMqttServer.IsRunning)
            {
                await LocalMqttServer.StopAsync();
            }
            else
            {
                var s = _store.Load().localServer;
                await LocalMqttServer.StartAsync(s.mqttPort);

                if (s.autoStartAccessApp)
                    await LaunchAccessAppFromSettingsAsync(s);
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Local server toggle failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleAccessApp()
    {
        try
        {
            if (_accessAppProcess != null && !_accessAppProcess.HasExited)
            {
                _accessAppProcess.Kill(entireProcessTree: true);
                return;
            }

            if (!LocalMqttServer.IsRunning)
            {
                ConnectionStatus = "Start the local MQTT server before launching AccessApp.";
                return;
            }

            var s = _store.Load().localServer;
            await LaunchAccessAppFromSettingsAsync(s);
        }
        catch (Exception ex)
        {
            ConnectionStatus = "AccessApp toggle failed: " + ex.Message;
        }
    }

    private Task LaunchAccessAppFromSettingsAsync(LocalServerSettings s)
    {
        return Task.Run(() =>
        {
            var localPath = string.IsNullOrWhiteSpace(s.localAccessAppPath) ? null : s.localAccessAppPath;
            var exe = AccessAppLauncherService.FindExecutable(localPath);
            if (exe == null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                    ConnectionStatus = "AccessApp executable not found. Use Local Server Settings to download it.");
                return;
            }

            var proc = AccessAppLauncherService.StartAccessApp(exe, LocalMqttServer.Port, NetworkId);
            SetAccessAppProcess(proc);

            Application.Current.Dispatcher.Invoke(() =>
                ConnectionStatus = proc != null
                    ? $"AccessApp started (PID {proc.Id})."
                    : "Failed to start AccessApp.");
        });
    }

    [RelayCommand]
    private void OpenLocalServerSettings()
    {
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var wnd = new LocalServerSettingsWindow(this)
                {
                    Owner = Application.Current.MainWindow
                };
                wnd.Show();
                wnd.Activate();
            });
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Open local server settings failed: " + ex.Message;
        }
    }

    public void SetAccessAppProcess(Process? proc)
    {
        _accessAppProcess = proc;
        RefreshAccessAppStatus();

        if (proc != null)
        {
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                _accessAppProcess = null;
                Application.Current.Dispatcher.Invoke(RefreshAccessAppStatus);
            };
        }
    }

    public Process? GetAccessAppProcess() => _accessAppProcess;

    private void RefreshAccessAppStatus()
    {
        var running = _accessAppProcess != null && !_accessAppProcess.HasExited;
        IsAccessAppRunning = running;
        AccessAppProcessStatus = running ? $"Running (PID {_accessAppProcess!.Id})" : "Not running";
    }

    public void ShutdownLocalServices()
    {
        try
        {
            if (_accessAppProcess != null && !_accessAppProcess.HasExited)
                _accessAppProcess.Kill(entireProcessTree: true);
        }
        catch { }

        try { _ = LocalMqttServer.StopAsync(); } catch { }
    }
}
