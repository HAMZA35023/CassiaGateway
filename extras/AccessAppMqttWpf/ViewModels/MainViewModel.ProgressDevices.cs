using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.VisualBasic;
using AccessAppMqttWpf;
using System.Diagnostics;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly TimeSpan WeakRssiGraceWindow = TimeSpan.FromSeconds(60);
    private readonly IgnoredDevicesStore _ignoredDevicesStore = new();
    private readonly HashSet<string> _ignoredDeviceMacs = new(StringComparer.OrdinalIgnoreCase);
    private const string AllFilterOption = "All";
    private bool _pendingProductFilterOptionsRefresh;


    public ObservableCollection<string> SensorFilterOptions { get; } =
        new(new[] { AllFilterOption, "P41", "P42", "P46", "P47", "P48" });
    public ObservableCollection<string> ProductFilterOptions { get; } = new(new[] { AllFilterOption });
    public ObservableCollection<int> WeakRssiThresholdOptions { get; } = new(new[] { -80, -75, -70, -65, -60, -55, -50, -45, -40, -35 });

    [ObservableProperty] private bool hideCompletedDevices = false;
    [ObservableProperty] private bool hideWeakRssiEnabled = true;
    [ObservableProperty] private int hideWeakRssiThreshold = -70;
    [ObservableProperty] private bool suppressSameFirmwareWarnings = true;
    [ObservableProperty] private bool showModelP41 = true;
    [ObservableProperty] private bool showModelP42 = true;
    [ObservableProperty] private bool showModelP46 = true;
    [ObservableProperty] private bool showModelP47 = true;
    [ObservableProperty] private bool showModelP48 = true;

    [ObservableProperty] private string deviceFilter = "";
    [ObservableProperty] private string sensorFilter = AllFilterOption;
    [ObservableProperty] private string productFilter = AllFilterOption;

    // ---- Progress buffering (prevents UI lag / lost clicks when many % updates arrive) ----
    private readonly object _progressBufLock = new();
    private readonly Dictionary<string, BufferedProgress> _progressByMac = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _progressFlushPending;

    private sealed class BufferedProgress
    {
        public string Cassia { get; set; } = "";
        public string Mac { get; set; } = "";
        public string Stage { get; set; } = "";
        public string QueueStatus { get; set; } = "";
        public string FirmwareTarget { get; set; } = "";
        public string ChipUsed { get; set; } = "";
        public bool HasProgressPercent { get; set; } = true;
        public double ProgressPercent { get; set; }
        public bool HasSpeedPctPerMin { get; set; }
        public double? SpeedPctPerMin { get; set; }
        public bool ClearSpeed { get; set; }
        public DateTimeOffset TimeUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private void InitDeviceFiltering()
    {
        FilteredDevices = CollectionViewSource.GetDefaultView(_devices);

        FilteredDevices.Filter = obj =>
        {
            if (obj is not DiscoveredDevice d) return false;

            if (IsIgnoredDevice(d.Mac))
                return false;

            // Always hide devices reporting RSSI -127 (invalid/no signal) unless they are in the queue
            if (d.BestRssi == -127 && !d.IsInQueue)
                return false;

            if (HideCompletedDevices && d.IsUpgradeSuccess)
                return false;

            if (!string.IsNullOrWhiteSpace(SensorFilter) && !SensorFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                if (!d.SensorModel.Equals(SensorFilter, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            var model = (d.SensorModel ?? "").Trim();
            if (model.Equals("P41", StringComparison.OrdinalIgnoreCase) && !ShowModelP41) return false;
            if (model.Equals("P42", StringComparison.OrdinalIgnoreCase) && !ShowModelP42) return false;
            if (model.Equals("P46", StringComparison.OrdinalIgnoreCase) && !ShowModelP46) return false;
            if (model.Equals("P47", StringComparison.OrdinalIgnoreCase) && !ShowModelP47) return false;
            if (model.Equals("P48", StringComparison.OrdinalIgnoreCase) && !ShowModelP48) return false;

            if (!string.IsNullOrWhiteSpace(ProductFilter) && !ProductFilter.Equals(AllFilterOption, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals((d.ProductNumber ?? "").Trim(), ProductFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (HideWeakRssiEnabled)
            {
                var threshold = HideWeakRssiThreshold;
                // Treat -127 as "no RSSI" (do not hide those devices).
                var hasRssi = d.BestRssi != int.MinValue && d.BestRssi > -127;
                if (hasRssi && d.BestRssi < threshold)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (!d.WasAboveThresholdRecently(threshold, WeakRssiGraceWindow, now))
                        return false;
                }
            }

            if (string.IsNullOrWhiteSpace(DeviceFilter))
                return true;

            var f = DeviceFilter.Trim();
            return (d.Mac?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.Name?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.ProductNumber?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.BestCassia?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.RssiAll?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);
        };
    }

    private void InitProductFilterTracking()
    {
        _devices.CollectionChanged += OnDevicesCollectionChangedForProductFilter;
        RefreshProductFilterOptions();
    }

    private void OnDevicesCollectionChangedForProductFilter(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var oldItem in e.OldItems)
            {
                if (oldItem is DiscoveredDevice d)
                    d.PropertyChanged -= OnTrackedDevicePropertyChangedForProductFilter;
            }
        }

        if (e.NewItems != null)
        {
            foreach (var newItem in e.NewItems)
            {
                if (newItem is DiscoveredDevice d)
                    d.PropertyChanged += OnTrackedDevicePropertyChangedForProductFilter;
            }
        }

        RequestProductFilterOptionsRefresh();
    }

    private void OnTrackedDevicePropertyChangedForProductFilter(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(DiscoveredDevice.ProductNumber), StringComparison.OrdinalIgnoreCase))
            RequestProductFilterOptionsRefresh();
    }

    private void RequestProductFilterOptionsRefresh(bool immediate = false)
    {
        if (_pendingProductFilterOptionsRefresh && !immediate) return;
        _pendingProductFilterOptionsRefresh = true;

        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            if (!immediate)
                await Task.Delay(350);

            _pendingProductFilterOptionsRefresh = false;
            RefreshProductFilterOptions();
        });
    }

    private void RefreshProductFilterOptions()
    {
        var selected = (ProductFilter ?? "").Trim();
        var availableProducts = _devices
            .Select(d => (d.ProductNumber ?? "").Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ProductFilterOptions.Clear();
        ProductFilterOptions.Add(AllFilterOption);
        foreach (var product in availableProducts)
            ProductFilterOptions.Add(product);

        if (string.IsNullOrWhiteSpace(selected) || selected.Equals(AllFilterOption, StringComparison.OrdinalIgnoreCase))
        {
            ProductFilter = AllFilterOption;
            return;
        }

        var matchingOption = ProductFilterOptions.FirstOrDefault(p => string.Equals(p, selected, StringComparison.OrdinalIgnoreCase));
        ProductFilter = matchingOption ?? AllFilterOption;
    }

    private void InitIgnoredDevices()
    {
        _ignoredDeviceMacs.Clear();

        foreach (var raw in _ignoredDevicesStore.Load())
        {
            var mac = NormalizeMac(raw);
            if (!string.IsNullOrWhiteSpace(mac))
                _ignoredDeviceMacs.Add(mac);
        }
    }

    private static string NormalizeMac(string? mac) => (mac ?? "").Trim().ToUpperInvariant();

    private bool IsIgnoredDevice(string? mac)
    {
        var normalized = NormalizeMac(mac);
        return normalized.Length > 0 && _ignoredDeviceMacs.Contains(normalized);
    }

    private void SaveIgnoredDevices()
    {
        _ignoredDevicesStore.Save(_ignoredDeviceMacs);
    }

    private void InitProgressBuffering()
    {
        // No-op: progress is flushed event-driven via ScheduleProgressFlushOnUi().
    }

    internal void ScheduleProgressFlushOnUi()
    {
        if (_progressFlushPending) return;
        _progressFlushPending = true;
        var t0Flush = Stopwatch.GetTimestamp();
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            var flushLag = (long)Stopwatch.GetElapsedTime(t0Flush).TotalMilliseconds;
            _progressFlushPending = false;
            var swFlush = Stopwatch.StartNew();
            FlushBufferedProgressOnUi();
            PerfLog.UiWork("progress-flush", flushLag, swFlush.ElapsedMilliseconds);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    partial void OnDeviceFilterChanged(string value)
    {
        RequestDevicesRefresh();
        OnPropertyChanged(nameof(DevicesSubtitle));
    }

    partial void OnHideCompletedDevicesChanged(bool value)
    {
        FilteredDevices.Refresh();
    }

    partial void OnHideWeakRssiEnabledChanged(bool value)
    {
        RequestDevicesRefresh();
        OnPropertyChanged(nameof(DevicesSubtitle));
    }

    partial void OnHideWeakRssiThresholdChanged(int value)
    {
        RequestDevicesRefresh();
        OnPropertyChanged(nameof(DevicesSubtitle));
    }

    partial void OnSuppressSameFirmwareWarningsChanged(bool value)
    {
        RefreshUpgradeSuccessFromLatestGroups();
        RequestUpgradeLogViewRefresh();
        RequestDevicesRefresh();
        RequestQueueRefresh();
        try { FlushHostBleToUi(); } catch { }
        try { HostBleDevicesView?.Refresh(); } catch { }
    }

    partial void OnSensorFilterChanged(string value)
    {
        RequestDevicesRefresh();
        OnPropertyChanged(nameof(DevicesSubtitle));
    }

    partial void OnProductFilterChanged(string value)
    {
        RequestDevicesRefresh();
        OnPropertyChanged(nameof(DevicesSubtitle));
    }

    partial void OnShowModelP41Changed(bool value) { RequestDevicesRefresh(); OnPropertyChanged(nameof(SelectedModelFilterSummary)); OnPropertyChanged(nameof(DevicesSubtitle)); }
    partial void OnShowModelP42Changed(bool value) { RequestDevicesRefresh(); OnPropertyChanged(nameof(SelectedModelFilterSummary)); OnPropertyChanged(nameof(DevicesSubtitle)); }
    partial void OnShowModelP46Changed(bool value) { RequestDevicesRefresh(); OnPropertyChanged(nameof(SelectedModelFilterSummary)); OnPropertyChanged(nameof(DevicesSubtitle)); }
    partial void OnShowModelP47Changed(bool value) { RequestDevicesRefresh(); OnPropertyChanged(nameof(SelectedModelFilterSummary)); OnPropertyChanged(nameof(DevicesSubtitle)); }
    partial void OnShowModelP48Changed(bool value) { RequestDevicesRefresh(); OnPropertyChanged(nameof(SelectedModelFilterSummary)); OnPropertyChanged(nameof(DevicesSubtitle)); }

    [RelayCommand]
    private void SelectAllModelFilters()
    {
        ShowModelP41 = true;
        ShowModelP42 = true;
        ShowModelP46 = true;
        ShowModelP47 = true;
        ShowModelP48 = true;
    }

    [RelayCommand]
    private void ClearModelFilters()
    {
        ShowModelP41 = false;
        ShowModelP42 = false;
        ShowModelP46 = false;
        ShowModelP47 = false;
        ShowModelP48 = false;
    }

    [RelayCommand]
    private void IgnoreSelectedDevices()
    {
        var selected = _devices.Where(d => d != null && d.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        var added = 0;
        foreach (var d in selected)
        {
            var mac = NormalizeMac(d.Mac);
            if (string.IsNullOrWhiteSpace(mac)) continue;

            if (_ignoredDeviceMacs.Add(mac))
                added++;

            d.IsSelected = false;
        }

        if (SelectedDevice != null && IsIgnoredDevice(SelectedDevice.Mac))
            SelectedDevice = null;

        SaveIgnoredDevices();
        RequestDevicesRefresh();
        OnPropertyChanged(nameof(DevicesSubtitle));
        ConnectionStatus = $"Ignored {added} device(s)";
    }

    [RelayCommand]
    private void ClearIgnoredDevices()
    {
        if (_ignoredDeviceMacs.Count == 0)
            return;

        _ignoredDeviceMacs.Clear();
        SaveIgnoredDevices();
        RequestDevicesRefresh();
        OnPropertyChanged(nameof(DevicesSubtitle));
        ConnectionStatus = "Cleared ignored devices";
    }

    public string SelectedModelFilterSummary
    {
        get
        {
            var selected = new List<string>(5);
            if (ShowModelP41) selected.Add("P41");
            if (ShowModelP42) selected.Add("P42");
            if (ShowModelP46) selected.Add("P46");
            if (ShowModelP47) selected.Add("P47");
            if (ShowModelP48) selected.Add("P48");
            if (selected.Count == 0) return "None";
            return selected.Count == 5 ? AllFilterOption : string.Join(",", selected);
        }
    }

}
