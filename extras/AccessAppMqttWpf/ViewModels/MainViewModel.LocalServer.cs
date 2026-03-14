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
    private readonly LocalDiscoveryBeaconService _discoveryBeacon = new();
    private readonly AccessAppDiscoveryService _accessAppDiscovery = new();

    private Process? _accessAppProcess;

    // True while the MQTT client is connected to the local broker (runtime override, never persisted)
    private bool _localMqttActive;

    // Saved remote connection params — restored when switching back from local
    private string _savedRemoteNetworkId = "";
    private string _savedRemoteHost = "";
    private int _savedRemotePort = 0;

    [ObservableProperty] private bool isLocalServerRunning;
    [ObservableProperty] private bool isAccessAppRunning;
    [ObservableProperty] private bool isLocalMqttActive;
    [ObservableProperty] private string localServerStatus = "Stopped";
    [ObservableProperty] private string accessAppProcessStatus = "Not running";

    // Labels used in the main menu
    public string LocalServerToggleLabel => IsLocalServerRunning ? "Stop local server" : "Start local server";
    public string AccessAppToggleLabel => IsAccessAppRunning ? "Stop AccessApp" : "Start AccessApp";

    // Badge shown next to "Cassia gateways" heading
    public string MqttConnectionLabel => IsLocalMqttActive ? "LOCAL" : "REMOTE";

    // Connection subtitle shown below "Cassia gateways" heading
    public string ActiveConnectionLabel =>
        _localMqttActive && LocalMqttServer.IsRunning
            ? $"127.0.0.1:{LocalMqttServer.Port} · {NetworkId}"
            : $"{MqttHost} · {NetworkId}";
    public string MqttConnectionTooltip =>
        IsLocalServerRunning
            ? (IsLocalMqttActive
                ? "Connected to local MQTT — click to switch to remote"
                : "Connected to remote MQTT — click to switch to local")
            : "Remote MQTT — start local server to switch";

    partial void OnIsLocalServerRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(LocalServerToggleLabel));
        OnPropertyChanged(nameof(MqttConnectionTooltip));
    }

    partial void OnIsLocalMqttActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(MqttConnectionLabel));
        OnPropertyChanged(nameof(MqttConnectionTooltip));
        OnPropertyChanged(nameof(ActiveConnectionLabel));
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

            // Announce / stop announcing the local broker on the LAN
            if (running)
            {
                _discoveryBeacon.Start(LocalMqttServer.Port, NetworkId);
                _accessAppDiscovery.Start(LocalMqttServer.Port, NetworkId, LocalMqttServer);
            }
            else
            {
                _discoveryBeacon.Stop();
                _accessAppDiscovery.Stop();
            }

            // Switch the client's MQTT connection to/from localhost when the server starts/stops
            if (running)
                await SwitchMqttToLocalAsync();
            else
                await SwitchMqttToPublicAsync();
        };

        // Auto-start on startup (deferred after UI is up)
        _ = AutoStartLocalServicesAsync();
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
            await LaunchAccessAppWithUpdateCheckAsync(s.localServer);
    }

    /// <summary>
    /// Checks if a newer AccessApp version is available. Returns (hasUpdate, latestVersion).
    /// Returns (false, "") if no update or check failed.
    /// </summary>
    private async Task<(bool hasUpdate, string latest, string installed, string channel)> CheckForAccessAppUpdateAsync()
    {
        AppSettings s;
        try { s = _store.Load(); } catch { return (false, "", "", ""); }

        // Only meaningful when using the auto-download dir
        if (!string.IsNullOrWhiteSpace(s.localServer.localAccessAppPath)) return (false, "", "", "");

        var installed = AccessAppLauncherService.GetInstalledVersion();
        if (installed == "Not installed") return (false, "", "", "");

        try
        {
            var manifestUrl = $"{s.localServer.manifestBaseUrl.TrimEnd('/')}/{s.localServer.accessAppChannel}/manifest.json";
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await http.GetStringAsync(manifestUrl);
            var manifest = System.Text.Json.JsonDocument.Parse(json);
            var latest = manifest.RootElement.TryGetProperty("latest", out var l)
                && l.TryGetProperty("version", out var v)
                ? (v.GetString() ?? "") : "";

            if (string.IsNullOrWhiteSpace(latest) || latest == installed) return (false, "", "", "");
            return (true, latest, installed, s.localServer.accessAppChannel);
        }
        catch { return (false, "", "", ""); }
    }

    /// <summary>Reconnect the WPF MQTT client to the local broker. Stored host/port is NOT changed.</summary>
    private async Task SwitchMqttToLocalAsync()
    {
        // Read current values first (may be on background thread)
        string remoteHost = MqttHost;
        int remotePort = MqttPort;
        string remoteNetworkId = NetworkId;

        _savedRemoteNetworkId = remoteNetworkId;
        _savedRemoteHost = remoteHost;
        _savedRemotePort = remotePort;
        _localMqttActive = true;

        int localPort = LocalMqttServer.Port;
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsLocalMqttActive = true;
            MqttHost = "127.0.0.1";
            MqttPort = localPort;
            ConnectionStatus = $"Switching to local MQTT (127.0.0.1:{localPort})…";
        });
        try
        {
            // Treat broker switching as a manual handoff so delayed reconnect attempts
            // cannot revive the previous broker while we are changing targets.
            _manualDisconnectRequested = true;
            _autoReconnectCts?.Cancel();

            if (IsConnected)
            {
                await _mqtt.DisconnectAsync();
            }
            // ConnectWithEffectiveParamsAsync resets _manualDisconnectRequested = false
            await ConnectWithEffectiveParamsAsync(delayResyncMs: 1000);
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
        string restoreHost = _savedRemoteHost;
        int restorePort = _savedRemotePort;
        string restoreNetworkId = _savedRemoteNetworkId;

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrWhiteSpace(restoreNetworkId))
                NetworkId = restoreNetworkId;
            if (!string.IsNullOrWhiteSpace(restoreHost))
            {
                MqttHost = restoreHost;
                MqttPort = restorePort;
            }
            IsLocalMqttActive = false;
            ConnectionStatus = "Switching back to public MQTT…";
        });
        try
        {
            // Treat broker switching as a manual handoff so delayed reconnect attempts
            // cannot revive the previous broker while we are changing targets.
            _manualDisconnectRequested = true;
            _autoReconnectCts?.Cancel();

            if (IsConnected)
            {
                await _mqtt.DisconnectAsync();
            }
            await ConnectWithEffectiveParamsAsync(delayResyncMs: 1000);
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Switch to public MQTT failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Connect using either local or stored MQTT parameters, without touching stored properties.
    /// </summary>
    internal async Task ConnectWithEffectiveParamsAsync(int delayResyncMs = 0)
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
            user = "local";
            pass = LocalMqttServerService.LocalToken;
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

        if (delayResyncMs > 0)
            await Task.Delay(delayResyncMs, _appCts.Token).ConfigureAwait(false);

        await ResyncCoreAsync(true, clearUi: true).ConfigureAwait(false);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleMqttConnection()
    {
        if (!LocalMqttServer.IsRunning) return;
        try
        {
            if (_localMqttActive)
                await SwitchMqttToPublicAsync();
            else
                await SwitchMqttToLocalAsync();
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Switch MQTT connection failed: " + ex.Message;
        }
    }

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
                    await LaunchAccessAppWithUpdateCheckAsync(s);
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
            await LaunchAccessAppWithUpdateCheckAsync(s);
        }
        catch (Exception ex)
        {
            ConnectionStatus = "AccessApp toggle failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Checks for an available update, prompts the user if one exists, then either opens the
    /// settings window to download+launch or launches the current version directly.
    /// </summary>
    private async Task LaunchAccessAppWithUpdateCheckAsync(LocalServerSettings s)
    {
        var (hasUpdate, latest, installed, channel) = await CheckForAccessAppUpdateAsync();
        if (hasUpdate)
        {
            var answer = Application.Current.Dispatcher.Invoke(() =>
                MessageBox.Show(
                    $"A new AccessApp version is available.\n\nInstalled: {installed}\nLatest ({channel}): {latest}\n\nUpdate and then launch AccessApp?",
                    "AccessApp update available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information));

            if (answer == MessageBoxResult.Yes)
            {
                OpenLocalServerSettingsForUpdate();
                return;
            }
        }

        await LaunchAccessAppFromSettingsAsync(s);
    }

    private void OpenLocalServerSettingsForUpdate()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var wnd = new LocalServerSettingsWindow(this, autoDownloadAndLaunch: true)
            {
                Owner = Application.Current.MainWindow
            };
            wnd.Show();
            wnd.Activate();
        });
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

            var proc = AccessAppLauncherService.StartAccessApp(exe, LocalMqttServer.Port, NetworkId,
                cassia: Environment.MachineName.ToLower(),
                username: "local", password: LocalMqttServerService.LocalToken);
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

        try { _discoveryBeacon.Stop(); } catch { }
        try { _accessAppDiscovery.Stop(); } catch { }
        try { _ = LocalMqttServer.StopAsync(); } catch { }
    }
}
