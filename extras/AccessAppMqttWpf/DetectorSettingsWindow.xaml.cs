using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.ViewModels;
using System;
using System.Windows;

namespace AccessAppMqttWpf;

public partial class DetectorSettingsWindow : Window
{
    private readonly DetectorSettingsViewModel _vm;

    public DetectorSettingsWindow(MainViewModel main, DiscoveredDevice? device)
    {
        InitializeComponent();
        _vm = new DetectorSettingsViewModel(main, device);
        DataContext = _vm;
        Title = _vm.WindowTitle;

        _vm.RequestClose += OnRequestClose;
        Closed += (_, __) =>
        {
            _vm.RequestClose -= OnRequestClose;
        };
    }

    private void OnRequestClose()
    {
        try { Close(); } catch { }
    }
}
