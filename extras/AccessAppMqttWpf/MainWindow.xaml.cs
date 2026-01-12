using System;
using System.Collections;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AccessAppMqttWpf.ViewModels;

namespace AccessAppMqttWpf;

public partial class MainWindow : Window
{
    private bool _queueDefaultSortActive = true;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        Loaded += (_, _) => ApplyQueueDefaultSort();
    }

    private void DevicesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only queue on double click when explicitly enabled
        if (EnableDoubleClickQueueCheckBox?.IsChecked != true)
            return;

        if (DataContext is MainViewModel vm)
            ExecuteQueueSingle(vm, vm.SelectedDevice);
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is PasswordBox pb)
            vm.MqttPassword = pb.Password;
    }

    private void ClearDevices_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        // Prefer a command if it exists
        if (TryExecuteCommand(vm, "ClearDevicesCommand", null))
            return;

        // Try common method names
        if (TryInvokeMethod(vm, "ClearDevices") || TryInvokeMethod(vm, "ClearAllDevices"))
            return;

        // Last resort: try to clear a public collection property
        var devicesProp = vm.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => typeof(IList).IsAssignableFrom(p.PropertyType)
                              || (p.PropertyType.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType)));

        var val = devicesProp?.GetValue(vm);
        if (val is IList list)
            list.Clear();
        else
        {
            // Try ObservableCollection<T>.Clear via reflection
            var clear = val?.GetType().GetMethod("Clear", Type.EmptyTypes);
            clear?.Invoke(val, null);
        }
    }

    private void GetFw_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var device = (sender as FrameworkElement)?.DataContext;

        // Execute if VM has a compatible command
        if (TryExecuteCommand(vm, "GetCurrentFirmwareCommand", device)) return;
        if (TryExecuteCommand(vm, "GetFirmwareCommand", device)) return;
        if (TryExecuteCommand(vm, "GetFwCommand", device)) return;
    }

    private void UpdateFw_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var device = (sender as FrameworkElement)?.DataContext;

        // Ensure selection matches the row button pressed
        vm.SelectedDevice = device as dynamic;

        ExecuteQueueSingle(vm, device);
    }

    private void QueueDefaultSort_Click(object sender, RoutedEventArgs e)
    {
        ApplyQueueDefaultSort();

        // Clear column sort glyphs
        if (QueueGrid != null)
        {
            foreach (var c in QueueGrid.Columns)
                c.SortDirection = null;
        }
    }

    private void QueueGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // User started a manual sort: disable default custom sort so DataGrid sorting works normally
        _queueDefaultSortActive = false;

        if (DataContext is MainViewModel vm)
        {
            if (vm.QueueView is ListCollectionView lcv)
                lcv.CustomSort = null;
        }
    }

    private void ApplyQueueDefaultSort()
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (vm.QueueView is ListCollectionView lcv)
        {
            lcv.CustomSort = new QueueStatusComparer();
            _queueDefaultSortActive = true;
        }
    }

    private static void ExecuteQueueSingle(MainViewModel vm, object? device)
    {
        // Prefer passing the row item if the command supports it; else fall back to null (SelectedDevice already set)
        if (vm.QueueSingleCommand is ICommand cmd)
        {
            if (cmd.CanExecute(device))
                cmd.Execute(device);
            else if (cmd.CanExecute(null))
                cmd.Execute(null);
        }
    }

    private static bool TryExecuteCommand(object target, string commandPropertyName, object? parameter)
    {
        var prop = target.GetType().GetProperty(commandPropertyName, BindingFlags.Public | BindingFlags.Instance);
        if (prop?.GetValue(target) is ICommand cmd)
        {
            if (cmd.CanExecute(parameter))
            {
                cmd.Execute(parameter);
                return true;
            }
            if (cmd.CanExecute(null))
            {
                cmd.Execute(null);
                return true;
            }
        }
        return false;
    }

    private static bool TryInvokeMethod(object target, string methodName)
    {
        var mi = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        if (mi == null) return false;
        if (mi.GetParameters().Length != 0) return false;
        mi.Invoke(target, null);
        return true;
    }

    private sealed class QueueStatusComparer : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var sx = GetRank(GetStatus(x));
            var sy = GetRank(GetStatus(y));
            var r = sx.CompareTo(sy);
            if (r != 0) return r;

            // Stable-ish: tie-break by MAC if present
            var mx = GetMac(x);
            var my = GetMac(y);
            return string.Compare(mx, my, StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetStatus(object? o) => o?.GetType().GetProperty("Status")?.GetValue(o)?.ToString();
        private static string? GetMac(object? o) => o?.GetType().GetProperty("Mac")?.GetValue(o)?.ToString();

        private static int GetRank(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return 2;

            var s = status.Trim().ToLowerInvariant();

            // top
            if (s.Contains("program")) return 0;

            // middle
            if (s.Contains("queue")) return 1;
            if (s.Contains("requested")) return 1;
            if (s.Contains("upload") || s.Contains("updat")) return 1;

            // bottom: done
            if (s.Contains("done") || s.Contains("complete") || s.Contains("success")) return 9;

            // everything else between queued and done
            return 5;
        }
    }

    private void ClearUpgradeLogCommand(object sender, RoutedEventArgs e)
    {

    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        ClearUpgradeLogCommand(sender, e);
    }
}
