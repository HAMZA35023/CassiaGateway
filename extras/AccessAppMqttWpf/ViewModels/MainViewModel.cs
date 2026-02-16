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

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly MqttClientService _mqtt = new();
    private readonly SettingsStore _store = new();

    private CancellationTokenSource? _autoReconnectCts;
    private Task? _autoReconnectTask;
    private bool _manualDisconnectRequested;
    private bool _autoReconnectEnabled;
    private bool _hasEverConnected;
    private bool _isConnecting;
    private readonly TimeSpan[] _autoReconnectBackoff = new[]
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30)
    };

    public ObservableCollection<CassiaGateway> CassiaGateways { get; } = new();

    // Speed graph: include virtual options without polluting the main Cassia list in the UI.
    private readonly CassiaGateway _speedAllGateways = new() { Name = "(All gateways)" };
    private readonly CassiaGateway _speedTotalGateways = new() { Name = "(Total)" };
    public ObservableCollection<CassiaGateway> SpeedGraphGateways { get; } = new();
    private readonly Dictionary<string, List<SpeedSample>> _speedHistoryByGateway = new(StringComparer.OrdinalIgnoreCase);
    private string _speedHistoryScopeKey = "";

    // Names for dropdowns (assignment, commands, etc.)
    public ObservableCollection<string> CassiaNameOptions { get; } = new();



    private readonly System.Collections.Generic.Dictionary<string, string> _productToModel =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _appCts = new();

    private readonly System.Windows.Threading.DispatcherTimer _gatewayStaleTimer;
    private static readonly TimeSpan GatewayOfflineAfter = TimeSpan.FromMinutes(1);

    public string ConnectButtonText => IsConnected ? "Disconnect" : "Connect";
    public string DevicesSubtitle =>
        $"{FilteredDevices.Cast<object>().Count()} device(s) - models: {SelectedModelFilterSummary} - product: {ProductFilter} - filter: {DeviceFilter}";

    private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> _gwSeenMacs
    = new(StringComparer.OrdinalIgnoreCase);


    // ---- Cached status from upgrade-log / progress (do NOT create "discovered devices" from logs) ----
    private sealed class CachedDeviceStatus
    {
        public string ProcessStatus = "";
        public int ProcessProgress = 0;
        public string ProcessCassia = "";
        public string ProcessFirmware = "";
        public string ChipUsed = "";
        public DateTimeOffset LastUpdateUtc = DateTimeOffset.MinValue;

        public string CurrentFw = "";
        public bool CurrentFwFromGetFw = false;
        public bool IsUpgradeSuccess = false;
        public bool IsUpgradeWarn = false;
        public bool IsUpgradeFailed = false;
        public bool IsUpgradeNoFwRead = false;
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

        // Upgrade result flags come from upgrade-log completion stage.
        // These are independent from queue/progress UI and are overridden visually by IsInQueue.
        dev.IsUpgradeWarn = cs.IsUpgradeWarn;
        dev.IsUpgradeNoFwRead = cs.IsUpgradeNoFwRead;
        dev.IsUpgradeFailed = cs.IsUpgradeFailed && !cs.IsUpgradeNoFwRead;

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
    public event Action<string, IReadOnlyDictionary<string, RuntimeVariableValue>>? RuntimeVariablesReceived;

    [ObservableProperty] private DiscoveredDevice? selectedDevice;
    [ObservableProperty] private HostBleScanItem? selectedHostBleDevice;


    // Devices list options




    [ObservableProperty] private string mqttHost = "acd270e774e848e8a55de829dc58bc6c.s1.eu.hivemq.cloud";
    [ObservableProperty] private int mqttPort = 8883;
    [ObservableProperty] private string mqttTopic = "accessapp/#";
    [ObservableProperty] private string mqttUser = "accessapp";
    [ObservableProperty] private string? mqttPassword = "Niko1234!";
    [ObservableProperty] private bool useTls = true;
    [ObservableProperty] private bool ignoreTlsErrors = false;

    [ObservableProperty] private string notesText = "";

    // Runtime-only: set/get number of parallel programmers.
    // "All" value is used when pressing Set all / Get all.
    [ObservableProperty] private int parallelProgrammersAllDesired = 3;


    [ObservableProperty] private string networkId = "dk-lab";
    public ObservableCollection<string> AvailableScopes { get; } = new();
    [ObservableProperty] private string selectedScope = "";
    [ObservableProperty] private string commandTopicTemplate = "accessapp/{networkId}/cmd/{cassia}/{command}";
    [ObservableProperty] private string defaultCommand = "start-update";

    // If true, we include forceUpdate=true in start-update payloads.
    // Default is false on startup.
    [ObservableProperty] private bool forceUpdateEnabled = false;

    // If true, auto-adjust workers from queued model mix:
    // DALI master only (P47/P48) => 4, otherwise => 2.
    [ObservableProperty] private bool autoSetWorkersByModelEnabled = false;
    // If true, apply "Production-Update" runtime overrides before start-update.
    [ObservableProperty] private bool productionUpdateEnabled = false;

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

    partial void OnSelectedFirmwareP41Changed(string value) => PersistSelectedFirmware("P41", value);
    partial void OnSelectedFirmwareP42Changed(string value) => PersistSelectedFirmware("P42", value);
    partial void OnSelectedFirmwareP46Changed(string value) => PersistSelectedFirmware("P46", value);
    partial void OnSelectedFirmwareP47Changed(string value) => PersistSelectedFirmware("P47", value);
    partial void OnSelectedFirmwareP48Changed(string value) => PersistSelectedFirmware("P48", value);

    [ObservableProperty] private string connectionStatus = "Disconnected";
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private string statusBarText = "Ready";
    [ObservableProperty] private string totalSpeedStatusText = "Speed: -- %/min";
    [ObservableProperty] private bool developerModeUnlocked = false;

    // ---- LED range visualization (connect/login/LED by RSSI) ----
    public ObservableCollection<LedRangeDeviceRow> LedRangeConnectedDevices { get; } = new();
    public ObservableCollection<LedRangeDeviceRow> LedRangeFailedDevices { get; } = new();
    [ObservableProperty] private string ledRangeCassia = "all";
    [ObservableProperty] private int ledRangeMinRssi = -75;
    public ObservableCollection<string> LedRangeModelOptions { get; } = new(new[] { "All", "MASTER", "SECONDARY", "BMS" });
    [ObservableProperty] private string selectedLedRangeModel = "All";
    [ObservableProperty] private int ledRangeMaxConnectAttempts = 3;
    [ObservableProperty] private string ledRangePincode = "1234";
    [ObservableProperty] private string ledRangeStatusText = "Idle";
    [ObservableProperty] private int ledRangeRequestedTotal;
    [ObservableProperty] private int ledRangeTriedCount;
    [ObservableProperty] private int ledRangeConnectedCount;
    [ObservableProperty] private int ledRangeFailedCount;
    [ObservableProperty] private double ledRangeProgressPercent;
    [ObservableProperty] private string ledRangeProgressText = "0 / 0 tried";

    [ObservableProperty] private CassiaGateway? selectedSpeedGateway;

    private bool _isInitializing = true;
    private bool _syncingScopeSelection;
    private readonly SemaphoreSlim _scopeResyncLock = new(1, 1);
    private string _lastScopeResyncNetworkId = "";

    

 

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
        RegisterObservedScope(NetworkId);
        SelectedScope = NetworkId;
        CommandTopicTemplate = s.accessapp.commandTopicTemplate;
        DefaultCommand = s.accessapp.defaultCommand;
        ForceUpdateEnabled = s.accessapp.forceUpdate;
        AutoSetWorkersByModelEnabled = s.accessapp.autoSetWorkersByModel;
        ProductionUpdateEnabled = s.accessapp.productionUpdate;

        // Firmware selections: remember across restarts/resync.
        try
        {
            var fwMap = s.accessapp.selectedFirmwareByModel ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (fwMap.TryGetValue("P41", out var fw41)) SelectedFirmwareP41 = fw41 ?? "";
            if (fwMap.TryGetValue("P42", out var fw42)) SelectedFirmwareP42 = fw42 ?? "";
            if (fwMap.TryGetValue("P46", out var fw46)) SelectedFirmwareP46 = fw46 ?? "";
            if (fwMap.TryGetValue("P47", out var fw47)) SelectedFirmwareP47 = fw47 ?? "";
            if (fwMap.TryGetValue("P48", out var fw48)) SelectedFirmwareP48 = fw48 ?? "";
        }
        catch { /* best-effort */ }

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
                _manualDisconnectRequested = false;
                _autoReconnectEnabled = true;
                _hasEverConnected = true;
                _autoReconnectCts?.Cancel();

                _connectedAtUtc = DateTimeOffset.UtcNow;
                _fwManifestRequestedForGw.Clear();
                _runtimeStateRequestedForGw.Clear();
                _runtimeVarsByGw.Clear();
                _runtimeVarsPending.Clear();
                _deviceListRequestedForGw.Clear();
                _deviceListRequestedAfterConnect = false;
            }
            else
            {
                // Keep the last subscriptions remembered; next connect/resync will subscribe again.
                // (We don't force-clear UI on disconnect; user asked for re-sync on reconnect.)
                StartAutoReconnect();
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
        CassiaGateways.CollectionChanged += (s, e) =>
        {
            RebuildSpeedGraphGateways();

            if (e.NewItems != null)
                foreach (CassiaGateway gw in e.NewItems)
                    gw.PropertyChanged += OnGatewayPropertyChanged;

            if (e.OldItems != null)
                foreach (CassiaGateway gw in e.OldItems)
                    gw.PropertyChanged -= OnGatewayPropertyChanged;

            UpdateTotalSpeedStatus();
        };
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


        InitIgnoredDevices();
        InitDeviceFiltering();

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


        InitQueueView();
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
                gw.UpdateDerivedTimes(nowUtc);
            }
        };


        InitProgressBuffering();
        _gatewayStaleTimer.Start();

        InitUpgradeLog();
        CassiaNameOptions.Clear();
        CassiaNameOptions.Add("(auto)");
    

        InitHostBle();

        _isInitializing = false;
}

    partial void OnSelectedScopeChanged(string value)
    {
        if (_syncingScopeSelection || _isInitializing) return;

        var scope = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(scope)) return;
        if (string.Equals(NetworkId, scope, StringComparison.OrdinalIgnoreCase)) return;

        _syncingScopeSelection = true;
        try
        {
            NetworkId = scope;
        }
        finally
        {
            _syncingScopeSelection = false;
        }
    }

    partial void OnNetworkIdChanged(string value)
    {
        var scope = (value ?? "").Trim();
        if (!string.Equals(value, scope, StringComparison.Ordinal))
        {
            NetworkId = scope;
            return;
        }

        RegisterObservedScope(scope);

        if (!_syncingScopeSelection)
        {
            var selected = AvailableScopes.FirstOrDefault(s => s.Equals(scope, StringComparison.OrdinalIgnoreCase)) ?? "";
            _syncingScopeSelection = true;
            try
            {
                SelectedScope = selected;
            }
            finally
            {
                _syncingScopeSelection = false;
            }
        }

        if (_isInitializing || !IsConnected) return;
        _ = ResyncAfterScopeChangeAsync(scope);
    }

    private void RegisterObservedScopeFromTopic(string topic)
    {
        var scope = ExtractScopeFromTopic(topic);
        RegisterObservedScope(scope);
    }

    private static string ExtractScopeFromTopic(string? topic)
    {
        var parts = (topic ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "";
        if (!parts[0].Equals("accessapp", StringComparison.OrdinalIgnoreCase)) return "";
        return (parts[1] ?? "").Trim();
    }

    private void RegisterObservedScope(string? scope)
    {
        var value = (scope ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value)) return;

        void AddScope()
        {
            if (AvailableScopes.Any(s => s.Equals(value, StringComparison.OrdinalIgnoreCase))) return;

            var sorted = AvailableScopes
                .Append(value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            AvailableScopes.Clear();
            foreach (var item in sorted)
                AvailableScopes.Add(item);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            AddScope();
        else
            dispatcher.BeginInvoke((Action)AddScope);
    }

    private async Task ResyncAfterScopeChangeAsync(string requestedScope)
    {
        requestedScope = (requestedScope ?? "").Trim();
        if (string.IsNullOrWhiteSpace(requestedScope)) return;

        await _scopeResyncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var currentScope = (NetworkId ?? "").Trim();
            if (!string.Equals(currentScope, requestedScope, StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(_lastScopeResyncNetworkId, currentScope, StringComparison.OrdinalIgnoreCase)) return;

            await ResyncCoreAsync(ShouldResetSpeedHistoryForCurrentScope(), clearUi: true).ConfigureAwait(false);
            _lastScopeResyncNetworkId = currentScope;
            ConnectionStatus = $"Scope changed to '{currentScope}'. Resynced.";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Scope resync failed: {ex.Message}";
        }
        finally
        {
            _scopeResyncLock.Release();
        }
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
        // Preserve current selections (loaded from appsettings.json) so a resync/restart doesn't reset the dropdowns.
        var keepP48 = SelectedFirmwareP48;
        var keepP47 = SelectedFirmwareP47;
        var keepP46 = SelectedFirmwareP46;
        var keepP41 = SelectedFirmwareP41;
        var keepP42 = SelectedFirmwareP42;

        FirmwareOptionsP48.Clear();
      
        FirmwareOptionsP47.Clear();
      
        FirmwareOptionsP46.Clear();
        
        FirmwareOptionsP41.Clear();
        
        FirmwareOptionsP42.Clear();
      
        // Only fall back to "latest" when we don't already have a valid selection.
        SelectedFirmwareP48 = PreserveFirmwareSelection(FirmwareOptionsP48, keepP48);
        SelectedFirmwareP47 = PreserveFirmwareSelection(FirmwareOptionsP47, keepP47);
        SelectedFirmwareP46 = PreserveFirmwareSelection(FirmwareOptionsP46, keepP46);
        SelectedFirmwareP41 = PreserveFirmwareSelection(FirmwareOptionsP41, keepP41);
        SelectedFirmwareP42 = PreserveFirmwareSelection(FirmwareOptionsP42, keepP42);
    }

    private static string PreserveFirmwareSelection(ObservableCollection<string> options, string? current)
    {
        var cur = (current ?? "").Trim();
        if (options == null || options.Count == 0) return cur;
        if (cur.Length > 0 && options.Any(o => string.Equals((o ?? "").Trim(), cur, StringComparison.OrdinalIgnoreCase)))
            return cur;
        return options.LastOrDefault() ?? "";
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

                // Accept both legacy "P4x" and motion "M4x" short descriptions,
                // then normalize Mxx -> Pxx for UI/model routing.
                var m = Regex.Match(shortDesc, @"^([PM]\d{2})", RegexOptions.IgnoreCase);
                if (!m.Success) continue;

                var model = m.Groups[1].Value.ToUpperInvariant();
                if (model.StartsWith("M", StringComparison.Ordinal))
                    model = "P" + model.Substring(1);
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
                _manualDisconnectRequested = true;
                _autoReconnectEnabled = false;
                _autoReconnectCts?.Cancel();
                await _mqtt.DisconnectAsync();
                return;
            }

            _manualDisconnectRequested = false;
            _autoReconnectEnabled = true;
            _autoReconnectCts?.Cancel();

            // Always start a fresh session when connecting (clears UI + internal caches)
            // so reconnect behaves the same as a "clean" connect.
            var resetSpeedHistory = ShouldResetSpeedHistoryForCurrentScope();
            ClearAllUiAndState(resetSpeedHistory);

            _isConnecting = true;
            try
            {
                await _mqtt.ConnectAsync(
                    MqttHost,
                    MqttPort,
                    MqttUser,
                    MqttPassword ?? "",
                    UseTls,
                    IgnoreTlsErrors,
                    MqttTopic,
                    _appCts.Token);
            }
            finally
            {
                _isConnecting = false;
            }

            // Full clean re-sync (subscribe + request snapshots).
            await ResyncCoreAsync(resetSpeedHistory, clearUi: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Error: " + ex.Message;
        }
    }

    private void StartAutoReconnect()
    {
        if (!_autoReconnectEnabled || _manualDisconnectRequested || !_hasEverConnected || _isConnecting) return;
        if (_autoReconnectTask != null && !_autoReconnectTask.IsCompleted) return;

        _autoReconnectCts?.Cancel();
        _autoReconnectCts = new CancellationTokenSource();
        _autoReconnectTask = AutoReconnectLoopAsync(_autoReconnectCts.Token);
    }

    private async Task AutoReconnectLoopAsync(CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested && !IsConnected)
        {
            var delay = _autoReconnectBackoff[Math.Min(attempt, _autoReconnectBackoff.Length - 1)];
            attempt++;

            Application.Current.Dispatcher.Invoke(() =>
            {
                ConnectionStatus = $"Disconnected. Reconnecting in {delay.TotalSeconds:0}s...";
            });

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested || IsConnected) return;

            try
            {
                var resetSpeedHistory = ShouldResetSpeedHistoryForCurrentScope();
                _isConnecting = true;
                try
                {
                    await _mqtt.ConnectAsync(
                        MqttHost,
                        MqttPort,
                        MqttUser,
                        MqttPassword ?? "",
                        UseTls,
                        IgnoreTlsErrors,
                        MqttTopic,
                        _appCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _isConnecting = false;
                }

                await ResyncCoreAsync(resetSpeedHistory, clearUi: true).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ConnectionStatus = $"Reconnect failed: {ex.Message}";
                });
            }
        }
    }

    [RelayCommand]
    private async Task SaveSettings()
    {
        _store.Save(BuildSettingsSnapshot(_store.Load()));

        ConnectionStatus = "Saved appsettings.json";

        // If we are connected, immediately re-sync to reflect the new NetworkId/topic scope.
        if (IsConnected)
            await ResyncCoreAsync(ShouldResetSpeedHistoryForCurrentScope(), clearUi: true).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task SetCassiaMqttScope(string cassiaName)
    {
        cassiaName = (cassiaName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassiaName)) return;

        // Named arguments are case-sensitive in C#. Use positional arguments here to avoid issues.
        var newNet = Interaction.InputBox(
            $"Enter new NetworkId (MQTT scope) for '{cassiaName}':",
            "Set MQTT scope",
            NetworkId ?? "");

        newNet = (newNet ?? "").Trim();
        if (string.IsNullOrWhiteSpace(newNet)) return;

        try
        {
            var topic = BuildCmdTopic(cassiaName, "set-network");
            await _mqtt.PublishJsonAsync(topic, new { networkId = newNet }, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            ConnectionStatus = $"Sent set-network to {cassiaName} → {newNet}";
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Error: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task SetCassiaMqttBroker(string cassiaName)
    {
        cassiaName = (cassiaName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassiaName)) return;
        if (!IsConnected) { ConnectionStatus = "Not connected"; return; }

        var host = Interaction.InputBox(
            $"Enter MQTT broker host for '{cassiaName}' (applies after restart):",
            "Set MQTT broker",
            MqttHost ?? "");

        host = (host ?? "").Trim();
        if (string.IsNullOrWhiteSpace(host)) return;

        var portText = Interaction.InputBox(
            $"Enter MQTT broker port for '{cassiaName}':",
            "Set MQTT broker",
            Math.Max(1, MqttPort).ToString());

        if (!int.TryParse((portText ?? "").Trim(), out var port) || port <= 0 || port > 65535)
        {
            ConnectionStatus = "Invalid port. Must be 1-65535.";
            return;
        }

        var tlsText = Interaction.InputBox(
            $"Use TLS for '{cassiaName}'? (true/false):",
            "Set MQTT broker",
            UseTls ? "true" : "false");

        if (!TryParseBoolLoose(tlsText, out var useTls))
        {
            ConnectionStatus = "Invalid TLS value. Use true/false or 1/0.";
            return;
        }

        try
        {
            var topic = BuildCmdTopic(cassiaName, "set-mqtt-broker");
            var payload = new { host, port, useTls };
            await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            ConnectionStatus = $"Sent set-mqtt-broker to {cassiaName} -> {host}:{port} tls={useTls} (applies after restart)";
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Error: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task SetMqttBrokerForAllCassiasPrompt()
        => await SetCassiaMqttBroker("all");

    [RelayCommand]
    private async Task SetCassiaName(string cassiaName)
    {
        cassiaName = (cassiaName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassiaName)) return;

        var newName = Interaction.InputBox(
            $"Enter new Cassia name for '{cassiaName}':",
            "Set Cassia name",
            cassiaName);

        newName = (newName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(newName)) return;

        try
        {
            var topic = BuildCmdTopic(cassiaName, "set-name");
            await _mqtt.PublishJsonAsync(topic, new { name = newName }, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            ConnectionStatus = $"Sent set-name to {cassiaName} → {newName}";
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Error: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task SetCassiaIdentity(string cassiaName)
    {
        cassiaName = (cassiaName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassiaName)) return;

        var newName = Interaction.InputBox(
            $"Enter Cassia name for '{cassiaName}':",
            "Set Cassia identity",
            cassiaName);

        newName = (newName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(newName)) return;

        var newNet = Interaction.InputBox(
            $"Enter NetworkId for '{newName}':",
            "Set Cassia identity",
            NetworkId ?? "");

        newNet = (newNet ?? "").Trim();
        if (string.IsNullOrWhiteSpace(newNet)) return;

        try
        {
            var topic = BuildCmdTopic(cassiaName, "set-identity");
            await _mqtt.PublishJsonAsync(topic, new { networkId = newNet, name = newName }, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            ConnectionStatus = $"Sent set-identity to {cassiaName} → {newName} ({newNet})";
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Error: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenRuntimeSettings(string cassiaName)
    {
        cassiaName = (cassiaName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassiaName)) return;

        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var wnd = new RuntimeSettingsWindow(this, cassiaName, cassiaName, applyToAll: false)
                {
                    Owner = Application.Current.MainWindow
                };
                wnd.Show();
                wnd.Activate();
            });
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Open runtime settings failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenRuntimeSettingsAll()
    {
        var source = CassiaGateways
            .FirstOrDefault(g => string.Equals(g.State, "online", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(g.Name))
            ?? CassiaGateways.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g.Name));

        if (source == null)
        {
            ConnectionStatus = "No Cassia gateway available to load runtime variables.";
            return;
        }

        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var wnd = new RuntimeSettingsWindow(this, targetCassia: "all", sourceCassia: source.Name, applyToAll: true)
                {
                    Owner = Application.Current.MainWindow
                };
                wnd.Show();
                wnd.Activate();
            });
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Open runtime settings (all) failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task SelfUpdateCassia(string cassiaName)
    {
        cassiaName = (cassiaName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassiaName)) return;
        if (!IsConnected) { ConnectionStatus = "Not connected"; return; }

        var proceed = false;
        try
        {
            var result = MessageBox.Show(
                $"Trigger remote self-update on '{cassiaName}'?\n\n" +
                "This will queue a service restart so the updater can run before AccessAPP starts.",
                "Remote self-update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            proceed = result == MessageBoxResult.Yes;
        }
        catch { }

        if (!proceed) return;

        try
        {
            await PublishSelfUpdateAsync(cassiaName).ConfigureAwait(false);
            ConnectionStatus = $"Sent self-update to {cassiaName}";
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Error: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task SelfUpdateAllCassias()
    {
        if (!IsConnected) { ConnectionStatus = "Not connected"; return; }

        var targets = CassiaGateways
            .Select(g => (g.Name ?? "").Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
        {
            ConnectionStatus = "No Cassia gateways available.";
            return;
        }

        var proceed = false;
        try
        {
            var result = MessageBox.Show(
                $"Trigger remote self-update on {targets.Count} Cassia(s)?\n\n" +
                "This will queue a service restart on each gateway so the updater can run before AccessAPP starts.",
                "Remote self-update (all)",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            proceed = result == MessageBoxResult.Yes;
        }
        catch { }

        if (!proceed) return;

        var sent = 0;
        foreach (var cassiaName in targets)
        {
            try
            {
                await PublishSelfUpdateAsync(cassiaName).ConfigureAwait(false);
                sent++;
            }
            catch
            {
                // Continue with remaining gateways.
            }
        }

        ConnectionStatus = sent == targets.Count
            ? $"Sent self-update to {sent} Cassia(s)"
            : $"Sent self-update to {sent}/{targets.Count} Cassia(s)";
    }

    private Task PublishSelfUpdateAsync(string cassiaName)
    {
        var topic = BuildCmdTopic(cassiaName, "self-update");
        var payload = new
        {
            requestId = Guid.NewGuid().ToString("N"),
            restartService = true,
            serviceName = "accessapp"
        };

        return _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token);
    }

    [RelayCommand]
    private async Task SetUpdateChannelCassia(string cassiaName)
    {
        cassiaName = (cassiaName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassiaName)) return;
        if (!IsConnected) { ConnectionStatus = "Not connected"; return; }

        var value = Interaction.InputBox(
            $"Set update channel for '{cassiaName}' (stable, test, develop):",
            "Set update channel",
            "stable");

        var normalized = NormalizeUpdateChannelToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            ConnectionStatus = "Invalid update channel. Use stable, test, or develop.";
            return;
        }

        try
        {
            var topic = BuildCmdTopic(cassiaName, "set-update-channel");
            var payload = new
            {
                requestId = Guid.NewGuid().ToString("N"),
                channel = normalized
            };

            await _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            ConnectionStatus = $"Sent set-update-channel to {cassiaName}: {normalized}";
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Error: " + ex.Message;
        }
    }

    private static string NormalizeUpdateChannelToken(string? channel)
    {
        var value = (channel ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "stable" => "stable",
            "test" => "test",
            "develop" => "develop",
            "dev" => "develop",
            "prod-stable" => "stable",
            "prod-test" => "test",
            "prod-develop" => "develop",
            _ => string.Empty
        };
    }

    private static bool TryParseBoolLoose(string? value, out bool result)
    {
        var v = (value ?? "").Trim();
        if (bool.TryParse(v, out result)) return true;
        if (string.Equals(v, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v, "y", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }
        if (string.Equals(v, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v, "no", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v, "n", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }
        result = false;
        return false;
    }

    internal async Task SetRuntimeForCassiaAsync(string cassiaName, IReadOnlyDictionary<string, object?> payload)
    {
        if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
        cassiaName = (cassiaName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassiaName)) return;
        if (payload == null || payload.Count == 0) return;

        try
        {
            await _mqtt.PublishJsonAsync(BuildCmdTopic(cassiaName, "set-runtime"), payload, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            ConnectionStatus = $"Sent set-runtime to {cassiaName}";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"set-runtime failed ({cassiaName}): {ex.Message}";
        }
    }

    internal async Task SetRuntimeForAllCassiasAsync(IReadOnlyDictionary<string, object?> payload)
    {
        if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
        if (payload == null || payload.Count == 0) return;

        var targets = CassiaGateways
            .Select(g => (g.Name ?? "").Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
        {
            ConnectionStatus = "No Cassia gateways available.";
            return;
        }

        try
        {
            foreach (var cassia in targets)
            {
                await _mqtt.PublishJsonAsync(BuildCmdTopic(cassia, "set-runtime"), payload, retain: false, qos: 1, ct: _appCts.Token)
                    .ConfigureAwait(false);
            }

            ConnectionStatus = $"Sent set-runtime to {targets.Count} Cassia(s)";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"set-runtime failed (all): {ex.Message}";
        }
    }

    private AppSettings BuildSettingsSnapshot(AppSettings? baseSettings)
    {
        var s = baseSettings ?? new AppSettings();
        var existingTheme = s.accessapp?.theme;

        s.mqtt = new MqttSettings
        {
            host = MqttHost,
            port = MqttPort,
            topic = MqttTopic,
            username = MqttUser,
            password = MqttPassword ?? "",
            useTls = UseTls,
            ignoreTlsErrors = IgnoreTlsErrors
        };

        // Preserve previous firmware selections unless we explicitly overwrite below.
        var fwMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (s.accessapp?.selectedFirmwareByModel != null)
            {
                foreach (var kv in s.accessapp.selectedFirmwareByModel)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key))
                        fwMap[kv.Key.Trim()] = kv.Value ?? "";
                }
            }
        }
        catch { }

        // Always write current in-memory selections.
        fwMap["P41"] = SelectedFirmwareP41 ?? "";
        fwMap["P42"] = SelectedFirmwareP42 ?? "";
        fwMap["P46"] = SelectedFirmwareP46 ?? "";
        fwMap["P47"] = SelectedFirmwareP47 ?? "";
        fwMap["P48"] = SelectedFirmwareP48 ?? "";

        s.accessapp = new AccessAppSettings
        {
            networkId = NetworkId,
            commandTopicTemplate = CommandTopicTemplate,
            defaultCommand = DefaultCommand,
            theme = string.IsNullOrWhiteSpace(existingTheme) ? App.CurrentTheme : existingTheme,
            forceUpdate = ForceUpdateEnabled,
            autoSetWorkersByModel = AutoSetWorkersByModelEnabled,
            productionUpdate = ProductionUpdateEnabled,
            selectedFirmwareByModel = fwMap
        };

        return s;
    }

    private void PersistSelectedFirmware(string model, string? firmware)
    {
        // Avoid writing partial state while the app is still starting up.
        // (e.g. LoadFirmwareOptions sets defaults and triggers the setter)
        if (_isInitializing) return;

        try
        {
            var s = _store.Load();
            s.accessapp ??= new AccessAppSettings();
            s.accessapp.selectedFirmwareByModel ??= new Dictionary<string, string>();
            s.accessapp.selectedFirmwareByModel[model] = firmware ?? "";
            _store.Save(BuildSettingsSnapshot(s));
        }
        catch
        {
            // ignore persistence errors
        }
    }

    



    [RelayCommand]
    private void ClearDevices()
    {
        _devices.Clear();
        CassiaGateways.Clear();
        CassiaNameOptions.Clear();
        _gwSeenMacs.Clear(); // <-- reset unique counters
        RefreshProductFilterOptions();
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
        RefreshProductFilterOptions();

        // Keep queue/progress cache so queued/programming still shows if devices come back.
        // But reset per-device assignment counts.
        foreach (var gw in CassiaGateways)
        {
            gw.AssignedP41 = gw.AssignedP42 = gw.AssignedP46 = gw.AssignedP47 = gw.AssignedP48 = 0;
        }

        RequestDevicesRefresh();

        // Ask all gateways to clear their internal cache first (scan listener will repopulate).
        await ClearDeviceListAsync("all").ConfigureAwait(false);
        await Task.Delay(800, _appCts.Token).ConfigureAwait(false);

        // Request full device list from all gateways.
        await RequestDeviceListAsync("all").ConfigureAwait(false);
    }


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

    private string BuildSpeedHistoryScopeKey()
        => $"{(MqttHost ?? "").Trim().ToLowerInvariant()}|{MqttPort}|{(NetworkId ?? "").Trim().ToLowerInvariant()}";

    private bool ShouldResetSpeedHistoryForCurrentScope()
    {
        var key = BuildSpeedHistoryScopeKey();
        if (string.IsNullOrWhiteSpace(_speedHistoryScopeKey))
        {
            _speedHistoryScopeKey = key;
            return false;
        }

        if (!string.Equals(_speedHistoryScopeKey, key, StringComparison.OrdinalIgnoreCase))
        {
            _speedHistoryScopeKey = key;
            return true;
        }

        return false;
    }

    private void CaptureSpeedHistorySnapshot()
    {
        var snapshot = new Dictionary<string, List<SpeedSample>>(StringComparer.OrdinalIgnoreCase);
        foreach (var gw in CassiaGateways)
        {
            if (gw.SpeedHistory.Count == 0) continue;
            snapshot[gw.Name] = gw.SpeedHistory.ToList();
        }

        if (snapshot.Count == 0)
            return;

        _speedHistoryByGateway.Clear();
        foreach (var kv in snapshot)
            _speedHistoryByGateway[kv.Key] = kv.Value;
    }

    private void RestoreSpeedHistoryIfPresent(CassiaGateway gw)
    {
        if (gw == null) return;
        if (gw.SpeedHistory.Count > 0) return;

        if (_speedHistoryByGateway.TryGetValue(gw.Name, out var history))
        {
            foreach (var s in history)
                gw.SpeedHistory.Add(s);
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
            {
                if (cassiaName.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                    cassiaName.Equals("(all gateways)", StringComparison.OrdinalIgnoreCase))
                    gw = _speedAllGateways;
                else if (cassiaName.Equals("(total)", StringComparison.OrdinalIgnoreCase))
                    gw = _speedTotalGateways;
                else
                    gw = CassiaGateways.FirstOrDefault(g => string.Equals(g.Name, cassiaName, StringComparison.OrdinalIgnoreCase));
            }

            gw ??= CassiaGateways.FirstOrDefault() ?? _speedAllGateways;

            OpenSpeedGraphForGateway(gw);
        }
        catch { }
    }

    [RelayCommand]
    private void OpenSpeedGraphAll()
    {
        try { OpenSpeedGraphForGateway(_speedAllGateways); } catch { }
    }

    private void OpenSpeedGraphForGateway(CassiaGateway? gw)
    {
        if (gw == null) return;

        SelectedSpeedGateway = gw;

        var wnd = new SpeedGraphWindow(this)
        {
            Owner = Application.Current.MainWindow
        };
        wnd.Show();
        wnd.Activate();
    }

    [RelayCommand]
    private void OpenLedRangeVisualizer(string? cassiaName)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(cassiaName))
                LedRangeCassia = cassiaName.Trim();
            else if (string.IsNullOrWhiteSpace(LedRangeCassia) || LedRangeCassia.Equals("all", StringComparison.OrdinalIgnoreCase))
                LedRangeCassia = CassiaGateways.FirstOrDefault()?.Name ?? "all";

            var wnd = new LedRangeVisualizerWindow(this)
            {
                Owner = Application.Current.MainWindow
            };
            wnd.Show();
            wnd.Activate();
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Open LED visualizer failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenLedRangeVisualizerAll()
    {
        try
        {
            LedRangeCassia = "all";
            var wnd = new LedRangeVisualizerWindow(this)
            {
                Owner = Application.Current.MainWindow
            };
            wnd.Show();
            wnd.Activate();
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Open LED visualizer failed: {ex.Message}";
        }
    }

}
