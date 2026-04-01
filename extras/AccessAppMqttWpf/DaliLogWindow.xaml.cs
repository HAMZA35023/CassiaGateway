using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace AccessAppMqttWpf;

public partial class DaliLogWindow : Window, IDisposable
{
    private readonly MainViewModel _vm;
    private readonly ObservableCollection<DaliLogEntry> _entries = new();

    private bool _autoScroll = true;
    private bool _disposed;

    // Pre-selection set before Show()
    private string? _cassiaName;
    private string? _mac;

    private const int MaxDisplayEntries = 50_000;

    public DaliLogWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Grid.ItemsSource = _entries;

        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ── Pre-selection API ─────────────────────────────────────────────────────

    public void PreSelectDevice(string cassiaName, string mac)
    {
        _cassiaName = cassiaName;
        _mac        = mac;
        Title       = $"DALI Logger — {mac}";
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.DaliLogEntriesReceived += OnEntriesReceived;

        if (!string.IsNullOrEmpty(_cassiaName) && !string.IsNullOrEmpty(_mac))
        {
            // Load any cached history
            var key     = $"{_cassiaName}|{_mac}";
            var history = _vm.GetDaliLogHistory(key);
            if (history.Count > 0)
                BulkAppend(history);

            // Start the live session
            _ = _vm.SendStartDaliLogAsync(_cassiaName, _mac);
            SetStatus(running: true);
        }
        else
        {
            SetStatus(running: false);
            FooterLabel.Text = "No device selected.";
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _vm.DaliLogEntriesReceived -= OnEntriesReceived;

        if (!string.IsNullOrEmpty(_cassiaName) && !string.IsNullOrEmpty(_mac))
            _ = _vm.SendStopDaliLogAsync(_cassiaName, _mac);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vm.DaliLogEntriesReceived -= OnEntriesReceived;
    }

    // ── Entry handling ────────────────────────────────────────────────────────

    private void OnEntriesReceived(string key, IReadOnlyList<DaliLogEntry> entries)
    {
        // Already on UI thread (fired via Dispatcher.Invoke in AppendDaliLogEntries)
        if (key != $"{_cassiaName}|{_mac}") return;
        BulkAppend(entries);
    }

    private void BulkAppend(IReadOnlyList<DaliLogEntry> entries)
    {
        foreach (var e in entries)
            _entries.Add(e);

        // Prune oldest if over cap
        int excess = _entries.Count - MaxDisplayEntries;
        while (excess-- > 0)
            _entries.RemoveAt(0);

        UpdateCountLabel();

        if (_autoScroll && _entries.Count > 0)
            Grid.ScrollIntoView(_entries[_entries.Count - 1]);
    }

    // ── Toolbar handlers ──────────────────────────────────────────────────────

    private void AutoScrollBtn_Click(object sender, RoutedEventArgs e)
    {
        _autoScroll = !_autoScroll;
        AutoScrollBtn.Content = _autoScroll ? "Auto-scroll: ON" : "Auto-scroll: OFF";
        AutoScrollBtn.Background = _autoScroll
            ? (Brush)(TryFindResource("AccentBrush") ?? SystemColors.HighlightBrush)
            : (Brush)(TryFindResource("Card2Brush")  ?? SystemColors.ControlBrush);
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        _entries.Clear();
        if (_cassiaName != null && _mac != null)
            _vm.ClearDaliLogHistory($"{_cassiaName}|{_mac}");
        UpdateCountLabel();
    }

    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = Grid.SelectedItems.Cast<DaliLogEntry>().ToList();
        var source   = selected.Count > 0 ? selected : _entries.ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Time\tType\tSeq\tRaw\tAddress\tAddr#\tInstance\tDescription\tValue\tGap");
        foreach (var row in source)
        {
            sb.Append(row.Timestamp.ToString("HH:mm:ss.fff")).Append('\t');
            sb.Append(row.SignalType).Append('\t');
            sb.Append(row.Seq?.ToString() ?? "").Append('\t');
            sb.Append(row.RawHex).Append('\t');
            sb.Append(row.AddressType ?? "").Append('\t');
            sb.Append(row.Address?.ToString() ?? "").Append('\t');
            sb.Append(row.InstanceType ?? "").Append('\t');
            sb.Append(row.Description).Append('\t');
            sb.Append(row.ValueHex ?? "").Append('\t');
            sb.AppendLine(row.MissingEntries?.ToString() ?? "");
        }

        try { Clipboard.SetText(sb.ToString()); }
        catch { /* clipboard might be locked */ }

        FooterLabel.Text = $"Copied {source.Count} rows to clipboard.";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetStatus(bool running)
    {
        StatusDot.Fill   = running
            ? (Brush)(TryFindResource("GoodBrush") ?? SystemColors.MenuHighlightBrush)
            : (Brush)(TryFindResource("BadBrush")  ?? SystemColors.ControlDarkBrush);
        StatusLabel.Text = running ? "Running" : "Stopped";
    }

    private void UpdateCountLabel()
    {
        CountLabel.Text = $"{_entries.Count:N0} entries";
    }
}
