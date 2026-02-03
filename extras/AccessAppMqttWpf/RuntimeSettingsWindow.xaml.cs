using AccessAppMqttWpf.ViewModels;
using System;
using System.Windows;

namespace AccessAppMqttWpf;

public partial class RuntimeSettingsWindow : Window
{
    private readonly RuntimeSettingsViewModel _vm;

    public RuntimeSettingsWindow(MainViewModel main, string cassiaName)
    {
        InitializeComponent();
        _vm = new RuntimeSettingsViewModel(main, cassiaName);
        DataContext = _vm;
        Title = $"Runtime Variables - {cassiaName}";

        _vm.RequestClose += () =>
        {
            try { Close(); } catch { }
        };

        Closed += (_, __) =>
        {
            try { _vm.Dispose(); } catch { }
        };
    }
}
