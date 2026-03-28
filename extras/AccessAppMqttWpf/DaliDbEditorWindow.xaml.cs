using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.ViewModels;
using System.Windows;

namespace AccessAppMqttWpf;

public partial class DaliDbEditorWindow : Window
{
    private readonly DaliDbEditorViewModel _vm;

    public DaliDbEditorWindow(MainViewModel main, DiscoveredDevice device, DaliDbSnapshotVm snapshot)
    {
        InitializeComponent();
        _vm = new DaliDbEditorViewModel(main, device, snapshot);
        _vm.RequestClose += () => Close();
        DataContext = _vm;

        Closing += (_, _) =>
        {
            try { _vm.Dispose(); } catch { }
        };
    }
}
