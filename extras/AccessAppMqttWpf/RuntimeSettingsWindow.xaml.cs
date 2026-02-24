using AccessAppMqttWpf.ViewModels;
using System;
using System.Windows;

namespace AccessAppMqttWpf;

public partial class RuntimeSettingsWindow : Window
{
    private readonly RuntimeSettingsViewModel _vm;

    public RuntimeSettingsWindow(MainViewModel main, string targetCassia, string sourceCassia, bool applyToAll)
    {
        InitializeComponent();
        _vm = new RuntimeSettingsViewModel(main, targetCassia, sourceCassia, applyToAll);
        DataContext = _vm;
        Title = _vm.WindowTitle;

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
