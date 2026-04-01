using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.Services;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel
{
    // ── History store: "cassiaName|MAC" → ring-buffer of entries ─────────────
    private readonly ConcurrentDictionary<string, List<DaliLogEntry>> _daliLogHistory =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Per-session decoders: same key as history ─────────────────────────────
    private readonly ConcurrentDictionary<string, DaliDecoder> _daliDecoders =
        new(StringComparer.OrdinalIgnoreCase);

    private const int DaliLogMaxEntries = 50_000;

    /// <summary>
    /// Fired on the UI thread whenever a batch of new DALI log entries arrives.
    /// The window subscribes to redraw / append.
    /// </summary>
    public event Action<string, IReadOnlyList<DaliLogEntry>>? DaliLogEntriesReceived;

    // ── Entry management ──────────────────────────────────────────────────────

    internal void AppendDaliLogFrames(string cassiaName, string mac, string framesHex, DateTimeOffset batchTime)
    {
        if (string.IsNullOrEmpty(framesHex)) return;

        byte[] raw;
        try { raw = Convert.FromHexString(framesHex); }
        catch { return; }

        int entryCount = raw.Length / 5;
        if (entryCount == 0) return;

        var key     = $"{cassiaName}|{mac}";
        var decoder = _daliDecoders.GetOrAdd(key, _ => new DaliDecoder());

        var entries = new List<DaliLogEntry>(entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            var entry = decoder.Decode(raw.AsSpan(i * 5, 5), batchTime);
            if (entry != null)
                entries.Add(entry);
        }

        if (entries.Count > 0)
            AppendDaliLogEntries(cassiaName, mac, entries);
    }

    internal void AppendDaliLogEntries(string cassiaName, string mac, IReadOnlyList<DaliLogEntry> entries)
    {
        if (entries.Count == 0) return;

        var key  = $"{cassiaName}|{mac}";
        var list = _daliLogHistory.GetOrAdd(key, _ => new List<DaliLogEntry>());

        lock (list)
        {
            list.AddRange(entries);

            // Prune oldest when over the cap
            int excess = list.Count - DaliLogMaxEntries;
            if (excess > 0)
                list.RemoveRange(0, excess);
        }

        Application.Current.Dispatcher.Invoke(() =>
            DaliLogEntriesReceived?.Invoke(key, entries));
    }

    internal IReadOnlyList<DaliLogEntry> GetDaliLogHistory(string key)
    {
        if (_daliLogHistory.TryGetValue(key, out var list))
        {
            lock (list) return list.ToArray();
        }
        return Array.Empty<DaliLogEntry>();
    }

    internal void ClearDaliLogHistory(string key)
    {
        if (_daliLogHistory.TryGetValue(key, out var list))
            lock (list) list.Clear();
    }

    // ── MQTT commands ─────────────────────────────────────────────────────────

    internal async Task SendStartDaliLogAsync(string cassiaName, string mac)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(cassiaName) || string.IsNullOrWhiteSpace(mac)) return;

        var payload = new { sensor = mac };
        try
        {
            await _mqtt.PublishJsonAsync(
                BuildCmdTopic(cassiaName, "start-dali-log"),
                payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    internal async Task SendStopDaliLogAsync(string cassiaName, string mac)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(cassiaName) || string.IsNullOrWhiteSpace(mac)) return;

        var payload = new { sensor = mac };
        try
        {
            await _mqtt.PublishJsonAsync(
                BuildCmdTopic(cassiaName, "stop-dali-log"),
                payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    // ── Context-menu command ──────────────────────────────────────────────────

    [RelayCommand]
    private void OpenDaliLogForDevice(DiscoveredDevice? device)
    {
        if (device == null) return;
        try
        {
            var wnd = new DaliLogWindow(this)
            {
                Owner = Application.Current.MainWindow
            };
            wnd.PreSelectDevice(device.AssignedCassia, device.Mac);
            wnd.Show();
            wnd.Activate();
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Open DALI Log failed: {ex.Message}";
        }
    }
}
