using AccessAppMqttWpf.Models;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel
{
    // Ring-buffer of PIR peak samples per device MAC.
    // Keyed by "cassiaName|MAC" so samples from different gateways are separate.
    private readonly ConcurrentDictionary<string, List<PirPeakSample>> _pirPeakHistory = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan PirPeakMaxHistory = TimeSpan.FromHours(1);

    /// <summary>
    /// Fired on the UI thread whenever a new PIR peak sample arrives.
    /// The window subscribes to this event to redraw the chart.
    /// </summary>
    public event Action<string, PirPeakSample>? PirPeakSampleReceived;

    internal void AppendPirPeakSample(string cassiaName, PirPeakSample sample)
    {
        var key = $"{cassiaName}|{sample.Mac}";

        var list = _pirPeakHistory.GetOrAdd(key, _ => new List<PirPeakSample>());

        lock (list)
        {
            list.Add(sample);

            // Prune entries older than max history
            var cutoff = DateTimeOffset.UtcNow - PirPeakMaxHistory;
            while (list.Count > 0 && list[0].TimeUtc < cutoff)
                list.RemoveAt(0);
        }

        Application.Current.Dispatcher.Invoke(() =>
            PirPeakSampleReceived?.Invoke(key, sample));
    }

    internal IReadOnlyList<PirPeakSample> GetPirPeakHistory(string key)
    {
        if (_pirPeakHistory.TryGetValue(key, out var list))
        {
            lock (list) return list.ToList();
        }
        return Array.Empty<PirPeakSample>();
    }

    internal IEnumerable<string> GetPirPeakKeys() => _pirPeakHistory.Keys;

    internal void ClearPirPeakHistory(string? key = null)
    {
        if (key == null)
        {
            _pirPeakHistory.Clear();
        }
        else if (_pirPeakHistory.TryGetValue(key, out var list))
        {
            lock (list) list.Clear();
        }
    }

    // -------------------------------------------------------------------------
    // Walk-test
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fired on the UI thread when a walktest tele result arrives.
    /// Parameters: key ("cassiaName|MAC"), enabled, success, error.
    /// </summary>
    public event Action<string, bool, bool, string?>? WalktestResultReceived;

    internal void RaiseWalktestResult(string cassiaName, string mac, bool enabled, bool success, string? error)
    {
        var key = $"{cassiaName}|{mac}";
        Application.Current.Dispatcher.Invoke(() =>
            WalktestResultReceived?.Invoke(key, enabled, success, error));
    }

    /// <summary>
    /// Sends a set-walktest command to the specified gateway for a single MAC.
    /// </summary>
    internal async Task SendSetWalktestCommandAsync(string cassiaName, string mac, bool enabled, string? pincode = null)
    {
        if (!IsConnected) return;
        if (string.IsNullOrWhiteSpace(cassiaName)) return;

        var payload = new
        {
            sensors = new[] { mac },
            enabled,
            pincode = string.IsNullOrWhiteSpace(pincode) ? null : pincode
        };

        try
        {
            var topic = BuildCmdTopic(cassiaName, "set-walktest");
            await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    /// <summary>
    /// Sends a get-pir-peak command to the specified gateway for the given MAC addresses.
    /// </summary>
    internal async Task SendGetPirPeakCommandAsync(string cassiaName, IEnumerable<string> macs, string? pincode = null)
    {
        if (!IsConnected) return;
        if (string.IsNullOrWhiteSpace(cassiaName)) return;

        var payload = new
        {
            sensors = macs.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList(),
            pincode = string.IsNullOrWhiteSpace(pincode) ? null : pincode
        };

        try
        {
            var topic = BuildCmdTopic(cassiaName, "get-pir-peak");
            await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    [RelayCommand]
    private void OpenPirPeakStatus(string? cassiaName)
    {
        try
        {
            var wnd = new PirPeakStatusWindow(this)
            {
                Owner = Application.Current.MainWindow
            };

            if (!string.IsNullOrWhiteSpace(cassiaName))
                wnd.PreSelectCassia(cassiaName);

            wnd.Show();
            wnd.Activate();
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Open PIR Peak Status failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenPirPeakStatusAll()
    {
        try
        {
            var wnd = new PirPeakStatusWindow(this)
            {
                Owner = Application.Current.MainWindow
            };
            wnd.Show();
            wnd.Activate();
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Open PIR Peak Status failed: {ex.Message}";
        }
    }
}
