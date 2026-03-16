using AccessAppMqttWpf.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace AccessAppMqttWpf;

public partial class LocalServerSettingsWindow : Window
{
    private readonly LocalServerSettingsViewModel _vm;

    public LocalServerSettingsWindow(MainViewModel main, bool autoDownloadAndLaunch = false)
    {
        InitializeComponent();
        _vm = new LocalServerSettingsViewModel(main, autoDownloadAndLaunch);
        DataContext = _vm;

        _vm.RequestClose += Close;
        Closed += (_, _) => { try { _vm.Dispose(); } catch { } };

        if (autoDownloadAndLaunch)
            Loaded += (_, _) => _vm.DownloadUpdateCommand.Execute(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ClearGatewayIps_Click(object sender, RoutedEventArgs e) => _vm.GatewayIpsText = "";

    private void LocalPathBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _vm.BrowseLocalPathCommand.Execute(null);
        e.Handled = true;
    }
}
