using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly MqttClientService _mqtt = new();
    private readonly SettingsStore _store = new();

    // ---- Firmware manifest (tele/.../fw-manifest) ----
    private readonly DispatcherTimer _fwManifestValidateTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _fwManifestTimeoutTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _fwManifestTimeoutArmed;
    private string _lastFwManifestMissingHash = "";

    // After each connect we wait for per-gateway status, then request its FW manifest once.
    private readonly HashSet<string> _fwManifestRequestedForGw = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _connectedAtUtc = DateTimeOffset.MinValue;


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
    [ObservableProperty] private string? selectedQueueMac;

    [ObservableProperty] private bool enableDoubleClickQueue;

    [ObservableProperty] private string deviceFilter = "";
    [ObservableProperty] private string sensorFilter = "All";

    [ObservableProperty] private string mqttHost = "prod.statistics.niko-test.nu";
    [ObservableProperty] private int mqttPort = 18883;
    [ObservableProperty] private string mqttTopic = "accessapp/#";
    [ObservableProperty] private string mqttUser = "accessapp";
    [ObservableProperty] private string? mqttPassword = "Niko1234!";
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

    // ---- Upgrade log viewer (tele/.../upgrade-log) ----
    // Raw lines (kept for debugging / copy-paste)
    public ObservableCollection<string> UpgradeLogLines { get; } = new();

    // Grouped view by logId
    public ObservableCollection<UpgradeLogGroup> UpgradeLogGroups { get; } = new();
    public ICollectionView UpgradeLogGroupsView { get; }

    [ObservableProperty] private string upgradeLogText = "";
    [ObservableProperty] private string upgradeLogStatus = "Idle";
    [ObservableProperty] private int upgradeLogTotalLines;
    [ObservableProperty] private int upgradeLogReceivedLines;
    [ObservableProperty] private CassiaGateway? selectedLogGateway;

    public ObservableCollection<string> LogGatewayOptions { get; } = new();
    [ObservableProperty] private string selectedLogGatewayName = "All";

    // Search (matches MAC/logId/line; works across all groups)
    [ObservableProperty] private string upgradeLogSearchText = "";

    public string UpgradeLogSummary =>
        UpgradeLogTotalLines > 0
            ? $"{UpgradeLogStatus} • {UpgradeLogReceivedLines}/{UpgradeLogTotalLines} lines"
            : UpgradeLogStatus;

    partial void OnUpgradeLogStatusChanged(string value) => OnPropertyChanged(nameof(UpgradeLogSummary));
    partial void OnUpgradeLogTotalLinesChanged(int value) => OnPropertyChanged(nameof(UpgradeLogSummary));
    partial void OnUpgradeLogReceivedLinesChanged(int value) => OnPropertyChanged(nameof(UpgradeLogSummary));

    private readonly System.Text.StringBuilder _upgradeLogSb = new();
    private bool _pendingUpgradeLogTextRefresh;

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

            // We request FW manifests when we see each gateway's status (online)
            if (connected)
            {
                _connectedAtUtc = DateTimeOffset.UtcNow;
                _fwManifestRequestedForGw.Clear();
            }
        };

        _mqtt.Message += OnMqttMessage;

        _fwManifestValidateTimer.Tick += (_, _) =>
        {
            _fwManifestValidateTimer.Stop();
            ValidateFwManifestsAndUpdateOptions();
        };

        _fwManifestTimeoutTimer.Tick += (_, _) =>
        {
            _fwManifestTimeoutTimer.Stop();
            if (_fwManifestTimeoutArmed)
            {
                _fwManifestTimeoutArmed = false;
                ShowFwManifestTimeoutIfAny();
            }
        };

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

        UpgradeLogGroupsView = CollectionViewSource.GetDefaultView(UpgradeLogGroups);
        UpgradeLogGroupsView.SortDescriptions.Clear();
        UpgradeLogGroupsView.SortDescriptions.Add(new SortDescription(nameof(UpgradeLogGroup.LastTimeLocal), ListSortDirection.Descending));
        UpgradeLogGroupsView.Filter = obj =>
        {
            if (obj is not UpgradeLogGroup g) return false;
            // Gateway filter
            if (!string.IsNullOrWhiteSpace(SelectedLogGatewayName)
                && !SelectedLogGatewayName.Equals("All", StringComparison.OrdinalIgnoreCase)
                && !g.Cassia.Equals(SelectedLogGatewayName, StringComparison.OrdinalIgnoreCase))
                return false;

            // Search filter (MAC/logId/line)
            var q = (UpgradeLogSearchText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(q)) return true;

            return (g.Mac?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (g.LogId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (g.LogIdMacPart?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || g.Entries.Any(e =>
                       (e.Mac?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (e.Line?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        };

        LogGatewayOptions.Clear();
        LogGatewayOptions.Add("All");
    }

    partial void OnSelectedLogGatewayNameChanged(string value)
    {
        UpgradeLogGroupsView.Refresh();
    }

    partial void OnUpgradeLogSearchTextChanged(string value)
    {
        UpgradeLogGroupsView.Refresh();
    }

    partial void OnSelectedQueueItemChanged(QueueItem? value)
    {
        // Sync queue selection -> device selection
        SelectedQueueMac = value?.Mac;

        if (string.IsNullOrWhiteSpace(SelectedQueueMac))
            return;

        var dev = _devices.FirstOrDefault(d =>
            string.Equals(d.Mac, SelectedQueueMac, StringComparison.OrdinalIgnoreCase));

        if (dev != null)
            SelectedDevice = dev;
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
      
        FirmwareOptionsP47.Clear();
      
        FirmwareOptionsP46.Clear();
        
        FirmwareOptionsP41.Clear();
        
        FirmwareOptionsP42.Clear();
      
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

    // Upgrade-log / text-line parsing
    private static readonly Regex LogLineMacRx =
        new(@"\bmac=(?<mac>([0-9A-F]{2}:){5}[0-9A-F]{2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LogLineStageRx =
        new(@"\bstage=(?<stage>.*?)\s+time=", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LogLineStatusRx =
        new(@"\bstatus=(?<status>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LogLineFwRx =
        new(@"\bfw=(?<fw>[^\s]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SensorAppFromStatusRx =
        new(@"Sensor:\s*App:\s*(?<app>[^\s|]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LogLineIdRx =
        new(@"\[logId=(?<id>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LogLineTimeRx =
        new(@"\btime=(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
                double totalSpeedpct = root.TryGetProperty("totalSpeedpct", out var sp) ? sp.GetDouble() : 0;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var gw = CassiaGateways.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (gw == null)
                    {
                        gw = new CassiaGateway { Name = name, NetworkId = net };
                        CassiaGateways.Add(gw);
                    }

                    // default for upgrade log tab
                    if (SelectedLogGateway == null)
                        SelectedLogGateway = gw;

                    if (!LogGatewayOptions.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        LogGatewayOptions.Add(name);

                    gw.State = state;
                    gw.LastSeenUtc = ts;
                    gw.Queue = queue;
                    gw.TotalSpeedpct = totalSpeedpct;


                    // When a gateway announces itself, ask it for FW manifest once per connect.
                    MaybeAutoRequestFirmwareManifestAfterStatus(gw);
                });
            }
            catch { }
            return;
        }

        if (kind == "tele" && leaf == "upgrade-log")
        {
            HandleUpgradeLogTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "fw-manifest")
        {
            HandleFwManifestTele(cassia, payload);
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

                        if (!LogGatewayOptions.Any(x => x.Equals(cassia, StringComparison.OrdinalIgnoreCase)))
                            LogGatewayOptions.Add(cassia);

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

    private void HandleUpgradeLogTele(string cassia, string payload)
    {
        // Example messages seen in mqtt.log:
        //  {"type":"saved-log-begin","totalLines":2340,"timeLocal":"2026-01-12 15:48:28"}
        //  {"type":"saved-log-chunk","seq":0,"lines":["..."]}
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
            type = type.Trim().ToLowerInvariant();

            Application.Current.Dispatcher.Invoke(() =>
            {
                // Default the gateway picker (handy when you only have one cassia)
                SelectedLogGateway ??= CassiaGateways.FirstOrDefault();

                if (type == "saved-log-begin")
                {
                    UpgradeLogLines.Clear();
                    _upgradeLogSb.Clear();

                    UpgradeLogTotalLines = root.TryGetProperty("totalLines", out var tl) && tl.TryGetInt32(out var total) ? total : 0;
                    UpgradeLogReceivedLines = 0;
                    var timeLocal = root.TryGetProperty("timeLocal", out var tlc) ? (tlc.GetString() ?? "") : "";
                    UpgradeLogStatus = string.IsNullOrWhiteSpace(timeLocal)
                        ? $"Receiving log from {cassia}…"
                        : $"Receiving log from {cassia}… (saved {timeLocal})";

                    UpgradeLogText = "";
                    return;
                }

                if (type == "saved-log-chunk")
                {
                    if (root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var le in linesEl.EnumerateArray())
                        {
                            var line = le.GetString() ?? "";
                            if (string.IsNullOrEmpty(line)) continue;
                            UpgradeLogLines.Add(line);
                            _upgradeLogSb.AppendLine(line);
                            UpgradeLogReceivedLines++;

                            // Grouped view + status mirror
                            AddUpgradeLogEntryFromLine(cassia, line);

                            // Use upgrade-log as a secondary status source (useful when progress isn't emitted)
                            ApplyStatusFromUpgradeLogLine(cassia, line);
                        }

                        RequestUpgradeLogTextRefresh();
                        UpgradeLogStatus = UpgradeLogTotalLines > 0
                            ? $"Receiving… {UpgradeLogReceivedLines}/{UpgradeLogTotalLines} lines"
                            : $"Receiving… {UpgradeLogReceivedLines} lines";
                    }
                    return;
                }

                // Some deployments publish a single JSON log entry (no "type"):
                // {
                //   "logId":"...", "mac":"..", "stage":"...", "status":"...", "fw":"...", "timeLocal":"...", "line":"[...]"
                // }
                if (string.IsNullOrWhiteSpace(type) && root.ValueKind == JsonValueKind.Object)
                {
                    if (TryAddUpgradeLogEntryFromJson(cassia, root, out var line2))
                    {
                        if (!string.IsNullOrWhiteSpace(line2))
                        {
                            UpgradeLogLines.Add(line2);
                            _upgradeLogSb.AppendLine(line2);
                            UpgradeLogReceivedLines++;
                            ApplyStatusFromUpgradeLogLine(cassia, line2);
                            RequestUpgradeLogTextRefresh();
                        }
                        UpgradeLogStatus = "upgrade-log";
                        return;
                    }
                }

                if (type == "saved-log-end")
                {
                    UpgradeLogStatus = UpgradeLogTotalLines > 0
                        ? $"Done ({UpgradeLogReceivedLines}/{UpgradeLogTotalLines} lines)"
                        : $"Done ({UpgradeLogReceivedLines} lines)";
                    RequestUpgradeLogTextRefresh();
                    return;
                }

                // fallback
                UpgradeLogStatus = string.IsNullOrWhiteSpace(type) ? "upgrade-log" : type;
            });
        }
        catch
        {
            // ignore malformed chunks
        }
    }

    private void AddUpgradeLogEntryFromLine(string cassia, string line)
    {
        try
        {
            var idm = LogLineIdRx.Match(line);
            if (!idm.Success) return;
            var logId = idm.Groups["id"].Value.Trim();
            if (string.IsNullOrWhiteSpace(logId)) return;

            var macm = LogLineMacRx.Match(line);
            var mac = macm.Success ? macm.Groups["mac"].Value.Trim() : "";

            var stagem = LogLineStageRx.Match(line);
            var stage = stagem.Success ? stagem.Groups["stage"].Value.Trim() : "";

            var statusm = LogLineStatusRx.Match(line);
            var status = statusm.Success ? statusm.Groups["status"].Value.Trim() : "";

            var fwm = LogLineFwRx.Match(line);
            var fw = fwm.Success ? fwm.Groups["fw"].Value.Trim() : "";

            var timem = LogLineTimeRx.Match(line);
            var timeLocal = ParseLocalTime(timem.Success ? timem.Groups["time"].Value : null);

            var entry = new UpgradeLogEntry
            {
                Cassia = cassia,
                LogId = logId,
                Mac = mac,
                Stage = stage,
                Status = status,
                Firmware = fw,
                TimeLocal = timeLocal,
                Line = line
            };

            AddUpgradeLogEntry(entry);
        }
        catch
        {
            // ignore
        }
    }

    private bool TryAddUpgradeLogEntryFromJson(string cassia, JsonElement root, out string line)
    {
        line = "";
        try
        {
            var logId = root.TryGetProperty("logId", out var idEl) ? (idEl.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(logId)) return false;

            var mac = root.TryGetProperty("mac", out var macEl) ? (macEl.GetString() ?? "") : "";
            var stage = root.TryGetProperty("stage", out var stEl) ? (stEl.GetString() ?? "") : "";
            var status = root.TryGetProperty("status", out var sEl) ? (sEl.GetString() ?? "") : "";
            var fw = root.TryGetProperty("fw", out var fwEl) ? (fwEl.GetString() ?? "") : "";
            var timeStr = root.TryGetProperty("timeLocal", out var tlEl) ? (tlEl.GetString() ?? "") : "";
            line = root.TryGetProperty("line", out var lEl) ? (lEl.GetString() ?? "") : "";

            if (string.IsNullOrWhiteSpace(line))
            {
                // Fallback recreate a readable line
                line = $"[logId={logId}] stage={stage} time={timeStr} mac={mac} fw={fw} status={status}";
            }

            var entry = new UpgradeLogEntry
            {
                Cassia = cassia,
                LogId = logId.Trim(),
                Mac = mac.Trim(),
                Stage = stage.Trim(),
                Status = status.Trim(),
                Firmware = fw.Trim(),
                TimeLocal = ParseLocalTime(timeStr),
                Line = line
            };

            AddUpgradeLogEntry(entry);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void AddUpgradeLogEntry(UpgradeLogEntry entry)
    {
        // Find group
        var g = UpgradeLogGroups.FirstOrDefault(x => x.LogId.Equals(entry.LogId, StringComparison.OrdinalIgnoreCase)
                                                 && x.Cassia.Equals(entry.Cassia, StringComparison.OrdinalIgnoreCase));
        if (g == null)
        {
            g = new UpgradeLogGroup
            {
                Cassia = entry.Cassia,
                LogId = entry.LogId,
                Mac = entry.Mac
            };
            UpgradeLogGroups.Add(g);
        }

        if (string.IsNullOrWhiteSpace(g.Mac) && !string.IsNullOrWhiteSpace(entry.Mac))
            g.Mac = entry.Mac;

        g.AddEntry(entry);
        UpgradeLogGroupsView.Refresh();
    }

    private static DateTimeOffset ParseLocalTime(string? timeStr)
    {
        if (string.IsNullOrWhiteSpace(timeStr)) return DateTimeOffset.MinValue;

        // Formats we see: "2026-01-12 16:23:45" (no tz)
        if (DateTime.TryParse(timeStr, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var dt))
        {
            if (dt.Kind == DateTimeKind.Unspecified)
                dt = DateTime.SpecifyKind(dt, DateTimeKind.Local);
            return new DateTimeOffset(dt);
        }

        return DateTimeOffset.MinValue;
    }

    private void ApplyStatusFromUpgradeLogLine(string cassia, string line)
    {
        try
        {
            var mm = LogLineMacRx.Match(line);
            if (!mm.Success) return;
            var mac = mm.Groups["mac"].Value;
            if (string.IsNullOrWhiteSpace(mac)) return;

            var stage = "";
            var sm = LogLineStageRx.Match(line);
            if (sm.Success) stage = sm.Groups["stage"].Value.Trim();

            var status = "";
            var stm = LogLineStatusRx.Match(line);
            if (stm.Success) status = stm.Groups["status"].Value.Trim();

            // Update QueueItem
            var qi = QueueItems.FirstOrDefault(q => q.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
            if (qi == null)
            {
                // Only auto-create if it looks like an upgrade-related line
                if (string.IsNullOrWhiteSpace(stage)) return;
                qi = new QueueItem
                {
                    Mac = mac,
                    Cassia = cassia,
                    Status = stage,
                    Notes = status,
                    LastUpdateUtc = DateTimeOffset.UtcNow
                };
                QueueItems.Add(qi);
            }
            else
            {
                qi.Cassia = string.IsNullOrWhiteSpace(qi.Cassia) ? cassia : qi.Cassia;
                if (!string.IsNullOrWhiteSpace(stage)) qi.Status = stage;
                if (!string.IsNullOrWhiteSpace(status)) qi.Notes = status;
                qi.LastUpdateUtc = DateTimeOffset.UtcNow;
            }

            // Update DiscoveredDevice view
            var dev = EnsureDeviceExistsForProgress(mac);
            if (!string.IsNullOrWhiteSpace(stage)) dev.ProcessStatus = stage;
            dev.ProcessCassia = string.IsNullOrWhiteSpace(dev.ProcessCassia) ? cassia : dev.ProcessCassia;
            dev.ProcessLastUpdateUtc = DateTimeOffset.UtcNow;

            // Try extract "Sensor: App: <X>" into CurrentFw for the device list
            if (!string.IsNullOrWhiteSpace(status))
            {
                var appm = SensorAppFromStatusRx.Match(status);
                if (appm.Success)
                {
                    var app = appm.Groups["app"].Value;
                    if (!string.IsNullOrWhiteSpace(app)) dev.CurrentFw = app;
                }
            }

            MirrorQueueToDevice(qi);
            QueueView.Refresh();
            RequestDevicesRefresh();
        }
        catch
        {
            // ignore per-line parse errors
        }
    }

    private void RequestUpgradeLogTextRefresh()
    {
        if (_pendingUpgradeLogTextRefresh) return;
        _pendingUpgradeLogTextRefresh = true;

        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(150);
            _pendingUpgradeLogTextRefresh = false;
            UpgradeLogText = _upgradeLogSb.ToString();
        });
    }

    [RelayCommand]
    private async Task RequestUpgradeLogAsync()
    {
        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        var cassia = !string.IsNullOrWhiteSpace(SelectedLogGatewayName) && !SelectedLogGatewayName.Equals("All", StringComparison.OrdinalIgnoreCase)
            ? SelectedLogGatewayName
            : (SelectedLogGateway?.Name ?? CassiaGateways.FirstOrDefault()?.Name ?? "");

        if (string.IsNullOrWhiteSpace(cassia))
        {
            ConnectionStatus = "No Cassia gateway known yet";
            return;
        }

        var topic = CommandTopicTemplate
            .Replace("{networkId}", NetworkId)
            .Replace("{cassia}", cassia)
            .Replace("{command}", "send-upgrade-log");

        UpgradeLogStatus = $"Requesting saved log from {cassia}…";
        try
        {
            await _mqtt.PublishAsync(topic, "{}", retain: false);
        }
        catch (Exception ex)
        {
            UpgradeLogStatus = "Request failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ClearUpgradeLog()
    {
        UpgradeLogLines.Clear();
        UpgradeLogGroups.Clear();
        _upgradeLogSb.Clear();
        UpgradeLogText = "";
        UpgradeLogStatus = "Idle";
        UpgradeLogTotalLines = 0;
        UpgradeLogReceivedLines = 0;
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

    private void MaybeAutoRequestFirmwareManifestAfterStatus(CassiaGateway gw)
    {
        if (!IsConnected) return;
        if (!string.Equals(gw.StateLower, "online", StringComparison.OrdinalIgnoreCase)) return;

        // Only do this once per connection per gateway.
        if (_fwManifestRequestedForGw.Contains(gw.Name)) return;

        // If we already have a manifest received after this connect, don't re-request automatically.
        var needs = !gw.HasFwManifest || gw.FwManifestLastSeenUtc < _connectedAtUtc;
        if (!needs) return;

        _fwManifestRequestedForGw.Add(gw.Name);
        _ = RequestFirmwareManifestAsync(gw.Name, manual: false);
    }

    [RelayCommand]
    private async Task RefreshFwManifestForCassia(string cassiaName)
    {
        if (string.IsNullOrWhiteSpace(cassiaName)) return;
        await RequestFirmwareManifestAsync(cassiaName, manual: true).ConfigureAwait(false);
    }

    private async Task RequestFirmwareManifestAsync(string? cassiaName, bool manual)
    {
        try
        {
            if (!IsConnected) return;

            // Reset state for a fresh run
            _fwManifestTimeoutArmed = true;
            _fwManifestTimeoutTimer.Stop();
            _fwManifestTimeoutTimer.Start();

            // Ask target gateway (preferred), plus fall back to all aggregator (if present)
            // Examples:
            //   accessapp/{net}/cmd/cassia-01/get-fw-manifest : {}
            //   accessapp/{net}/cmd/all/get-fw-manifest : {}

            if (!string.IsNullOrWhiteSpace(cassiaName))
            {
                var perGwTopic = $"accessapp/{NetworkId}/cmd/{cassiaName}/get-fw-manifest";
                await _mqtt.PublishJsonAsync(perGwTopic, new { }, retain: false, qos: 1).ConfigureAwait(false);
            }

            //var aggTopic = $"accessapp/{NetworkId}/cmd/all/get-fw-manifest";
            //await _mqtt.PublishJsonAsync(aggTopic, new { }, retain: false, qos: 1).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
                MessageBox.Show($"Failed to request firmware manifest.\n\n{ex.Message}", "FW manifest", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    private void HandleFwManifestTele(string cassia, string payload)
    {
        try
        {
            var resp = JsonSerializer.Deserialize<FirmwareManifestTele>(payload, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (resp?.FirmwareManifest == null || resp.FirmwareManifest.Count == 0)
                return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                var gw = CassiaGateways.FirstOrDefault(x => x.Name.Equals(cassia, StringComparison.OrdinalIgnoreCase));
                if (gw == null)
                {
                    gw = new CassiaGateway { Name = cassia, NetworkId = NetworkId };
                    CassiaGateways.Add(gw);
                }

                gw.FwManifestLastSeenUtc = DateTimeOffset.UtcNow;
                gw.FirmwareManifest = new Dictionary<string, string[]>(resp.FirmwareManifest, StringComparer.OrdinalIgnoreCase);

                // Debounced validate + update dropdowns
                _fwManifestValidateTimer.Stop();
                _fwManifestValidateTimer.Start();
            });
        }
        catch
        {
            // ignore malformed payloads
        }
    }

    private void ShowFwManifestTimeoutIfAny()
    {
        // If we have at least one manifest, don't show a timeout warning
        var haveAny = CassiaGateways.Any(g => g.HasFwManifest);
        if (haveAny) return;

        MessageBox.Show(
            "No firmware manifest received yet.\n\n" +
            "Expected one or more retained/tele messages on:\n" +
            $"  accessapp/{NetworkId}/tele/<cassia>/fw-manifest\n\n" +
            "Make sure the Cassia gateways are online and publishing manifests.",
            "FW manifest",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ValidateFwManifestsAndUpdateOptions()
    {
        var union = GetUnionManifest();
        if (union.Count == 0) return;

        UpdateFirmwareOptionsFromUnion(union);

        // Check per gateway for missing versions (relative to union)
        var missingLines = new List<string>();

        foreach (var gw in CassiaGateways.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!gw.HasFwManifest) continue;

            foreach (var kv in union)
            {
                var product = kv.Key;
                var expected = kv.Value;

                if (!gw.FirmwareManifest.TryGetValue(product, out var gotArr) || gotArr == null)
                {
                    missingLines.Add($"{gw.Name}: missing {product}: {string.Join(", ", expected)}");
                    continue;
                }

                var got = new HashSet<string>(gotArr, StringComparer.OrdinalIgnoreCase);
                var miss = expected.Where(v => !got.Contains(v)).ToList();
                if (miss.Count > 0)
                    missingLines.Add($"{gw.Name}: missing {product}: {string.Join(", ", miss)}");
            }
        }

        if (missingLines.Count == 0) return;

        var hash = string.Join("|", missingLines);
        if (hash.Equals(_lastFwManifestMissingHash, StringComparison.Ordinal))
            return;

        _lastFwManifestMissingHash = hash;

        MessageBox.Show(
            "Some Cassia gateways do not contain all firmwares (compared to the union of received manifests):\n\n" +
            string.Join("\n", missingLines),
            "FW manifest mismatch",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private Dictionary<string, List<string>> GetUnionManifest()
    {
        var union = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var gw in CassiaGateways)
        {
            if (!gw.HasFwManifest) continue;

            foreach (var kv in gw.FirmwareManifest)
            {
                if (!union.TryGetValue(kv.Key, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    union[kv.Key] = set;
                }

                foreach (var v in kv.Value ?? Array.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(v))
                        set.Add(v.Trim());
            }
        }

        // Convert to sorted lists
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in union)
            result[kv.Key] = kv.Value.OrderBy(ParseFwVersionSafe).ThenBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

        return result;
    }

    private static Version ParseFwVersionSafe(string s)
    {
        // expects v02.36 etc
        if (string.IsNullOrWhiteSpace(s)) return new Version(0, 0);
        var m = Regex.Match(s.Trim(), @"^v?(\d+)\.(\d+)$", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var maj) && int.TryParse(m.Groups[2].Value, out var min))
            return new Version(maj, min);

        return new Version(0, 0);
    }

    private void UpdateFirmwareOptionsFromUnion(Dictionary<string, List<string>> union)
    {
        void apply(ObservableCollection<string> target, string key)
        {
            if (!union.TryGetValue(key, out var list) || list.Count == 0) return;

            target.Clear();
            foreach (var v in list)
                target.Add(v);
        }

        apply(FirmwareOptionsP48, "P48");
        apply(FirmwareOptionsP47, "P47");
        apply(FirmwareOptionsP46, "P46");
        apply(FirmwareOptionsP41, "P41");
        apply(FirmwareOptionsP42, "P42");

        // Always auto-select the latest FW (last = highest after sorting)
        SelectedFirmwareP48 = FirmwareOptionsP48.LastOrDefault() ?? "";
        SelectedFirmwareP47 = FirmwareOptionsP47.LastOrDefault() ?? "";
        SelectedFirmwareP46 = FirmwareOptionsP46.LastOrDefault() ?? "";
        SelectedFirmwareP41 = FirmwareOptionsP41.LastOrDefault() ?? "";
        SelectedFirmwareP42 = FirmwareOptionsP42.LastOrDefault() ?? "";
    }

}