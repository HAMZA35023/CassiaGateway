using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.ViewModels;
using System;
using System.Windows;

namespace AccessAppMqttWpf;

public partial class WriteReadWindow : Window
{
    private readonly WriteReadViewModel _vm;

    public WriteReadWindow(MainViewModel main, DiscoveredDevice device)
    {
        InitializeComponent();
        _vm = new WriteReadViewModel(main, device);
        DataContext = _vm;
        Title = _vm.Title;

        Closing += (_, _) =>
        {
            try { _vm.Dispose(); } catch { }
        };
    }
}
