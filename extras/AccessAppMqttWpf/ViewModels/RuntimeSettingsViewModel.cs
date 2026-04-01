using AccessAppMqttWpf.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

using Choice = AccessAppMqttWpf.Models.RuntimeVariableChoice;

namespace AccessAppMqttWpf.ViewModels;

public partial class RuntimeSettingsViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly bool _applyToAll;

    private const string GroupGeneral = "general";
    private const string GroupGatewayBle = "gateway-ble";
    private const string GroupNativeBle = "native-ble";
    private const string GroupUpgradeFlow = "upgrade-flow";
    private const string GroupConnectLogin = "connect-login";
    private const string GroupFirmwareRead = "firmware-read";
    private const string GroupSettingsDali = "settings-dali";
    private const string GroupResultLogging = "result-logging";
    private const string GroupLedRange = "led-range";
    private const string GroupBootActor = "boot-actor";
    private const string GroupUnorganized = "unorganized";

    private static readonly (string Key, string Title)[] GroupOrder =
    {
        (GroupGeneral, "General"),
        (GroupGatewayBle, "Gateway / BLE"),
        (GroupNativeBle, "Native BLE"),
        (GroupUpgradeFlow, "Upgrade Flow"),
        (GroupConnectLogin, "Connect + Login"),
        (GroupFirmwareRead, "Firmware Read"),
        (GroupSettingsDali, "Settings / DALI"),
        (GroupResultLogging, "Result Logging"),
        (GroupLedRange, "LED Range"),
        (GroupBootActor, "Boot / Actor"),
        (GroupUnorganized, "Unorganized")
    };

    private static readonly Dictionary<string, string> GroupTitleByKey =
        GroupOrder.ToDictionary(x => x.Key, x => x.Title, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> ExplicitGroupByName = new(StringComparer.OrdinalIgnoreCase)
    {
        // General
        ["LOG_MIN_LEVEL"] = GroupGeneral,
        ["MQTT_STATUS_HEARTBEAT_SECONDS"] = GroupGeneral,
        ["TUNABLE_WHITE_UNIX_TIME_OFFSET_SECONDS"] = GroupGeneral,

        // Gateway / BLE
        ["WRITE_SLEEP_MS"] = GroupGatewayBle,
        ["ACTOR_CHUNK_SIZE"] = GroupGatewayBle,
        ["ACTOR_INTER_CHUNK_SLEEP_MS"] = GroupGatewayBle,
        ["USE_BOTH_CASSIA_CHIPS"] = GroupGatewayBle,
        ["DEFAULT_CASSIA_CHIP"] = GroupGatewayBle,
        ["CASSIA_MAX_INFLIGHT_PER_CHIP"] = GroupGatewayBle,
        ["CASSIA_CONNECT_DISCOVER_GATT"] = GroupGatewayBle,
        ["UPGRADE_CONNECT_DISCOVER_GATT_AFTER_BOOT_JUMP"] = GroupGatewayBle,
        ["BLE_SCAN_UNDER_PROGRAMMING"] = GroupGatewayBle,
        ["BLE_SCAN_CHIP_MODE"] = GroupGatewayBle,
        ["BLE_STALE_DELAY_AFTER_SCAN_RESUME_MS"] = GroupGatewayBle,

        // Native BLE
        ["BLE_BACKEND"] = GroupNativeBle,
        ["WINDOWS_BLE_MAC_PREFIX"] = GroupNativeBle,

        // Upgrade flow
        ["RebootDetectorAfterUpgrade"] = GroupUpgradeFlow,
        ["Restore102DBAfterUpgrade"] = GroupUpgradeFlow,
        ["RestoreSettingsAfterUpgrade"] = GroupUpgradeFlow,
        ["AutoSetSysFailLevelUnderUpdate"] = GroupUpgradeFlow,
        ["UPGRADE_DELAY_AFTER_END_DISCONNECT_MS"] = GroupUpgradeFlow,
        ["UPGRADE_DELAY_AFTER_BOOT_JUMP_MS"] = GroupUpgradeFlow,
        ["UPGRADE_DELAY_AFTER_FAILED_CONNECT_MS"] = GroupUpgradeFlow,
        ["UPGRADE_CONNECT_MAX_ATTEMPTS"] = GroupUpgradeFlow,
        ["UPGRADE_OPTIMIZE_RECONNECT_FLOW"] = GroupUpgradeFlow,
        ["UPGRADE_CONNECT_TRUST_GATEWAY_CONNECTED_STATE"] = GroupUpgradeFlow,
        ["UPGRADE_POST_UPDATE_BLUE_LED_HOLD_ENABLED"] = GroupUpgradeFlow,

        // Connect + login
        ["UPGRADE_DELAY_AFTER_LOGIN_BEFORE_FW_READ_MS"] = GroupConnectLogin,

        // Firmware read
        ["UPGRADE_POST_UPGRADE_FW_READ_DELAY_MS"] = GroupFirmwareRead,

        // Settings / DALI
        ["UPGRADE_DALI_SYSFAIL_TIMEOUT_MS"] = GroupSettingsDali
    };

    private static readonly (string Prefix, string GroupKey)[] PrefixGroupMap =
    {
        ("UPGRADE_CONNECT_", GroupConnectLogin),
        ("UPGRADE_LOGIN_", GroupConnectLogin),
        ("UPGRADE_PRECHECK_", GroupConnectLogin),
        ("UPGRADE_PROBE_", GroupConnectLogin),
        ("UPGRADE_FW_", GroupFirmwareRead),
        ("UPGRADE_POST_UPGRADE_FW_", GroupFirmwareRead),
        ("UPGRADE_WORKER_BALANCER_", GroupUpgradeFlow),
        ("UPGRADE_SETTINGS_", GroupSettingsDali),
        ("UPGRADE_DALI_", GroupSettingsDali),
        ("UPGRADE_RESULT_DB_LOG_", GroupResultLogging),
        ("LED_RANGE_", GroupLedRange),
        ("BOOTMODE_", GroupBootActor),
        ("LINUX_BLE_BOOTMODE_", GroupBootActor),
        ("UPGRADE_ACTOR_APP_MODE_", GroupBootActor),
        ("UPGRADE_ACTOR_", GroupBootActor),
        ("UPGRADE_SENSOR_", GroupBootActor),
        ("UPGRADE_BOOTLOADER_", GroupBootActor),
        ("UPGRADE_PROGRAMMING_", GroupBootActor),
        ("LINUX_BLE_", GroupNativeBle)
    };

    private static readonly Dictionary<string, string> VariableDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        // General
        ["LOG_MIN_LEVEL"] = "Serilog minimum log level (Verbose, Debug, Information, Warning, Error, Fatal)",
        ["MQTT_STATUS_HEARTBEAT_SECONDS"] = "How often to publish tele/.../status (seconds)",
        ["TUNABLE_WHITE_UNIX_TIME_OFFSET_SECONDS"] = "Seconds offset when syncing Unix time after a Tunable White write",

        // Gateway / BLE
        ["WRITE_SLEEP_MS"] = "Pacing delay between bootloader write packets (ms)",
        ["ACTOR_CHUNK_SIZE"] = "Chunk size for actor boot packets in bytes (0 = default 80)",
        ["ACTOR_INTER_CHUNK_SLEEP_MS"] = "Delay between consecutive actor chunk writes (ms)",
        ["USE_BOTH_CASSIA_CHIPS"] = "Use both BLE chips on Cassia X2000 gateways",
        ["DEFAULT_CASSIA_CHIP"] = "Which chip to use by default on dual-chip gateways (0 or 1)",
        ["CASSIA_MAX_INFLIGHT_PER_CHIP"] = "Max concurrent REST/GATT requests allowed per Cassia chip",
        ["CASSIA_CONNECT_DISCOVER_GATT"] = "Use cached GATT when available on Cassia connect (0 = off)",
        ["UPGRADE_CONNECT_DISCOVER_GATT_AFTER_BOOT_JUMP"] = "GATT discovery strategy after boot-jump (−1 = inherit from CASSIA_CONNECT_DISCOVER_GATT)",
        ["BLE_SCAN_UNDER_PROGRAMMING"] = "Keep BLE scanning active while programming more than one device",
        ["BLE_SCAN_CHIP_MODE"] = "BLE scan chip mode (−1 = default, 0 = chip 0, 1 = chip 1, 2 = both)",
        ["BLE_STALE_DELAY_AFTER_SCAN_RESUME_MS"] = "Delay before publishing stale-device notices after scan resumes (ms)",

        // Native BLE
        ["BLE_BACKEND"] = "BLE backend to use (auto, cassia, linux-native, windows-native)",
        ["WINDOWS_BLE_MAC_PREFIX"] = "MAC address prefix filter for Windows native BLE",
        ["LINUX_BLE_ADAPTER"] = "HCI adapter name for Linux native BLE",
        ["LINUX_BLE_ADAPTERS"] = "Comma-separated HCI adapters used for scanning",
        ["LINUX_BLE_MAC_PREFIX"] = "MAC address prefix filter for Linux native BLE",
        ["LINUX_BLE_SERVICES_RESOLVED_TIMEOUT_MS"] = "Max wait for BlueZ ServicesResolved after connect (ms)",
        ["LINUX_BLE_MODE_DETECT_SR_GRACE_MS"] = "Grace period in GATT mode detection before checking ServicesResolved (ms)",
        ["LINUX_BLE_CONTROL_HANDLE"] = "GATT handle used for control writes and notifications",
        ["LINUX_BLE_NOTIFY_CCCD_SENSOR_HANDLE"] = "GATT CCCD handle to enable sensor notifications",
        ["LINUX_BLE_NOTIFY_CCCD_ACTOR_HANDLE"] = "GATT CCCD handle to enable actor notifications",
        ["LINUX_BLE_WRITE_FIND_CHAR_TIMEOUT_MS"] = "Wait for characteristic handle discovery after connect (ms)",
        ["LINUX_BLE_DATA_NOTIFICATION_TIMEOUT_MS"] = "Max wait for a notification after a write command (ms)",
        ["LINUX_BLE_LOGIN_NOTIFY_READY_TIMEOUT_MS"] = "Wait for notify pipeline to be ready before sending login (ms)",
        ["LINUX_BLE_ENABLE_CI_UPDATE"] = "Request a shorter BLE connection interval after connect",

        // Upgrade flow
        ["RebootDetectorAfterUpgrade"] = "Run reboot detector after firmware upgrade",
        ["Restore102DBAfterUpgrade"] = "Restore DALI 102 database after firmware upgrade",
        ["RestoreSettingsAfterUpgrade"] = "Back up and restore device settings across firmware upgrade",
        ["AutoSetSysFailLevelUnderUpdate"] = "Temporarily set DALI SysFail level to safe value during update",
        ["UPGRADE_DELAY_AFTER_END_DISCONNECT_MS"] = "Extra delay after final disconnect at end of upgrade (ms)",
        ["UPGRADE_DELAY_AFTER_BOOT_JUMP_MS"] = "Extra delay after JumpToBootloader command (ms)",
        ["UPGRADE_DELAY_AFTER_FAILED_CONNECT_MS"] = "Extra delay inserted after a failed connection attempt (ms)",
        ["UPGRADE_CONNECT_MAX_ATTEMPTS"] = "Maximum connection attempts per upgrade step",
        ["UPGRADE_OPTIMIZE_RECONNECT_FLOW"] = "Enable optimized reconnect flow with BLE session reuse",
        ["UPGRADE_CONNECT_TRUST_GATEWAY_CONNECTED_STATE"] = "Trust /gap/nodes connected state to skip redundant checks",
        ["UPGRADE_POST_UPDATE_BLUE_LED_HOLD_ENABLED"] = "Keep session alive to show blue LED after upgrade completes",
        ["UPGRADE_WORKER_BALANCER_ENABLED"] = "Adaptive balancer that throttles fast workers when slow ones are detected",
        ["UPGRADE_WORKER_BALANCER_SLOW_THRESHOLD_PCT_PER_MIN"] = "Progress rate below which a worker is considered slow (%/min)",
        ["UPGRADE_WORKER_BALANCER_MIN_PROGRESS_PCT"] = "Minimum overall progress before the balancer activates (%)",
        ["UPGRADE_WORKER_BALANCER_LOW_STREAK"] = "Consecutive low-rate samples before marking a worker as slow",
        ["UPGRADE_WORKER_BALANCER_RECOVERY_STREAK"] = "Consecutive recovery samples needed to clear slow state",
        ["UPGRADE_WORKER_BALANCER_MIN_ACTIVE_WORKERS"] = "Minimum active workers required for balancer to act",
        ["UPGRADE_WORKER_BALANCER_RELIEF_DELAY_MS"] = "Extra delay added to non-slow workers by the balancer (ms)",

        // Connect + login
        ["UPGRADE_DELAY_AFTER_LOGIN_BEFORE_FW_READ_MS"] = "Delay from successful login before starting firmware read (ms)",
        ["UPGRADE_CONNECT_ATTEMPT_TIMEOUT_MS"] = "Timeout per individual connection attempt (ms)",
        ["UPGRADE_CONNECT_STABILIZATION_DELAY_MS"] = "Delay after connect before sending login (ms)",
        ["UPGRADE_CONNECT_GATEWAY_STATE_CHECK_ATTEMPTS"] = "Number of gateway state polls after a non-OK connect response",
        ["UPGRADE_CONNECT_GATEWAY_STATE_CHECK_DELAY_MS"] = "Delay between gateway state polls (ms)",
        ["UPGRADE_CONNECT_GATEWAY_STATE_CHECK_ATTEMPTS_ON_500"] = "Gateway state polls after HTTP 500",
        ["UPGRADE_CONNECT_GATEWAY_STATE_CHECK_DELAY_MS_ON_500"] = "Delay between state polls after HTTP 500 (ms)",
        ["UPGRADE_CONNECT_GATEWAY_STATE_CHECK_ATTEMPTS_ON_500_PRE_RETRY"] = "State polls before retry after HTTP 500",
        ["UPGRADE_CONNECT_GATEWAY_STATE_CHECK_DELAY_MS_ON_500_PRE_RETRY"] = "Delay before retry state polls after HTTP 500 (ms)",
        ["UPGRADE_CONNECT_GATEWAY_STATE_CHECK_INITIAL_DELAY_MS_ON_500"] = "Initial delay before first state poll after HTTP 500 (ms)",
        ["UPGRADE_CONNECT_GATEWAY_STATE_CHECK_INITIAL_DELAY_MS_ON_500_PRE_RETRY"] = "Initial delay before pre-retry state poll after HTTP 500 (ms)",
        ["UPGRADE_CONNECT_TRANSIENT_500_RETRIES_PER_ATTEMPT"] = "Quick retries on transient HTTP 500 before escalating",
        ["UPGRADE_CONNECT_TRANSIENT_500_RETRY_DELAY_MS"] = "Delay between transient HTTP 500 retries (ms)",
        ["UPGRADE_CONNECT_SKIP_DISCONNECT_ON_500"] = "Skip explicit disconnect step when HTTP 500 is received",
        ["UPGRADE_CONNECT_LOGIN_USE_PER_CHIP_GATE"] = "Serialize connect+login operations per chip to avoid conflicts",
        ["UPGRADE_CONNECT_RETRY_BACKOFF_MULTIPLIER_X100"] = "Backoff multiplier between connection retries (×100, e.g. 200 = 2×)",
        ["UPGRADE_CONNECT_RETRY_BACKOFF_MAX_MS"] = "Maximum backoff delay between connection attempts (ms)",
        ["UPGRADE_CONNECT_RETRY_JITTER_PCT"] = "Random jitter applied to retry delays (0–90%)",
        ["UPGRADE_LOGIN_ATTEMPT_TIMEOUT_MS"] = "Timeout per login telegram attempt (ms)",
        ["UPGRADE_LOGIN_RETRIES_PER_CONNECTED_SESSION"] = "Login retries before giving up and reconnecting",
        ["UPGRADE_LOGIN_RETRY_DELAY_MS"] = "Delay between login retries (ms)",
        ["UPGRADE_LOGIN_DELAY_AFTER_CONNECT_MS"] = "Delay from connected state before sending login telegram (ms)",
        ["UPGRADE_PRECHECK_LOGIN_ATTEMPT_TIMEOUT_MS"] = "Login timeout during precheck firmware-read phase (ms)",
        ["UPGRADE_PRECHECK_LOGIN_SETTLE_DELAY_MS"] = "Settle delay before login during precheck phase (ms)",
        ["UPGRADE_PRECHECK_DELAY_AFTER_LOGIN_BEFORE_FW_READ_MS"] = "Delay after login before firmware read during precheck (ms)",
        ["UPGRADE_PRECHECK_PROBE_CONNECT_MAX_ATTEMPTS"] = "Max connection attempts for precheck probe",
        ["UPGRADE_PRECHECK_PROBE_CONNECT_RETRY_DELAY_MS"] = "Delay between precheck probe connection retries (ms)",
        ["UPGRADE_PRECHECK_PROBE_CONNECT_ATTEMPT_TIMEOUT_MS"] = "Timeout per precheck probe connection attempt (ms)",
        ["UPGRADE_PRECHECK_FW_READ_CONNECT_LOGIN_MAX_ATTEMPTS"] = "Max connect+login attempts for precheck firmware-read reconnect",
        ["UPGRADE_PROBE_CONNECT_ATTEMPT_TIMEOUT_MS"] = "Timeout per pipeline probe connection attempt (ms)",

        // Firmware read
        ["UPGRADE_POST_UPGRADE_FW_READ_DELAY_MS"] = "Delay before reading firmware version to verify upgrade success (ms)",
        ["UPGRADE_FW_READ_ATTEMPTS"] = "Max firmware read attempts per part",
        ["UPGRADE_FW_READ_RETRY_DELAY_MS"] = "Delay between firmware read attempts (ms)",
        ["UPGRADE_FW_COMMAND_RETRY_ATTEMPTS"] = "Retries for each individual firmware read command",
        ["UPGRADE_FW_COMMAND_RETRY_DELAY_MS"] = "Delay between firmware command retries (ms)",

        // Settings / DALI
        ["UPGRADE_DALI_SYSFAIL_TIMEOUT_MS"] = "Max time to wait for DALI SysFail get/set (ms)",
        ["UPGRADE_SETTINGS_BACKUP_READ_ATTEMPTS"] = "Per-field read attempts during settings backup",
        ["UPGRADE_SETTINGS_BACKUP_READ_RETRY_DELAY_MS"] = "Delay between settings backup read retries (ms)",
        ["UPGRADE_SETTINGS_BACKUP_RETRY_ROUNDS"] = "Full backup retry rounds if backup fails",
        ["UPGRADE_SETTINGS_BACKUP_RETRY_DELAY_MS"] = "Delay between full backup retry rounds (ms)",
        ["UPGRADE_SETTINGS_RESTORE_WRITE_TIMEOUT_MS"] = "Max wait per BLE write during settings restore (ms)",

        // Result logging
        ["UPGRADE_RESULT_DB_LOG_ENABLED"] = "Enable posting upgrade results to the statistics endpoint",
        ["UPGRADE_RESULT_DB_LOG_URL"] = "HTTP endpoint for upgrade result logging",
        ["UPGRADE_RESULT_DB_LOG_TIMEOUT_MS"] = "HTTP request timeout for upgrade result logging (ms)",

        // LED range
        ["LED_RANGE_MIN_RSSI"] = "Minimum RSSI for a device to be considered in range (dBm)",
        ["LED_RANGE_GREEN_THRESHOLD"] = "RSSI threshold for green LED indication (dBm)",
        ["LED_RANGE_BLUE_THRESHOLD"] = "RSSI threshold for blue LED indication (dBm)",
        ["LED_RANGE_PARALLEL_PER_CHIP"] = "Max parallel LED range connect operations per chip",
        ["LED_RANGE_MAX_CONNECT_ATTEMPTS"] = "Max connection attempts per LED range operation",
        ["LED_RANGE_AUTORETRY_ROUNDS"] = "Auto-retry rounds for failed LED range operations",
        ["LED_RANGE_AUTORETRY_DELAY_MS"] = "Delay between LED range auto-retry rounds (ms)",
        ["LED_RANGE_CONNECT_RETRY_DELAY_MS"] = "Delay between connect retries in LED range (ms)",
        ["LED_RANGE_EXPECTATIONFAILED_EXTRA_DELAY_MS"] = "Extra delay after HTTP 417 in LED range (ms)",
        ["LED_RANGE_INTERNAL_ERROR_EXTRA_DELAY_MS"] = "Extra delay after HTTP 500 in LED range (ms)",

        // Boot / Actor
        ["BOOTMODE_RETRY_COUNT"] = "Retries for the Cassia boot mode check request",
        ["BOOTMODE_RETRY_DELAY_MS"] = "Delay between Cassia boot mode check retries (ms)",
        ["LINUX_BLE_BOOTMODE_CHECK_TIMEOUT_MS"] = "Per-attempt timeout for GATT boot mode detection (ms)",
        ["LINUX_BLE_BOOTMODE_RETRY_COUNT"] = "Retries when boot mode is unknown or unavailable",
        ["LINUX_BLE_BOOTMODE_RETRY_DELAY_MS"] = "Delay between boot mode detection retries (ms)",
        ["LINUX_BLE_BOOTMODE_CHAR_LOOKUP_TIMEOUT_MS"] = "Fallback UUID lookup timeout for boot mode detection (ms)",
        ["UPGRADE_SENSOR_BOOTMODE_VERIFY_BUDGET_MS"] = "Total time budget to verify boot mode after firmware jump (ms)",
        ["UPGRADE_SENSOR_BOOTMODE_VERIFY_POLL_MS"] = "Interval between boot mode verification polls (ms)",
        ["UPGRADE_SENSOR_BOOT_PRE_RECONNECT_SETTLE_MS"] = "Wait after disconnect before reconnecting during sensor boot (ms)",
        ["UPGRADE_SENSOR_BOOT_GATT_SETTLE_MS"] = "Wait after connect before querying characteristics during sensor boot (ms)",
        ["UPGRADE_ACTOR_APP_MODE_WAIT_ATTEMPTS"] = "Attempts waiting for actor to reach application mode",
        ["UPGRADE_ACTOR_APP_MODE_WAIT_DELAY_MS"] = "Delay between application mode wait attempts (ms)",
        ["UPGRADE_ACTOR_BOOTMODE_RETRY_COUNT"] = "Boot mode detection retries for actor upgrade",
        ["UPGRADE_ACTOR_BOOTMODE_RETRY_DELAY_MS"] = "Delay between actor boot mode retries (ms)",
        ["UPGRADE_ACTOR_BOOTMODE_CHECK_TIMEOUT_MS"] = "Total timeout for actor boot mode check (ms)",
        ["UPGRADE_ACTOR_POST_BOOTMODE_DELAY_MS"] = "Settle delay after actor boot mode is detected (ms)",
        ["UPGRADE_ACTOR_UPLOAD_MAX_ATTEMPTS"] = "Max upload attempts for actor firmware",
        ["UPGRADE_ACTOR_UPLOAD_RETRY_DELAY_MS"] = "Delay between actor upload retries (ms)",
        ["UPGRADE_ACTOR_WRITE_SLEEP_MS"] = "Pacing delay between actor write operations (ms)",
        ["UPGRADE_SENSOR_UPLOAD_MAX_ATTEMPTS"] = "Max upload attempts for sensor firmware",
        ["UPGRADE_SENSOR_UPLOAD_RETRY_DELAY_MS"] = "Delay between sensor upload retries (ms)",
        ["UPGRADE_BOOTLOADER_UPLOAD_MAX_ATTEMPTS"] = "Max upload attempts for bootloader firmware",
        ["UPGRADE_BOOTLOADER_UPLOAD_RETRY_DELAY_MS"] = "Delay between bootloader upload retries (ms)",
        ["UPGRADE_PROGRAMMING_NOTIFICATION_WAIT_MS"] = "Max wait for a programming notification callback (ms)",
    };

    private static readonly Dictionary<string, IReadOnlyList<Choice>> VariableChoices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LOG_MIN_LEVEL"] = new Choice[]
        {
            new("Verbose",     "Verbose"),
            new("Debug",       "Debug"),
            new("Information", "Information"),
            new("Warning",     "Warning"),
            new("Error",       "Error"),
            new("Fatal",       "Fatal"),
        },
        ["BLE_BACKEND"] = new Choice[]
        {
            new("auto",             "auto"),
            new("cassia",           "cassia"),
            new("linux-native",     "linux-native"),
            new("windows-native",   "windows-native"),
        },
        ["DEFAULT_CASSIA_CHIP"] = new Choice[]
        {
            new(0, "0"),
            new(1, "1"),
        },
        ["BLE_SCAN_CHIP_MODE"] = new Choice[]
        {
            new(-1, "-1  (default)"),
            new(0,  "0   chip 0 only"),
            new(1,  "1   chip 1 only"),
            new(2,  "2   both chips"),
        },
        ["CASSIA_CONNECT_DISCOVER_GATT"] = new Choice[]
        {
            new(0, "0  (disabled — always re-discover)"),
            new(1, "1  (use cached GATT when available)"),
        },
        ["UPGRADE_CONNECT_DISCOVER_GATT_AFTER_BOOT_JUMP"] = new Choice[]
        {
            new(-1, "-1  (inherit from CASSIA_CONNECT_DISCOVER_GATT)"),
            new(0,  "0   disabled — always re-discover"),
            new(1,  "1   use cached GATT when available"),
        },
    };

    public RuntimeSettingsViewModel(MainViewModel main, string targetCassia, string sourceCassia, bool applyToAll)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        TargetCassia = (targetCassia ?? "").Trim();
        SourceCassia = (sourceCassia ?? "").Trim();
        _applyToAll = applyToAll;
        TargetLabel = _applyToAll
            ? $"ALL Cassias (loaded from {SourceCassia})"
            : TargetCassia;
        WindowTitle = _applyToAll
            ? "Runtime Variables - ALL"
            : $"Runtime Variables - {TargetCassia}";
        Variables = new ObservableCollection<RuntimeVariableItem>();
        VariableGroups = new ObservableCollection<RuntimeVariableGroup>();
        _main.RuntimeVariablesReceived += OnRuntimeVariablesReceived;
        _ = RefreshAsync();
    }

    public string TargetCassia { get; }
    public string SourceCassia { get; }
    public string TargetLabel { get; }
    public string WindowTitle { get; }

    public ObservableCollection<RuntimeVariableItem> Variables { get; }
    public ObservableCollection<RuntimeVariableGroup> VariableGroups { get; }

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private RuntimeVariableGroup? selectedGroup;

    public event Action? RequestClose;

    public void Dispose()
    {
        _main.RuntimeVariablesReceived -= OnRuntimeVariablesReceived;
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (string.IsNullOrWhiteSpace(TargetCassia))
        {
            RequestClose?.Invoke();
            return;
        }

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var v in Variables)
        {
            if (v.TryGetValue(out var value, out var err))
                payload[v.Name] = value;
            else
                errors.Add($"{v.Name}: {err}");
        }

        if (errors.Count > 0)
        {
            StatusText = "Fix invalid values: " + string.Join("  ", errors.Take(3));
            return;
        }

        if (_applyToAll)
        {
            await _main.SetRuntimeForAllCassiasAsync(payload).ConfigureAwait(false);
            StatusText = "Sent update to ALL Cassias. Waiting for refresh...";
        }
        else
        {
            await _main.SetRuntimeForCassiaAsync(TargetCassia, payload).ConfigureAwait(false);
            StatusText = "Sent update. Waiting for refresh...";
        }

        await RefreshAsync().ConfigureAwait(false);

        Application.Current.Dispatcher.InvokeAsync(() => RequestClose?.Invoke(), System.Windows.Threading.DispatcherPriority.Background);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    private async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceCassia))
            return;

        IsLoading = true;
        StatusText = "Loading runtime variables...";

        var snapshot = await _main.RequestRuntimeVariablesAsync(SourceCassia).ConfigureAwait(false);

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (snapshot != null)
            {
                ReplaceVariables(snapshot, "Loaded");
            }
            else
            {
                Variables.Clear();
                RebuildVariableGroups();
                StatusText = "No runtime variables returned.";
            }

            IsLoading = false;
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnRuntimeVariablesReceived(string cassia, IReadOnlyDictionary<string, RuntimeVariableValue> vars)
    {
        if (!string.Equals(cassia, SourceCassia, StringComparison.OrdinalIgnoreCase)) return;

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ReplaceVariables(vars, "Updated");
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ReplaceVariables(IReadOnlyDictionary<string, RuntimeVariableValue> vars, string statusPrefix)
    {
        Variables.Clear();
        foreach (var v in vars.Values.OrderBy(x => x.Name))
        {
            var item = RuntimeVariableItem.FromValue(v);
            if (VariableDescriptions.TryGetValue(v.Name, out var desc))
                item.Description = desc;
            if (VariableChoices.TryGetValue(v.Name, out var choices))
            {
                item.Choices = choices;
                var currentStr = item.TextValue ?? "";
                item.SelectedChoice = choices.FirstOrDefault(c =>
                    string.Equals(c.Value?.ToString(), currentStr, StringComparison.OrdinalIgnoreCase))
                    ?? choices.FirstOrDefault();
            }
            Variables.Add(item);
        }

        RebuildVariableGroups();
        StatusText = $"{statusPrefix} {Variables.Count} variables.";
    }

    private void RebuildVariableGroups()
    {
        var selectedKey = SelectedGroup?.Key;
        var groupsByKey = new Dictionary<string, RuntimeVariableGroup>(StringComparer.OrdinalIgnoreCase);

        RuntimeVariableGroup GetOrCreate(string groupKey)
        {
            if (!groupsByKey.TryGetValue(groupKey, out var group))
            {
                group = new RuntimeVariableGroup(groupKey, GetGroupTitle(groupKey));
                groupsByKey[groupKey] = group;
            }

            return group;
        }

        foreach (var variable in Variables.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            var groupKey = GetGroupKey(variable.Name);
            GetOrCreate(groupKey).Variables.Add(variable);
        }

        var orderedGroups = new List<RuntimeVariableGroup>();
        foreach (var (key, _) in GroupOrder)
        {
            if (groupsByKey.TryGetValue(key, out var existingGroup))
            {
                orderedGroups.Add(existingGroup);
                continue;
            }

            if (string.Equals(key, GroupUnorganized, StringComparison.OrdinalIgnoreCase))
            {
                orderedGroups.Add(new RuntimeVariableGroup(GroupUnorganized, GetGroupTitle(GroupUnorganized)));
            }
        }

        foreach (var extraGroup in groupsByKey.Values
                     .Where(g => GroupOrder.All(x => !string.Equals(x.Key, g.Key, StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase))
        {
            orderedGroups.Add(extraGroup);
        }

        VariableGroups.Clear();
        foreach (var group in orderedGroups)
            VariableGroups.Add(group);

        SelectedGroup = VariableGroups.FirstOrDefault(g =>
                string.Equals(g.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
            ?? VariableGroups.FirstOrDefault(g =>
                !string.Equals(g.Key, GroupUnorganized, StringComparison.OrdinalIgnoreCase))
            ?? VariableGroups.FirstOrDefault();
    }

    private static string GetGroupKey(string variableName)
    {
        if (string.IsNullOrWhiteSpace(variableName))
            return GroupUnorganized;

        if (ExplicitGroupByName.TryGetValue(variableName, out var explicitGroup))
            return explicitGroup;

        foreach (var (prefix, groupKey) in PrefixGroupMap)
        {
            if (variableName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return groupKey;
        }

        return GroupUnorganized;
    }

    private static string GetGroupTitle(string key)
    {
        return GroupTitleByKey.TryGetValue(key, out var title) ? title : key;
    }
}
