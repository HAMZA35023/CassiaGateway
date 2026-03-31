using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    // ---- Developer mode → PerfLog ----

    partial void OnDeveloperModeUnlockedChanged(bool value)
    {
        PerfLog.Enabled = value;
    }

    // ---- Discovered batching ----
    // Buffers all incoming "discovered" messages so a burst of N messages from
    // multiple gateways results in exactly one UI dispatch instead of N.

    private sealed class DiscoveredBatch
    {
        public string Net { get; set; } = "";
        public DateTimeOffset Ts { get; set; } = DateTimeOffset.MinValue;
        // Keyed by MAC — last-write wins, deduplicates within a flush window.
        public Dictionary<string, (int Rssi, string Name, string ProductNumber, string Family, string Type)> Devices { get; }
            = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly object _discoveredBufLock = new();
    private readonly Dictionary<string, DiscoveredBatch> _discoveredBufByCassia = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _discoveredFlushPending;

    internal void BufferDiscovered(
        string cassia, string net, DateTimeOffset ts,
        List<(string Mac, int Rssi, string Name, string ProductNumber, string Family, string Type)> devices)
    {
        lock (_discoveredBufLock)
        {
            if (!_discoveredBufByCassia.TryGetValue(cassia, out var batch))
            {
                batch = new DiscoveredBatch { Net = net };
                _discoveredBufByCassia[cassia] = batch;
            }
            if (ts > batch.Ts) batch.Ts = ts;
            foreach (var (mac, rssi, name, pn, fam, typ) in devices)
                batch.Devices[mac] = (rssi, name, pn, fam, typ);
        }
        ScheduleDiscoveredFlushOnUi();
    }

    private void ScheduleDiscoveredFlushOnUi()
    {
        if (_discoveredFlushPending) return;
        _discoveredFlushPending = true;
        var t0 = Stopwatch.GetTimestamp();
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            var lag = (long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            var sw = Stopwatch.StartNew();
            _discoveredFlushPending = false;
            FlushDiscoveredOnUi();
            PerfLog.UiWork("discovered-flush", lag, sw.ElapsedMilliseconds);
        }, DispatcherPriority.Background);
    }

    private void FlushDiscoveredOnUi()
    {
        Dictionary<string, DiscoveredBatch> snapshot;
        lock (_discoveredBufLock)
        {
            if (_discoveredBufByCassia.Count == 0) return;
            snapshot = new Dictionary<string, DiscoveredBatch>(_discoveredBufByCassia, StringComparer.OrdinalIgnoreCase);
            _discoveredBufByCassia.Clear();
        }

        var anyNewDevices = false;

        foreach (var (cassia, batch) in snapshot)
        {
            var gw = CassiaGateways.FirstOrDefault(x => x.Name.Equals(cassia, StringComparison.OrdinalIgnoreCase));
            if (gw == null)
            {
                gw = new CassiaGateway { Name = cassia, NetworkId = batch.Net };
                CassiaGateways.Add(gw);
                SortCassiaGatewaysByName();
            }

            EnsureCassiaOption(gw.Name);
            EnsureCassiaOption(cassia);

            if (!LogGatewayOptions.Any(x => x.Equals(cassia, StringComparison.OrdinalIgnoreCase)))
                LogGatewayOptions.Add(cassia);

            if (batch.Ts > DateTimeOffset.MinValue)
                gw.LastSeenUtc = batch.Ts;
            gw.State = "online";

            if (!_gwSeenMacs.TryGetValue(cassia, out var seen))
            {
                seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _gwSeenMacs[cassia] = seen;
            }

            foreach (var (mac, (rssi, name, pn, fam, typ)) in batch.Devices)
            {
                seen.Add(mac);

                if (!_deviceByMac.TryGetValue(mac, out var existing))
                {
                    existing = new DiscoveredDevice { Mac = mac };
                    _deviceByMac[mac] = existing;
                    _devices.Add(existing);
                    anyNewDevices = true;
                }

                EnsureDeviceAssignmentWiring(existing);
                ApplyCachedStatusToDevice(existing);

                ApplyDeviceNameWithGuards(existing, name);
                if (!string.IsNullOrWhiteSpace(fam)) existing.DetectorFamily = fam;
                if (!string.IsNullOrWhiteSpace(typ)) existing.DetectorType = typ;

                if (!string.IsNullOrWhiteSpace(pn))
                {
                    existing.ProductNumber = pn;
                    if (_productToModel.TryGetValue(pn, out var model))
                        existing.SensorModel = model;
                }
                else if (!string.IsNullOrWhiteSpace(existing.ProductNumber) && _productToModel.TryGetValue(existing.ProductNumber, out var model2))
                {
                    existing.SensorModel = model2;
                }

                existing.UpdateFromCassia(cassia, rssi, batch.Ts);
                UpdateQueueRssiForMac(mac);
                EnsureStickyAssignment(existing);
            }

            gw.DevicesSeen = seen.Count;
        }

        RecalculateAssignmentCounts();
        // Only refresh the CollectionView when new devices appear — existing-device updates
        // (RSSI, last-seen, name) are handled by INotifyPropertyChanged on DiscoveredDevice.
        if (anyNewDevices)
            RequestDevicesRefresh();
        OnPropertyChanged(nameof(DevicesSubtitle));
    }

    // ---- DeviceList batching ----
    // Same pattern: multiple device-list responses from a gateway reconnect are
    // collapsed into a single UI dispatch.

    private sealed class DeviceListEntry
    {
        public string Cassia { get; set; } = "";
        public int Rssi { get; set; } = int.MinValue;
        public string Name { get; set; } = "";
        public string ProductNumber { get; set; } = "";
        public string DetectorFamily { get; set; } = "";
        public string DetectorType { get; set; } = "";
        public DateTimeOffset LastSeenUtc { get; set; }
    }

    private readonly object _deviceListBufLock = new();
    // Keyed by MAC — last-write wins across all arriving device-list messages.
    private readonly Dictionary<string, DeviceListEntry> _deviceListBufByMac = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _deviceListFlushPending;

    internal void BufferDeviceList(
        string cassia,
        List<(string Mac, int Rssi, string Name, string ProductNumber, string DetectorFamily, string DetectorType, DateTimeOffset LastSeenUtc)> devices)
    {
        lock (_deviceListBufLock)
        {
            foreach (var (mac, rssi, name, pn, family, type, lastSeen) in devices)
            {
                _deviceListBufByMac[mac] = new DeviceListEntry
                {
                    Cassia = cassia,
                    Rssi = rssi,
                    Name = name,
                    ProductNumber = pn,
                    DetectorFamily = family,
                    DetectorType = type,
                    LastSeenUtc = lastSeen
                };
            }
        }
        ScheduleDeviceListFlushOnUi();
    }

    private void ScheduleDeviceListFlushOnUi()
    {
        if (_deviceListFlushPending) return;
        _deviceListFlushPending = true;
        var t0 = Stopwatch.GetTimestamp();
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            var lag = (long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            var sw = Stopwatch.StartNew();
            _deviceListFlushPending = false;
            FlushDeviceListOnUi();
            PerfLog.UiWork("device-list-flush", lag, sw.ElapsedMilliseconds);
        }, DispatcherPriority.Background);
    }

    private void FlushDeviceListOnUi()
    {
        Dictionary<string, DeviceListEntry> snapshot;
        lock (_deviceListBufLock)
        {
            if (_deviceListBufByMac.Count == 0) return;
            snapshot = new Dictionary<string, DeviceListEntry>(_deviceListBufByMac, StringComparer.OrdinalIgnoreCase);
            _deviceListBufByMac.Clear();
        }

        var anyNewDevices = false;

        foreach (var (mac, e) in snapshot)
        {
            if (!_deviceByMac.TryGetValue(mac, out var d))
            {
                d = new DiscoveredDevice { Mac = mac };
                WireDeviceAssignmentHooks(d);
                _deviceByMac[mac] = d;
                _devices.Add(d);
                anyNewDevices = true;
            }

            ApplyDeviceNameWithGuards(d, e.Name);
            d.ProductNumber = string.IsNullOrWhiteSpace(e.ProductNumber) ? d.ProductNumber : e.ProductNumber;
            d.DetectorFamily = string.IsNullOrWhiteSpace(e.DetectorFamily) ? d.DetectorFamily : e.DetectorFamily;
            d.DetectorType = string.IsNullOrWhiteSpace(e.DetectorType) ? d.DetectorType : e.DetectorType;

            if (!string.IsNullOrWhiteSpace(e.DetectorType) && e.DetectorType.Trim().StartsWith("P", StringComparison.OrdinalIgnoreCase))
                d.SensorModel = e.DetectorType.Trim().ToUpperInvariant();
            else if (!string.IsNullOrWhiteSpace(d.ProductNumber) && _productToModel.TryGetValue(d.ProductNumber, out var m))
                d.SensorModel = m;

            if (e.Rssi != int.MinValue)
                d.UpdateFromCassia(e.Cassia, e.Rssi, e.LastSeenUtc);
            else
                d.LastSeenUtc = e.LastSeenUtc;

            UpdateQueueRssiForMac(mac);
            ApplyCachedStatusToDevice(d);
            EnsureStickyAssignment(d);
        }

        RecalculateAssignmentCounts();
        if (anyNewDevices)
            RequestDevicesRefresh();
    }
}
