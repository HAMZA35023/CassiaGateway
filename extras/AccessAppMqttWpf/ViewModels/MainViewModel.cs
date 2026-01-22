using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
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

    private readonly HostBleScannerService _hostBleScanner = new("10:B9:F7");
    private readonly ConcurrentDictionary<string, HostBleScannerService.HostBleUpdate> _hostBleLatest = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _hostBleUiTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<string, HostBleScanItem> _hostBleRowsByMac = new(StringComparer.OrdinalIgnoreCase);

    public event Action? RequestClearHostBleSelection;

    // ---- UI update cadence (throttled at MQTT client level) ----
    // Progress updates are emitted every 5 seconds, discovered every 15 seconds.
    // We show countdowns so users understand why numbers/statuses are not "live" per message.
    private readonly DispatcherTimer _uiCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _progressCountdownSec = 5;
    private int _discoveredCountdownSec = 15;


    // Suppress repeated weak-RSSI warnings during bulk/batch queue operations
    private bool _suppressWeakRssiPrompt;
    [ObservableProperty] private string progressUiCountdownText = "Progress UI update in 5s";
    [ObservableProperty] private string discoveredUiCountdownText = "Discovered UI update in 15s";


    // ---- Firmware manifest (tele/.../fw-manifest) ----
    private readonly DispatcherTimer _fwManifestValidateTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _fwManifestTimeoutTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly DispatcherTimer _notesAutosaveTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _fwManifestTimeoutArmed;
    private string _lastFwManifestMissingHash = "";

    // After each connect we wait for per-gateway status, then request its FW manifest once.
    private readonly HashSet<string> _fwManifestRequestedForGw = new(StringComparer.OrdinalIgnoreCase);
    // After each connect we request queue/programming/parallel-programmers once per gateway.
    private readonly HashSet<string> _runtimeStateRequestedForGw = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deviceListRequestedForGw = new(StringComparer.OrdinalIgnoreCase);
    private bool _deviceListRequestedAfterConnect;
    private DateTimeOffset _connectedAtUtc = DateTimeOffset.MinValue;

    // Track last subscribed NetworkId to avoid duplicate resubscribe spam.
    private string _lastSubscribedNetworkId = "";


    private readonly ObservableCollection<DiscoveredDevice> _devices = new();
    private readonly Dictionary<string, DiscoveredDevice> _deviceByMac = new(StringComparer.OrdinalIgnoreCase);

    // Raw discovered devices collection (useful for code-behind context menus)
    public ObservableCollection<DiscoveredDevice> Devices => _devices;

    public ICollectionView FilteredDevices { get; }

    public ObservableCollection<HostBleScanItem> HostBleDevices { get; } = new();
    public ICollectionView HostBleDevicesView { get; }

    public ObservableCollection<int> HostRssiAverageOptions { get; } = new() { 5, 10, 20, 30, 60 };

    [ObservableProperty] private int hostRssiAverageSeconds = 10;

    public ObservableCollection<int> HostBleUiUpdateOptions { get; } = new() { 2, 5, 10, 20, 30, 60 };

    [ObservableProperty] private int hostBleUiUpdateSeconds = 10;

    [ObservableProperty] private bool hostBleAutoUpdate = false;
// Host BLE model filter
    public ObservableCollection<string> HostBleModelOptions { get; } = new(new[] { "All" });
    [ObservableProperty] private string hostBleModelFilter = "All";

    // Host BLE extra filters
    [ObservableProperty] private bool hostBleHideCompleted;
    [ObservableProperty] private bool hostBleHideInQueue;
    [ObservableProperty] private string hostBleSearchText = "";

    [ObservableProperty] private string hostBleUiStatusText = "";

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

    private readonly System.Windows.Threading.DispatcherTimer _gatewayStaleTimer;
    private static readonly TimeSpan GatewayOfflineAfter = TimeSpan.FromMinutes(1);

    public string ConnectButtonText => IsConnected ? "Disconnect" : "Connect";
    public string DevicesSubtitle => $"{FilteredDevices.Cast<object>().Count()} device(s) • model: {SensorFilter} • filter: {DeviceFilter}";

    private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> _gwSeenMacs
    = new(StringComparer.OrdinalIgnoreCase);

    // Sticky per-device assignment.
    // - We auto-assign ONCE when a device first appears.
    // - We NEVER change assignment when RSSI changes, unless user presses "Reassign".
    private const int AssignmentRssiSlack = 20; // if another cassia is within 8-10 RSSI, it can take the device for balancing

    // ---- RSSI balancing thresholds (requested to be variables at top of the class) ----
    // Note: RSSI values are negative; e.g. -60 is stronger than -80.
    private const int RssiAllowBalancingThreshold = -65;   // >= -65: allow balancing among eligible Cassias
    private const int RssiWarnQueueThreshold = -70;        // < -70: show warning before queueing (still allowed)

    // Weights for balancing: lower score wins. Score = (load * weight) - (rssi * 1). Since RSSI is negative, stronger (less negative) lowers score.
    private const int AssignmentLoadWeight = 10;            // how much 1 queued/programming item counts vs 1 dB RSSI
    private const int RssiForceClosestThreshold = -999; // unused (kept for compatibility)     // <= -75: always use the closest Cassia (best RSSI)

    // Balancing goal: finish fastest by keeping roughly the same amount of work per Cassia.
    // We count "assigned detectors" as part of the load, not only queue/programming, because
    // your workflow tends to keep using the assigned Cassia for that device.
    private const int AssignedDetectorsWeight = 1; // 1 = treat one assigned detector as one unit of load

    private readonly HashSet<string> _deviceAssignmentWired = new(StringComparer.OrdinalIgnoreCase);

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
        public bool IsUpgradeWarn = false;
        public bool IsUpgradeFailed = false;
        public string LastTargetFw = "";
        public DateTimeOffset? LastUpgradeSuccessUtc = null;

        public bool IsInQueue = false;
    }

    private readonly Dictionary<string, CachedDeviceStatus> _cachedStatusByMac = new(StringComparer.OrdinalIgnoreCase);

    private DiscoveredDevice? FindDiscoveredDevice(string mac) =>
        _devices.FirstOrDefault(d => d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));

    // Hook per-device property changes so we can initialize AssignedCassia from BestCassia
    // (but never overwrite a user/manual assignment).
    private void WireDeviceAssignmentHooks(DiscoveredDevice dev)
    {
        if (dev == null) return;

        // If the device is first seen and has no assignment yet, seed it from BestCassia when available.
        void EnsureSeed()
        {
            if (!string.IsNullOrWhiteSpace(dev.AssignedCassia)) return;
            if (string.IsNullOrWhiteSpace(dev.BestCassia)) return;
            dev.AssignedCassia = dev.BestCassia;
        }

        EnsureSeed();

        dev.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiscoveredDevice.BestCassia))
                EnsureSeed();
        };
    }

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

        // Upgrade result flags come from upgrade-log completion stage.
        // These are independent from queue/progress UI and are overridden visually by IsInQueue.
        dev.IsUpgradeWarn = cs.IsUpgradeWarn;
        dev.IsUpgradeFailed = cs.IsUpgradeFailed;

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

    private static readonly Regex ProductNumberRx =
        new(@"^\d{3}-\d{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ProductNumberUnderscoreRx =
        new(@"^\d{3}_\d{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static void ApplyDeviceNameWithGuards(DiscoveredDevice d, string? incomingName)
    {
        if (d == null) return;
        var newName = (incomingName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(newName)) return;

        var cur = (d.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cur))
        {
            d.Name = newName;
            return;
        }

        // If we already have a name, never overwrite it with a plain product-number name (xxx-xxxxxx).
        // Exception: if our current name is itself a product-number variant using '_' instead of '-',
        // allow upgrading/normalizing it.
        if (ProductNumberRx.IsMatch(newName) && !ProductNumberUnderscoreRx.IsMatch(cur))
            return;

        d.Name = newName;
    }
    public event Action<string, string>? PlainReplyReceived; // mac, message

    [ObservableProperty] private DiscoveredDevice? selectedDevice;
    [ObservableProperty] private HostBleScanItem? selectedHostBleDevice;
    [ObservableProperty] private QueueItem? selectedQueueItem;
    [ObservableProperty] private string? selectedQueueMac;

    [ObservableProperty] private bool enableDoubleClickQueue;

    // Devices list options
    [ObservableProperty] private bool hideCompletedDevices = false;



    [ObservableProperty] private string deviceFilter = "";
    [ObservableProperty] private string sensorFilter = "All";

    [ObservableProperty] private string mqttHost = "prod.statistics.niko-test.nu";
    [ObservableProperty] private int mqttPort = 18883;
    [ObservableProperty] private string mqttTopic = "accessapp/#";
    [ObservableProperty] private string mqttUser = "accessapp";
    [ObservableProperty] private string? mqttPassword = "Niko1234!";
    [ObservableProperty] private bool useTls;
    [ObservableProperty] private bool ignoreTlsErrors = true;

    [ObservableProperty] private string notesText = "";

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


    // Upgrade log view filters
    public ObservableCollection<string> UpgradeLogShowOptions { get; } = new()
    {
        "All",
        "Only failed",
        "Hide success",
        "Only success"
    };

    [ObservableProperty] private string selectedUpgradeLogShowOption = "All";
    [ObservableProperty] private bool upgradeLogLatestOnlyPerMac = false;



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

    // Latest-per-MAC filtering support for UpgradeLogGroupsView.
    // Rebuilt on-demand when filters change or new entries arrive.
    private readonly object _latestUpgradeLogMapLock = new();
    private readonly Dictionary<string, string> _latestUpgradeLogIdByMac = new(StringComparer.OrdinalIgnoreCase);
    private bool _latestUpgradeLogMapDirty = true;

    
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

        // Notes: load autosaved content (survives restarts)
        NotesText = NotesService.LoadAutoNotes();

        _notesAutosaveTimer.Tick += (_, __) =>
        {
            NotesService.SaveAutoNotes(NotesText);
        };
        _notesAutosaveTimer.Start();

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
                _deviceListRequestedForGw.Clear();
                _deviceListRequestedAfterConnect = false;
            }
            else
            {
                // Keep the last subscriptions remembered; next connect/resync will subscribe again.
                // (We don't force-clear UI on disconnect; user asked for re-sync on reconnect.)
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

        HostBleDevicesView = CollectionViewSource.GetDefaultView(HostBleDevices);
        HostBleDevicesView.SortDescriptions.Add(new SortDescription(nameof(HostBleScanItem.AvgHostRssi), ListSortDirection.Descending));
        HostBleDevicesView.SortDescriptions.Add(new SortDescription(nameof(HostBleScanItem.Mac), ListSortDirection.Ascending));

        HostBleDevicesView.Filter = obj =>
        {
            if (obj is not HostBleScanItem r) return false;

            // Search by MAC
            var q = (HostBleSearchText ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(q))
            {
                if (!(r.Mac?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                    return false;
            }

            // Hide if completed / queued
            if (HostBleHideCompleted && r.IsUpgradeSuccess)
                return false;
            if (HostBleHideInQueue && r.IsInQueue)
                return false;

            var filter = (HostBleModelFilter ?? "All").Trim();
            if (filter.Length == 0 || filter.Equals("All", StringComparison.OrdinalIgnoreCase))
                return true;
            var m = (r.SensorModel ?? "").Trim();
            if (m.Length == 0) m = "Unknown";
            return m.Equals(filter, StringComparison.OrdinalIgnoreCase);
        };
        FilteredDevices.Filter = obj =>
        {
            if (obj is not DiscoveredDevice d) return false;

                        if (HideCompletedDevices && d.IsUpgradeSuccess)
                return false;

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

            if (!UpgradeLogGroupPassesFilters(g))
                return false;

            // Latest-per-MAC filter (after other filters)
            if (UpgradeLogLatestOnlyPerMac)
            {
                EnsureLatestUpgradeLogMap();
                if (_latestUpgradeLogIdByMac.TryGetValue(g.Mac ?? "", out var latestId))
                    return string.Equals(latestId, g.LogId, StringComparison.OrdinalIgnoreCase);
                return false;
            }

            return true;
        };

        LogGatewayOptions.Clear();
        LogGatewayOptions.Add("All");

        CassiaNameOptions.Clear();
        CassiaNameOptions.Add("(auto)");
    
// --- Host BLE scanner (PC side) ---
// We keep the BLE scanner "hot" (updates every second), but throttle UI refreshes to once per 10 seconds
// to avoid grid churn while the user is interacting.
_hostBleScanner.WindowSeconds = HostRssiAverageSeconds;
_hostBleScanner.Updated += u =>
{
    // Just buffer latest values; UI is updated on a timer.
    _hostBleLatest[u.Mac] = u;
};

_hostBleUiTimer.Tick += (_, _) => FlushHostBleToUi();
UpdateHostBleUiStatus();
if (HostBleAutoUpdate) _hostBleUiTimer.Start();

_hostBleScanner.Start();
}

    partial void OnSelectedLogGatewayNameChanged(string value)
    {
        MarkLatestUpgradeLogMapDirty();
        UpgradeLogGroupsView.Refresh();
    }

    
    partial void OnSelectedUpgradeLogShowOptionChanged(string value)
    {
        MarkLatestUpgradeLogMapDirty();
        UpgradeLogGroupsView.Refresh();
    }

    partial void OnUpgradeLogLatestOnlyPerMacChanged(bool value)
    {
        MarkLatestUpgradeLogMapDirty();
        UpgradeLogGroupsView.Refresh();
    }

partial void OnUpgradeLogSearchTextChanged(string value)
    {
        MarkLatestUpgradeLogMapDirty();
        UpgradeLogGroupsView.Refresh();
    }

    partial void OnHostRssiAverageSecondsChanged(int value)
    {
        // Update scanner window immediately.
        try { _hostBleScanner.WindowSeconds = value; } catch { }
    }

    partial void OnHostBleAutoUpdateChanged(bool value)
    {
        try
        {
            _hostBleUiTimer.Stop();
            if (value)
            {
                _hostBleUiTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, HostBleUiUpdateSeconds));
                _hostBleUiTimer.Start();
            }
            UpdateHostBleUiStatus();
        }
        catch { }
    }

