using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.ViewModels;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Data;
using System.Windows.Input;

namespace AccessAppMqttWpf;

public partial class MainWindow : Window
{
    private bool _queueDefaultSortActive = true;
    private DispatcherTimer? _queueDefaultSortTimer;

    // Keep Upgrade-log expand/collapse state stable across collection refreshes
    private readonly Dictionary<string, bool> _upgradeLogExpandedState = new(StringComparer.OrdinalIgnoreCase);


    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        DataContext = new MainViewModel();

        Loaded += (_, _) => _queueDefaultSortActive = true;
        ApplyQueueDefaultSort();
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

        var device = (sender as FrameworkElement)?.DataContext as DiscoveredDevice;
        if (device == null)
            return;

        // Ensure selection matches the row button pressed
        vm.SelectedDevice = device;

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

    private static string? GetUpgradeLogGroupKey(object? dataContext)
    {
        if (dataContext == null) return null;

        // Expecting UpgradeLogGroup with properties: LogId, Cassia
        var t = dataContext.GetType();
        var logId = t.GetProperty("LogId")?.GetValue(dataContext)?.ToString() ?? "";
        var cassia = t.GetProperty("Cassia")?.GetValue(dataContext)?.ToString() ?? "";

        if (string.IsNullOrWhiteSpace(logId) && string.IsNullOrWhiteSpace(cassia))
            return null;

        return $"{cassia}|{logId}";
    }

    private void UpgradeLogExpander_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander ex) return;

        var key = GetUpgradeLogGroupKey(ex.DataContext);
        if (key == null) return;

        // Restore previously chosen state (default is collapsed)
        if (_upgradeLogExpandedState.TryGetValue(key, out var expanded))
            ex.IsExpanded = expanded;
    }

    private void UpgradeLogExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander ex) return;
        var key = GetUpgradeLogGroupKey(ex.DataContext);
        if (key == null) return;
        _upgradeLogExpandedState[key] = true;
    }

    private void UpgradeLogExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander ex) return;
        var key = GetUpgradeLogGroupKey(ex.DataContext);
        if (key == null) return;
        _upgradeLogExpandedState[key] = false;
    }


    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Auto-connect shortly after startup (lets UI render first)
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, __) =>
        {
            t.Stop();
            if (DataContext is not MainViewModel vm) return;
            if (vm.IsConnected) return;

            try
            {
                // CommunityToolkit generates ToggleConnectCommand from ToggleConnectAsync()
                if (vm.ToggleConnectCommand is ICommand cmd && cmd.CanExecute(null))
                    cmd.Execute(null);
            }
            catch { }
        };
        t.Start();

        // Re-apply default queue sort every 5 seconds while default sort is active
        _queueDefaultSortTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _queueDefaultSortTimer.Tick -= QueueDefaultSortTimer_Tick;
        _queueDefaultSortTimer.Tick += QueueDefaultSortTimer_Tick;
        _queueDefaultSortTimer.Start();

    }


    private void QueueDefaultSortTimer_Tick(object? sender, EventArgs e)
    {
        if (_queueDefaultSortActive)
            ApplyQueueDefaultSort();
    }

    private void DevicesContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            if (sender is not ContextMenu cm) return;

            var device = (cm.PlacementTarget as FrameworkElement)?.DataContext as DiscoveredDevice;
            var assignRoot = cm.Items.OfType<MenuItem>().FirstOrDefault(mi => (mi.Tag as string) == "AssignCassia");
            if (assignRoot == null) return;

            assignRoot.Items.Clear();

            if (device == null || device.CassiaRssi.Count == 0)
            {
                assignRoot.IsEnabled = false;
                return;
            }

            assignRoot.IsEnabled = true;

            foreach (var kv in device.CassiaRssi.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                var cassia = (kv.Key ?? "").Trim();
                if (string.IsNullOrWhiteSpace(cassia)) continue;

                var mi = new MenuItem
                {
                    Header = $"{cassia} ({kv.Value})",
                    IsCheckable = true,
                    IsChecked = cassia.Equals((device.AssignedCassia ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                };

                mi.Click += (_, __) => vm.AssignDeviceToCassia(device, cassia);
                assignRoot.Items.Add(mi);
            }
        }
        catch { }
    }

    // Build the device context menu right before it opens.
    // Using ContextMenuOpening on the row is more reliable than ContextMenu.Opened in the designer.
    private void DevicesRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        try
        {
            if (sender is not DataGridRow row) return;
            if (row.ContextMenu == null) return;
            DevicesContextMenu_Opened(row.ContextMenu, new RoutedEventArgs());
        }
        catch { }
    }

    // Same pattern as DevicesRow_ContextMenuOpening: avoids XAML designer errors when using ContextMenu.Opened.
    private void QueueRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        try
        {
            if (sender is not DataGridRow row) return;
            if (row.ContextMenu == null) return;
            QueueContextMenu_Opened(row.ContextMenu, new RoutedEventArgs());
        }
        catch { }
    }

    private void QueueContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            if (sender is not ContextMenu cm) return;

            var qi = (cm.PlacementTarget as FrameworkElement)?.DataContext as QueueItem;
            if (qi == null || string.IsNullOrWhiteSpace(qi.Mac)) return;

            // Rebuild menu each time to avoid duplicate click handlers.
            cm.Items.Clear();

            var remove = new MenuItem { Header = "Remove from queue" };
            remove.Click += async (_, __) =>
            {
                var nl = Environment.NewLine;
                var res = MessageBox.Show($"Remove {qi.Mac} from pending queue?{nl}{nl}(This does not stop active programming.)", "Remove from queue", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;

                // Prefer sending to the cassia the item is currently assigned to; fall back to all.
                var target = string.IsNullOrWhiteSpace(qi.Cassia) ? "all" : qi.Cassia;
                try
                {
                    qi.Status = "Remove requested";
                    await vm.RemoveFromQueueAsync(target, new[] { qi.Mac }).ConfigureAwait(false);
                }
                catch { }
            };
            cm.Items.Add(remove);

            var move = new MenuItem { Header = "Move to Cassia" };
            cm.Items.Add(move);

            // Only show Cassias that can see the device (RSSI known).
            var dev = vm.Devices.FirstOrDefault(d => d != null && qi.Mac.Equals((d.Mac ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (dev == null || dev.CassiaRssi.Count == 0)
            {
                move.IsEnabled = false;
                return;
            }

            foreach (var kv in dev.CassiaRssi.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                var cassia = (kv.Key ?? "").Trim();
                if (string.IsNullOrWhiteSpace(cassia)) continue;
                if (!string.IsNullOrWhiteSpace(qi.Cassia) && cassia.Equals(qi.Cassia, StringComparison.OrdinalIgnoreCase))
                    continue;

                var mi = new MenuItem { Header = $"{cassia} ({kv.Value})" };
                mi.Click += async (_, __) =>
                {
                    var nl = Environment.NewLine;
                    var res = MessageBox.Show(
                        $"Move {qi.Mac} from {qi.Cassia} to {cassia}?{nl}{nl}" +
                        $"Step 1: remove from pending queue{nl}" +
                        $"Step 2: add to queue on new Cassia{nl}{nl}" +
                        $"RSSI to {cassia}: {kv.Value}",
                        "Move queue item",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (res != MessageBoxResult.Yes) return;

                    try
                    {
                        await vm.MoveQueueItemToCassiaAsync(qi, cassia).ConfigureAwait(false);
                    }
                    catch { }
                };
                move.Items.Add(mi);
            }

            move.IsEnabled = move.Items.Count > 0;
        }
        catch { }
    }

}
