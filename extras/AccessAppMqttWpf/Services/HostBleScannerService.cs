using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Windows.Devices.Bluetooth.Advertisement;

namespace AccessAppMqttWpf.Services;

/// <summary>
/// Continuously scans BLE advertisements on the host PC and maintains a rolling 10s average RSSI per device.
/// Filters on MAC prefix (e.g. "10:B9:F7").
/// </summary>
public sealed class HostBleScannerService : IDisposable
{
    public sealed record HostBleUpdate(string Mac, int AvgRssi, DateTimeOffset LastSeenUtc);

    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly ConcurrentDictionary<string, DeviceWindow> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _tick;
    private readonly string _macPrefix;
    private bool _started;

    // We tick each second to prune samples and compute rolling averages.
    // NOTE: 'volatile TimeSpan' is illegal (CS0677). Store seconds as a volatile int.
    private volatile int _windowSeconds = 10;

    public int WindowSeconds
    {
        get => Math.Max(1, _windowSeconds);
        set
        {
            var v = Math.Max(1, value);
            _windowSeconds = v;
        }
    }

    public event Action<HostBleUpdate>? Updated;

    public HostBleScannerService(string macPrefix = "10:B9:F7")
    {
        _macPrefix = (macPrefix ?? "").Trim().ToUpperInvariant();

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        _watcher.Received += Watcher_Received;

        _tick = new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        _watcher.Start();
        _tick.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;

        try { _watcher.Stop(); } catch { }
        _tick.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void Watcher_Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        // Convert BluetoothAddress to MAC string
        var mac = FormatMac(args.BluetoothAddress);
        if (string.IsNullOrWhiteSpace(mac)) return;

        if (!mac.StartsWith(_macPrefix, StringComparison.OrdinalIgnoreCase))
            return;

        var rssi = args.RawSignalStrengthInDBm;
        var now = DateTimeOffset.UtcNow;

        var w = _devices.GetOrAdd(mac, _ => new DeviceWindow());
        w.AddSample(now, rssi);
        w.LastSeenUtc = now;
    }

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var kv in _devices)
        {
            var mac = kv.Key;
            var w = kv.Value;

            var window = TimeSpan.FromSeconds(Math.Max(1, _windowSeconds));
            var avg = w.ComputeAvgAndPrune(now, window);
            if (avg == null)
                continue;

            Updated?.Invoke(new HostBleUpdate(mac, avg.Value, w.LastSeenUtc));
        }

        // Optional: remove entries not seen for a while (keeps list tidy)
        var staleCutoff = now - TimeSpan.FromMinutes(5);
        foreach (var kv in _devices)
        {
            if (kv.Value.LastSeenUtc < staleCutoff)
                _devices.TryRemove(kv.Key, out _);
        }
    }

    private static string FormatMac(ulong bluetoothAddress)
    {
        // BluetoothAddress is 48-bit, little-endian representation in Windows APIs.
        // Convert to standard MAC display (big-endian bytes).
        // Example: 0xA1B2C3D4E5F6 => "F6:E5:D4:C3:B2:A1"
        var bytes = BitConverter.GetBytes(bluetoothAddress);
        // BitConverter returns little endian 8 bytes; use first 6 bytes and reverse.
        var macBytes = bytes.Take(6).Reverse().ToArray();
        return string.Join(":", macBytes.Select(b => b.ToString("X2")));
    }

    public void Dispose()
    {
        Stop();
        _watcher.Received -= Watcher_Received;
        _tick.Dispose();
    }

    private sealed class DeviceWindow
    {
        private readonly object _gate = new();
        private readonly List<(DateTimeOffset ts, int rssi)> _samples = new();

        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.MinValue;

        public void AddSample(DateTimeOffset ts, int rssi)
        {
            lock (_gate)
            {
                _samples.Add((ts, rssi));
            }
        }

        public int? ComputeAvgAndPrune(DateTimeOffset now, TimeSpan window)
        {
            lock (_gate)
            {
                if (_samples.Count == 0) return null;

                var cutoff = now - window;
                // prune
                _samples.RemoveAll(s => s.ts < cutoff);

                if (_samples.Count == 0) return null;

                // average in dBm, rounded to nearest int
                var avg = (int)Math.Round(_samples.Average(s => (double)s.rssi));
                return avg;
            }
        }
    }
}
