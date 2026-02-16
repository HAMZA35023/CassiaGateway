using AccessAppMqttWpf.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace AccessAppMqttWpf.ViewModels;

public partial class RuntimeSettingsViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly bool _applyToAll;

    private const string GroupGatewayBle = "gateway-ble";
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
        (GroupGatewayBle, "Gateway / BLE"),
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
        ("UPGRADE_FW_", GroupFirmwareRead),
        ("UPGRADE_POST_UPGRADE_FW_", GroupFirmwareRead),
        ("UPGRADE_WORKER_BALANCER_", GroupUpgradeFlow),
        ("UPGRADE_SETTINGS_BACKUP_", GroupSettingsDali),
        ("UPGRADE_DALI_", GroupSettingsDali),
        ("UPGRADE_RESULT_DB_LOG_", GroupResultLogging),
        ("LED_RANGE_", GroupLedRange),
        ("BOOTMODE_", GroupBootActor),
        ("UPGRADE_ACTOR_APP_MODE_", GroupBootActor)
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

        Application.Current.Dispatcher.Invoke(() => RequestClose?.Invoke());
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

        Application.Current.Dispatcher.Invoke(() =>
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
        });
    }

    private void OnRuntimeVariablesReceived(string cassia, IReadOnlyDictionary<string, RuntimeVariableValue> vars)
    {
        if (!string.Equals(cassia, SourceCassia, StringComparison.OrdinalIgnoreCase)) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            ReplaceVariables(vars, "Updated");
        });
    }

    private void ReplaceVariables(IReadOnlyDictionary<string, RuntimeVariableValue> vars, string statusPrefix)
    {
        Variables.Clear();
        foreach (var item in vars.Values.OrderBy(v => v.Name).Select(RuntimeVariableItem.FromValue))
            Variables.Add(item);

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
