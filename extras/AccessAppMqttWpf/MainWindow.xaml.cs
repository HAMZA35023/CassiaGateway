using System.Windows;
using System.Windows.Controls;
using AccessAppMqttWpf.ViewModels;

namespace AccessAppMqttWpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new AccessAppMqttWpf.ViewModels.MainViewModel();
    }

    private void DevicesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.QueueSingleCommand.Execute(null);
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is PasswordBox pb)
            vm.MqttPassword = pb.Password;
    }


}
