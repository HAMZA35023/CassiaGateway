using AccessAppMqttWpf.ViewModels;
using System.Windows;

namespace AccessAppMqttWpf;

public partial class CassiaSettingsWindow : Window
{
    private readonly CassiaSettingsViewModel _vm;

    public CassiaSettingsWindow(MainViewModel main, string cassiaName)
    {
        InitializeComponent();
        _vm = new CassiaSettingsViewModel(main, cassiaName);
        DataContext = _vm;
        Title = _vm.WindowTitle;

        _vm.RequestClose += () =>
        {
            try { Close(); } catch { }
        };
    }
}