partial void OnHostBleUiUpdateSecondsChanged(int value)
    {
        try
        {
            var v = Math.Max(1, value);
            _hostBleUiTimer.Interval = TimeSpan.FromSeconds(v);
            ResetHostBleUiTimer();
        }
        catch { }
    }

    partial void OnHostBleModelFilterChanged(string value)
    {
        try { HostBleDevicesView.Refresh(); } catch { }
    }

    partial void OnHostBleHideCompletedChanged(bool value)
    {
        try { HostBleDevicesView.Refresh(); } catch { }
    }

    partial void OnHostBleHideInQueueChanged(bool value)
    {
        try { HostBleDevicesView.Refresh(); } catch { }
    }

    partial void OnHostBleSearchTextChanged(string value)
    {
        try { HostBleDevicesView.Refresh(); } catch { }
    }

    [RelayCommand]
    private void HostBleRefreshUi()
    {
        // Manual refresh button.
        FlushHostBleToUi();
        ResetHostBleUiTimer();
    }

    private void ScheduleHostBleUiRefresh(TimeSpan delay)
    {
        try
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(delay).ConfigureAwait(false);
                try
                {
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        try { HostBleRefreshUi(); } catch { }
                    });
                }
                catch { }
            });
        }
        catch { }
    }

    public void ResetHostBleUiTimer()
    {
        // "Freeze" grid churn while the user clicks/reads: next refresh will be N seconds after the last click.
        try
        {
            _hostBleUiTimer.Stop();
            _hostBleUiTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, HostBleUiUpdateSeconds));

            if (HostBleAutoUpdate)
                _hostBleUiTimer.Start();

            UpdateHostBleUiStatus();
        }
        catch { }
    }

    private void UpdateHostBleUiStatus()
    {
        try
        {
            HostBleUiStatusText = HostBleAutoUpdate
                ? ("Auto-update: " + HostBleUiUpdateSeconds + "s")
                : "Auto-update: off";
        }
        catch { }
    }


    private void FlushHostBleToUi()
    {
        // Preserve selection by MAC
        var selectedMac = SelectedHostBleDevice?.Mac;

        var items = _hostBleLatest.Values
            .OrderByDescending(x => x.AvgRssi)
            .ThenBy(x => x.Mac, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Update rows in-place so selection doesn't drop and buttons work without a "first click".
        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in items)
        {
            alive.Add(u.Mac);

            if (!_hostBleRowsByMac.TryGetValue(u.Mac, out var row))
            {
                row = new HostBleScanItem { Mac = u.Mac };
                _hostBleRowsByMac[u.Mac] = row;
                HostBleDevices.Add(row);
            }

            row.AvgHostRssi = u.AvgRssi;
            row.LastSeenUtc = u.LastSeenUtc;

            // Merge Cassia RSSIs + closest + current FW from discovered device list (if present)
            var d = FindDiscoveredDevice(u.Mac);
            if (d != null)
            {
                row.SetCassiaRssi(d.CassiaRssi);
                row.ClosestCassia = d.BestCassia ?? "";
                row.SensorModel = (d.SensorModel ?? "").Trim();
                row.CurrentFw = d.DisplayFw ?? "";

                // Coloring semantics (same as device list)
                row.IsInQueue = d.IsInQueue;
                row.IsUpgradeSuccess = d.IsUpgradeSuccess;
                row.IsUpgradeWarn = d.IsUpgradeWarn;
                row.IsUpgradeFailed = d.IsUpgradeFailed;
            }
            else
            {
                row.SetCassiaRssi(null);
                row.ClosestCassia = "";
                row.SensorModel = row.SensorModel; // keep previous if any
                // keep CurrentFw (so it doesn't flicker empty if host sees adv before MQTT sees device)

                // Default flags when unknown
                row.IsInQueue = false;
                row.IsUpgradeSuccess = false;
                row.IsUpgradeWarn = false;
                row.IsUpgradeFailed = false;
            }
        }

        // Remove stale rows not present in latest set (keeps grid tight)
        for (int i = HostBleDevices.Count - 1; i >= 0; i--)
        {
            var r = HostBleDevices[i];
            if (r == null) continue;
            if (!alive.Contains(r.Mac))
            {
                HostBleDevices.RemoveAt(i);
                _hostBleRowsByMac.Remove(r.Mac);
            }
        }

        // Restore selection
        if (!string.IsNullOrWhiteSpace(selectedMac))
        {
            var sel = HostBleDevices.FirstOrDefault(x => string.Equals(x.Mac, selectedMac, StringComparison.OrdinalIgnoreCase));
            if (sel != null)
                SelectedHostBleDevice = sel;
        }

        try { HostBleDevicesView.Refresh(); } catch { }

        // Refresh model filter options (All + distinct models + Unknown)
        try
        {
            var models = HostBleDevices
                .Where(x => x != null)
                .Select(x => (x.SensorModel ?? "").Trim())
                .Select(m => string.IsNullOrWhiteSpace(m) ? "Unknown" : m)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var wanted = new List<string> { "All" };
            wanted.AddRange(models);

            // keep current selection if possible
            var current = (HostBleModelFilter ?? "All").Trim();

            HostBleModelOptions.Clear();
            foreach (var m in wanted) HostBleModelOptions.Add(m);

            if (HostBleModelOptions.Any(x => x.Equals(current, StringComparison.OrdinalIgnoreCase)))
                HostBleModelFilter = HostBleModelOptions.First(x => x.Equals(current, StringComparison.OrdinalIgnoreCase));
            else
                HostBleModelFilter = "All";
        }
        catch { }
    }

    private void MarkLatestUpgradeLogMapDirty()
    {
        _latestUpgradeLogMapDirty = true;
    }

    private void EnsureLatestUpgradeLogMap()
    {
        if (!_latestUpgradeLogMapDirty)
            return;

        lock (_latestUpgradeLogMapLock)
        {
            if (!_latestUpgradeLogMapDirty)
                return;

            _latestUpgradeLogIdByMac.Clear();

            foreach (var g in UpgradeLogGroups)
            {
                if (g == null) continue;
                if (!UpgradeLogGroupPassesFilters(g))
                    continue;

                var mac = (g.Mac ?? "").Trim();
                if (string.IsNullOrWhiteSpace(mac))
                    continue;

                if (_latestUpgradeLogIdByMac.TryGetValue(mac, out var existingLogId))
                {
                    var existing = UpgradeLogGroups.FirstOrDefault(x => string.Equals(x.LogId, existingLogId, StringComparison.OrdinalIgnoreCase));
                    if (existing != null && existing.LastTimeLocal >= g.LastTimeLocal)
                        continue;
                }

                _latestUpgradeLogIdByMac[mac] = g.LogId;
            }

            // Mark groups that have a newer group for the same MAC (yellow "newer entry" badge)
            foreach (var g in UpgradeLogGroups)
            {
                if (g == null) continue;
                var mac = (g.Mac ?? "").Trim();
                if (string.IsNullOrWhiteSpace(mac))
                {
                    g.HasNewerForMac = false;
                    continue;
                }

                if (_latestUpgradeLogIdByMac.TryGetValue(mac, out var latestId))
                    g.HasNewerForMac = !string.Equals(latestId, g.LogId, StringComparison.OrdinalIgnoreCase);
                else
                    g.HasNewerForMac = false;

                g.NotifyHeaderChanged();
            }

            _latestUpgradeLogMapDirty = false;
        }
    }

    private bool UpgradeLogGroupPassesFilters(UpgradeLogGroup g)
    {
        if (g == null) return false;

        // Gateway filter
        var gw = (SelectedLogGatewayName ?? "All").Trim();
        if (!string.IsNullOrWhiteSpace(gw) && !string.Equals(gw, "All", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals((g.Cassia ?? "").Trim(), gw, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Text search filter
        var s = (UpgradeLogSearchText ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(s))
        {
            if (!ContainsIgnoreCase(g.LogId, s)
                && !ContainsIgnoreCase(g.Mac, s)
                && !ContainsIgnoreCase(g.Cassia, s)
                && !ContainsIgnoreCase(g.LatestFirmware, s)
                && !ContainsIgnoreCase(g.LatestStage, s)
                && !ContainsIgnoreCase(g.LatestStatus, s)
                && !ContainsIgnoreCase(g.LatestSummary, s))
                return false;
        }

        // Show option filter
        var option = (SelectedUpgradeLogShowOption ?? "All").Trim();
        var status = (g.LatestStatus ?? "").Trim();
        var stage = (g.LatestStage ?? "").Trim();

        var isSuccess = string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)
                        || (string.Equals(stage, "Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase));

        // Treat anything that looks like an error/fail as failure
        var statusLower = status.ToLowerInvariant();
        var stageLower = stage.ToLowerInvariant();
        var isFailure = statusLower.Contains("fail") || statusLower.Contains("error") || statusLower.Contains("timeout") || statusLower.Contains("aborted")
                        || stageLower.Contains("fail") || stageLower.Contains("error") || stageLower.Contains("timeout") || stageLower.Contains("aborted");

        if (string.Equals(option, "Only success", StringComparison.OrdinalIgnoreCase))
            return isSuccess;

        if (string.Equals(option, "Hide success", StringComparison.OrdinalIgnoreCase))
            return !isSuccess;

        if (string.Equals(option, "Only failed", StringComparison.OrdinalIgnoreCase))
            return isFailure || (!isSuccess && !string.IsNullOrWhiteSpace(status) && !string.Equals(status, "Info", StringComparison.OrdinalIgnoreCase));

        return true;
    }

    private static bool ContainsIgnoreCase(string? haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(haystack)) return false;
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
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

    
    partial void OnHideCompletedDevicesChanged(bool value)
    {
        FilteredDevices.Refresh();
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

            // Always start a fresh session when connecting (clears UI + internal caches)
            // so reconnect behaves the same as a "clean" connect.
            ClearAllUiAndState();

            await _mqtt.ConnectAsync(
                MqttHost,
                MqttPort,
                MqttUser,
                MqttPassword ?? "",
                UseTls,
                IgnoreTlsErrors,
                MqttTopic,
                _appCts.Token);

            // Full clean re-sync (subscribe + request snapshots).
            await ResyncCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Error: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveSettings()
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

        // If we are connected, immediately re-sync to reflect the new NetworkId/topic scope.
        if (IsConnected)
            await ResyncCoreAsync().ConfigureAwait(false);
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
    private async Task ClearAndReloadDeviceList()
    {
        // Clear local discovered devices (does NOT stop running upgrades on the Cassias).
        _devices.Clear();
        _deviceByMac.Clear();
        _gwSeenMacs.Clear();

        // Keep queue/progress cache so queued/programming still shows if devices come back.
        // But reset per-device assignment counts.
        foreach (var gw in CassiaGateways)
        {
            gw.AssignedP41 = gw.AssignedP42 = gw.AssignedP46 = gw.AssignedP47 = gw.AssignedP48 = 0;
        }

        RequestDevicesRefresh();

        // Request full device list from all gateways.
        await RequestDeviceListAsync("all").ConfigureAwait(false);
    }

[RelayCommand]
    private void ClearQueue() => QueueItems.Clear();


    [RelayCommand]
    private void CopyNotes()
    {
        try { Clipboard.SetText(NotesText ?? ""); } catch { }
    }

    [RelayCommand]
    private void ClearNotes()
    {
        NotesText = "";
        NotesService.SaveAutoNotes(NotesText);
    }

    [RelayCommand]
    private void LoadNotes()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = ".txt",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() == true)
            {
                NotesText = NotesService.LoadFromFile(dlg.FileName);
                NotesService.SaveAutoNotes(NotesText);
            }
        }
        catch { }
    }

    [RelayCommand]
    private void SaveNotes()
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = ".txt",
                FileName = "notes.txt",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog() == true)
            {
                NotesService.SaveToFile(dlg.FileName, NotesText);
                NotesService.SaveAutoNotes(NotesText);
            }
        }
        catch { }
    }

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

        if (selected.Count == 0) return;

        // If any selected device has very weak RSSI (< -70), warn once (device is still queueable).
        var weak = selected
            .Where(d => d != null && d.CassiaRssi != null && d.CassiaRssi.Count > 0)
            .Select(d => new
            {
                Dev = d,
                Best = d.CassiaRssi.Where(kv => !string.IsNullOrWhiteSpace(kv.Key)).OrderByDescending(kv => kv.Value).FirstOrDefault()
            })
            .Select(x => new { x.Dev, BestCassia = (x.Best.Key ?? "").Trim(), BestRssi = x.Best.Value })
            .Where(x => x.BestRssi < RssiWarnQueueThreshold)
            .ToList();

        if (weak.Count > 0)
        {
            var lines = weak
                .OrderBy(x => x.BestRssi)
                .Take(20)
                .Select(x => $"{x.Dev.Mac}  best={x.BestCassia}:{x.BestRssi} dBm")
                .ToList();

            var more = weak.Count > 20 ? $"\n... and {weak.Count - 20} more" : "";

            var res = MessageBox.Show(
                "Warning: Some devices have weak RSSI (< -70 dBm).\n\n" +
                string.Join("\n", lines) + more +
                "\n\nQueue anyway?",
                "Weak RSSI",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes)
                return;
        }

        // Build a preview of what will be queued and where (batch-aware load balancing).
        var plan = ComputeBatchAssignmentPlan(selected);

        // Show planned changes in a dedicated dialog (instead of MessageBox).
        var dialogRows = BuildAssignmentRowsFromDevices(selected, plan);
        var loadRows = BuildLoadSummaryForPlannedAdds(dialogRows);

        var dlgResult = ShowAssignmentPlanDialog(
            title: "Add to queue",
            subtitle: "Review suggested Cassia assignment (RSSI + load balancing) before queueing",
            rows: dialogRows,
            loadRows: loadRows,
            footer: "Apply = use suggested assignment • Keep current = use current assignment • Cancel = abort",
            notes: $"Rules: If best RSSI < {RssiAllowBalancingThreshold} we always pick the closest Cassia. Otherwise we balance using (assigned*{AssignedDetectorsWeight} + queue + programming), preferring ONLINE gateways and using RSSI as tie-break. If best RSSI < {RssiWarnQueueThreshold}, you get a warning.",
            showKeepButton: true);

        if (dlgResult == AssignmentPlanDialogResult.Cancel) return;

        if (dlgResult == AssignmentPlanDialogResult.Apply)
        {
            ApplySuggestedAssignmentsToDevices(selected, dialogRows);
        }

        _suppressWeakRssiPrompt = true;
        try
        {
            foreach (var d in selected)
            {
                await QueueDeviceAndRequestAsync(d);
                d.IsSelected = false;
            }
        }
        finally
        {
            _suppressWeakRssiPrompt = false;
        }
    }

    [RelayCommand]
    private async Task IdentifyHost(HostBleScanItem? item)
    {
        item ??= SelectedHostBleDevice;
        if (item == null || string.IsNullOrWhiteSpace(item.Mac))
            return;

        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        var mac = item.Mac.Trim();
        var target = string.IsNullOrWhiteSpace(item.ClosestCassia) ? "all" : item.ClosestCassia.Trim();

        var topic = BuildCmdTopic(target, "identify");
        var payload = new { sensors = new[] { mac } };

        try
        {
            await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token);
            ConnectionStatus = $"Identify sent to {target} for {mac}";
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Identify failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task AddToQueueHost(HostBleScanItem? item)
    {
        // Backwards-compat command name. UI uses UpdateHost.
        await UpdateHost(item);
    }

    [RelayCommand]
    private async Task UpdateHost(HostBleScanItem? item)
    {
        item ??= SelectedHostBleDevice;
        if (item == null || string.IsNullOrWhiteSpace(item.Mac))
            return;

        var mac = item.Mac.Trim();
        // UX: clear selection immediately when pressing Update (even if user clicked the button on an unselected row).
        try { RequestClearHostBleSelection?.Invoke(); } catch { }

        // Reuse discovered device object if present; otherwise create a minimal one.
        var dev = FindDiscoveredDevice(mac);
        if (dev == null)
        {
            dev = new DiscoveredDevice
            {
                Mac = mac,
                Name = "BLE (host scan)",
            };
            _devices.Add(dev);
            _deviceByMac[mac] = dev;
        }

        // IMPORTANT: Host BLE scan rows keep per-Cassia RSSIs in HostBleScanItem.CassiaRssi.
        // The balancing algorithm uses DiscoveredDevice.CassiaRssi, so we must sync them here.
        // Otherwise the algorithm only sees the "closest" Cassia and cannot choose a free alternative.
        if (item.CassiaRssi != null && item.CassiaRssi.Count > 0)
        {
            dev.CassiaRssi.Clear();
            foreach (var kv in item.CassiaRssi)
            {
                var k = (kv.Key ?? "").Trim();
                if (k.Length == 0) continue;
                dev.CassiaRssi[k] = kv.Value;
            }

            // Refresh best Cassia/RSSI fields.
            var best = dev.CassiaRssi.OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(best.Key))
            {
                dev.BestCassia = best.Key.Trim();
                dev.BestRssi = best.Value;
            }
        }

        // Suggested Cassia: use the SAME algorithm as Add-to-queue/Reassign.
        // - If best RSSI < -65 => closest Cassia.
        // - If best RSSI >= -65 => balance using Cassia reported Queue/Programming, but still prefer closest when similar.
        if (!TryChooseCassiaForUpdate(dev, plannedLoad: null, out var suggested, out var suggestedReason))
        {
            suggested = (item.ClosestCassia ?? "").Trim();
            if (suggested.Length == 0) suggested = (dev.BestCassia ?? "").Trim();
            if (suggested.Length == 0) suggested = (dev.AssignedCassia ?? "").Trim();
            suggestedReason = "closest fallback";
        }

        var current = (dev.AssignedCassia ?? "").Trim();
        if (current.Length == 0) current = (dev.BestCassia ?? "").Trim();

        int GetRssiNormalized(string c)
        {
            c = (c ?? "").Trim();
            if (c.Length == 0) return int.MinValue;
            foreach (var kv in dev.CassiaRssi)
            {
                var k = (kv.Key ?? "").Trim();
                if (string.Equals(k, c, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
            return int.MinValue;
        }

        var closestCassia = (dev.BestCassia ?? suggested).Trim();
        var closestRssi = GetRssiNormalized(closestCassia);
        var suggestedRssi = GetRssiNormalized(suggested);

        // If weak (< -70) and user hasn't suppressed warnings, warn before queueing (single-device case).
        if (!_suppressWeakRssiPrompt && suggestedRssi != int.MinValue && suggestedRssi < RssiWarnQueueThreshold)
        {
            var res = MessageBox.Show(
                $"Warning: weak RSSI (< {RssiWarnQueueThreshold} dBm) for {mac}.\n\n" +
                $"Suggested: {suggested} @ {suggestedRssi} dBm\n\nQueue anyway?",
                "Weak RSSI",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes)
                return;
        }

        var rows = new ObservableCollection<AssignmentChangeRow>
        {
            new AssignmentChangeRow
            {
                Mac = mac,
                ClosestCassia = closestCassia,
                ClosestRssi = closestRssi == int.MinValue ? 0 : closestRssi,
                CurrentAssigned = current,
                SuggestedAssigned = suggested.Length == 0 ? current : suggested,
                SuggestedRssi = suggestedRssi == int.MinValue ? 0 : suggestedRssi,
                Reason = suggestedReason
            }
        };

        var loadRows = BuildLoadSummaryForPlannedAdds(rows);

        var dlg = ShowAssignmentPlanDialog(
            title: "Update device",
            subtitle: $"{mac} • choose Cassia assignment before queueing",
            rows: rows,
            loadRows: loadRows,
            footer: "Apply = set suggested Cassia and queue update • Keep current = queue without changing assignment",
            notes: $"Suggestion uses the same balancing rules as Add-to-queue/Reassign.\n" +
                   $"If best RSSI < {RssiAllowBalancingThreshold}: closest Cassia. Otherwise: balance using Cassia Queue/Programming, while still preferring closest when similar.",
            showKeepButton: true);

        if (dlg == AssignmentPlanDialogResult.Cancel)
            return;

        if (dlg == AssignmentPlanDialogResult.Apply)
        {
            var chosen = (rows.FirstOrDefault()?.SuggestedAssigned ?? "").Trim();
            if (chosen.Length > 0)
            {
                dev.BestCassia = chosen;
                dev.AssignedCassia = chosen;
            }
        }

        // Queue + request FW on the chosen/assigned Cassia
        await QueueDeviceAndRequestAsync(dev);
        // UX: refresh Host BLE list shortly after Update so queued/assigned status & FW request results show up.
        ScheduleHostBleUiRefresh(TimeSpan.FromSeconds(2));

    }

    [RelayCommand]
    private async Task GetFirmwareHost(HostBleScanItem? item)
    {
        item ??= SelectedHostBleDevice;
        if (item == null || string.IsNullOrWhiteSpace(item.Mac))
            return;

        var mac = item.Mac.Trim();
        var dev = FindDiscoveredDevice(mac);
        if (dev == null)
        {
            dev = new DiscoveredDevice
            {
                Mac = mac,
                Name = "BLE (host scan)",
            };
            _devices.Add(dev);
            _deviceByMac[mac] = dev;
        }

        // Ensure we use the closest Cassia (if known)
        if (!string.IsNullOrWhiteSpace(item.ClosestCassia))
        {
            dev.BestCassia = item.ClosestCassia.Trim();
            dev.AssignedCassia = item.ClosestCassia.Trim();
        }

        await SendGetFwVersionAsync(new[] { dev });
    }

    private async Task RebalanceQueuedItems()
    {
        // Only rebalance items that are still pending in the queue (not actively programming/done).
        var queued = QueueItems
            .Where(q => q != null
                        && !string.IsNullOrWhiteSpace(q.Mac)
                        && string.Equals((q.Status ?? "").Trim(), "Queued", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (queued.Count == 0)
            return;

        // Map queue items to discovered devices (for RSSI/closest Cassia).
        var devices = queued
            .Select(q => FindDiscoveredDevice((q.Mac ?? "").Trim()))
            .Where(d => d != null)
            .Cast<DiscoveredDevice>()
            .ToList();

        if (devices.Count == 0)
            return;

        var plan = ComputeBatchAssignmentPlan(devices);
        var rows = BuildAssignmentRowsFromQueue(queued, plan);
        var loadRows = BuildLoadSummaryForMoves(rows);

        var dlgResult = ShowAssignmentPlanDialog(
            title: "Rebalance queued items",
            subtitle: "Suggested moves based on RSSI + workload balancing",
            rows: rows,
            loadRows: loadRows,
            footer: "Apply = move queue items • Cancel = do nothing",
            notes: $"Rules: If best RSSI < {RssiAllowBalancingThreshold} we always pick the closest Cassia. Otherwise we balance using (assigned*{AssignedDetectorsWeight} + queue + programming), preferring ONLINE gateways and using RSSI as tie-break. If best RSSI < {RssiWarnQueueThreshold}, you get a warning.",
            showKeepButton: false);

        if (dlgResult != AssignmentPlanDialogResult.Apply)
            return;

        // Apply changes sequentially (MQTT best-effort) so we can show reasons before the action.
        foreach (var r in rows.Where(r => r.IsChange))
        {
            var qi = queued.FirstOrDefault(q => (q.Mac ?? "").Trim().Equals((r.Mac ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (qi == null) continue;

            // Skip if it changed since plan was built.
            if (!string.Equals((qi.Cassia ?? "").Trim(), (r.CurrentAssigned ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            await MoveQueueItemToCassiaAsync(qi, r.SuggestedAssigned).ConfigureAwait(false);

            // Also update sticky assignment so future actions keep the device balanced on the same Cassia.
            var dev = FindDiscoveredDevice((r.Mac ?? "").Trim());
            if (dev != null && !string.IsNullOrWhiteSpace(r.SuggestedAssigned))
                dev.AssignedCassia = r.SuggestedAssigned.Trim();
        }

        RecalculateAssignmentCounts();
        RequestDevicesRefresh();
    }

    private void ApplySuggestedAssignmentsToDevices(IReadOnlyList<DiscoveredDevice> selected, ObservableCollection<AssignmentChangeRow> rows)
    {
        foreach (var r in rows.Where(r => r.IsChange))
        {
            var d = selected.FirstOrDefault(x => (x.Mac ?? "").Trim().Equals((r.Mac ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (d == null) continue;
            if (IsDeviceInWork(d)) continue;
            if (!string.IsNullOrWhiteSpace(r.SuggestedAssigned))
                d.AssignedCassia = r.SuggestedAssigned;
        }
        RecalculateAssignmentCounts();
        RequestDevicesRefresh();
    }

    private ObservableCollection<AssignmentChangeRow> BuildAssignmentRowsFromDevices(
        IReadOnlyList<DiscoveredDevice> devices,
        IReadOnlyList<AssignmentPlanItem> plan)
    {
        var rows = new ObservableCollection<AssignmentChangeRow>();
        var byMac = plan.ToDictionary(p => (p.Mac ?? "").Trim(), p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var d in devices.Where(d => d != null).OrderBy(d => d.Mac, StringComparer.OrdinalIgnoreCase))
        {
            var mac = (d.Mac ?? "").Trim();
            if (mac.Length == 0) continue;

            byMac.TryGetValue(mac, out var p);
            var suggested = (p?.Cassia ?? "").Trim();
            var current = (d.AssignedCassia ?? d.BestCassia ?? "").Trim();
            var closest = (d.BestCassia ?? "").Trim();

            rows.Add(new AssignmentChangeRow
            {
                Mac = mac,
                ClosestCassia = closest,
                ClosestRssi = d.BestRssi == int.MinValue ? 0 : d.BestRssi,
                CurrentAssigned = current,
                SuggestedAssigned = suggested.Length == 0 ? current : suggested,
                SuggestedRssi = (suggested.Length > 0 && d.CassiaRssi.TryGetValue(suggested, out var rr)) ? rr : (d.BestRssi == int.MinValue ? 0 : d.BestRssi),
                Reason = p?.Reason ?? ""
            });
        }

        return rows;
    }

    private ObservableCollection<AssignmentChangeRow> BuildAssignmentRowsFromQueue(
        IReadOnlyList<QueueItem> queued,
        IReadOnlyList<AssignmentPlanItem> plan)
    {
        var rows = new ObservableCollection<AssignmentChangeRow>();
        var byMac = plan.ToDictionary(p => (p.Mac ?? "").Trim(), p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var qi in queued.Where(q => q != null).OrderBy(q => q.Mac, StringComparer.OrdinalIgnoreCase))
        {
            var mac = (qi.Mac ?? "").Trim();
            if (mac.Length == 0) continue;

            var dev = FindDiscoveredDevice(mac);
            byMac.TryGetValue(mac, out var p);

            var suggested = (p?.Cassia ?? "").Trim();
            var current = (qi.Cassia ?? "").Trim();
            var closest = (dev?.BestCassia ?? "").Trim();
            var closestRssi = dev?.BestRssi ?? 0;

            rows.Add(new AssignmentChangeRow
            {
                Mac = mac,
                ClosestCassia = closest,
                ClosestRssi = closestRssi == int.MinValue ? 0 : closestRssi,
                CurrentAssigned = current,
                SuggestedAssigned = suggested.Length == 0 ? current : suggested,
                SuggestedRssi = (dev != null && suggested.Length > 0 && dev.CassiaRssi.TryGetValue(suggested, out var rr)) ? rr : (closestRssi == int.MinValue ? 0 : closestRssi),
                Reason = p?.Reason ?? (dev == null ? "device not in list" : "")
            });
        }

        return rows;
    }

    private ObservableCollection<CassiaLoadSummaryRow> BuildLoadSummaryForPlannedAdds(ObservableCollection<AssignmentChangeRow> rows)
    {
        // Summary is QUEUE + PROGRAMMING (these come from MQTT status).
        var before = CassiaGateways
            .Where(g => g != null && !string.IsNullOrWhiteSpace(g.Name))
            .ToDictionary(g => g.Name.Trim(), g => Math.Max(0, g.Queue) + Math.Max(0, g.Programming), StringComparer.OrdinalIgnoreCase);

        // After = before + planned queue adds per suggested Cassia
        var adds = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.SuggestedAssigned))
            .GroupBy(r => r.SuggestedAssigned.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var after = new Dictionary<string, int>(before, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in adds)
        {
            after[kv.Key] = (after.TryGetValue(kv.Key, out var v) ? v : 0) + kv.Value;
        }

        return BuildLoadRows(before, after);
    }

    private ObservableCollection<CassiaLoadSummaryRow> BuildLoadSummaryForMoves(ObservableCollection<AssignmentChangeRow> rows)
    {
        // Summary is QUEUE + PROGRAMMING (these come from MQTT status).
        var before = CassiaGateways
            .Where(g => g != null && !string.IsNullOrWhiteSpace(g.Name))
            .ToDictionary(g => g.Name.Trim(), g => Math.Max(0, g.Queue) + Math.Max(0, g.Programming), StringComparer.OrdinalIgnoreCase);

        // After = before + deltas from moves (queue moves only).
        var after = new Dictionary<string, int>(before, StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows.Where(r => r.IsChange))
        {
            var from = (r.CurrentAssigned ?? "").Trim();
            var to = (r.SuggestedAssigned ?? "").Trim();
            if (from.Length > 0)
                after[from] = (after.TryGetValue(from, out var v) ? v : 0) - 1;
            if (to.Length > 0)
                after[to] = (after.TryGetValue(to, out var v) ? v : 0) + 1;
        }
        return BuildLoadRows(before, after);
    }

    private ObservableCollection<CassiaLoadSummaryRow> BuildLoadRows(Dictionary<string, int> before, Dictionary<string, int> after)
    {
        var rows = new ObservableCollection<CassiaLoadSummaryRow>();
        var keys = before.Keys.Concat(after.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

        foreach (var k in keys)
        {
            var b = before.TryGetValue(k, out var bv) ? bv : 0;
            var a = after.TryGetValue(k, out var av) ? av : 0;

            var gw = CassiaGateways.FirstOrDefault(g => g != null && (g.Name ?? "").Trim().Equals(k, StringComparison.OrdinalIgnoreCase));
            rows.Add(new CassiaLoadSummaryRow
            {
                Cassia = k,
                BeforeLoad = b,
                AfterLoad = a,
                Delta = a - b,
                BeforeQueue = gw?.Queue ?? 0,
                BeforeProgramming = gw?.Programming ?? 0,
            });
        }
        return rows;
    }

    private AssignmentPlanDialogResult ShowAssignmentPlanDialog(
        string title,
        string subtitle,
        ObservableCollection<AssignmentChangeRow> rows,
        ObservableCollection<CassiaLoadSummaryRow> loadRows,
        string footer,
        string notes,
        bool showKeepButton)
    {
        var result = AssignmentPlanDialogResult.Cancel;

        Application.Current.Dispatcher.Invoke(() =>
        {
            AssignmentPlanWindow? win = null;
            var vm = new AssignmentPlanWindowViewModel(
                title: title,
                subtitle: subtitle,
                rows: rows,
                loadRows: loadRows,
                footer: footer,
                notes: notes,
                showKeepButton: showKeepButton,
                close: r =>
                {
                    result = r;
                    try { win?.Close(); } catch { }
                });

            win = new AssignmentPlanWindow(vm)
            {
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            try { win.ShowDialog(); } catch { }
        });

        return result;
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

    [RelayCommand]
    private async Task GetFwForDevice(DiscoveredDevice? device)
        => await GetFwForDeviceAsync(device);

    [RelayCommand]
    private async Task GetFwSelected()
        => await GetFwForSelectedAsync();

    internal async Task ConnectDeviceAsync(DiscoveredDevice? device)
    {
        if (device == null) return;
        await SendConnectOrDisconnectAsync(device, action: "connect");
    }

    internal async Task DisconnectDeviceAsync(DiscoveredDevice? device)
    {
        if (device == null) return;
        await SendDisconnectAsync(new[] { device });
    }

    internal async Task DisconnectDevicesAsync(IEnumerable<DiscoveredDevice> devices)
        => await SendDisconnectAsync(devices);

    internal async Task GetFwForDeviceAsync(DiscoveredDevice? device)
    {
        if (device == null) return;
        await SendGetFwVersionAsync(new[] { device });
    }

    internal async Task GetFwForSelectedAsync()
    {
        var selected = _devices.Where(d => d != null && d.IsSelected).ToList();
        if (selected.Count == 0) return;

        // If any selected device has very weak RSSI (< -70), warn once (device is still queueable).
        var weak = selected
            .Where(d => d != null && d.CassiaRssi != null && d.CassiaRssi.Count > 0)
            .Select(d => new
            {
                Dev = d,
                Best = d.CassiaRssi.Where(kv => !string.IsNullOrWhiteSpace(kv.Key)).OrderByDescending(kv => kv.Value).FirstOrDefault()
            })
            .Select(x => new { x.Dev, BestCassia = (x.Best.Key ?? "").Trim(), BestRssi = x.Best.Value })
            .Where(x => x.BestRssi < RssiWarnQueueThreshold)
            .ToList();

        if (weak.Count > 0)
        {
            var lines = weak
                .OrderBy(x => x.BestRssi)
                .Take(20)
                .Select(x => $"{x.Dev.Mac}  best={x.BestCassia}:{x.BestRssi} dBm")
                .ToList();

            var more = weak.Count > 20 ? $"\n... and {weak.Count - 20} more" : "";

            var res = MessageBox.Show(
                "Warning: Some devices have weak RSSI (< -70 dBm).\n\n" +
                string.Join("\n", lines) + more +
                "\n\nQueue anyway?",
                "Weak RSSI",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes)
                return;
        }
        await SendGetFwVersionAsync(selected);
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

        // Connect still uses cmd/<cassia>/connect.
        // Disconnect is now a dedicated cmd/<cassia>/disconnect endpoint.
        var isDisconnect = action.Equals("disconnect", StringComparison.OrdinalIgnoreCase);
        var topic = BuildCmdTopic(cassia, isDisconnect ? "disconnect" : "connect");

        object payload = isDisconnect
            ? new { sensors = new[] { mac } }
            : new { sensors = new[] { mac } };

        device.BleLink = action.Equals("disconnect", StringComparison.OrdinalIgnoreCase) ? "disconnecting…" : "connecting…";
        await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
    }

    private const string DefaultPincode = "1234";

    private async Task SendGetFwVersionAsync(IEnumerable<DiscoveredDevice> devices)
    {
        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        var list = devices?.Where(d => d != null && !string.IsNullOrWhiteSpace(d.Mac)).Distinct().ToList() ?? new();
        if (list.Count == 0) return;

        // Group by target Cassia because the topic contains the cassia name.
        var groups = list
            .Select(d => new
            {
                Dev = d,
                Cassia = ResolveCassiaForCommand(d)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Cassia))
            .GroupBy(x => x.Cassia, StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            var cassia = g.Key;
            var macs = g.Select(x => (x.Dev.Mac ?? "").Trim()).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (macs.Length == 0) continue;

            // UI: show that a FW query was requested.
            foreach (var x in g)
            {
                var mac = (x.Dev.Mac ?? "").Trim();
                var cs = GetOrCreateCache(mac);
                cs.CurrentFw = "requested";
                x.Dev.CurrentFw = "requested";
            }

            var topic = BuildCmdTopic(cassia, "get-fw-version");
            var payload = new { sensors = macs, pincode = DefaultPincode };

            try
            {
                await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ConnectionStatus = $"get-fw-version publish failed: {ex.Message}";
            }
        }
    }

    private async Task SendDisconnectAsync(IEnumerable<DiscoveredDevice> devices)
    {
        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return;
        }

        var list = devices?.Where(d => d != null && !string.IsNullOrWhiteSpace(d.Mac)).Distinct().ToList() ?? new();
        if (list.Count == 0) return;

        var groups = list
            .Select(d => new { Dev = d, Cassia = ResolveCassiaForCommand(d) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Cassia))
            .GroupBy(x => x.Cassia, StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            var cassia = g.Key;
            var macs = g.Select(x => (x.Dev.Mac ?? "").Trim()).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (macs.Length == 0) continue;

            foreach (var x in g)
                x.Dev.BleLink = "disconnecting…";

            var topic = BuildCmdTopic("all", "disconnect");
            var payload = new { sensors = macs };

            try
            {
                await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ConnectionStatus = $"disconnect publish failed: {ex.Message}";
            }
        }
    }

    private string ResolveCassiaForCommand(DiscoveredDevice d)
    {
        var cassia = (d.AssignedCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassia))
            cassia = (d.BestCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassia))
            cassia = CassiaGateways.FirstOrDefault(g => string.Equals(g.State, "online", StringComparison.OrdinalIgnoreCase))?.Name
                     ?? CassiaGateways.FirstOrDefault()?.Name
                     ?? "";
        return cassia;
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
    private int GetGatewayLoad(string cassia)
    {
        if (string.IsNullOrWhiteSpace(cassia)) return 0;
        var gw = CassiaGateways.FirstOrDefault(g => g.Name.Equals(cassia, StringComparison.OrdinalIgnoreCase));
        if (gw == null) return 0;
        // Workload must reflect what Cassia reports (queue + programming). Assigned counts are NOT used here.
        return Math.Max(0, gw.Queue) + Math.Max(0, gw.Programming);
    }

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

    if (!TryChooseCassiaForUpdate(d, plannedLoad: null, out var chosen, out _))
        return;

    d.AssignedCassia = chosen;
}


    private (string cassia, string reason) SuggestCassiaForDevice(DiscoveredDevice d)
{
    if (d == null) return ("", "no device");
    if (d.CassiaRssi.Count == 0) return ("", "no RSSI");

    if (!TryChooseCassiaForUpdate(d, plannedLoad: null, out var cassia, out var reason))
        return ("", reason);

    return (cassia, reason);
}

/// <summary>
/// Chooses the best Cassia for updating a device, respecting RSSI threshold and load balancing.
/// Rules:
///  - Only Cassias with RSSI >= RssiAllowBalancingThreshold are eligible (e.g. -65).
///  - Prefer ONLINE gateways when possible.
///  - Primary sort: lowest effective load (assigned detectors + queue + programming + optional planned load).
///  - Tie-break: higher RSSI, then name.
/// Returns false if no eligible Cassia meets the RSSI threshold.
/// </summary>
private bool TryChooseCassiaForUpdate(
    DiscoveredDevice d,
    Dictionary<string, int>? plannedLoad,
    out string cassia,
    out string reason)
{
    cassia = "";
    reason = "";

    if (d == null)
    {
        reason = "no device";
        return false;
    }

    if (d.CassiaRssi.Count == 0)
    {
        reason = "no RSSI";
        return false;
    }

    // Determine the closest Cassia (best RSSI).
    var best = d.CassiaRssi
        .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
        .OrderByDescending(kv => kv.Value)
        .FirstOrDefault();

    var bestCassia = (best.Key ?? "").Trim();
    var bestRssi = best.Value;

    if (string.IsNullOrWhiteSpace(bestCassia))
    {
        reason = "no RSSI";
        return false;
    }

    // Rule: if the strongest RSSI is weaker than the balancing threshold, ALWAYS use the closest Cassia.
    // This guarantees a device is always queueable and avoids "legal but weak" balancing moves.
    if (bestRssi < RssiAllowBalancingThreshold)
    {
        cassia = bestCassia;
        reason = $"rssi {bestRssi} (< {RssiAllowBalancingThreshold}): weak link, chose closest={cassia}";
        return true;
    }

    // For strong links (>= threshold): allow balancing, but still prefer the closest when choices are otherwise equal.
    // Eligible: RSSI >= threshold AND within slack of the closest Cassia (so we don't pick a much worse radio just for load).
    var candidates = d.CassiaRssi
        .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
        .Where(kv => kv.Value >= RssiAllowBalancingThreshold)        .Select(kv => kv.Key.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (candidates.Count == 0)
    {
        // Should not happen because bestRssi >= threshold, but keep it safe.
        cassia = bestCassia;
        reason = $"rssi {bestRssi} (>= {RssiAllowBalancingThreshold}): closest fallback={cassia}";
        return true;
    }

    int EffectiveLoad(string c)
    {
        var baseLoad = GetGatewayLoad(c); // uses latest Cassia-reported queue/programming + assigned group counts
        var extra = (plannedLoad != null && plannedLoad.TryGetValue(c, out var v)) ? v : 0;
        return baseLoad + extra;
    }

    bool IsOnline(string c)
    {
        var gw = CassiaGateways.FirstOrDefault(g => g != null && string.Equals(g.Name, c, StringComparison.OrdinalIgnoreCase));
        return gw != null && string.Equals(gw.State, "online", StringComparison.OrdinalIgnoreCase);
    }

    var anyOnline = candidates.Any(IsOnline);
    var pool = anyOnline ? candidates.Where(IsOnline).ToList() : candidates;

    int GetRssi(string c)
    {
        foreach (var kv in d.CassiaRssi)
        {
            var k = (kv.Key ?? "").Trim();
            if (string.Equals(k, c, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return int.MinValue;
    }


    // Strong-link rule (best RSSI >= threshold):
    //   1) Always prefer the Cassia with the LOWEST *current* workload (queue + programming) (plus planned batch load)
    //   2) If tied, prefer the closest Cassia (highest RSSI)
    // This matches the expected field behavior: if multiple Cassias are "good enough" radio-wise, we spread work first.
    cassia = pool
        .OrderBy(c => EffectiveLoad(c))
        .ThenByDescending(c => GetRssi(c))
        .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault() ?? bestCassia;

    var chosenRssi = GetRssi(cassia);
    var extraTxt = plannedLoad != null && plannedLoad.TryGetValue(cassia, out var extra) ? $"+{extra}" : "";
    reason = $"rssi {chosenRssi} (>= {RssiAllowBalancingThreshold}): balance(load-first), chose={cassia}, load={EffectiveLoad(cassia)} (base={GetGatewayLoad(cassia)}{extraTxt})";
    return true;
}



private sealed record AssignmentPlanItem(string Mac, string Cassia, string Reason);


    /// <summary>
    /// Computes a batch-aware assignment plan for the given devices.
    /// Rules:
    ///  - If best RSSI is >= -65: allow balancing among eligible Cassias (load + rssi tie-break).
    ///  - If best RSSI is weaker than -65: load-balance among eligible Cassias.
    ///  - If best RSSI is <= -75: always pick the closest Cassia.
    /// Eligible = within AssignmentRssiSlack dB of best.
    /// Load = (assigned detectors * AssignedDetectorsWeight) + Cassia status (queue+programming) + already planned assignments in this batch.
    /// </summary>
    /// <summary>
/// Computes a batch-aware assignment plan for the given devices.
/// Rules:
///  - Only Cassias with RSSI >= RssiAllowBalancingThreshold are eligible (e.g. -65).
///  - We load-balance across all eligible Cassias using reported workload:
///      load = (assigned detectors * AssignedDetectorsWeight) + queue + programming + already planned assigns in this batch.
///  - Prefer ONLINE gateways when possible.
///  - If no Cassia meets the RSSI threshold for a device, the plan keeps the current assignment (no suggested change).
/// </summary>
private List<AssignmentPlanItem> ComputeBatchAssignmentPlan(IReadOnlyList<DiscoveredDevice> devices)
{
    var result = new List<AssignmentPlanItem>();
    if (devices == null || devices.Count == 0) return result;

    // Planned incremental load per Cassia for this batch.
    var planned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    void AddPlanned(string cassia)
    {
        cassia = (cassia ?? "").Trim();
        if (cassia.Length == 0) return;
        planned[cassia] = planned.TryGetValue(cassia, out var v) ? v + 1 : 1;
    }

    // Deterministic order: assign strongest devices first (more options) so later items can still balance well.
    foreach (var d in devices
                 .Where(x => x != null)
                 .OrderByDescending(x => x.CassiaRssi.Count == 0 ? int.MinValue : x.CassiaRssi.Max(kv => kv.Value))
                 .ThenBy(x => x.Mac, StringComparer.OrdinalIgnoreCase))
    {
        var mac = (d.Mac ?? "").Trim();
        if (mac.Length == 0) continue;

        // If we have no RSSI, keep current assignment (or best known) as "no suggestion".
        if (d.CassiaRssi.Count == 0)
        {
            var keep = (d.AssignedCassia ?? d.BestCassia ?? "").Trim();
            result.Add(new AssignmentPlanItem(mac, keep, "no RSSI"));
            AddPlanned(keep);
            continue;
        }

        if (TryChooseCassiaForUpdate(d, planned, out var chosen, out var reason))
        {
            result.Add(new AssignmentPlanItem(mac, chosen, reason));
            AddPlanned(chosen);
        }
        else
        {
            // No eligible Cassia (RSSI < threshold). Keep current assignment so we don't propose illegal moves.
            var keep = (d.AssignedCassia ?? "").Trim();
            result.Add(new AssignmentPlanItem(mac, keep, reason));
            AddPlanned(keep);
        }
    }

    return result;
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
        // If some devices are checked, only reassign those. Otherwise reassign all.
        var checkedDevices = _devices.Where(d => d != null && d.IsSelected).ToList();
        var targets = checkedDevices.Count > 0 ? checkedDevices : _devices.ToList();

        // Clear assignment for targets (except devices already queued/programming).
        foreach (var dev in targets)
        {
            if (dev == null) continue;
            if (IsDeviceInWork(dev)) continue;
            dev.AssignedCassia = "";
        }

        foreach (var dev in targets.OrderBy(d => d.SensorModel).ThenBy(d => d.Mac, StringComparer.OrdinalIgnoreCase))
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

        
// Determine Cassia for update:
//  - If strongest RSSI is < RssiAllowBalancingThreshold: ALWAYS use the closest Cassia (highest RSSI).
//  - If strongest RSSI is >= RssiAllowBalancingThreshold: allow load-balancing (queue+programming+assigned), but still prefer the closest on ties.
//  - Device should ALWAYS be queueable. If strongest RSSI is < RssiWarnQueueThreshold: show a warning before queueing.
var cassia = (d.AssignedCassia ?? "").Trim();

string bestCassia = "";
int bestRssi = int.MinValue;

if (d.CassiaRssi.Count > 0)
{
    var best = d.CassiaRssi
        .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
        .OrderByDescending(kv => kv.Value)
        .FirstOrDefault();

    bestCassia = (best.Key ?? "").Trim();
    bestRssi = best.Value;

    if (!_suppressWeakRssiPrompt && bestRssi < RssiWarnQueueThreshold)
    {
        var res = MessageBox.Show(
            $"Warning: Weak RSSI for {d.Mac} (best={bestCassia}:{bestRssi} dBm).\n\n" +
            $"The device is below {RssiWarnQueueThreshold} dBm, which can cause failures.\n\n" +
            "Do you still want to queue it?",
            "Weak RSSI",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (res != MessageBoxResult.Yes)
        {
            ConnectionStatus = $"Not queued: {d.Mac} (weak RSSI)";
            return;
        }
    }

    // Under the balancing threshold -> always closest Cassia.
    if (!string.IsNullOrWhiteSpace(bestCassia) && bestRssi < RssiAllowBalancingThreshold)
    {
        cassia = bestCassia;
    }
}

// If still no cassia chosen, try to keep sticky (only if it has strong RSSI), else balance.
if (string.IsNullOrWhiteSpace(cassia))
{
    // Validate sticky assignment against threshold when we have RSSI readings.
    var sticky = (d.AssignedCassia ?? "").Trim();
    if (!string.IsNullOrWhiteSpace(sticky) && d.CassiaRssi.Count > 0)
    {
        if (d.CassiaRssi.TryGetValue(sticky, out var stickyRssi) && stickyRssi >= RssiAllowBalancingThreshold)
            cassia = sticky;
    }

    if (string.IsNullOrWhiteSpace(cassia))
    {
        if (d.CassiaRssi.Count > 0)
        {
            // Strong RSSI case: balance among eligible Cassias.
            if (!TryChooseCassiaForUpdate(d, plannedLoad: null, out cassia, out _))
            {
                // Fallback: closest Cassia (if any)
                cassia = bestCassia;
            }
        }
        else
        {
            // Fallback: no RSSI at all -> pick first online Cassia to avoid blocking.
            cassia = CassiaGateways.FirstOrDefault(g => string.Equals(g.State, "online", StringComparison.OrdinalIgnoreCase))?.Name
                     ?? CassiaGateways.FirstOrDefault()?.Name
                     ?? "";
        }
    }
}

if (string.IsNullOrWhiteSpace(cassia))
{
    ConnectionStatus = "No Cassia gateway known yet (cannot send start-update)";
    return;
}

// Create/update queue item
        var qi = QueueItems.FirstOrDefault(q => q.Mac.Equals(d.Mac, StringComparison.OrdinalIgnoreCase));
        var wasAlreadyInQueue = (qi != null);
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

        // Before queueing: send disconnect to /all to ensure no gateway is stuck on this device.
        // Only do this if the MAC wasn't already present in our queue list (avoid spamming disconnect).
        if (!wasAlreadyInQueue)
        {
            try
            {
                await _mqtt.PublishJsonAsync(BuildCmdTopic("all", "disconnect"),
                    new { sensors = new[] { d.Mac } },
                    retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            }
            catch { /* best-effort */ }
        }

        AppendQueuedMacToNotes(d.Mac);


        try
        {
            await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token);

            // Keep "Requested update" until we see tele/progress for that MAC.
            qi.LastUpdateUtc = DateTimeOffset.UtcNow;
            MirrorQueueToDevice(qi);
            RequestQueueRefresh();

            // IMPORTANT: ask the Cassia for its queue/programming snapshot shortly after queuing.
            // This is the authoritative "accepted" confirmation (tele/queue-list & tele/programming-list).
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(600, _appCts.Token).ConfigureAwait(false);
                    await RequestQueueListAsync(cassia).ConfigureAwait(false);
                    await RequestProgrammingListAsync(cassia).ConfigureAwait(false);
                }
                catch { }
            });
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


    private void AppendQueuedMacToNotes(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
            return;

        // Always append at the end, on its own line, with timestamp.
        // Keep whatever the user has written above intact.
        var t = NotesText ?? string.Empty;

        if (t.Length > 0 && !t.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            t += Environment.NewLine;

        t += $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} -> {mac.Trim()}{Environment.NewLine}";
        NotesText = t;
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

                    MaybeAutoRequestDeviceListAfterStatus(gw);

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

        if (kind == "tele" && leaf == "device-list")
        {
            HandleDeviceListTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "queue-remove")
        {
            HandleQueueRemoveTele(cassia, payload);
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

        if (kind == "tele" && leaf == "fw-version")
        {
            HandleFwVersionTele(cassia, payload);
            return;
        }

        if (kind == "tele" && leaf == "disconnect")
        {
            HandleDisconnectTele(cassia, payload);
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

                            ApplyDeviceNameWithGuards(existing, dn);
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
                            ApplyLiveProcessStatusFromUpgradeLogLine(cassia, line);


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
        MarkLatestUpgradeLogMapDirty();
        // IMPORTANT: Device "green" (IsUpgradeSuccess) must only be true when the latest
        // logId grouping for a MAC contains a successful "Device Upgrade Completed." entry.
        RefreshUpgradeSuccessFromLatestGroups();
        UpgradeLogGroupsView.Refresh();
    }

    /// <summary>
    /// Recomputes per-MAC upgrade success based on the *latest* UpgradeLogGroup for that MAC.
    /// This prevents an older successful run from keeping the device green when a newer run exists.
    /// </summary>
    private void RefreshUpgradeSuccessFromLatestGroups()
    {
        // Determine latest group per MAC across ALL groups (do not depend on UI filters).
        var latestByMac = new Dictionary<string, UpgradeLogGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in UpgradeLogGroups)
        {
            if (g == null) continue;
            var mac = (g.Mac ?? "").Trim();
            if (string.IsNullOrWhiteSpace(mac)) continue;

            if (!latestByMac.TryGetValue(mac, out var existing) || g.LastTimeLocal > existing.LastTimeLocal)
                latestByMac[mac] = g;
        }

        foreach (var kvp in latestByMac)
        {
            var mac = kvp.Key;
            var g = kvp.Value;
            // IMPORTANT:
            // The per-device result MUST be taken from the "Device Upgrade Completed." line (Warn/Success/Failed).
            // Do NOT rely on the last informational line.
            var completion = g.Entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Stage)
                            && e.Stage.Trim().Equals("Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.TimeLocal)
                .FirstOrDefault();

            var completionStatus = (completion?.Status ?? "").Trim();
            var isSuccess = completionStatus.Equals("Success", StringComparison.OrdinalIgnoreCase);
            var isWarn = completionStatus.Equals("Warn", StringComparison.OrdinalIgnoreCase);
            var isFailed = completionStatus.Equals("Failed", StringComparison.OrdinalIgnoreCase)
                           || completionStatus.StartsWith("Fail", StringComparison.OrdinalIgnoreCase);

            var cs = GetOrCreateCache(mac);
            cs.IsUpgradeSuccess = isSuccess;
            cs.IsUpgradeWarn = isWarn;
            cs.IsUpgradeFailed = isFailed;
            // Use the group's completion timestamp if present.
            if (isSuccess)
            {
                var t = g.Entries
                    .Where(e =>
                        !string.IsNullOrWhiteSpace(e.Stage)
                        && e.Stage.Trim().Equals("Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(e.Status)
                        && e.Status.Trim().Equals("Success", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(e => e.TimeLocal)
                    .FirstOrDefault()?.TimeLocal ?? DateTimeOffset.MinValue;

                if (t != DateTimeOffset.MinValue)
                    cs.LastUpgradeSuccessUtc = t.ToUniversalTime();

                if (!string.IsNullOrWhiteSpace(g.LatestFirmware))
                    cs.LastTargetFw = g.LatestFirmware;
            }

            var dev = FindDiscoveredDevice(mac);
            if (dev != null)
            {
                dev.IsUpgradeSuccess = isSuccess;
                dev.IsUpgradeWarn = isWarn;
                dev.IsUpgradeFailed = isFailed;
                dev.LastUpgradeSuccessUtc = cs.LastUpgradeSuccessUtc;
                dev.LastTargetFw = cs.LastTargetFw;
            }
        }

        // For MACs that are present in cache/devices but have no groups at all, ensure we don't
        // leave a stale green state.
        foreach (var dev in _devices)
        {
            if (dev == null || string.IsNullOrWhiteSpace(dev.Mac)) continue;
            if (latestByMac.ContainsKey(dev.Mac)) continue;
            dev.IsUpgradeSuccess = false;
            dev.IsUpgradeWarn = false;
            dev.IsUpgradeFailed = false;
        }
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

    private static bool LooksLikeNewRunStage(string? stage, int progressPercent)
    {
        // Heuristic: stages that indicate a fresh run starting (even if progress is still low).
        if (string.IsNullOrWhiteSpace(stage))
            return progressPercent <= 5;

        var s = stage.Trim();
        if (progressPercent <= 5)
        {
            if (s.Contains("Process Start", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Contains("Connect+Login", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Contains("Current FW Version", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Contains("Requested update", StringComparison.OrdinalIgnoreCase)) return true;
        }

        // Some runs can jump directly to a start stage.
        if (s.Contains("Process Start", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsNonTerminalStage(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
            return true;

        var s = stage.Trim();
        if (s.Equals("Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase))
            return false;

        // Treat common terminal words as terminal.
        if (s.Contains("completed", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Contains("success", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Contains("failed", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Contains("error", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Contains("aborted", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
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
                // Do NOT mark device as success here.
                // Success/green is computed from the latest UpgradeLogGroup per MAC.
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

            var isCompletedSuccess = stage.Equals("Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase)
                && status.Equals("Success", StringComparison.OrdinalIgnoreCase);

            var queueText = isCompletedSuccess ? "Done" : text;

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
                    if (qi.LastUpdateUtc != default && tsUtc < qi.LastUpdateUtc)
                        return;

                    qi.Cassia = cassia;
                    qi.Status = queueText.Trim();
                    if (isCompletedSuccess)
                        qi.Progress = 100;
                    if (LooksLikeFirmwareVersion(fw))
                        qi.FirmwareVersion = fw;
                    qi.LastUpdateUtc = tsUtc;

                    RequestQueueRefresh();
                }

                // Cache + device list mirror (without creating devices from logs)
                var cs = GetOrCreateCache(mac);
                if (cs.LastUpdateUtc != default && tsUtc < cs.LastUpdateUtc)
                    return;

                cs.ProcessCassia = cassia;
                cs.ProcessStatus = text.Trim();
                if (isCompletedSuccess)
                    cs.ProcessProgress = 100;

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

            try
            {
                // Refresh the collection view (do NOT call RequestQueueRefresh recursively)
                QueueView?.Refresh();
            }
            catch { }

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



    private void MaybeAutoRequestDeviceListAfterStatus(CassiaGateway gw)
    {
        if (!IsConnected) return;
        if (!string.Equals(gw.StateLower, "online", StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(gw.Name)) return;

        // Only request once per connection.
        if (_deviceListRequestedAfterConnect) return;

        // Wait until we have at least one status after connect.
        if (_connectedAtUtc != DateTimeOffset.MinValue && (DateTimeOffset.UtcNow - _connectedAtUtc) > TimeSpan.FromMinutes(10))
            return;

        _deviceListRequestedAfterConnect = true;
        _ = RequestDeviceListAsync("all");
    }

    private Task RequestDeviceListAsync(string target)
        => _mqtt.PublishJsonAsync(BuildCmdTopic(target, "get-device-list"), new { requestId = Guid.NewGuid().ToString("N") }, retain: false, qos: 1, ct: _appCts.Token);

    public Task RemoveFromQueueAsync(string target, IEnumerable<string> macAddresses)
    {
        if (string.IsNullOrWhiteSpace(target)) target = "all";
        var macs = (macAddresses ?? Array.Empty<string>()).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (macs.Length == 0) return Task.CompletedTask;

        // Backend accepts many payload shapes; we always use the object form.
        object payload = macs.Length == 1
            ? new { macAddress = macs[0] }
            : new { macAddresses = macs };

        return _mqtt.PublishJsonAsync(BuildCmdTopic(target, "remove-from-queue"), payload, retain: false, qos: 1, ct: _appCts.Token);
    }


    public async Task MoveQueueItemToCassiaAsync(QueueItem qi, string newCassia)
    {
        if (qi == null) return;
        var mac = (qi.Mac ?? "").Trim();
        if (string.IsNullOrWhiteSpace(mac)) return;
        newCassia = (newCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(newCassia)) return;

        // Step 1: remove from pending queue on the current Cassia (best effort)
        var fromCassia = string.IsNullOrWhiteSpace(qi.Cassia) ? "all" : qi.Cassia.Trim();
        await RemoveFromQueueAsync(fromCassia, new[] { mac }).ConfigureAwait(false);

        // Step 2: queue on the new Cassia
        var model = (qi.DetectorType ?? "").Trim();
        var fw = (qi.FirmwareVersion ?? "").Trim();

        Application.Current.Dispatcher.Invoke(() =>
        {
            qi.Cassia = newCassia;
            qi.Status = "Requested update";
            qi.Progress = 0;
            qi.Notes = "";
            qi.LastUpdateUtc = DateTimeOffset.UtcNow;
            MirrorQueueToDevice(qi);
            RequestQueueRefresh();
        });

        // Before queueing: send disconnect to /all to ensure no gateway is stuck on this device.
        try
        {
            await _mqtt.PublishJsonAsync(BuildCmdTopic("all", "disconnect"), new { sensors = new[] { mac } }, retain: false, qos: 1, ct: _appCts.Token)
                      .ConfigureAwait(false);
        }
        catch { /* best-effort */ }

        await PublishStartUpdateAsync(newCassia, mac, model, fw).ConfigureAwait(false);
    }

    private Task PublishStartUpdateAsync(string cassia, string mac, string model, string fw)
    {
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
                MacAddress = mac,
                Pincode = ""
            }
        };

        return _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token);
    }

    public void AssignDeviceToCassia(DiscoveredDevice device, string cassia)
    {
        if (device == null) return;
        if (string.IsNullOrWhiteSpace(cassia)) return;
        device.AssignedCassia = cassia.Trim();
        RecalculateAssignmentCounts();
        RequestDevicesRefresh();
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
    private async Task ClearDeviceSettingsBackupsForCassia(string cassiaName)
    {
        if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
        cassiaName = (cassiaName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassiaName)) return;

        try
        {
            // cmd -> clear-device-settings-backups payload {}
            await _mqtt.PublishJsonAsync(BuildCmdTopic(cassiaName, "clear-device-settings-backups"), new { }, retain: false, qos: 1, ct: _appCts.Token)
                      .ConfigureAwait(false);
            ConnectionStatus = $"Sent clear-device-settings-backups to {cassiaName}";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Clear backups failed ({cassiaName}): {ex.Message}";
        }
    }

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


    private void HandleDeviceListTele(string cassia, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (!root.TryGetProperty("deviceList", out var listEl) || listEl.ValueKind != JsonValueKind.Array)
                return;

            var now = DateTimeOffset.UtcNow;

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var devEl in listEl.EnumerateArray())
                {
                    if (devEl.ValueKind != JsonValueKind.Object) continue;

                    var mac = devEl.TryGetProperty("macAddress", out var macEl) ? (macEl.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(mac))
                        mac = devEl.TryGetProperty("mac", out var macEl2) ? (macEl2.GetString() ?? "") : "";

                    if (string.IsNullOrWhiteSpace(mac)) continue;
                    mac = mac.Trim();

                    int rssi = int.MinValue;
                    if (devEl.TryGetProperty("rssi", out var rssiEl))
                    {
                        if (rssiEl.ValueKind == JsonValueKind.Number) rssi = rssiEl.GetInt32();
                        else if (rssiEl.ValueKind == JsonValueKind.String && int.TryParse(rssiEl.GetString(), out var rv)) rssi = rv;
                    }

                    var detectorType = devEl.TryGetProperty("detectorType", out var dtEl) ? (dtEl.GetString() ?? "") : "";
                    var detectorFamily = devEl.TryGetProperty("detectorFamily", out var dfEl) ? (dfEl.GetString() ?? "") : "";
                    var productNumber = devEl.TryGetProperty("productNumber", out var pnEl) ? (pnEl.GetString() ?? "") : "";
                    var name = devEl.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "";
                    var lastSeenUtc = now;
                    if (devEl.TryGetProperty("lastSeenUtc", out var lsEl) && lsEl.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(lsEl.GetString(), out var dto))
                        lastSeenUtc = dto;

                    if (!_deviceByMac.TryGetValue(mac, out var d))
                    {
                        d = new DiscoveredDevice { Mac = mac };
                        WireDeviceAssignmentHooks(d);
                        _deviceByMac[mac] = d;
                        _devices.Add(d);
                    }

                    ApplyDeviceNameWithGuards(d, name);
                    d.ProductNumber = string.IsNullOrWhiteSpace(productNumber) ? d.ProductNumber : productNumber;
                    d.DetectorFamily = string.IsNullOrWhiteSpace(detectorFamily) ? d.DetectorFamily : detectorFamily;
                    d.DetectorType = string.IsNullOrWhiteSpace(detectorType) ? d.DetectorType : detectorType;

                    // SensorModel: prefer detectorType if it looks like Pxx
                    if (!string.IsNullOrWhiteSpace(detectorType) && detectorType.Trim().StartsWith("P", StringComparison.OrdinalIgnoreCase))
                        d.SensorModel = detectorType.Trim().ToUpperInvariant();
                    else if (!string.IsNullOrWhiteSpace(d.ProductNumber) && _productToModel.TryGetValue(d.ProductNumber, out var m))
                        d.SensorModel = m;

                    if (rssi != int.MinValue)
                        d.UpdateFromCassia(cassia, rssi, lastSeenUtc);
                    else
                        d.LastSeenUtc = lastSeenUtc;

                    ApplyCachedStatusToDevice(d);
                    EnsureStickyAssignment(d);
                }

                RecalculateAssignmentCounts();
                RequestDevicesRefresh();
            });
        }
        catch { }
    }

    private void HandleQueueRemoveTele(string cassia, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            var success = root.TryGetProperty("success", out var sEl) && sEl.ValueKind == JsonValueKind.True;
            if (!success) return;

            var requested = new List<string>();
            if (root.TryGetProperty("requested", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in reqEl.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String)
                        requested.Add(x.GetString() ?? "");
            }

            if (requested.Count == 0) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var macRaw in requested)
                {
                    var mac = (macRaw ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(mac)) continue;

                    var qi = QueueItems.FirstOrDefault(q => q != null && mac.Equals((q.Mac ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                    if (qi != null)
                        QueueItems.Remove(qi);

                    if (_deviceByMac.TryGetValue(mac, out var dev))
                    {
                        dev.IsInQueue = false;
                        if (dev.ProcessProgress == 0)
                            dev.ProcessStatus = "";
                    }

                    var cs = GetOrCreateCache(mac);
                    cs.IsInQueue = false;
                }

                RequestQueueRefresh();
                RequestDevicesRefresh();
            });
        }
        catch { }
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

                RequestQueueRefresh();
                RequestDevicesRefresh();
            });

        }
        catch { }
    }

    [RelayCommand]
    private async Task ResyncAsync()
    {
        if (!IsConnected)
        {
            // Still clear the UI so user starts from a clean slate.
            ClearAllUiAndState();
            ConnectionStatus = "Not connected";
            return;
        }

        await ResyncCoreAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Clears all UI collections and internal caches.
    /// </summary>
    private void ClearAllUiAndState()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            // Devices
            _devices.Clear();
            _deviceByMac.Clear();
            _cachedStatusByMac.Clear();

            // Queue / programming
            QueueItems.Clear();

            // Gateways + dropdowns
            CassiaGateways.Clear();
            CassiaNameOptions.Clear();

            // Upgrade log views
            UpgradeLogLines.Clear();
            UpgradeLogGroups.Clear();
            UpgradeLogText = "";
            _upgradeLogSb.Clear();
            UpgradeLogReceivedLines = 0;
            UpgradeLogTotalLines = 0;
            UpgradeLogStatus = "";

            // Filters/selections that commonly keep stale selection pointers
            SelectedDevice = null;
            SelectedQueueItem = null;
            // (no SelectedCassia property in this project; gateway selections are re-initialized as data arrives)
            SelectedLogGateway = null;
            SelectedSpeedGateway = null;
        });

        // Internal trackers
        _latestUpgradeLogIdByMac.Clear();
        _progressByMac.Clear();
        _gwSeenMacs.Clear();
        _deviceAssignmentWired.Clear();
        _requestedUpgradeLogCassias.Clear();

        _fwManifestRequestedForGw.Clear();
        _runtimeStateRequestedForGw.Clear();
        _deviceListRequestedForGw.Clear();
        _deviceListRequestedAfterConnect = false;
        _connectedAtUtc = DateTimeOffset.UtcNow;

        _lastFwManifestMissingHash = "";
        _fwManifestTimeoutArmed = false;
    }

    /// <summary>
    /// Clears UI/state and requests fresh snapshots the same way as on a new connect.
    /// </summary>
    private async Task ResyncCoreAsync()
    {
        ClearAllUiAndState();

        // Ensure subscriptions exist for the current NetworkId.
        try
        {
            var net = (NetworkId ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(net) && !string.Equals(_lastSubscribedNetworkId, net, StringComparison.OrdinalIgnoreCase))
            {
                await _mqtt.SubscribeAsync($"accessapp/{net}/tele/#").ConfigureAwait(false);
                await _mqtt.SubscribeAsync($"accessapp/{net}/cmd/#").ConfigureAwait(false);
                _lastSubscribedNetworkId = net;
            }
        }
        catch
        {
            // best effort
        }

        // Kick off a full device-list request immediately (backend supports target="all").
        try { _ = RequestDeviceListAsync("all"); } catch { }

        // The rest of the snapshots are auto-requested when we receive each gateway's status:
        // - FW manifest
        // - queue/programming/parallel programmers
        // - upgrade log
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

    private void HandleFwVersionTele(string cassia, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
                return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var r in resultsEl.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    var mac = r.TryGetProperty("mac", out var m) ? (m.GetString() ?? "") : "";
                    var ver = r.TryGetProperty("version", out var v) ? (v.GetString() ?? "") : "";
                    mac = (mac ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(mac)) continue;

                    // Extract the Sensor App version when the backend returns a full combined string.
                    var app = "";
                    var mm = SensorAppFromStatusRx.Match(ver ?? "");
                    if (mm.Success) app = mm.Groups["app"].Value;
                    if (string.IsNullOrWhiteSpace(app))
                        app = (ver ?? "").Trim();

                    var cs = GetOrCreateCache(mac);
                    cs.CurrentFw = app;

                    var dev = FindDiscoveredDevice(mac);
                    if (dev != null)
                        dev.CurrentFw = app;
                }
            });
        }
        catch { }
    }

    private void HandleDisconnectTele(string cassia, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
                return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var r in resultsEl.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    var mac = r.TryGetProperty("mac", out var m) ? (m.GetString() ?? "") : "";
                    mac = (mac ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(mac)) continue;

                    var ok = true;
                    if (r.TryGetProperty("success", out var s))
                    {
                        if (s.ValueKind == JsonValueKind.False) ok = false;
                        else if (s.ValueKind == JsonValueKind.True) ok = true;
                    }

                    var dev = FindDiscoveredDevice(mac);
                    if (dev != null)
                        dev.BleLink = ok ? "disconnected" : "disconnect failed";
                }
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

        var anyQueueChanged = false;

        foreach (var p in batch)
        {
            var pctRounded = (int)Math.Round(p.ProgressPercent, 0);

            // Protect terminal completion state from being overwritten by late/duplicate progress=100 "Programming" updates.
            var cs = GetOrCreateCache(p.Mac);

            if (cs.IsUpgradeSuccess && cs.LastUpgradeSuccessUtc.HasValue)
            {
                // Older than completion -> ignore
                if (p.TimeUtc <= cs.LastUpgradeSuccessUtc.Value)
                    continue;

                // New run starts -> clear completion
                if (LooksLikeNewRunStage(p.Stage, pctRounded))
                {
                    cs.IsUpgradeSuccess = false;
                    cs.LastUpgradeSuccessUtc = null;
                    cs.LastTargetFw = "";
                }
                else if (pctRounded >= 100 && IsNonTerminalStage(p.Stage)
                         && string.Equals(cs.ProcessStatus?.Trim(), "Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase))
                {
                    // Late progress=100 "Programming"/etc after completion -> ignore
                    continue;
                }
            }

            // Keep FW field as target firmware (not model)
            if (LooksLikeFirmwareVersion(p.FirmwareTarget))
                cs.ProcessFirmware = p.FirmwareTarget;

            if (!string.IsNullOrWhiteSpace(p.Cassia))
                cs.ProcessCassia = p.Cassia;

            if (!string.IsNullOrWhiteSpace(p.Stage))
                cs.ProcessStatus = p.Stage;

            cs.ProcessProgress = pctRounded;
            cs.LastUpdateUtc = p.TimeUtc;

            // Update discovered device if present (apply cached so timestamp rules are respected)
            if (_deviceByMac.TryGetValue(p.Mac, out var dev))
                ApplyCachedStatusToDevice(dev);

            // Update queue item (keyed by mac)
            var qi = QueueItems.FirstOrDefault(x => x.Mac.Equals(p.Mac, StringComparison.OrdinalIgnoreCase));
            if (qi == null)
            {
                qi = new QueueItem { Mac = p.Mac };
                QueueItems.Add(qi);
                anyQueueChanged = true;
            }

            // Only apply if newer than the current queue row
            if (qi.LastUpdateUtc != default && p.TimeUtc < qi.LastUpdateUtc)
                continue;

            qi.Cassia = cs.ProcessCassia ?? "";
            qi.FirmwareVersion = LooksLikeFirmwareVersion(cs.ProcessFirmware) ? cs.ProcessFirmware : qi.FirmwareVersion;

            // If we already know the device completed successfully, keep the queue row "Done".
            if (cs.IsUpgradeSuccess && string.Equals(cs.ProcessStatus?.Trim(), "Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase))
            {
                qi.Status = "Done";
                qi.Progress = 100;
            }
            else
            {
                qi.Status = cs.ProcessStatus ?? "";
                qi.Progress = pctRounded;
            }

            qi.LastUpdateUtc = p.TimeUtc;
        }

        if (anyQueueChanged)
            RequestQueueRefresh();
        else
            RequestQueueRefresh();
    }




    [RelayCommand]
    private void RefreshUiNow()
    {
        try
        {
            // Re-apply cached status to all known devices (cheap)
            foreach (var d in _devices)
                ApplyCachedStatusToDevice(d);

            FilteredDevices.Refresh();
            QueueView.Refresh();
            MarkLatestUpgradeLogMapDirty();
            UpgradeLogGroupsView.Refresh();
        }
        catch { }
    }

    [RelayCommand]
    private void RemoveRssiMinus127Devices()
    {
        try
        {
            var toRemove = _devices.Where(d => d.BestRssi <= -127).ToList();
            foreach (var d in toRemove)
            {
                _devices.Remove(d);
                _deviceByMac.Remove(d.Mac);
            }
            FilteredDevices.Refresh();
        }
        catch { }
    }

    [RelayCommand]
    private void RemoveCompletedFromQueue()
    {
        try
        {
            var done = QueueItems.Where(q =>
                    q.IsDone
                    || string.Equals(q.Status?.Trim(), "Device Upgrade Completed.", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(q.Status?.Trim(), "Success", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var q in done)
                QueueItems.Remove(q);

            RequestQueueRefresh();
        }
        catch { }
    }

    [RelayCommand]
    private void ExportUpgradeLogToExcel()
    {
        try
        {
            // Export from CURRENT VIEW (what the operator sees)
            var groups = UpgradeLogGroupsView.Cast<object>()
                .OfType<UpgradeLogGroup>()
                .OrderByDescending(g => g.LastTimeLocal)
                .ToList();

            if (groups.Count == 0)
            {
                MessageBox.Show("No upgrade log entries to export (current view is empty).", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = $"upgrade-log_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
            };

            if (dlg.ShowDialog() != true)
                return;

            using var wb = new ClosedXML.Excel.XLWorkbook();

            // ---------------- Summary ----------------
            var ws1 = wb.Worksheets.Add("Summary");
            ws1.Cell(1, 1).Value = "Cassia";
            ws1.Cell(1, 2).Value = "MAC";
            ws1.Cell(1, 3).Value = "LogId";
            ws1.Cell(1, 4).Value = "Started time";
            ws1.Cell(1, 5).Value = "Last time";
            ws1.Cell(1, 6).Value = "Old FW";
            ws1.Cell(1, 7).Value = "FW (target)";
            ws1.Cell(1, 8).Value = "Latest stage";
            ws1.Cell(1, 9).Value = "Latest status";
            ws1.Cell(1, 10).Value = "Has newer entry";
            ws1.Cell(1, 11).Value = "Summary";

            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                var r = i + 2;
                ws1.Cell(r, 1).Value = g.Cassia;
                ws1.Cell(r, 2).Value = g.Mac;
                ws1.Cell(r, 3).Value = g.LogId;
                ws1.Cell(r, 4).Value = g.StartedAtLocalText;
                ws1.Cell(r, 5).Value = g.LastTimeLocalText;
                ws1.Cell(r, 6).Value = g.OldFirmwareText;
                ws1.Cell(r, 7).Value = g.TargetFirmware;
                ws1.Cell(r, 8).Value = g.LatestStage;
                ws1.Cell(r, 9).Value = g.LatestStatus;
                ws1.Cell(r, 10).Value = g.HasNewerForMac ? "Yes" : "No";
                ws1.Cell(r, 11).Value = g.LatestSummary;
            }

            ws1.Columns().AdjustToContents();
            ws1.Column(11).Width = 80;

            // ---------------- Details ----------------
            var ws2 = wb.Worksheets.Add("Details");
            ws2.Cell(1, 1).Value = "Cassia";
            ws2.Cell(1, 2).Value = "MAC";
            ws2.Cell(1, 3).Value = "LogId";
            ws2.Cell(1, 4).Value = "Time";
            ws2.Cell(1, 5).Value = "Stage";
            ws2.Cell(1, 6).Value = "Status";
            ws2.Cell(1, 7).Value = "Display status";
            ws2.Cell(1, 8).Value = "Firmware";
            ws2.Cell(1, 9).Value = "Line";

            int row = 2;
            foreach (var g in groups)
            {
                foreach (var e in g.Entries.OrderBy(x => x.TimeLocal))
                {
                    ws2.Cell(row, 1).Value = e.Cassia;
                    ws2.Cell(row, 2).Value = e.Mac;
                    ws2.Cell(row, 3).Value = e.LogId;
                    ws2.Cell(row, 4).Value = e.TimeLocalText;
                    ws2.Cell(row, 5).Value = e.Stage;
                    ws2.Cell(row, 6).Value = e.Status;
                    ws2.Cell(row, 7).Value = e.DisplayStatus;
                    ws2.Cell(row, 8).Value = e.Firmware;
                    ws2.Cell(row, 9).Value = e.Line;
                    row++;
                }
            }

            ws2.Columns(1, 8).AdjustToContents();
            ws2.Column(9).Width = 120;

            wb.SaveAs(dlg.FileName);

            MessageBox.Show("Export completed.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Export failed: " + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

}