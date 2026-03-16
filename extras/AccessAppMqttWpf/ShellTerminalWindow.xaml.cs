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
        switch (e.Key)
        {
            case Key.Enter:
                _vm.SendCommandCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                _vm.NavigateHistory(-1);
                // Move caret to end after history fills the TextBox.
                InputBox.CaretIndex = InputBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Down:
                _vm.NavigateHistory(+1);
                InputBox.CaretIndex = InputBox.Text.Length;
                e.Handled = true;
                break;
        }
    }
}
