using AccessAppMqttWpf.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace AccessAppMqttWpf;

public partial class LocalServerSettingsWindow : Window
{
    private readonly LocalServerSettingsViewModel _vm;

    public LocalServerSettingsWindow(MainViewModel main)
    {
        InitializeComponent();
        _vm = new LocalServerSettingsViewModel(main);
        DataContext = _vm;

        Closed += (_, _) =>
        {
            try { _vm.Dispose(); } catch { }
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void LocalPathBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _vm.BrowseLocalPathCommand.Execute(null);
        e.Handled = true;
    }
}
