using AccessAppMqttWpf.ViewModels;
using System.Windows;

namespace AccessAppMqttWpf;

public partial class LedRangeVisualizerWindow : Window
{
    public LedRangeVisualizerWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
