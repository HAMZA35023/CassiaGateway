using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly MqttClientService _mqtt = new();
    private readonly SettingsStore _store = new();

    private readonly ObservableCollection<DiscoveredDevice> _devices = new();
    public ICollectionView FilteredDevices { get; }

    public ObservableCollection<CassiaGateway> CassiaGateways { get; } = new();
    public ObservableCollection<QueueItem> QueueItems { get; } = new();

    public ICollectionView QueueView { get; }

    public ObservableCollection<string> SensorFilterOptions { get; } =
        new(new[] { "All", "P41", "P42", "P46", "P47", "P48" });

    private readonly System.Collections.Generic.Dictionary<string, string> _productToModel =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _appCts = new();

    [ObservableProperty] private DiscoveredDevice? selectedDevice;
    [ObservableProperty] private QueueItem? selectedQueueItem;

    [ObservableProperty] private string deviceFilter = "";
    [ObservableProperty] private string sensorFilter = "All";

    [ObservableProperty] private string mqttHost = "192.168.0.10";
    [ObservableProperty] private int mqttPort = 1883;
    [ObservableProperty] private string mqttTopic = "accessapp/#";
    [ObservableProperty] private string mqttUser = "user";
    [ObservableProperty] private string? mqttPassword = "password";
    [ObservableProperty] private bool useTls;
    [ObservableProperty] private bool ignoreTlsErrors = true;

    [ObservableProperty] private string networkId = "dk-lab";
    [ObservableProperty] private string commandTopicTemplate = "accessapp/{networkId}/cmd/{cassia}/{command}";
    [ObservableProperty] private string defaultCommand = "start-update";

    // Firmware selection per model (dropdowns). Will later be populated from MQTT; for now hardcoded list.
    public ObservableCollection<string> FirmwareOptionsP41 { get; } = new();
    public ObservableCollection<string> FirmwareOptionsP42 { get; } = new();
    public ObservableCollection<string> FirmwareOptionsP46 { get; } = new();
    public ObservableCollection<string> FirmwareOptionsP47 { get; } = new();
    public ObservableCollection<string> FirmwareOptionsP48 { get; } = new();

    [ObservableProperty] private string selectedFirmwareP41 = "";
    [ObservableProperty] private string selectedFirmwareP42 = "";
    [ObservableProperty] private string selectedFirmwareP46 = "";
    [ObservableProperty] private string selectedFirmwareP47 = "";
    [ObservableProperty] private string selectedFirmwareP48 = "";

    [ObservableProperty] private string connectionStatus = "Disconnected";
    [ObservableProperty] private bool isConnected;

    private readonly System.Windows.Threading.DispatcherTimer _gatewayStaleTimer;
    private static readonly TimeSpan GatewayOfflineAfter = TimeSpan.FromMinutes(5);

    public string ConnectButtonText => IsConnected ? "Disconnect" : "Connect";
    public string DevicesSubtitle => $"{_devices.Count} unique device(s) • model: {SensorFilter} • filter: {DeviceFilter}";

    private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> _gwSeenMacs
    = new(StringComparer.OrdinalIgnoreCase);
    public MainViewModel()
    {
        var s = _store.Load();

        // your current settings model uses nested objects
        MqttHost = s.mqtt.host;
        MqttPort = s.mqtt.port;
        MqttTopic = s.mqtt.topic;
        MqttUser = s.mqtt.username;
        MqttPassword = s.mqtt.password;
        UseTls = s.mqtt.useTls;
        IgnoreTlsErrors = s.mqtt.ignoreTlsErrors;

        NetworkId = s.accessapp.networkId;
        CommandTopicTemplate = s.accessapp.commandTopicTemplate;
        DefaultCommand = s.accessapp.defaultCommand;

        _mqtt.ConnectionChanged += (connected, status) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsConnected = connected;
                ConnectionStatus = status;
                OnPropertyChanged(nameof(ConnectButtonText));
            });
        };

        _mqtt.Message += OnMqttMessage;

        LoadProductMap();
        LoadFirmwareOptions();

        FilteredDevices = CollectionViewSource.GetDefaultView(_devices);
        FilteredDevices.Filter = obj =>
        {
            if (obj is not DiscoveredDevice d) return false;

            if (!string.IsNullOrWhiteSpace(SensorFilter) && !SensorFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                if (!d.SensorModel.Equals(SensorFilter, StringComparison.OrdinalIgnoreCase))
                    return false;
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

        QueueView = CollectionViewSource.GetDefaultView(QueueItems);
        QueueView.SortDescriptions.Clear();
        // Put Done items at the bottom, then newest updates on top
        QueueView.SortDescriptions.Add(new SortDescription(nameof(QueueItem.QueueSortKey), ListSortDirection.Ascending));
        QueueView.SortDescriptions.Add(new SortDescription(nameof(QueueItem.LastUpdateUtc), ListSortDirection.Descending));

        _gatewayStaleTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10) // responsive, but cheap
        };

        _gatewayStaleTimer.Tick += (_, _) =>
        {
            var nowUtc = DateTimeOffset.UtcNow;

            foreach (var gw in CassiaGateways)
            {
                // If never seen: you can decide whether to force offline or keep unknown.
                if (gw.LastSeenUtc == DateTimeOffset.MinValue)
                    continue;

                var stale = (nowUtc - gw.LastSeenUtc) > GatewayOfflineAfter;

                if (stale)
                {
                    if (!string.Equals(gw.State, "offline", StringComparison.OrdinalIgnoreCase))
                        gw.State = "offline";
                }
                // NOTE: do NOT force online here — only force offline when stale.
            }
        };

        _gatewayStaleTimer.Start();
    }

    partial void OnDeviceFilterChanged(string value)
    {
        RequestDevicesRefresh();
        OnPropertyChanged(nameof(DevicesSubtitle));
    }

    partial void OnSensorFilterChanged(string value)
    {
        RequestDevicesRefresh();
        OnPropertyChanged(nameof(DevicesSubtitle));
    }

    private string GetFirmwareForModel(string model)
    {
        model = (model ?? "").ToUpperInvariant();
        return model switch
        {
            "P41" => SelectedFirmwareP41,
            "P42" => SelectedFirmwareP42,
            "P46" => SelectedFirmwareP46,
            "P47" => SelectedFirmwareP47,
            "P48" => SelectedFirmwareP48,
            _ => ""
        };
    }

    private void LoadFirmwareOptions()
    {
        FirmwareOptionsP48.Clear();
        foreach (var v in new[] { "v02.12", "v02.14", "v02.15", "v02.18", "v02.21", "v02.27", "v02.30", "v02.32" })
            FirmwareOptionsP48.Add(v);

        FirmwareOptionsP47.Clear();
        foreach (var v in new[] { "v02.18", "v02.27" })
            FirmwareOptionsP47.Add(v);

        FirmwareOptionsP46.Clear();
        foreach (var v in new[] { "v02.16", "v02.20", "v02.25", "v02.28", "v02.31", "v02.33", "v02.35" })
            FirmwareOptionsP46.Add(v);

        FirmwareOptionsP41.Clear();
        foreach (var v in new[] { "v02.12", "v02.14", "v02.15", "v02.17", "v02.21", "v02.27", "v02.30", "v02.32", "v02.36" })
            FirmwareOptionsP41.Add(v);

        FirmwareOptionsP42.Clear();
        foreach (var v in new[] { "v02.12", "v02.14", "v02.15", "v02.17", "v02.21", "v02.27", "v02.30", "v02.32", "v02.36" })
            FirmwareOptionsP42.Add(v);

        SelectedFirmwareP48 = FirmwareOptionsP48.LastOrDefault() ?? "";
        SelectedFirmwareP47 = FirmwareOptionsP47.LastOrDefault() ?? "";
        SelectedFirmwareP46 = FirmwareOptionsP46.LastOrDefault() ?? "";
        SelectedFirmwareP41 = FirmwareOptionsP41.LastOrDefault() ?? "";
        SelectedFirmwareP42 = FirmwareOptionsP42.LastOrDefault() ?? "";
    }

    private void LoadProductMap()
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "SensorSourceBLE.json");
            if (!System.IO.File.Exists(path)) return;

            var json = System.IO.File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                var shortDesc = el.TryGetProperty("DetectorShortDescription", out var s) ? s.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(shortDesc))
                    continue;

                var m = Regex.Match(shortDesc, @"^(P\d{2})", RegexOptions.IgnoreCase);
                if (!m.Success) continue;

                var model = m.Groups[1].Value.ToUpperInvariant();
                if (model == "P49") model = "P46";

                _productToModel[name] = model;
            }
        }
        catch
        {
            // optional mapping; ignore if missing
        }
    }

    [RelayCommand]
    private async Task ToggleConnectAsync()
    {
        try
        {
            if (IsConnected)
            {
                await _mqtt.DisconnectAsync();
                return;
            }

            await _mqtt.ConnectAsync(
                MqttHost,
                MqttPort,
                MqttUser,
                MqttPassword ?? "",
                UseTls,
                IgnoreTlsErrors,
                MqttTopic,
                _appCts.Token);

            ConnectionStatus = "Connected";
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Error: " + ex.Message;
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _store.Save(new AppSettings
        {
            mqtt = new MqttSettings
            {
                host = MqttHost,
                port = MqttPort,
                topic = MqttTopic,
                username = MqttUser,
                password = MqttPassword ?? "",
                useTls = UseTls,
                ignoreTlsErrors = IgnoreTlsErrors
            },
            accessapp = new AccessAppSettings
            {
                networkId = NetworkId,
                commandTopicTemplate = CommandTopicTemplate,
                defaultCommand = DefaultCommand
            }
        });

        ConnectionStatus = "Saved appsettings.json";
    }

    [RelayCommand]
    private void ClearDevices()
    {
        _devices.Clear();
        CassiaGateways.Clear();
        _gwSeenMacs.Clear(); // <-- reset unique counters
        OnPropertyChanged(nameof(DevicesSubtitle));
    }

    [RelayCommand]
    private void ClearQueue() => QueueItems.Clear();

    // IMPORTANT: Keep method names QueueSingle/QueueSelected so your XAML/code-behind bindings keep working.
    // These are async, so toolkit generates QueueSingleCommand/QueueSelectedCommand as IAsyncRelayCommand.
    [RelayCommand]
    private async Task QueueSingle()
    {
        if (SelectedDevice != null)
            await QueueDeviceAndRequestAsync(SelectedDevice);
    }

    [RelayCommand]
    private async Task QueueSelected()
    {
        var selected = _devices.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0 && SelectedDevice != null)
            selected.Add(SelectedDevice);

        foreach (var d in selected)
            await QueueDeviceAndRequestAsync(d);
    }

    /// <summary>
    /// Queue + publish start-update immediately.
    /// Status becomes "Requested update" and we wait for tele/progress to mark it really queued.
    /// </summary>
    private async Task QueueDeviceAndRequestAsync(DiscoveredDevice d)
    {
        if (d == null || string.IsNullOrWhiteSpace(d.Mac))
            return;

        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        // Determine model
        var model = (d.SensorModel ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(model))
        {
            // try derive from product number if present
            if (!string.IsNullOrWhiteSpace(d.ProductNumber) && _productToModel.TryGetValue(d.ProductNumber, out var m2))
                model = m2;
        }
        if (string.IsNullOrWhiteSpace(model))
            model = "P46"; // safe default

        // Determine firmware from dropdown selection
        var fw = GetFirmwareForModel(model);

        // Determine cassia (best RSSI), else first online cassia, else any cassia
        var cassia = (d.BestCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassia) || cassia.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            cassia = CassiaGateways.FirstOrDefault(g => string.Equals(g.State, "online", StringComparison.OrdinalIgnoreCase))?.Name
                     ?? CassiaGateways.FirstOrDefault()?.Name
                     ?? "";
        }
        if (string.IsNullOrWhiteSpace(cassia))
        {
            ConnectionStatus = "No Cassia gateway known yet (cannot send start-update)";
            return;
        }

        // Create/update queue item
        var qi = QueueItems.FirstOrDefault(q => q.Mac.Equals(d.Mac, StringComparison.OrdinalIgnoreCase));
        if (qi == null)
        {
            qi = new QueueItem
            {
                Mac = d.Mac,
                Command = DefaultCommand
            };
            QueueItems.Add(qi);
        }

        qi.Cassia = cassia;
        qi.DetectorType = model;          // payload DetectorType
        qi.FirmwareVersion = fw;          // payload FirmwareVersion
        qi.Command = DefaultCommand;      // start-update
        qi.Status = "Requested update";
        qi.Progress = 0;
        qi.Notes = "";
        qi.LastUpdateUtc = DateTimeOffset.UtcNow;

        // Mirror into discovered list immediately
        MirrorQueueToDevice(qi);

        QueueView.Refresh();

        // Publish request
        var topic = CommandTopicTemplate
            .Replace("{networkId}", NetworkId)
            .Replace("{cassia}", cassia)
            .Replace("{command}", DefaultCommand);

        var payload = new[]
        {
            new
            {
                DetectorType = model,
                FirmwareVersion = fw,
                MacAddress = d.Mac,
                Pincode = ""
            }
        };

        try
        {
            await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token);

            // Keep "Requested update" until we see tele/progress for that MAC.
            qi.LastUpdateUtc = DateTimeOffset.UtcNow;
            MirrorQueueToDevice(qi);
            QueueView.Refresh();
        }
        catch (Exception ex)
        {
            qi.Status = "Error";
            qi.Notes = "Publish failed: " + ex.Message;
            qi.LastUpdateUtc = DateTimeOffset.UtcNow;
            MirrorQueueToDevice(qi);
            QueueView.Refresh();
        }
    }

    [RelayCommand]
    private async Task StartQueueAsync()
    {
        // Optional “re-send” for items that are still not picked up.
        if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
        if (QueueItems.Count == 0) return;

        foreach (var item in QueueItems)
        {
            if (item.Status.Equals("Done", StringComparison.OrdinalIgnoreCase)) continue;

            var dev = _devices.FirstOrDefault(d => d.Mac.Equals(item.Mac, StringComparison.OrdinalIgnoreCase));
            if (dev == null) continue;

            await QueueDeviceAndRequestAsync(dev);
        }
    }

    private DiscoveredDevice EnsureDeviceExistsForProgress(string mac)
    {
        var dev = _devices.FirstOrDefault(d => d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
        if (dev != null) return dev;

        dev = new DiscoveredDevice { Mac = mac };
        _devices.Add(dev);
        return dev;
    }

    private void MirrorQueueToDevice(QueueItem qi)
    {
        var dev = _devices.FirstOrDefault(d => d.Mac.Equals(qi.Mac, StringComparison.OrdinalIgnoreCase));
        if (dev == null) return;

        dev.ProcessStatus = qi.Status ?? "";
        dev.ProcessProgress = qi.Progress;
        dev.ProcessCassia = qi.Cassia ?? "";
        dev.ProcessFirmware = qi.FirmwareVersion ?? "";
        dev.ProcessLastUpdateUtc = qi.LastUpdateUtc;
    }

    // ---- MQTT parsing ----
    // accessapp/dk-lab/tele/cassia-01/status
    // accessapp/dk-lab/tele/cassia-01/discovered
    // accessapp/dk-lab/tele/cassia-01/progress
    private static readonly Regex TopicRx =
        new(@"^accessapp/(?<net>[^/]+)/(?<kind>tele|cmd)/(?<cassia>[^/]+)/(?<leaf>[^/]+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private void OnMqttMessage(string topic, string payload)
    {
        var m = TopicRx.Match(topic);
        if (!m.Success) return;

        var net = m.Groups["net"].Value;
        if (!net.Equals(NetworkId, StringComparison.OrdinalIgnoreCase))
            return;

        var kind = m.Groups["kind"].Value.ToLowerInvariant();
        var cassia = m.Groups["cassia"].Value;
        var leaf = m.Groups["leaf"].Value.ToLowerInvariant();

        if (kind == "tele" && leaf == "status")
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? cassia : cassia;
                var state = root.TryGetProperty("state", out var s) ? s.GetString() ?? "unknown" : "unknown";
                var ts = root.TryGetProperty("time", out var t) && t.TryGetDateTimeOffset(out var dto) ? dto : DateTimeOffset.UtcNow;
                int queue = root.TryGetProperty("queue", out var q) ? q.GetInt32() : 0;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    var gw = CassiaGateways.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (gw == null)
                    {
                        gw = new CassiaGateway { Name = name, NetworkId = net };
                        CassiaGateways.Add(gw);
                    }

                    gw.State = state;
                    gw.LastSeenUtc = ts;
                    gw.Queue = queue;
                });
            }
            catch { }
            return;
        }

        if (kind == "tele" && leaf == "progress")
        {
            // { mac, progressPercent, stage, time, name, networkId }
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                var ts = root.TryGetProperty("time", out var t) && t.TryGetDateTimeOffset(out var dto) ? dto : DateTimeOffset.UtcNow;
                var mac = root.TryGetProperty("mac", out var macEl) ? macEl.GetString() ?? "" : "";
                var stage = root.TryGetProperty("stage", out var stEl) ? stEl.GetString() ?? "" : "";
                var fwTarget = root.TryGetProperty("firmwareTarget", out var ftEl) ? ftEl.GetString() ?? "" : "";

                double pct = 0;
                if (root.TryGetProperty("progressPercent", out var pEl) && pEl.TryGetDouble(out var pd))
                    pct = pd;

                if (!string.IsNullOrWhiteSpace(mac))
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _ = EnsureDeviceExistsForProgress(mac);

                        var qi = QueueItems.FirstOrDefault(q => q.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
                        if (qi == null)
                        {
                            qi = new QueueItem
                            {
                                Mac = mac,
                                Cassia = cassia,
                                Command = DefaultCommand,
                                Status = "Queued",
                                Notes = "Auto-added from progress",
                                LastUpdateUtc = ts
                            };
                            QueueItems.Add(qi);
                        }

                        qi.Progress = (int)Math.Clamp(Math.Round(pct), 0, 100);
                        qi.Cassia = cassia;
                        qi.LastUpdateUtc = ts;

                        // IMPORTANT: first progress means the backend accepted it.
                        if (qi.Status.Equals("Requested update", StringComparison.OrdinalIgnoreCase))
                            qi.Status = "Queued";

                        // stage wins if present
                        if (!string.IsNullOrWhiteSpace(stage))
                            qi.Status = stage;

                        if (!string.IsNullOrWhiteSpace(fwTarget))
                            qi.FirmwareVersion = fwTarget;

                        if (qi.Progress >= 100)
                            qi.Status = "Done";

                        // Mirror into discovered list (no full refresh spam)
                        var dev2 = _devices.FirstOrDefault(d => d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
                        if (dev2 == null)
                        {
                            dev2 = new DiscoveredDevice { Mac = mac };
                            _devices.Add(dev2);
                        }

                        dev2.ProcessStatus = qi.Status ?? "";
                        dev2.ProcessProgress = qi.Progress;
                        dev2.ProcessCassia = qi.Cassia ?? cassia;
                        dev2.ProcessFirmware = qi.FirmwareVersion ?? "";
                        dev2.ProcessLastUpdateUtc = qi.LastUpdateUtc;

                        QueueView.Refresh();
                        // keep selection stable; throttled refresh only
                        RequestDevicesRefresh();
                    });
                }
            }
            catch { }
            return;
        }

        if (kind == "tele" && leaf == "discovered")
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                var ts = root.TryGetProperty("time", out var t) && t.TryGetDateTimeOffset(out var dto) ? dto : DateTimeOffset.UtcNow;

                if (root.TryGetProperty("devices", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var gw = CassiaGateways.FirstOrDefault(x => x.Name.Equals(cassia, StringComparison.OrdinalIgnoreCase));
                        if (gw == null)
                        {
                            gw = new CassiaGateway { Name = cassia, NetworkId = net };
                            CassiaGateways.Add(gw);
                        }

                        gw.LastSeenUtc = ts;
                        gw.State = "online";

                        if (!_gwSeenMacs.TryGetValue(cassia, out var seen))
                        {
                            seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            _gwSeenMacs[cassia] = seen;
                        }

                        foreach (var dev in arr.EnumerateArray())
                        {
                            var mac = dev.TryGetProperty("mac", out var macEl) ? macEl.GetString() ?? "" : "";
                            if (string.IsNullOrWhiteSpace(mac)) continue;

                            // Track unique MACs per gateway
                            seen.Add(mac);

                            var rssi = dev.TryGetProperty("rssi", out var rssiEl) && rssiEl.TryGetInt32(out var r) ? r : int.MinValue;
                            var dn = dev.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                            var pn = dev.TryGetProperty("productNumber", out var pnEl) ? pnEl.GetString() ?? "" : "";
                            var fam = dev.TryGetProperty("detectorFamily", out var famEl) ? famEl.GetString() ?? "" : "";
                            var typ = dev.TryGetProperty("detectorType", out var typEl) ? typEl.GetString() ?? "" : "";

                            var existing = _devices.FirstOrDefault(x => x.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
                            if (existing == null)
                            {
                                existing = new DiscoveredDevice { Mac = mac };
                                _devices.Add(existing);
                            }

                            if (!string.IsNullOrWhiteSpace(dn)) existing.Name = dn;
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

                            existing.UpdateFromCassia(cassia, rssi, ts);
                        }


                        // show unique count since last clear
                        gw.DevicesSeen = seen.Count;

                        RequestDevicesRefresh();
                        OnPropertyChanged(nameof(DevicesSubtitle));
                    });
                }
            }
            catch { }
            return;
        }
    }

    private bool _pendingDevicesRefresh;

    private void RequestDevicesRefresh()
    {
        if (_pendingDevicesRefresh) return;
        _pendingDevicesRefresh = true;

        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(250); // throttle
            _pendingDevicesRefresh = false;

            // preserve selection
            var selectedMac = SelectedDevice?.Mac;

            FilteredDevices.Refresh();

            if (!string.IsNullOrWhiteSpace(selectedMac))
                SelectedDevice = _devices.FirstOrDefault(d => d.Mac.Equals(selectedMac, StringComparison.OrdinalIgnoreCase));
        });
    }
}
