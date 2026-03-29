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
    private DispatcherTimer? _selectionClearTimer;
    private static readonly TimeSpan SelectionHighlightTimeout = TimeSpan.FromSeconds(10);
    private DateTimeOffset _devicesSelectionLastChangedUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _queueSelectionLastChangedUtc = DateTimeOffset.MinValue;

    // Keep Upgrade-log expand/collapse state stable across collection refreshes
    private readonly Dictionary<string, bool> _upgradeLogExpandedState = new(StringComparer.OrdinalIgnoreCase);


    public MainWindow()
    {
        InitializeComponent();
        Title = $"AC Controller for AccessAPP - v{AppInfo.AppVersion}";
        Loaded += MainWindow_Loaded;
        DataContext = new MainViewModel();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        Closing += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.ShutdownLocalServices();
        };

        Loaded += (_, _) => _queueDefaultSortActive = true;
        ApplyQueueDefaultSort();

        _selectionClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _selectionClearTimer.Tick += (_, _) => ClearExpiredGridSelections();
        _selectionClearTimer.Start();
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        ClearExpiredGridSelections();
    }

    private void CassiaTile_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.DeveloperModeUnlocked) return;
        e.Handled = true;
    }

    private void DevicesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItems?.Count > 0 || grid.SelectedItem != null)
            _devicesSelectionLastChangedUtc = DateTimeOffset.UtcNow;
    }

    private void QueueGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItems?.Count > 0 || grid.SelectedItem != null)
            _queueSelectionLastChangedUtc = DateTimeOffset.UtcNow;
    }

    private void ClearExpiredGridSelections()
    {
        var now = DateTimeOffset.UtcNow;

        if (_devicesSelectionLastChangedUtc != DateTimeOffset.MinValue
            && now - _devicesSelectionLastChangedUtc >= SelectionHighlightTimeout)
        {
            ClearDataGridSelection(DevicesGrid);
            _devicesSelectionLastChangedUtc = DateTimeOffset.MinValue;
        }

        if (_queueSelectionLastChangedUtc != DateTimeOffset.MinValue
            && now - _queueSelectionLastChangedUtc >= SelectionHighlightTimeout)
        {
            ClearDataGridSelection(QueueGridInline);
            ClearDataGridSelection(QueueGridHostInline);
            ClearDataGridSelection(QueueGrid);
            _queueSelectionLastChangedUtc = DateTimeOffset.MinValue;
        }
    }

    private static void ClearDataGridSelection(DataGrid? grid)
    {
        if (grid == null) return;
        grid.SelectedItem = null;
        grid.SelectedItems?.Clear();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.D) return;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;

        if (DataContext is not MainViewModel vm) return;
        if (vm.DeveloperModeUnlocked) return;

        vm.DeveloperModeUnlocked = true;
        vm.ConnectionStatus = "Developer actions unlocked.";
        e.Handled = true;
    }

    private void ThemeLight_Click(object sender, RoutedEventArgs e)
    {
        App.ApplyTheme("Light");
        UpdateThemeMenuChecks();
    }

    private void ThemeDark_Click(object sender, RoutedEventArgs e)
    {
        App.ApplyTheme("Dark");
        UpdateThemeMenuChecks();
    }

    private void UpdateThemeMenuChecks()
    {
        var isDark = string.Equals(App.CurrentTheme, "Dark", StringComparison.OrdinalIgnoreCase);
        if (ThemeLightMenuItem != null) ThemeLightMenuItem.IsChecked = !isDark;
        if (ThemeDarkMenuItem != null) ThemeDarkMenuItem.IsChecked = isDark;
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is PasswordBox pb)
            vm.MqttPassword = pb.Password;
    }

    private void DetectorSettingsProfileTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not FrameworkElement fe)
            return;

        var model = (fe.Tag as string ?? "").Trim();
        if (string.IsNullOrWhiteSpace(model))
            return;

        if (vm.BrowseDetectorSettingsProfileCommand.CanExecute(model))
            vm.BrowseDetectorSettingsProfileCommand.Execute(model);

        e.Handled = true;
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
        UpdateThemeMenuChecks();

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

        // Build dynamic columns for the Host BLE grid (one RSSI column per Cassia)
        try
        {
            if (DataContext is MainViewModel vm)
            {
                BuildHostBleGridColumns(vm);
                // Allow VM commands (e.g., Update) to request clearing Host BLE selection.
                vm.RequestClearHostBleSelection -= ClearHostBleSelectionFromVm;
                vm.RequestClearHostBleSelection += ClearHostBleSelectionFromVm;
                vm.CassiaGateways.CollectionChanged += (_, __) => BuildHostBleGridColumns(vm);
                vm.PropertyChanged += (_, args) =>
                {
                    if (string.Equals(args.PropertyName, nameof(MainViewModel.HostRssiAverageSeconds), StringComparison.OrdinalIgnoreCase))
                        BuildHostBleGridColumns(vm);
                };
            }
        }
        catch { }

    }

    private void ClearHostBleSelectionFromVm()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (HostBleGrid == null) return;
            HostBleGrid.SelectedItem = null;
            HostBleGrid.SelectedItems?.Clear();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }


    private void HostBleGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (DataContext is MainViewModel vm)
                vm.ResetHostBleUiTimer();
        }
        catch { }
    }

    private void BuildHostBleGridColumns(MainViewModel vm)
    {
        if (HostBleGrid == null) return;

        HostBleGrid.Columns.Clear();

        // Fixed columns
        HostBleGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "MAC",
            Binding = new Binding(nameof(HostBleScanItem.Mac)),
            Width = new DataGridLength(180)
        });

        // Actions should be right after MAC (requested)
        var actionsCol = new DataGridTemplateColumn { Header = "", Width = new DataGridLength(210) };
        var panelFactory = new FrameworkElementFactory(typeof(StackPanel));
        panelFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        FrameworkElementFactory MakeButton(string caption, string commandPath, string styleKey = "TinyGhostButton")
        {
            var b = new FrameworkElementFactory(typeof(Button));
            b.SetValue(Button.ContentProperty, caption);
            b.SetValue(Button.MinWidthProperty, 58d);
            b.SetValue(Control.FontSizeProperty, 12d);
            b.SetValue(UIElement.FocusableProperty, false);
            b.SetValue(FrameworkElement.StyleProperty, FindResource(styleKey));

            // Command = DataGrid.DataContext.<Command>
            b.SetBinding(Button.CommandProperty, new Binding(commandPath)
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGrid), 1)
            });
            b.SetBinding(Button.CommandParameterProperty, new Binding());
            return b;
        }

        panelFactory.AppendChild(MakeButton("Identify", "DataContext.IdentifyHostCommand", styleKey: "TinyIdentifyButton"));
        panelFactory.AppendChild(MakeButton("Update", "DataContext.UpdateHostCommand"));
        panelFactory.AppendChild(MakeButton("Get FW", "DataContext.GetFirmwareHostCommand"));

        var template = new DataTemplate { VisualTree = panelFactory };
        actionsCol.CellTemplate = template;
        HostBleGrid.Columns.Add(actionsCol);

        HostBleGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Model",
            Binding = new Binding(nameof(HostBleScanItem.SensorModel)),
            Width = new DataGridLength(70)
        });

        HostBleGrid.Columns.Add(new DataGridTextColumn
        {
            Header = $"Host RSSI (avg {vm.HostRssiAverageSeconds}s)",
            Binding = new Binding(nameof(HostBleScanItem.AvgHostRssi)),
            Width = new DataGridLength(150)
        });

        HostBleGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Closest",
            Binding = new Binding(nameof(HostBleScanItem.ClosestCassia)),
            Width = new DataGridLength(110)
        });

        HostBleGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Current FW",
            Binding = new Binding(nameof(HostBleScanItem.CurrentFw)),
            Width = new DataGridLength(120)
        });

        // One column per Cassia RSSI (indexer binding: {Binding [cassia-01]})
        foreach (var gw in vm.CassiaGateways.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = gw?.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;

            HostBleGrid.Columns.Add(new DataGridTextColumn
            {
                Header = name,
                Binding = new Binding($"[{name}]"),
                Width = new DataGridLength(80)
            });
        }

        HostBleGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Last seen",
            Binding = new Binding(nameof(HostBleScanItem.LastSeenLocal)),
            Width = new DataGridLength(170)
        });
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
            if (device == null) return;

            // Rebuild menu each time to avoid duplicate click handlers.
            cm.Items.Clear();

            var connect = new MenuItem { Header = "Connect" };
            connect.Click += async (_, __) =>
            {
                try { await vm.ConnectDeviceAsync(device).ConfigureAwait(false); } catch { }
            };
            cm.Items.Add(connect);

            var disconnect = new MenuItem { Header = "Disconnect" };
            disconnect.Click += async (_, __) =>
            {
                try
                {
                    // Requirement: allow multi-select by DataGrid selection (not checkboxes) and disconnect all selected.
                    var selected = new List<DiscoveredDevice>();
                    if (DevicesGrid?.SelectedItems != null)
                        selected.AddRange(DevicesGrid.SelectedItems.OfType<DiscoveredDevice>());

                    if (selected.Count == 0)
                        selected.Add(device);

                    // Deduplicate and ignore nulls.
                    selected = selected.Where(d => d != null).Distinct().ToList();
                    if (selected.Count == 0) return;

                    await vm.DisconnectDevicesAsync(selected).ConfigureAwait(false);
                }
                catch { }
            };
            cm.Items.Add(disconnect);

            var assignRoot = new MenuItem { Header = "Assign to Cassia" };
            cm.Items.Add(assignRoot);

            cm.Items.Add(new Separator());

            var wr = new MenuItem { Header = "Write-Read…" };
            wr.Click += (_, __) =>
            {
                try { vm.OpenWriteReadCommand?.Execute(device); } catch { }
            };
            cm.Items.Add(wr);

            var detectorSettings = new MenuItem { Header = "Detector settings..." };
            detectorSettings.Click += (_, __) =>
            {
                try { vm.OpenDetectorSettingsCommand?.Execute(device); } catch { }
            };
            cm.Items.Add(detectorSettings);

            if (vm.DeveloperModeUnlocked)
            {
                var openDb = new MenuItem { Header = "Open DB…" };
                openDb.Click += (_, __) =>
                {
                    try { vm.OpenDaliDbEditorCommand?.Execute(device); } catch { }
                };
                cm.Items.Add(openDb);
            }

            cm.Items.Add(new Separator());

            var forceUpdate = new MenuItem { Header = "Update (Force)" };
            forceUpdate.Click += async (_, __) =>
            {
                try { await vm.QueueDeviceAndRequestForceAsync(device).ConfigureAwait(false); } catch { }
            };
            cm.Items.Add(forceUpdate);

            // Build Assign submenu
            assignRoot.Items.Clear();

            // Requirement:
            // - Allow assign when multiple rows are selected (DataGrid selection, NOT checkboxes).
            // - When multiple selected, do not show RSSI (use gateway list instead).
            var gridSelected = new List<DiscoveredDevice>();
            if (DevicesGrid?.SelectedItems != null)
                gridSelected.AddRange(DevicesGrid.SelectedItems.OfType<DiscoveredDevice>());

            gridSelected = gridSelected.Where(d => d != null).Distinct().ToList();
            var multiSelect = gridSelected.Count > 1;

            IEnumerable<string> cassiaNames;

            if (!multiSelect && device.CassiaRssi != null && device.CassiaRssi.Count > 0)
            {
                // Single-device: use RSSI ordering and show it.
                cassiaNames = device.CassiaRssi
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => (kv.Key ?? "").Trim())
                    .Where(n => !string.IsNullOrWhiteSpace(n));
            }
            else
            {
                // Multi-select (or no RSSI): allow assigning to any known gateway, no RSSI displayed.
                cassiaNames = vm.CassiaGateways
                    .Select(g => (g.Name ?? "").Trim())
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
            }

            var cassiaList = cassiaNames.ToList();
            if (cassiaList.Count == 0)
            {
                assignRoot.IsEnabled = false;
                return;
            }

            assignRoot.IsEnabled = true;

            // Determine "checked" state for multi-select: checked if ALL selected devices share the same assignment.
            string? commonAssignment = null;
            if (multiSelect)
            {
                commonAssignment = gridSelected
                    .Select(d => (d.AssignedCassia ?? "").Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToList() is var list && list.Count == 1 ? list[0] : null;
            }

            foreach (var cassia in cassiaList)
            {
                if (string.IsNullOrWhiteSpace(cassia)) continue;

                string header;
                if (!multiSelect && device.CassiaRssi != null && device.CassiaRssi.TryGetValue(cassia, out var rssi))
                    header = $"{cassia} ({rssi})";
                else
                    header = cassia;

                var miAssign = new MenuItem
                {
                    Header = header,
                    IsCheckable = true,
                    IsChecked = multiSelect
                        ? cassia.Equals(commonAssignment ?? "", StringComparison.OrdinalIgnoreCase)
                        : cassia.Equals((device.AssignedCassia ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                };

                miAssign.Click += (_, __) =>
                {
                    try
                    {
                        var targets = new List<DiscoveredDevice>();

                        // Prefer DataGrid selection (row multi-select).
                        if (DevicesGrid?.SelectedItems != null)
                            targets.AddRange(DevicesGrid.SelectedItems.OfType<DiscoveredDevice>());

                        targets = targets.Where(d => d != null).Distinct().ToList();

                        if (targets.Count == 0)
                            targets.Add(device);

                        foreach (var dev in targets)
                        {
                            if (dev == null) continue;
                            vm.AssignDeviceToCassia(dev, cassia);
                        }
                    }
                    catch { }
                };

                assignRoot.Items.Add(miAssign);
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
            // Ensure PlacementTarget is available even though we're building the menu during ContextMenuOpening.
            // Without this, cm.PlacementTarget may be null and the "Assign to Cassia" submenu becomes disabled.
            row.ContextMenu.PlacementTarget = row;
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
            // Same as devices: make sure PlacementTarget is set so we can resolve the QueueItem.
            row.ContextMenu.PlacementTarget = row;
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

                var gw = vm.CassiaGateways.FirstOrDefault(g =>
                    g != null && (g.Name ?? "").Trim().Equals(cassia, StringComparison.OrdinalIgnoreCase));
                var qCount = gw?.Queue ?? 0;
                var pCount = gw?.Programming ?? 0;

                // Requirement: show RSSI + programming + queue counts.
                var mi = new MenuItem { Header = $"{cassia} (RSSI {kv.Value}, P {pCount}, Q {qCount})" };
                mi.Click += async (_, __) =>
                {
                    var nl = Environment.NewLine;
                    var res = MessageBox.Show(
                        $"Move {qi.Mac} from {qi.Cassia} to {cassia}?{nl}{nl}" +
                        $"Step 1: remove from pending queue{nl}" +
                        $"Step 2: add to queue on new Cassia{nl}{nl}" +
                        $"RSSI: {kv.Value}{nl}Programming: {pCount}{nl}Queue: {qCount}",
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
