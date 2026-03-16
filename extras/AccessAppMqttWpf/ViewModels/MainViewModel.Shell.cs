using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel
{
    /// <summary>Fired when a tele/shell response arrives. Args: (cassiaName, payload).</summary>
    public event Action<string, string>? ShellResponseReceived;

    internal void RaiseShellResponse(string cassiaName, string payload)
        => ShellResponseReceived?.Invoke(cassiaName, payload);

    internal Task SendShellCommandAsync(string cassiaName, object payload)
    {
        var topic = BuildCmdTopic(cassiaName, "exec-shell");
        return _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token);
    }

    [RelayCommand]
    private void OpenShellTerminal(string cassiaName)
    {
        cassiaName = (cassiaName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassiaName)) return;

        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var wnd = new ShellTerminalWindow(this, cassiaName)
                {
                    Owner = Application.Current.MainWindow
                };
                wnd.Show();
                wnd.Activate();
            });
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Open shell terminal failed: " + ex.Message;
        }
    }
}
