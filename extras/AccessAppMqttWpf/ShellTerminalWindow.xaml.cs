using AccessAppMqttWpf.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace AccessAppMqttWpf;

public partial class ShellTerminalWindow : Window
{
    private readonly ShellTerminalViewModel _vm;

    public ShellTerminalWindow(MainViewModel main, string cassiaName)
    {
        InitializeComponent();
        _vm = new ShellTerminalViewModel(main, cassiaName);
        DataContext = _vm;
        Title = _vm.WindowTitle;

        // Scroll to end whenever output is appended.
        _vm.OutputAppended += () => OutputBox.ScrollToEnd();

        Closed += (_, __) =>
        {
            try { _vm.Dispose(); } catch { }
        };

        Loaded += (_, __) => InputBox.Focus();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.SendCommandCommand.Execute(null);
            e.Handled = true;
        }
    }
}
