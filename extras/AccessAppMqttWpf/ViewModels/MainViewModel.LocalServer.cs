using AccessAppMqttWpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.Windows;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public readonly LocalMqttServerService LocalMqttServer = new();

    private Process? _accessAppProcess;

    [ObservableProperty] private bool isLocalServerRunning;
    [ObservableProperty] private bool isAccessAppRunning;
    [ObservableProperty] private string localServerStatus = "Stopped";
    [ObservableProperty] private string accessAppProcessStatus = "Not running";

    public void InitLocalServer()
    {
        LocalMqttServer.StatusChanged += (running, msg) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsLocalServerRunning = running;
                LocalServerStatus = msg;
            });
        };
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
