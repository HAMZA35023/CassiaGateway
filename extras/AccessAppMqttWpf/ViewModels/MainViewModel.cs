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
using AccessAppMqttWpf;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly MqttClientService _mqtt = new();
    private readonly SettingsStore _store = new();

    // ---- UI update cadence (throttled at MQTT client level) ----
    // Progress updates are emitted every 5 seconds, discovered every 15 seconds.
    // We show countdowns so users understand why numbers/statuses are not "live" per message.
    private readonly DispatcherTimer _uiCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _progressCountdownSec = 5;
    private int _discoveredCountdownSec = 15;

    [ObservableProperty] private string progressUiCountdownText = "Progress UI update in 5s";
    [ObservableProperty] private string discoveredUiCountdownText = "Discovered UI update in 15s";


    // ---- Firmware manifest (tele/.../fw-manifest) ----
    private readonly DispatcherTimer _fwManifestValidateTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _fwManifestTimeoutTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _fwManifestTimeoutArmed;
    private string _lastFwManifestMissingHash = "";

    // After each connect we wait for per-gateway status, then request its FW manifest once.
    private readonly HashSet<string> _fwManifestRequestedForGw = new(StringComparer.OrdinalIgnoreCase);
    // After each connect we request queue/programming/parallel-programmers once per gateway.
    private readonly HashSet<string> _runtimeStateRequestedForGw = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _connectedAtUtc = DateTimeOffset.MinValue;


    private readonly ObservableCollection<DiscoveredDevice> _devices = new();
    private readonly Dictionary<string, DiscoveredDevice> _deviceByMac = new(StringComparer.OrdinalIgnoreCase);

    public ICollectionView FilteredDevices { get; }

    public ObservableCollection<CassiaGateway> CassiaGateways { get; } = new();

    // Speed graph: include virtual options without polluting the main Cassia list in the UI.
    private readonly CassiaGateway _speedAllGateways = new() { Name = "(All gateways)" };
    private readonly CassiaGateway _speedTotalGateways = new() { Name = "(Total)" };
    public ObservableCollection<CassiaGateway> SpeedGraphGateways { get; } = new();

    // Names for dropdowns (assignment, commands, etc.)
    public ObservableCollection<string> CassiaNameOptions { get; } = new();

    public ObservableCollection<QueueItem> QueueItems { get; } = new();

    public ICollectionView QueueView { get; }

    public ObservableCollection<string> SensorFilterOptions { get; } =
        new(new[] { "All", "P41", "P42", "P46", "P47", "P48" });

    private readonly System.Collections.Generic.Dictionary<string, string> _productToModel =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _appCts = new();

    // ---- Cached status from upgrade-log / progress (do NOT create "discovered devices" from logs) ----
    private sealed class CachedDeviceStatus
    {
        public string ProcessStatus = "";
        public int ProcessProgress = 0;
        public string ProcessCassia = "";
        public string ProcessFirmware = "";
        public DateTimeOffset LastUpdateUtc = DateTimeOffset.MinValue;

        public string CurrentFw = "";
        public bool IsUpgradeSuccess = false;
        public string LastTargetFw = "";
        public DateTimeOffset? LastUpgradeSuccessUtc = null;

        public bool IsInQueue = false;
    }

    private readonly Dictionary<string, CachedDeviceStatus> _cachedStatusByMac = new(StringComparer.OrdinalIgnoreCase);

    private DiscoveredDevice? FindDiscoveredDevice(string mac) =>
        _devices.FirstOrDefault(d => d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));

    private CachedDeviceStatus GetOrCreateCache(string mac)
    {
        if (!_cachedStatusByMac.TryGetValue(mac, out var cs))
        {
            cs = new CachedDeviceStatus();
            _cachedStatusByMac[mac] = cs;
        }
        return cs;
    }

    private void ApplyCachedStatusToDevice(DiscoveredDevice dev)
    {
        if (dev == null) return;
        if (string.IsNullOrWhiteSpace(dev.Mac)) return;

        if (!_cachedStatusByMac.TryGetValue(dev.Mac, out var cs)) return;

        // Apply cached process status ONLY if it's newer than what the device already shows.
        // This prevents an older cached "Requested update" from overwriting a newer "Queued"/progress status.
        var devTs = dev.ProcessLastUpdateUtc ?? DateTimeOffset.MinValue;
        var cacheTs = cs.LastUpdateUtc;

        if (cacheTs == DateTimeOffset.MinValue || cacheTs >= devTs)
        {
            if (!string.IsNullOrWhiteSpace(cs.ProcessStatus)) dev.ProcessStatus = cs.ProcessStatus;
            if (!string.IsNullOrWhiteSpace(cs.ProcessCassia)) dev.ProcessCassia = cs.ProcessCassia;
            if (!string.IsNullOrWhiteSpace(cs.ProcessFirmware)) dev.ProcessFirmware = cs.ProcessFirmware;
            if (cs.ProcessProgress > 0) dev.ProcessProgress = cs.ProcessProgress;
            if (cacheTs != DateTimeOffset.MinValue) dev.ProcessLastUpdateUtc = cacheTs;
        }

        if (!string.IsNullOrWhiteSpace(cs.CurrentFw)) dev.CurrentFw = cs.CurrentFw;

        if (cs.IsUpgradeSuccess)
        {
            dev.IsUpgradeSuccess = true;
            dev.LastUpgradeSuccessUtc = cs.LastUpgradeSuccessUtc ?? dev.LastUpgradeSuccessUtc;
            if (!string.IsNullOrWhiteSpace(cs.LastTargetFw)) dev.LastTargetFw = cs.LastTargetFw;
            dev.IsInQueue = false;
        }
        else
        {
            dev.IsInQueue = cs.IsInQueue;
        }
    }


    // Plain text responses for connect/write-read are published on a response topic (exact topic may vary).
    // Payload format: "AA:BB:CC:DD:EE:FF: <message>".
    // Plain-text replies are often published on tele/* topics as human readable lines.
    // They may be quoted ("...") and/or contain additional prefix text.
    // Example: "10:B9:F7:0F:F1:EB: connect OK" or "[info] 10:B9:..: disconnect OK".
    private static readonly Regex PlainReplyMacRx =
        new(@"(?<mac>(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public event Action<string, string>? PlainReplyReceived; // mac, message

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

    // Runtime-only: set/get number of parallel programmers.
    // "All" value is used when pressing Set all / Get all.
    [ObservableProperty] private int parallelProgrammersAllDesired = 3;


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

    [ObservableProperty] private CassiaGateway? selectedSpeedGateway;

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

    private readonly HashSet<string> _requestedUpgradeLogCassias = new(StringComparer.OrdinalIgnoreCase);

    private bool _pendingUpgradeLogTextRefresh;

    
    // ---- Progress buffering (prevents UI lag / lost clicks when many % updates arrive) ----
    private readonly object _progressBufLock = new();
    private readonly Dictionary<string, BufferedProgress> _progressByMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Threading.DispatcherTimer _progressFlushTimer;

    private sealed class BufferedProgress
    {
        public string Cassia { get; set; } = "";
        public string Mac { get; set; } = "";
        public string Stage { get; set; } = "";
        public string FirmwareTarget { get; set; } = "";
        public double ProgressPercent { get; set; }
        public DateTimeOffset TimeUtc { get; set; } = DateTimeOffset.UtcNow;

        // Throttle per device (avoid repainting 20+ rows every 200ms if value didn't change)
        public double LastAppliedPercent { get; set; } = double.NaN;
        public DateTimeOffset LastAppliedUtc { get; set; } = DateTimeOffset.MinValue;
    }

private readonly System.Windows.Threading.DispatcherTimer _gatewayStaleTimer;
    private static readonly TimeSpan GatewayOfflineAfter = TimeSpan.FromMinutes(5);

    public string ConnectButtonText => IsConnected ? "Disconnect" : "Connect";
    public string DevicesSubtitle => $"{_devices.Count} unique device(s) • model: {SensorFilter} • filter: {DeviceFilter}";

    private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> _gwSeenMacs
    = new(StringComparer.OrdinalIgnoreCase);

    // Sticky per-device assignment.
    // - We auto-assign ONCE when a device first appears.
    // - We NEVER change assignment when RSSI changes, unless user presses "Reassign".
    private const int AssignmentRssiSlack = 10; // if another cassia is within 8-10 RSSI, it can take the device for balancing
    private readonly HashSet<string> _deviceAssignmentWired = new(StringComparer.OrdinalIgnoreCase);

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
                _runtimeStateRequestedForGw.Clear();
            }
        };

        _mqtt.Message += OnMqttMessage;
        // Countdown labels for throttled UI updates (progress/discovered).
        _uiCountdownTimer.Tick += (_, _) =>
        {
            _progressCountdownSec--;
            if (_progressCountdownSec <= 0) _progressCountdownSec = 5;
            ProgressUiCountdownText = $"Progress UI update in {_progressCountdownSec}s";

            _discoveredCountdownSec--;
            if (_discoveredCountdownSec <= 0) _discoveredCountdownSec = 15;
            DiscoveredUiCountdownText = $"Discovered UI update in {_discoveredCountdownSec}s";
        };
        _uiCountdownTimer.Start();



        // Speed graph options: keep a separate list with virtual items.
        CassiaGateways.CollectionChanged += (_, __) => RebuildSpeedGraphGateways();
        RebuildSpeedGraphGateways();

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

        // Flush buffered progress updates in small batches to keep UI responsive
        _progressFlushTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _progressFlushTimer.Tick += (s2, e2) => FlushBufferedProgressOnUi();
        _progressFlushTimer.Start();


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

        CassiaNameOptions.Clear();
        CassiaNameOptions.Add("(auto)");
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
                _requestedUpgradeLogCassias.Clear();
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

            // Ensure we also receive telemetry responses (many backends publish connect/write-read replies on tele/*).
            // If the user configured a narrow subscription like accessapp/<net>/cmd/#, we still want tele/#.
            if (!string.IsNullOrWhiteSpace(NetworkId))
            {
                await _mqtt.SubscribeAsync($"accessapp/{NetworkId}/tele/#").ConfigureAwait(false);
                await _mqtt.SubscribeAsync($"accessapp/{NetworkId}/cmd/#").ConfigureAwait(false);

                // If we already know some gateways (from a previous run), request snapshots immediately.
                try
                {
                    foreach (var gw in CassiaGateways.ToList())
                    {
                        if (string.IsNullOrWhiteSpace(gw?.Name)) continue;
                        MaybeAutoRequestRuntimeStateAfterStatus(gw);
                    }
                }
                catch { }


            // Auto-gather saved upgrade logs on connect (per gateway).
            // We request for every gateway we currently know, and also auto-request when new gateways announce status.
            try
            {
                foreach (var gw in CassiaGateways.ToList())
                {
                    if (string.IsNullOrWhiteSpace(gw?.Name)) continue;
                    if (_requestedUpgradeLogCassias.Contains(gw.Name)) continue;

                    _requestedUpgradeLogCassias.Add(gw.Name);
                    _ = RequestUpgradeLogForCassiaAsync(gw.Name);
                }
            }
            catch { }
}

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
        CassiaNameOptions.Clear();
        _gwSeenMacs.Clear(); // <-- reset unique counters
        OnPropertyChanged(nameof(DevicesSubtitle));
    }

    

    [RelayCommand]
    private void CheckAllDevices()
    {
        // Toggle: if any visible device is checked -> uncheck all, else check all.
        bool anyChecked = false;
        foreach (var obj in FilteredDevices)
        {
            if (obj is DiscoveredDevice d && d.IsSelected)
            {
                anyChecked = true;
                break;
            }
        }

        foreach (var obj in FilteredDevices)
        {
            if (obj is DiscoveredDevice d)
                d.IsSelected = !anyChecked;
        }
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
        {
            await QueueDeviceAndRequestAsync(d);
            // After queueing, uncheck in device list as requested.
            d.IsSelected = false;
        }
    }

    // ---------------------------------------------------------------------
    // Device context actions (Connect / Disconnect / Write-Read)
    // These are used by the Devices grid right-click menu.
    // ---------------------------------------------------------------------

    [RelayCommand]
    private async Task ConnectDevice(DiscoveredDevice? device)
        => await ConnectDeviceAsync(device);

    [RelayCommand]
    private async Task DisconnectDevice(DiscoveredDevice? device)
        => await DisconnectDeviceAsync(device);

    internal async Task ConnectDeviceAsync(DiscoveredDevice? device)
    {
        if (device == null) return;
        await SendConnectOrDisconnectAsync(device, action: "connect");
    }

    internal async Task DisconnectDeviceAsync(DiscoveredDevice? device)
    {
        if (device == null) return;
        await SendConnectOrDisconnectAsync(device, action: "disconnect");
    }

    [RelayCommand]
    private void OpenWriteRead(DiscoveredDevice? device)
    {
        if (device == null) return;
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var win = new WriteReadWindow(this, device);
                win.Owner = Application.Current.MainWindow;
                win.Show();
                win.Activate();
            });
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Open Write-Read failed: " + ex.Message;
        }
    }

    private string BuildCmdTopic(string cassia, string command)
    {
        var tpl = string.IsNullOrWhiteSpace(CommandTopicTemplate)
            ? "accessapp/{networkId}/cmd/{cassia}/{command}"
            : CommandTopicTemplate;

        return tpl
            .Replace("{networkId}", NetworkId ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{cassia}", cassia ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{command}", command ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendConnectOrDisconnectAsync(DiscoveredDevice device, string action)
    {
        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        var mac = (device.Mac ?? "").Trim();
        if (string.IsNullOrWhiteSpace(mac)) return;

        var cassia = (device.AssignedCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassia))
            cassia = (device.BestCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassia))
        {
            ConnectionStatus = "No Cassia selected for device";
            return;
        }

        var topic = BuildCmdTopic(cassia, "connect"); // same command endpoint; action differentiates

        object payload = action.Equals("disconnect", StringComparison.OrdinalIgnoreCase)
            ? new { sensors = new[] { mac }, action = "disconnect" }
            : new { sensors = new[] { mac } }; // default connect

        device.BleLink = action.Equals("disconnect", StringComparison.OrdinalIgnoreCase) ? "disconnecting…" : "connecting…";
        await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
    }

    internal async Task SendWriteReadAsync(
        DiscoveredDevice device,
        string hex,
        int handle = 19,
        bool noResponse = true,
        bool expectReply = false,
        int? timeoutSeconds = null)
    {
        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        var mac = (device.Mac ?? "").Trim();
        var cassia = (device.AssignedCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassia))
            cassia = (device.BestCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(mac) || string.IsNullOrWhiteSpace(cassia))
            return;

        var topic = BuildCmdTopic(cassia, "write-read");

        hex = NormalizeHexInput(hex);

        // Minimal payload defaults: handle=19, noResponse=true, expectReply=false
        object payload = timeoutSeconds.HasValue
            ? new { sensors = new[] { mac }, handle, hex, noResponse, expectReply, timeoutSeconds = timeoutSeconds.Value }
            : new { sensors = new[] { mac }, handle, hex, noResponse, expectReply };

        await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
    }

    private static readonly Regex NonHexRx = new("[^0-9A-Fa-f]", RegexOptions.Compiled);

    private static string NormalizeHexInput(string? hex)
    {
        var s = (hex ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(2);

        // Allow common formats: "0110", "01 10", "01-10", etc.
        s = NonHexRx.Replace(s, "");
        return s.ToUpperInvariant();
    }

    // ---------------------------------------------------------------------
    // Sticky assignment (balanced between cassias)
    // ---------------------------------------------------------------------

    private void EnsureCassiaOption(string? name)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!CassiaNameOptions.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)))
            CassiaNameOptions.Add(name);
    }

    private void EnsureDeviceAssignmentWiring(DiscoveredDevice d)
    {
        if (d == null) return;
        var mac = (d.Mac ?? "").Trim();
        if (string.IsNullOrWhiteSpace(mac)) return;
        if (_deviceAssignmentWired.Contains(mac)) return;
        _deviceAssignmentWired.Add(mac);

        d.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiscoveredDevice.AssignedCassia)
                || e.PropertyName == nameof(DiscoveredDevice.SensorModel))
            {
                // User changed assignment from the dropdown, or model updated.
                RecalculateAssignmentCounts();
            }
        };
    }

    private static int GetGroupForModel(string? model)
    {
        model = (model ?? "").Trim().ToUpperInvariant();
        return model switch
        {
            "P41" or "P42" or "P46" => 1,
            "P47" or "P48" => 2,
            _ => 0
        };
    }

    /// <summary>
    /// Ensures the device has a sticky AssignedCassia.
    /// We only auto-assign once (when AssignedCassia is empty).
    /// </summary>
    
    // ---------------- Assignment helpers ----------------
    private bool IsDeviceInWork(DiscoveredDevice d)
    {
        if (d == null) return false;

        // If the device has an active queue entry (not done), consider it "in work".
        var mac = (d.Mac ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(mac))
        {
            if (QueueItems.Any(q => q != null &&
                                   mac.Equals((q.Mac ?? "").Trim(), StringComparison.OrdinalIgnoreCase) &&
                                   !q.IsDone &&
                                   (q.Progress < 100 || (DateTimeOffset.UtcNow - q.LastUpdateUtc) <= TimeSpan.FromMinutes(1))))
                return true;
        }

        // Also treat "process/progress tele" as work if progress is < 100.
        if (d.ProcessProgress > 0 && d.ProcessProgress < 100)
            return true;

        return false;
    }

    private bool IsDoneForBalancing(DiscoveredDevice d)
    {
        if (d == null) return false;

        // User rule: if % is 100 for over 1 minute, assume done and exclude from balancing counts.
        if (d.ProcessProgress >= 100 && d.ProcessLastUpdateUtc.HasValue)
        {
            if (DateTimeOffset.UtcNow - d.ProcessLastUpdateUtc.Value > TimeSpan.FromMinutes(1))
                return true;
        }

        // If we only have queue info, apply same heuristic.
        var mac = (d.Mac ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(mac))
        {
            var q = QueueItems.FirstOrDefault(x =>
                x != null && mac.Equals((x.Mac ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

            if (q != null && q.Progress >= 100 && (DateTimeOffset.UtcNow - q.LastUpdateUtc) > TimeSpan.FromMinutes(1))
                return true;

            if (q != null && q.IsDone)
                return true;
        }

        return false;
    }

private void EnsureStickyAssignment(DiscoveredDevice d)
    {
        if (d == null) return;
        if (!string.IsNullOrWhiteSpace(d.AssignedCassia)) return; // already assigned (sticky)

        // Do not auto-assign devices that are already being worked on (queued/programming).
        if (IsDeviceInWork(d)) return;

        // Need at least one RSSI reading.
        if (d.CassiaRssi.Count == 0)
            return;

        var best = d.CassiaRssi.OrderByDescending(kv => kv.Value).First();
        var bestCassia = (best.Key ?? "").Trim();
        var bestRssi = best.Value;
        if (string.IsNullOrWhiteSpace(bestCassia)) return;

        // Eligible cassias = within slack of the best RSSI (e.g. best=-55 => eligible >= -65)
        var eligible = d.CassiaRssi
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value >= bestRssi - AssignmentRssiSlack)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (eligible.Count == 0)
            eligible.Add(bestCassia);

        var model = (d.SensorModel ?? "").Trim().ToUpperInvariant();
        var group = GetGroupForModel(model);

        // Prefer balance within the group the user defined.
        // Tie-break by higher RSSI, then name.
        string chosen = bestCassia;
        if (group == 0)
        {
            chosen = bestCassia;
        }
        else
        {
            var groupCounts = GetCurrentGroupCounts(group);
            var modelCounts = GetCurrentModelCounts(model);

            chosen = eligible
                .OrderBy(c => groupCounts.TryGetValue(c, out var gc) ? gc : 0)
                .ThenBy(c => modelCounts.TryGetValue(c, out var mc) ? mc : 0)
                .ThenByDescending(c => d.CassiaRssi.TryGetValue(c, out var r) ? r : int.MinValue)
                .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? bestCassia;
        }

        d.AssignedCassia = chosen;
    }

    private Dictionary<string, int> GetCurrentGroupCounts(int group)
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var dev in _devices)
        {
            if (IsDoneForBalancing(dev)) continue;
            var cassia = (dev.AssignedCassia ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cassia)) continue;
            if (GetGroupForModel(dev.SensorModel) != group) continue;
            dict[cassia] = dict.TryGetValue(cassia, out var v) ? v + 1 : 1;
        }
        return dict;
    }

    private Dictionary<string, int> GetCurrentModelCounts(string model)
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        model = (model ?? "").Trim().ToUpperInvariant();
        foreach (var dev in _devices)
        {
            if (IsDoneForBalancing(dev)) continue;
            var cassia = (dev.AssignedCassia ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cassia)) continue;
            if (!string.Equals((dev.SensorModel ?? "").Trim(), model, StringComparison.OrdinalIgnoreCase)) continue;
            dict[cassia] = dict.TryGetValue(cassia, out var v) ? v + 1 : 1;
        }
        return dict;
    }

    private void RecalculateAssignmentCounts()
    {
        // Reset
        foreach (var gw in CassiaGateways)
        {
            gw.AssignedP41 = 0;
            gw.AssignedP42 = 0;
            gw.AssignedP46 = 0;
            gw.AssignedP47 = 0;
            gw.AssignedP48 = 0;
        }

        foreach (var dev in _devices)
        {
            if (IsDoneForBalancing(dev)) continue;
            var cassia = (dev.AssignedCassia ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cassia)) continue;

            var gw = CassiaGateways.FirstOrDefault(g => g.Name.Equals(cassia, StringComparison.OrdinalIgnoreCase));
            if (gw == null) continue;

            var model = (dev.SensorModel ?? "").Trim().ToUpperInvariant();
            switch (model)
            {
                case "P41": gw.AssignedP41++; break;
                case "P42": gw.AssignedP42++; break;
                case "P46": gw.AssignedP46++; break;
                case "P47": gw.AssignedP47++; break;
                case "P48": gw.AssignedP48++; break;
            }
        }
    }

    [RelayCommand]
    private void ReassignDevices()
    {
        // Clear and re-run assignment algorithm. (Still sticky until next manual reassign.)
        foreach (var dev in _devices)
        {
            // Keep assignment for devices already queued/programming.
            if (IsDeviceInWork(dev)) continue;
            dev.AssignedCassia = "";
        }

        foreach (var dev in _devices.OrderBy(d => d.SensorModel).ThenBy(d => d.Mac, StringComparer.OrdinalIgnoreCase))
            EnsureStickyAssignment(dev);

        RecalculateAssignmentCounts();
        RequestDevicesRefresh();
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


        // Guard: firmware must look like a version (v02.xx). If not, don't accidentally send a model string.
        if (!string.IsNullOrWhiteSpace(fw) && !fw.Trim().StartsWith("v", StringComparison.OrdinalIgnoreCase))
            fw = "";

        // Determine cassia (sticky assignment), else best RSSI, else first online cassia, else any cassia
        var cassia = (d.AssignedCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassia))
            cassia = (d.BestCassia ?? "").Trim();

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

        RequestQueueRefresh();

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
            RequestQueueRefresh();
        }
        catch (Exception ex)
        {
            qi.Status = "Error";
            qi.Notes = "Publish failed: " + ex.Message;
            qi.LastUpdateUtc = DateTimeOffset.UtcNow;
            MirrorQueueToDevice(qi);
            RequestQueueRefresh();
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
        // IMPORTANT: Do NOT create new "discovered devices" from progress/logs.
        // Only the scan/discovered feed may add devices to the device list.
        var dev = _devices.FirstOrDefault(d => d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
        if (dev != null) return dev;

        return new DiscoveredDevice { Mac = mac };
    }

    private void MirrorQueueToDevice(QueueItem qi)
    {
        if (qi == null || string.IsNullOrWhiteSpace(qi.Mac)) return;

        // Always update cache
        var cs = GetOrCreateCache(qi.Mac);
        cs.ProcessStatus = qi.Status ?? "";
        cs.ProcessProgress = qi.Progress;
        cs.ProcessCassia = qi.Cassia ?? "";
        cs.ProcessFirmware = qi.FirmwareVersion ?? "";
        cs.LastUpdateUtc = qi.LastUpdateUtc;

        // Mark queue state for row coloring (ignore items that have been 100% for > 1 minute)
        var doneExpired = qi.Progress >= 100 && (DateTimeOffset.UtcNow - qi.LastUpdateUtc) > TimeSpan.FromMinutes(1);
        cs.IsInQueue = !qi.IsDone && !doneExpired;

        var dev = _devices.FirstOrDefault(d => d.Mac.Equals(qi.Mac, StringComparison.OrdinalIgnoreCase));
        if (dev == null) return;

        dev.ProcessStatus = cs.ProcessStatus;
        dev.ProcessProgress = cs.ProcessProgress;
        dev.ProcessCassia = cs.ProcessCassia;
        // When a device is queued/programming, force AssignedCassia to the gateway currently handling it
        if (!string.IsNullOrWhiteSpace(dev.ProcessCassia) && dev.IsInQueue)
            dev.AssignedCassia = dev.ProcessCassia;
        dev.ProcessFirmware = cs.ProcessFirmware;
        dev.ProcessLastUpdateUtc = cs.LastUpdateUtc;

        dev.IsInQueue = cs.IsInQueue;
    }

    
    
    private void RebuildSpeedGraphGateways()
    {
        SpeedGraphGateways.Clear();
        SpeedGraphGateways.Add(_speedAllGateways);
        SpeedGraphGateways.Add(_speedTotalGateways);

        foreach (var gw in CassiaGateways)
            SpeedGraphGateways.Add(gw);

        // Keep selection valid
        if (SelectedSpeedGateway == null || !SpeedGraphGateways.Contains(SelectedSpeedGateway))
        {
            SelectedSpeedGateway = CassiaGateways.FirstOrDefault() ?? _speedAllGateways;
        }
    }

[RelayCommand]
    private void OpenSpeedGraph(string? cassiaName)
    {
        // Open a simple speed graph window (client-side history, max 1 hour).
        try
        {
            CassiaGateway? gw = null;

            if (!string.IsNullOrWhiteSpace(cassiaName))
                gw = CassiaGateways.FirstOrDefault(g => string.Equals(g.Name, cassiaName, StringComparison.OrdinalIgnoreCase));

            gw ??= CassiaGateways.FirstOrDefault();

            if (gw == null) return;

            SelectedSpeedGateway = gw;

            var wnd = new SpeedGraphWindow(this)
            {
                Owner = Application.Current.MainWindow
            };
            wnd.Show();
            wnd.Activate();
        }
        catch { }
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
        // 1) Handle plain-text replies regardless of topic.
        // We accept:
        //   "AA:BB:..: connect OK"
        //   "[info] AA:BB:..: disconnect OK"
        //   "\"AA:BB:..: notif=01-10-...\"" (quoted)
        // and we handle multiple lines in one payload.
        try
        {
            var text = payload ?? "";
            foreach (var raw in text.Split(new[] { "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                line = line.Trim().Trim('"');

                var mm = PlainReplyMacRx.Match(line);
                if (!mm.Success) continue;

                var mac = mm.Groups["mac"].Value.ToUpperInvariant();

                // Message is whatever comes after the MAC (optionally preceded by ':')
                var after = line.Substring(mm.Index + mm.Length).TrimStart();
                if (after.StartsWith(":")) after = after.Substring(1).TrimStart();
                var msg = after.Length > 0 ? after : line; // fallback

                // Always update on UI thread so subscribers can safely update ObservableCollections.
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    SetDeviceBleLinkFromPlainReply(mac, msg);
                    PlainReplyReceived?.Invoke(mac, msg);
                }));
            }
        }
        catch { /* ignore */ }


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
                var version = root.TryGetProperty("version", out var verEl) ? (verEl.GetString() ?? "") : "";
                var state = root.TryGetProperty("state", out var s) ? s.GetString() ?? "unknown" : "unknown";
                var ts = root.TryGetProperty("time", out var t) && t.TryGetDateTimeOffset(out var dto) ? dto : DateTimeOffset.UtcNow;
                int queue = root.TryGetProperty("queue", out var q) ? q.GetInt32() : 0;
                int programming = root.TryGetProperty("programming", out var pr) ? pr.GetInt32() : 0;
                double totalSpeedpct = root.TryGetProperty("totalSpeedpct", out var sp) ? sp.GetDouble() : 0;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var gw = CassiaGateways.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (gw == null)
                    {
                        gw = new CassiaGateway { Name = name, NetworkId = net };
                        CassiaGateways.Add(gw);
                    }

                    EnsureCassiaOption(name);

                    // default for upgrade log tab
                    if (SelectedLogGateway == null)
                        SelectedLogGateway = gw;

                    if (!LogGatewayOptions.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        LogGatewayOptions.Add(name);

                    gw.State = state;
                    gw.Version = version;
                    gw.LastSeenUtc = ts;
                    gw.Queue = queue;
                    gw.Programming = programming;
                    gw.TotalSpeedpct = totalSpeedpct;
                    gw.AddSpeedSample(ts, totalSpeedpct);


                    // When a gateway announces itself, ask it for FW manifest once per connect.
                    MaybeAutoRequestFirmwareManifestAfterStatus(gw);

                    // Also request runtime snapshot (queue / programming / parallel programmers) so the UI can reconnect mid-run.
                    MaybeAutoRequestRuntimeStateAfterStatus(gw);

                    MaybeAutoRequestUpgradeLogAfterStatus(gw);

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

        if (kind == "tele" && leaf == "queue-list")
        {
            HandleQueueListTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "programming-list")
        {
            HandleProgrammingListTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "parallel-programmers")
        {
            HandleParallelProgrammersTele(cassia, payload);
            return;
        }


        
if (kind == "tele" && leaf == "progress")
        {
            // { mac, progressPercent, stage, time, firmwareTarget, ... }
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                var ts = DateTimeOffset.UtcNow;
                if (root.TryGetProperty("time", out var tEl))
                {
                    if (tEl.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(tEl.GetString(), out var dto))
                        ts = dto;
                    else if (tEl.TryGetDateTimeOffset(out var dto2))
                        ts = dto2;
                }

                var mac = root.TryGetProperty("mac", out var macEl) ? (macEl.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(mac))
                    return;

                var stage = root.TryGetProperty("stage", out var stEl) ? (stEl.GetString() ?? "") : "";
                var fwTarget = root.TryGetProperty("firmwareTarget", out var ftEl) ? (ftEl.GetString() ?? "") : "";

                double pct = 0;
                if (root.TryGetProperty("progressPercent", out var pEl))
                {
                    if (pEl.ValueKind == JsonValueKind.Number) pct = pEl.GetDouble();
                    else if (pEl.ValueKind == JsonValueKind.String && double.TryParse(pEl.GetString(), out var pd)) pct = pd;
                }

                lock (_progressBufLock)
                {
                    if (!_progressByMac.TryGetValue(mac, out var bp))
                    {
                        bp = new BufferedProgress { Mac = mac };
                        _progressByMac[mac] = bp;
                    }
                    bp.Cassia = cassia;
                    bp.Stage = stage;
                    bp.FirmwareTarget = fwTarget;
                    bp.ProgressPercent = pct;
                    bp.TimeUtc = ts;
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

                        EnsureCassiaOption(gw.Name);

                    EnsureCassiaOption(cassia);

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

                            
if (!_deviceByMac.TryGetValue(mac, out var existing))
{
    existing = new DiscoveredDevice { Mac = mac };
    _deviceByMac[mac] = existing;
    _devices.Add(existing);
}

                            EnsureDeviceAssignmentWiring(existing);
                            ApplyCachedStatusToDevice(existing);

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
                            EnsureStickyAssignment(existing);
                        }


                        // show unique count since last clear
                        gw.DevicesSeen = seen.Count;

                        // Update per-gateway assignment counts
                        RecalculateAssignmentCounts();

                        RequestDevicesRefresh();
                        OnPropertyChanged(nameof(DevicesSubtitle));
                    });
                }
            }
            catch { }
            return;
        }
    }

    private void SetDeviceBleLinkFromPlainReply(string mac, string msg)
    {
        if (string.IsNullOrWhiteSpace(mac)) return;
        var d = _devices.FirstOrDefault(x => string.Equals(x.Mac, mac, StringComparison.OrdinalIgnoreCase));
        if (d == null) return;

        // Normalize a compact status for the grid
        var lower = (msg ?? "").ToLowerInvariant();
        if (lower.StartsWith("connect"))
            d.BleLink = msg;
        else if (lower.StartsWith("disconnect"))
            d.BleLink = msg;
        else if (lower.StartsWith("write"))
            d.BleLink = msg;
        else if (lower.StartsWith("notif"))
            d.BleLink = "notif";
        else if (lower.Contains("timeout"))
            d.BleLink = "timeout";
        else
            d.BleLink = msg;
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
                    // When requesting from multiple gateways, each gateway will send a begin.
                    // Only clear if this is the first begin in the current view.
                    if (UpgradeLogLines.Count == 0)
                    {
                        UpgradeLogLines.Clear();
                        _upgradeLogSb.Clear();
                    }
                    else
                    {
                        var sep = $"----- {cassia} saved-log-begin -----";
                        UpgradeLogLines.Add(sep);
                        _upgradeLogSb.AppendLine(sep);
                    }

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

                            // Grouped view
                            AddUpgradeLogEntryFromLine(cassia, line);
                            // Harvest Current FW + completion success for UI fields (safe for saved log playback)
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
                            ApplyLiveProcessStatusFromUpgradeLogLine(cassia, line2);
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

            if (!string.IsNullOrWhiteSpace(status) && status.Trim().Equals("success", StringComparison.OrdinalIgnoreCase))
                status = "Success";

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

    private static bool LooksLikeFirmwareVersion(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();

        // Ignore detector model strings like "P46", "P47", etc.
        if (Regex.IsMatch(s, @"^P\d{2}$", RegexOptions.IgnoreCase))
            return false;

        // Accept typical target versions like "v02.35" or "02.35"
        return Regex.IsMatch(s, @"^v?\d{2}\.\d{2}$", RegexOptions.IgnoreCase);
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

            // Timestamp embedded in log line (used to pick newest across gateways)
            DateTimeOffset tsUtc = DateTimeOffset.UtcNow;
            var tm = LogLineTimeRx.Match(line);
            if (tm.Success)
            {
                if (DateTime.TryParseExact(
                        tm.Groups["time"].Value.Trim(),
                        "yyyy-MM-dd HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal,
                        out var dtLocal))
                {
                    tsUtc = new DateTimeOffset(DateTime.SpecifyKind(dtLocal, DateTimeKind.Local)).ToUniversalTime();
                }
            }

            // Logs must NOT drive queue/progress UI.
            // We only harvest:
            //  - Current FW info (from "Current FW Version" lines)
            //  - Success completion + target FW (fw=v02.xx)
            var cs = GetOrCreateCache(mac);

            // 1) Completion success
            var isCompletedSuccess =
                !string.IsNullOrWhiteSpace(stage) &&
                stage.Trim().Equals("Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(status) &&
                status.Trim().Equals("Success", StringComparison.OrdinalIgnoreCase);

            if (isCompletedSuccess)
            {
                // Only accept if newer than previous success record
                if (!cs.LastUpgradeSuccessUtc.HasValue || tsUtc >= cs.LastUpgradeSuccessUtc.Value)
                {
                    cs.IsUpgradeSuccess = true;
                    cs.LastUpgradeSuccessUtc = tsUtc;

                    var fwm = LogLineFwRx.Match(line);
                    if (fwm.Success)
                        cs.LastTargetFw = fwm.Groups["fw"].Value.Trim();
                }
            }

            // 2) Current FW (Sensor: App: ...)
            if (!string.IsNullOrWhiteSpace(status))
            {
                var appm = SensorAppFromStatusRx.Match(status);
                if (appm.Success)
                {
                    var app = appm.Groups["app"].Value;
                    if (!string.IsNullOrWhiteSpace(app))
                        cs.CurrentFw = app;
                }
            }

            // Apply to discovered device if it exists (without touching ProcessStatus/queue fields)
            var dev = FindDiscoveredDevice(mac);
            if (dev != null)
            {
                dev.IsUpgradeSuccess = cs.IsUpgradeSuccess;
                dev.LastUpgradeSuccessUtc = cs.LastUpgradeSuccessUtc;
                dev.LastTargetFw = cs.LastTargetFw ?? "";
                dev.CurrentFw = cs.CurrentFw ?? "";
            }
        }
        catch
        {
            // ignore malformed lines
        }
    }

    
    private void ApplyLiveProcessStatusFromUpgradeLogLine(string cassia, string line)
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

            // Timestamp embedded in log line (used to pick newest across gateways)
            DateTimeOffset tsUtc = DateTimeOffset.UtcNow;
            var tm = LogLineTimeRx.Match(line);
            if (tm.Success)
            {
                if (DateTime.TryParseExact(
                        tm.Groups["time"].Value.Trim(),
                        "yyyy-MM-dd HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal,
                        out var dtLocal))
                {
                    tsUtc = new DateTimeOffset(DateTime.SpecifyKind(dtLocal, DateTimeKind.Local)).ToUniversalTime();
                }
            }

            // What we want to show in the device/queue lists:
            // Prefer stage, but if stage is empty, show status.
            var text = !string.IsNullOrWhiteSpace(stage) ? stage : status;
            if (string.IsNullOrWhiteSpace(text)) return;

            // Best effort fw=v02.xx (can be null in JSON)
            var fw = "";
            var fwm = LogLineFwRx.Match(line);
            if (fwm.Success) fw = fwm.Groups["fw"].Value.Trim();

            Application.Current.Dispatcher.Invoke(() =>
            {
                // Update queue row (if exists) so operators can see live stage changes.
                var qi = QueueItems.FirstOrDefault(q => q.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
                if (qi != null)
                {
                    qi.Cassia = cassia;
                        qi.Status = text.Trim();
                        if (LooksLikeFirmwareVersion(fw))
                            qi.FirmwareVersion = fw;
                        qi.LastUpdateUtc = tsUtc;

                        // Keep sorting helpers fresh
                        RequestQueueRefresh();
                    }

                // Cache + device list mirror (without creating devices from logs)
                var cs = GetOrCreateCache(mac);
                cs.ProcessCassia = cassia;
                cs.ProcessStatus = text.Trim();
                if (LooksLikeFirmwareVersion(fw))
                    cs.ProcessFirmware = fw;
                cs.LastUpdateUtc = tsUtc;

                var dev = FindDiscoveredDevice(mac);
                if (dev == null) return;
                dev.ProcessCassia = cassia;
                dev.ProcessStatus = cs.ProcessStatus;
                if (!string.IsNullOrWhiteSpace(cs.ProcessFirmware))
                    dev.ProcessFirmware = cs.ProcessFirmware;
                dev.ProcessLastUpdateUtc = tsUtc;
            });
        }
        catch
        {
            // ignore malformed lines
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

        // Clear current view (user-initiated)
        Application.Current.Dispatcher.Invoke(() =>
        {
            UpgradeLogLines.Clear();
            UpgradeLogGroups.Clear();
            UpgradeLogText = "";
            _upgradeLogSb.Clear();
            UpgradeLogReceivedLines = 0;
            UpgradeLogTotalLines = 0;
            UpgradeLogStatus = "Requesting saved logs from all gateways…";
        });

        var gateways = CassiaGateways
            .Where(g => g != null && !string.IsNullOrWhiteSpace(g.Name))
            .Select(g => g.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (gateways.Count == 0)
        {
            ConnectionStatus = "No Cassia gateways known yet";
            return;
        }

        try
        {
            foreach (var cassia in gateways)
            {
                _requestedUpgradeLogCassias.Add(cassia);
                await RequestUpgradeLogForCassiaAsync(cassia).ConfigureAwait(false);
            }

            UpgradeLogStatus = $"Requested saved logs from {gateways.Count} gateway(s)";
        }
        catch (Exception ex)
        {
            UpgradeLogStatus = "Request failed: " + ex.Message;
        }
    }


    /// <summary>
    /// Internal helper used by the auto-request logic (per gateway). Not a command.
    /// </summary>
    private async Task RequestUpgradeLogForCassiaAsync(string cassia)
    {
        if (!IsConnected) return;
        if (string.IsNullOrWhiteSpace(cassia)) return;

        var topic = CommandTopicTemplate
            .Replace("{networkId}", NetworkId)
            .Replace("{cassia}", cassia)
            .Replace("{command}", "send-upgrade-log");

        try
        {
            await _mqtt.PublishAsync(topic, "{}", retain: false).ConfigureAwait(false);
        }
        catch
        {
            // best effort; UI command path shows errors, auto path stays quiet
        }
    }


    [RelayCommand]
    private async Task ClearUpgradeLogOnCassiaAsync()
    {
        if (!IsConnected)
        {
            UpgradeLogStatus = "Not connected";
            return;
        }

        // If "All" is selected, send clear command to each Cassia sequentially.
        var selected = (SelectedLogGatewayName ?? "").Trim();

        List<string> targets;
        if (string.IsNullOrWhiteSpace(selected) || string.Equals(selected, "All", StringComparison.OrdinalIgnoreCase))
        {
            targets = CassiaGateways
                .Select(g => g.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            targets = new List<string> { selected };
        }

        if (targets.Count == 0)
        {
            UpgradeLogStatus = "No Cassia gateway known yet";
            return;
        }

        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var cassia = targets[i];

                var topic = CommandTopicTemplate
                    .Replace("{networkId}", NetworkId)
                    .Replace("{cassia}", cassia)
                    .Replace("{command}", "clear-upgrade-log");

                await _mqtt.PublishAsync(topic, "{}", retain: false).ConfigureAwait(false);

                UpgradeLogStatus = targets.Count == 1
                    ? $"Requested clear-upgrade-log on {cassia}"
                    : $"Requested clear-upgrade-log on {cassia} ({i + 1}/{targets.Count})";

                await Task.Delay(120).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            UpgradeLogStatus = "Clear request failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ClearUpgradeLog()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            UpgradeLogLines.Clear();
            UpgradeLogGroups.Clear();
            UpgradeLogGroupsView?.Refresh();
            _upgradeLogSb.Clear();
            UpgradeLogText = "";
            UpgradeLogSearchText = "";
            UpgradeLogReceivedLines = 0;
            UpgradeLogTotalLines = 0;
            UpgradeLogStatus = "Idle";
        });
    }

    private bool _pendingDevicesRefresh;
    private bool _pendingQueueRefresh;

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
    void RequestQueueRefresh()
    {
        if (_pendingQueueRefresh) return;
        _pendingQueueRefresh = true;

        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(250); // throttle
            _pendingQueueRefresh = false;

            // preserve selection
            var selectedMac = SelectedQueueItem?.Mac;

            try { RequestQueueRefresh(); } catch { }

            if (!string.IsNullOrWhiteSpace(selectedMac))
                SelectedQueueItem = QueueItems.FirstOrDefault(d => d.Mac.Equals(selectedMac, StringComparison.OrdinalIgnoreCase));
        });
    }



    private void MaybeAutoRequestUpgradeLogAfterStatus(CassiaGateway gw)
    {
        if (!IsConnected) return;
        if (!string.Equals(gw.StateLower, "online", StringComparison.OrdinalIgnoreCase)) return;

        // Only auto-request once per connection per gateway.
        // If we already tried requesting "all" on connect, we do not spam per-gateway requests.
        if (_requestedUpgradeLogCassias.Contains("all")) return;
        if (_requestedUpgradeLogCassias.Contains(gw.Name)) return;

        _requestedUpgradeLogCassias.Add(gw.Name);
        _ = RequestUpgradeLogForCassiaAsync(gw.Name);
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

    private void MaybeAutoRequestRuntimeStateAfterStatus(CassiaGateway gw)
    {
        if (!IsConnected) return;
        if (!string.Equals(gw.StateLower, "online", StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(gw.Name)) return;

        // Only request once per connect per gateway.
        if (_runtimeStateRequestedForGw.Contains(gw.Name)) return;
        _runtimeStateRequestedForGw.Add(gw.Name);

        _ = RequestQueueListAsync(gw.Name);
        _ = RequestProgrammingListAsync(gw.Name);
        _ = RequestParallelProgrammersAsync(gw.Name);
    }

    private Task RequestQueueListAsync(string cassiaName)
        => _mqtt.PublishJsonAsync(BuildCmdTopic(cassiaName, "get-queue-list"), new { }, retain: false, qos: 1, ct: _appCts.Token);

    private Task RequestProgrammingListAsync(string cassiaName)
        => _mqtt.PublishJsonAsync(BuildCmdTopic(cassiaName, "get-programming-list"), new { }, retain: false, qos: 1, ct: _appCts.Token);

    private Task RequestParallelProgrammersAsync(string cassiaName)
        => _mqtt.PublishJsonAsync(BuildCmdTopic(cassiaName, "get-parallel-programmers"), new { }, retain: false, qos: 1, ct: _appCts.Token);

    private Task SetParallelProgrammersAsync(string cassiaName, int value)
        => _mqtt.PublishJsonAsync(BuildCmdTopic(cassiaName, "set-parallel-programmers"), new { value }, retain: false, qos: 1, ct: _appCts.Token);

    [RelayCommand]
    private async Task GetParallelProgrammersForCassia(string cassiaName)
    {
        if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
        if (string.IsNullOrWhiteSpace(cassiaName)) return;
        await RequestParallelProgrammersAsync(cassiaName).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task SetParallelProgrammersForCassia(object? cassiaGateway)
    {
        if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
        if (cassiaGateway is not CassiaGateway gw) return;
        if (string.IsNullOrWhiteSpace(gw.Name)) return;

        var value = gw.ParallelProgrammersDesired;
        if (value <= 0) return;
        await SetParallelProgrammersAsync(gw.Name, value).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task GetParallelProgrammersForAllCassias()
    {
        if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
        foreach (var gw in CassiaGateways.ToList())
        {
            if (string.IsNullOrWhiteSpace(gw?.Name)) continue;
            await RequestParallelProgrammersAsync(gw.Name).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task SetParallelProgrammersForAllCassias()
    {
        if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
        var value = ParallelProgrammersAllDesired;
        if (value <= 0) return;

        foreach (var gw in CassiaGateways.ToList())
        {
            if (string.IsNullOrWhiteSpace(gw?.Name)) continue;
            await SetParallelProgrammersAsync(gw.Name, value).ConfigureAwait(false);
        }
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

    private void HandleQueueListTele(string cassia, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("queueList", out var listEl) || listEl.ValueKind != JsonValueKind.Array)
                return;

            var now = DateTimeOffset.UtcNow;

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var item in listEl.EnumerateArray())
                {
                    var mac = item.TryGetProperty("mac", out var m) ? (m.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(mac)) continue;

                    var detectorType = item.TryGetProperty("detectorType", out var dt) ? (dt.GetString() ?? "") : "";
                    var fw = item.TryGetProperty("firmwareVersion", out var fv) ? (fv.GetString() ?? "") : "";

                    var qi = QueueItems.FirstOrDefault(q => q.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
                    if (qi == null)
                    {
                        qi = new QueueItem
                        {
                            Mac = mac,
                            Cassia = cassia,
                            Command = DefaultCommand,
                            Status = "Queued",
                            Progress = 0,
                            FirmwareVersion = fw,
                            DetectorType = detectorType,
                            LastUpdateUtc = now
                        };
                        QueueItems.Add(qi);
                    }
                    else
                    {
                        qi.Cassia = cassia;
                        qi.Status = "Queued";
                        qi.DetectorType = string.IsNullOrWhiteSpace(qi.DetectorType) ? detectorType : qi.DetectorType;
                        if (!string.IsNullOrWhiteSpace(fw)) qi.FirmwareVersion = fw;
                        qi.LastUpdateUtc = now;
                    }

                    MirrorQueueToDevice(qi);
                }
            });
        }
        catch { }
    }

    private void HandleProgrammingListTele(string cassia, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("programmingList", out var listEl) || listEl.ValueKind != JsonValueKind.Array)
                return;

            var now = DateTimeOffset.UtcNow;

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var item in listEl.EnumerateArray())
                {
                    var mac = item.TryGetProperty("mac", out var m) ? (m.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(mac)) continue;

                    var detectorType = item.TryGetProperty("detectorType", out var dt) ? (dt.GetString() ?? "") : "";
                    var fw = item.TryGetProperty("firmwareVersion", out var fv) ? (fv.GetString() ?? "") : "";

                    var qi = QueueItems.FirstOrDefault(q => q.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
                    if (qi == null)
                    {
                        qi = new QueueItem
                        {
                            Mac = mac,
                            Cassia = cassia,
                            Command = DefaultCommand,
                            Status = "Programming",
                            Progress = 1,
                            FirmwareVersion = fw,
                            DetectorType = detectorType,
                            LastUpdateUtc = now
                        };
                        QueueItems.Add(qi);
                    }
                    else
                    {
                        qi.Cassia = cassia;
                        qi.Status = "Programming";
                        if (qi.Progress <= 0) qi.Progress = 1;
                        qi.DetectorType = string.IsNullOrWhiteSpace(qi.DetectorType) ? detectorType : qi.DetectorType;
                        if (!string.IsNullOrWhiteSpace(fw)) qi.FirmwareVersion = fw;
                        qi.LastUpdateUtc = now;
                    }

                    MirrorQueueToDevice(qi);
                }
            });
        }
        catch { }
    }

    private void HandleParallelProgrammersTele(string cassia, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            int value = 0;
            if (root.ValueKind == JsonValueKind.Number)
                value = root.GetInt32();
            else if (root.TryGetProperty("value", out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var vi)) value = vi;
                else if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var vsi)) value = vsi;
            }

            if (value <= 0) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                var gw = CassiaGateways.FirstOrDefault(x => x.Name.Equals(cassia, StringComparison.OrdinalIgnoreCase));
                if (gw == null)
                {
                    gw = new CassiaGateway { Name = cassia, NetworkId = NetworkId };
                    CassiaGateways.Add(gw);
                    EnsureCassiaOption(cassia);
                }

                gw.ParallelProgrammers = value;
                gw.ParallelProgrammersDesired = value;
            });
        }
        catch { }
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


    private void FlushBufferedProgressOnUi()
    {
        List<BufferedProgress> batch;
        lock (_progressBufLock)
        {
            if (_progressByMac.Count == 0) return;
            batch = _progressByMac.Values.ToList();
            _progressByMac.Clear();
        }

        // Apply minimal diffs - do NOT refresh CollectionViews per item.
        // QueueView refresh is throttled by doing it once after the batch.
        var anyQueueChanged = false;

        foreach (var p in batch)
        {
            // Per-device throttle: if percent didn't change and last apply was very recent, skip.
            var now = DateTimeOffset.UtcNow;
            var pctRounded = (int)Math.Round(p.ProgressPercent, 0);

            // Update discovered device if present
            if (_deviceByMac.TryGetValue(p.Mac, out var dev))
            {
                // Keep FW field as target firmware (not model)
                if (!string.IsNullOrWhiteSpace(p.FirmwareTarget) && dev.ProcessFirmware != p.FirmwareTarget)
                    dev.ProcessFirmware = p.FirmwareTarget;

                if (!string.IsNullOrWhiteSpace(p.Stage) && dev.ProcessStatus != p.Stage)
                    dev.ProcessStatus = p.Stage;

                if (dev.ProcessProgress != pctRounded)
                    dev.ProcessProgress = pctRounded;

                if (!string.IsNullOrWhiteSpace(p.Cassia) && dev.ProcessCassia != p.Cassia)
                    dev.ProcessCassia = p.Cassia;

                dev.ProcessLastUpdateUtc = p.TimeUtc;
            }

            // Update queue item (keyed by mac)
            var qi = QueueItems.FirstOrDefault(x => x.Mac.Equals(p.Mac, StringComparison.OrdinalIgnoreCase));
            if (qi == null)
            {
                qi = new QueueItem
                {
                    Mac = p.Mac,
                    Cassia = p.Cassia,
                    Status = p.Stage,
                    Progress = pctRounded,
                    LastUpdateUtc = p.TimeUtc,
                };
                QueueItems.Add(qi);
                anyQueueChanged = true;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(p.Cassia) && qi.Cassia != p.Cassia) qi.Cassia = p.Cassia;
                if (!string.IsNullOrWhiteSpace(p.Stage) && qi.Status != p.Stage) qi.Status = p.Stage;
                if (qi.Progress != pctRounded) qi.Progress = pctRounded;
                qi.LastUpdateUtc = p.TimeUtc;
                anyQueueChanged = true;
            }

            // Mirror to device list (existing logic)
            MirrorQueueToDevice(qi);
        }

        if (anyQueueChanged)
        {
            try { RequestQueueRefresh(); } catch { }
        }

        // Keep Cassia cards updated (total devices seen is updated elsewhere; here we at least keep queue/programming counts moving)
        try { UpdateCassiaCountsFromQueue(); } catch { }
    }

    private void UpdateCassiaCountsFromQueue()
    {
        // queue/programming derived from queue items; devicesSeen remains based on discovered tracking
        foreach (var gw in CassiaGateways)
        {
            var name = gw.Name ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;

            var q = QueueItems.Count(x => x.Cassia.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                          !((x.Status ?? "").Contains("Programming", StringComparison.OrdinalIgnoreCase)));
            var prog = QueueItems.Count(x => x.Cassia.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                             ((x.Status ?? "").Contains("Programming", StringComparison.OrdinalIgnoreCase) ||
                                              (x.Progress > 0 && x.Progress < 100)));

            gw.Queue = q;
            gw.Programming = prog;
        }
    }

}