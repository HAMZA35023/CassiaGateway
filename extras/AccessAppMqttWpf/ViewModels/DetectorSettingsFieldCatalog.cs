using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;

namespace AccessAppMqttWpf.ViewModels;

public enum DetectorSettingsSectionKind
{
    UserConfig,
    WiredPushButtons,
    BlePushButtons,
    DaliPushButtons,
    DaliDeviceCommonParam
}

public enum DetectorFieldEditorKind
{
    Number,
    Enum,
    Bool,
    Hex
}

public sealed class DetectorFieldOption
{
    public DetectorFieldOption(int value, string label)
    {
        Value = value;
        Label = label ?? string.Empty;
    }

    public int Value { get; }
    public string Label { get; }
}

public sealed class DetectorFieldDefinition
{
    public string Key { get; init; } = string.Empty;
    public DetectorSettingsSectionKind Section { get; init; }
    public string Group { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Offset { get; init; }
    public int Length { get; init; } = 1;
    public DetectorFieldEditorKind EditorKind { get; init; } = DetectorFieldEditorKind.Number;
    public int BitMask { get; init; } = 0xFF;
    public int BitShift { get; init; } = 0;
    public int Min { get; init; } = 0;
    public int Max { get; init; } = 255;
    public IReadOnlyList<DetectorFieldOption>? Options { get; init; }
    public Func<int, string>? CustomEnumValueLabelFactory { get; init; }
}

public partial class DetectorFieldRowViewModel : ObservableObject
{
    private readonly DetectorFieldDefinition _def;
    private bool _suspendAutoSelect;

    public DetectorFieldRowViewModel(DetectorFieldDefinition definition)
    {
        _def = definition ?? throw new ArgumentNullException(nameof(definition));
        if (_def.Options != null)
        {
            foreach (var option in _def.Options)
                Options.Add(option);
        }
    }

    public string Key => _def.Key;
    public DetectorSettingsSectionKind Section => _def.Section;
    public string Group => _def.Group;
    public string Label => _def.Label;
    public string Description => _def.Description;
    public int Offset => _def.Offset;
    public int Length => _def.Length;
    public DetectorFieldEditorKind EditorKind => _def.EditorKind;
    public bool IsNumberEditor => _def.EditorKind == DetectorFieldEditorKind.Number;
    public bool IsEnumEditor => _def.EditorKind == DetectorFieldEditorKind.Enum;
    public bool IsBoolEditor => _def.EditorKind == DetectorFieldEditorKind.Bool;
    public bool IsHexEditor => _def.EditorKind == DetectorFieldEditorKind.Hex;
    public ObservableCollection<DetectorFieldOption> Options { get; } = new();

    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private string valueText = string.Empty;
    [ObservableProperty] private int selectedOptionValue;
    [ObservableProperty] private bool boolValue;
    [ObservableProperty] private string currentValueText = string.Empty;
    [ObservableProperty] private string validationError = string.Empty;

    partial void OnValueTextChanged(string value)
    {
        if (!_suspendAutoSelect && !IsSelected)
            IsSelected = true;
    }

    partial void OnSelectedOptionValueChanged(int value)
    {
        if (!_suspendAutoSelect && !IsSelected)
            IsSelected = true;
    }

    partial void OnBoolValueChanged(bool value)
    {
        if (!_suspendAutoSelect && !IsSelected)
            IsSelected = true;
    }

    public void LoadFromBytes(byte[] source)
    {
        source ??= Array.Empty<byte>();
        var safeOffset = Math.Max(0, _def.Offset);
        var safeLength = Math.Max(1, _def.Length);

        _suspendAutoSelect = true;
        try
        {
            ValidationError = string.Empty;

            if (safeOffset >= source.Length)
            {
                ValueText = string.Empty;
                CurrentValueText = string.Empty;
                SelectedOptionValue = 0;
                BoolValue = false;
                IsSelected = false;
                return;
            }

            var available = Math.Min(safeLength, source.Length - safeOffset);
            var span = source.AsSpan(safeOffset, available);

            switch (_def.EditorKind)
            {
                case DetectorFieldEditorKind.Bool:
                {
                    var b = span[0];
                    var current = (b & _def.BitMask) != 0;
                    BoolValue = current;
                    CurrentValueText = current ? "true" : "false";
                    ValueText = CurrentValueText;
                    break;
                }
                case DetectorFieldEditorKind.Enum:
                {
                    var current = ReadIntLe(span);
                    if (_def.BitMask != 0xFF || _def.BitShift != 0)
                        current = (current & _def.BitMask) >> _def.BitShift;
                    EnsureEnumOptionExists(current);
                    SelectedOptionValue = current;
                    ValueText = current.ToString(CultureInfo.InvariantCulture);
                    CurrentValueText = ResolveEnumLabel(current);
                    break;
                }
                case DetectorFieldEditorKind.Hex:
                {
                    var currentHex = Convert.ToHexString(span).ToUpperInvariant();
                    ValueText = currentHex;
                    CurrentValueText = currentHex;
                    break;
                }
                default:
                {
                    var current = ReadIntLe(span);
                    ValueText = current.ToString(CultureInfo.InvariantCulture);
                    CurrentValueText = ValueText;
                    break;
                }
            }

            IsSelected = false;
        }
        finally
        {
            _suspendAutoSelect = false;
        }
    }

    public bool TryApplyTo(byte[] valueBytes, byte[] maskBytes, out string error)
    {
        error = string.Empty;
        if (!IsSelected)
            return true;

        if (valueBytes == null || maskBytes == null)
        {
            error = $"[{Label}] Internal error: output buffers are not initialized.";
            return false;
        }

        var safeOffset = Math.Max(0, _def.Offset);
        var safeLength = Math.Max(1, _def.Length);

        if (safeOffset >= valueBytes.Length || safeOffset >= maskBytes.Length)
        {
            error = $"[{Label}] Field offset is outside the section length.";
            return false;
        }

        if (safeOffset + safeLength > valueBytes.Length || safeOffset + safeLength > maskBytes.Length)
        {
            error = $"[{Label}] Field length exceeds the section length.";
            return false;
        }

        try
        {
            switch (_def.EditorKind)
            {
                case DetectorFieldEditorKind.Bool:
                {
                    var mask = (byte)(_def.BitMask & 0xFF);
                    var value = BoolValue ? mask : (byte)0;
                    valueBytes[safeOffset] = (byte)((valueBytes[safeOffset] & ~mask) | value);
                    maskBytes[safeOffset] |= mask;
                    break;
                }
                case DetectorFieldEditorKind.Enum:
                {
                    var raw = SelectedOptionValue;
                    if (raw < _def.Min || raw > _def.Max)
                    {
                        error = $"[{Label}] Value {raw} is outside range {_def.Min}..{_def.Max}.";
                        return false;
                    }

                    if (_def.BitMask != 0xFF || _def.BitShift != 0)
                    {
                        var mask = (byte)(_def.BitMask & 0xFF);
                        var shifted = (raw << _def.BitShift) & _def.BitMask;
                        valueBytes[safeOffset] = (byte)((valueBytes[safeOffset] & ~mask) | shifted);
                        maskBytes[safeOffset] |= mask;
                    }
                    else
                    {
                        WriteIntLe(raw, safeLength, valueBytes, safeOffset);
                        FillMask(maskBytes, safeOffset, safeLength, 0xFF);
                    }
                    break;
                }
                case DetectorFieldEditorKind.Hex:
                {
                    var cleanHex = NormalizeHex(ValueText);
                    if (cleanHex.Length == 0)
                    {
                        error = $"[{Label}] Hex value is empty.";
                        return false;
                    }
                    var requiredChars = safeLength * 2;
                    if (cleanHex.Length > requiredChars)
                    {
                        error = $"[{Label}] Hex value is too long. Expected {requiredChars} hex chars.";
                        return false;
                    }
                    cleanHex = cleanHex.PadLeft(requiredChars, '0');
                    var bytes = Convert.FromHexString(cleanHex);
                    Array.Copy(bytes, 0, valueBytes, safeOffset, safeLength);
                    FillMask(maskBytes, safeOffset, safeLength, 0xFF);
                    break;
                }
                default:
                {
                    if (!TryParseFlexibleInt(ValueText, out var parsed))
                    {
                        error = $"[{Label}] Enter a valid number (decimal or 0x.. hex).";
                        return false;
                    }
                    if (parsed < _def.Min || parsed > _def.Max)
                    {
                        error = $"[{Label}] Value {parsed} is outside range {_def.Min}..{_def.Max}.";
                        return false;
                    }
                    WriteIntLe(parsed, safeLength, valueBytes, safeOffset);
                    FillMask(maskBytes, safeOffset, safeLength, 0xFF);
                    break;
                }
            }

            ValidationError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"[{Label}] {ex.Message}";
            return false;
        }
    }

    public string ExportProfileValue()
    {
        return _def.EditorKind switch
        {
            DetectorFieldEditorKind.Bool => BoolValue ? "true" : "false",
            DetectorFieldEditorKind.Enum => SelectedOptionValue.ToString(CultureInfo.InvariantCulture),
            _ => ValueText ?? string.Empty
        };
    }

    public bool TryImportProfileValue(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();

        _suspendAutoSelect = true;
        try
        {
            switch (_def.EditorKind)
            {
                case DetectorFieldEditorKind.Bool:
                {
                    if (bool.TryParse(text, out var b))
                    {
                        BoolValue = b;
                        return true;
                    }
                    if (TryParseFlexibleInt(text, out var n))
                    {
                        BoolValue = n != 0;
                        return true;
                    }
                    return false;
                }
                case DetectorFieldEditorKind.Enum:
                {
                    if (TryParseFlexibleInt(text, out var n))
                    {
                        EnsureEnumOptionExists(n);
                        SelectedOptionValue = n;
                        ValueText = n.ToString(CultureInfo.InvariantCulture);
                        return true;
                    }
                    return false;
                }
                default:
                {
                    ValueText = text;
                    return true;
                }
            }
        }
        finally
        {
            _suspendAutoSelect = false;
        }
    }

    private string ResolveEnumLabel(int value)
    {
        var option = Options.FirstOrDefault(x => x.Value == value);
        if (option == null)
            return value.ToString(CultureInfo.InvariantCulture);

        var label = option.Label ?? string.Empty;
        if (label.StartsWith($"{option.Value} ", StringComparison.Ordinal) ||
            label.StartsWith($"{option.Value}-", StringComparison.Ordinal))
            return label;

        return $"{option.Value} - {label}";
    }

    private void EnsureEnumOptionExists(int value)
    {
        if (_def.EditorKind != DetectorFieldEditorKind.Enum)
            return;
        if (Options.Any(x => x.Value == value))
            return;

        var label = _def.CustomEnumValueLabelFactory?.Invoke(value)
            ?? $"{value} (custom)";
        var option = new DetectorFieldOption(value, label);

        var inserted = false;
        for (var i = 0; i < Options.Count; i++)
        {
            if (value < Options[i].Value)
            {
                Options.Insert(i, option);
                inserted = true;
                break;
            }
        }

        if (!inserted)
            Options.Add(option);
    }

    private static int ReadIntLe(ReadOnlySpan<byte> bytes)
    {
        var value = 0;
        for (var i = 0; i < bytes.Length; i++)
            value |= bytes[i] << (8 * i);
        return value;
    }

    private static void WriteIntLe(int value, int length, byte[] target, int offset)
    {
        for (var i = 0; i < length; i++)
            target[offset + i] = (byte)((value >> (8 * i)) & 0xFF);
    }

    private static void FillMask(byte[] target, int offset, int length, byte mask)
    {
        for (var i = 0; i < length; i++)
            target[offset + i] = mask;
    }

    private static string NormalizeHex(string? input)
        => new string((input ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private static bool TryParseFlexibleInt(string? input, out int value)
    {
        var s = (input ?? string.Empty).Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}

internal static class DetectorSettingsFieldCatalog
{
    public const int UserConfigLength = 160;
    public const int WiredPushButtonsLength = 21;
    public const int BlePushButtonsLength = 93;
    public const int DaliPushButtonsLength = 101;
    public const int DaliDeviceCommonParamLength = 7;

    public static IReadOnlyList<DetectorFieldDefinition> UserConfigFields { get; } = BuildUserConfigFields();
    public static IReadOnlyList<DetectorFieldDefinition> WiredPushButtonsFields { get; } = BuildWiredPushButtonsFields();
    public static IReadOnlyList<DetectorFieldDefinition> BlePushButtonsFields { get; } = BuildBlePushButtonsFields();
    public static IReadOnlyList<DetectorFieldDefinition> DaliPushButtonsFields { get; } = BuildDaliPushButtonsFields();
    public static IReadOnlyList<DetectorFieldDefinition> DaliDeviceCommonParamFields { get; } = BuildDaliDeviceCommonParamFields();

    private static IReadOnlyList<DetectorFieldDefinition> BuildUserConfigFields()
    {
        var list = new List<DetectorFieldDefinition>();
        const string durationTypeHint = "Duration type: valid range 15..65535. Common values: 15, 30, 120, 300, 600, 900, 1200, 1500, 1800, 2100, 2400, 2700, 3000, 3300, 3600. Special: 65533=pulse(1s/20s), 65534=pulse(1s/60s), 65535=disable timer.";
        const string luxTypeHint = "Lux type common values: 20, 50, 75, 100, 150, 200, 300, 400, 500, 750, 1000, 1500. Special: 65535=disable Lux (infinite).";

        AddNumber(list, "user.cfg_version", "General", "CFG_VERSION", 0, 1, 0, 255, "Version of user-config structure.");
        AddEnum(list, "user.cfg_system_configstate", "General", "CFG_SYSTEM_CONFIGSTATE", 1, BuildConfigStateOptions(), "Factory/user state.");
        AddNumber(list, "user.cfg_system_network_id", "General", "CFG_SYSTEM_NETWORK_ID", 2, 1, 0, 253, "BLE network ID.");
        AddEnumBitField(
            list,
            "user.cfg_system_common_func0.internal_relay_mode",
            DetectorSettingsSectionKind.UserConfig,
            "General",
            "CFG_INT_RELAY_MODE",
            3,
            0x07,
            0,
            BuildInternalRelayModeOptions(),
            "Internal relay mode (CFG_SYSTEM_COMMON_FUNC0 bits 0..2).");

        AddBool(list, "user.cfg_system_common_func1.wireless_neigh_slave", DetectorSettingsSectionKind.UserConfig, "General", "CFG_WIRELESS_NEIGH_SLAVE", 4, 0x80, "Bit 7.");
        AddBool(list, "user.cfg_system_common_func1.wireless_link_enabled", DetectorSettingsSectionKind.UserConfig, "General", "CFG_WIRELESS_LINK_ENABLED", 4, 0x40, "Bit 6.");
        AddBool(list, "user.cfg_system_common_func1.wireless_slave_enabled", DetectorSettingsSectionKind.UserConfig, "General", "CFG_WIRELESS_SLAVE_ENABLED", 4, 0x20, "Bit 5.");
        AddBool(list, "user.cfg_system_common_func1.eco_enabled", DetectorSettingsSectionKind.UserConfig, "General", "CFG_ECO_ENABLED", 4, 0x10, "Bit 4.");
        AddBool(list, "user.cfg_system_common_func1.wireless_pb_enabled", DetectorSettingsSectionKind.UserConfig, "General", "CFG_WIRELESS_PB_ENABLED", 4, 0x08, "Bit 3.");
        AddBool(list, "user.cfg_system_common_func1.walktest_active", DetectorSettingsSectionKind.UserConfig, "General", "CFG_WALKTEST_ACTIVE", 4, 0x04, "Bit 2.");
        AddBool(list, "user.cfg_system_common_func1.ygm_indication", DetectorSettingsSectionKind.UserConfig, "General", "CFG_YGM_INDICATION", 4, 0x02, "Bit 1.");
        AddBool(list, "user.cfg_system_common_func1.walktest_multicolor_leds", DetectorSettingsSectionKind.UserConfig, "General", "CFG_WALKTEST_MULTI_COLOR_LEDS", 4, 0x01, "Bit 0.");

        AddEnumBitField(
            list,
            "user.cfg_system_dali_func2.tunable_white_mode",
            DetectorSettingsSectionKind.UserConfig,
            "General",
            "CFG_TW_DYNAMIC",
            5,
            0xC0,
            6,
            BuildTunableWhiteModeOptions(),
            "Tunable white mode (CFG_SYSTEM_DALI_FUNC2 bits 7..6).");
        AddBool(list, "user.cfg_system_dali_func2.user_dynamic_setpoint_active", DetectorSettingsSectionKind.UserConfig, "General", "CFG_USR_DYN_SP_ACTIVE", 5, 0x20, "Bit 5.");
        AddEnumBitField(
            list,
            "user.cfg_system_dali_func2.dali_configured_mode",
            DetectorSettingsSectionKind.UserConfig,
            "General",
            "CFG_DALI_CONFIGURED",
            5,
            0x08,
            3,
            BuildDaliConfiguredOptions(),
            "DALI mode (CFG_SYSTEM_DALI_FUNC2 bit 3).");
        AddBool(list, "user.cfg_system_dali_func2.burnin_active", DetectorSettingsSectionKind.UserConfig, "General", "CFG_BURNIN_ACTIVE", 5, 0x04, "Bit 2.");
        AddBool(list, "user.cfg_system_dali_func2.ext_hvac_8hour_active", DetectorSettingsSectionKind.UserConfig, "General", "CFG_EXT_HVAC_8HOUR_ACTIVE", 5, 0x02, "Bit 1.");
        AddBool(list, "user.cfg_system_dali_func2.auto_add_1device", DetectorSettingsSectionKind.UserConfig, "General", "CFG_AUTO_ADD_1DEVICE", 5, 0x01, "Bit 0.");
        AddNumber(list, "user.cfg_system_reserved1", "General", "CFG_SYSTEM_RESERVED1", 6, 1, 0, 255, "Reserved.");
        AddNumber(list, "user.cfg_system_reserved2", "General", "CFG_SYSTEM_RESERVED2", 7, 1, 0, 255, "Reserved.");
        AddEnum(list, "user.cfg_v_pira", "General", "CFG_V_PIRA", 8, BuildPirSensitivityOptions(), "PIR sensitivity.");
        AddEnum(list, "user.cfg_v_pirb", "General", "CFG_V_PIRB", 9, BuildPirSensitivityOptions(), "PIR sensitivity.");
        AddEnum(list, "user.cfg_v_pirc", "General", "CFG_V_PIRC", 10, BuildPirSensitivityOptions(), "PIR sensitivity.");
        AddNumber(list, "user.cfg_t_sp", "General", "CFG_T_SP", 11, 2, 150, 10000, "Short press time (ms).");
        AddNumber(list, "user.cfg_t_lp", "General", "CFG_T_LP", 13, 2, 500, 17000, "Long press time (ms).");
        AddNumber(list, "user.cfg_t_vlp", "General", "CFG_T_VLP", 15, 2, 10000, 30000, "Very long press time (ms).");
        AddNumber(list, "user.cfg_t_lp_fail", "General", "CFG_T_LP_FAIL", 17, 2, 30001, 65535, "Long press fail time (ms).");
        AddEnum(
            list,
            "user.cfg_t_hvaccon_delay",
            "General",
            "CFG_T_HVACCON_DELAY",
            19,
            BuildDelayOptions(),
            $"HVAC on delay (s). {durationTypeHint}",
            CustomDelayOptionLabel,
            minOverride: 120,
            maxOverride: 7200,
            length: 2);
        AddEnum(
            list,
            "user.cfg_t_hvacoff_delay",
            "General",
            "CFG_T_HVACOFF_DELAY",
            21,
            BuildDelayOptions(),
            $"HVAC off delay (s). {durationTypeHint}",
            CustomDelayOptionLabel,
            minOverride: 0,
            maxOverride: 7200,
            length: 2);
        AddEnum(
            list,
            "user.cfg_t_stdby_min_delay",
            "General",
            "CFG_T_STDBY_MIN_DELAY",
            23,
            BuildDelayOptions(),
            $"Standby-min delay (s). {durationTypeHint}",
            CustomDelayOptionLabel,
            minOverride: 0,
            maxOverride: 7200,
            length: 2);

        for (var zone = 1; zone <= 5; zone++)
        {
            var baseOffset = 25 + (zone - 1) * 27;
            var group = $"Zone {zone}";
            var prefix = $"user.zone{zone}";

            AddBool(list, $"{prefix}.cfg_zone_common_func1.auto_on", DetectorSettingsSectionKind.UserConfig, group, "CFG_AUTO_ON", baseOffset + 0, 0x40, "CFG_ZONE_COMMON_FUNC1 bit 6.");
            AddBool(list, $"{prefix}.cfg_zone_common_func1.manual_on", DetectorSettingsSectionKind.UserConfig, group, "CFG_MANUAL_ON", baseOffset + 0, 0x20, "CFG_ZONE_COMMON_FUNC1 bit 5.");
            AddBool(list, $"{prefix}.cfg_zone_common_func1.manual_off", DetectorSettingsSectionKind.UserConfig, group, "CFG_MANUAL_OFF", baseOffset + 0, 0x10, "CFG_ZONE_COMMON_FUNC1 bit 4.");
            AddBool(list, $"{prefix}.cfg_zone_common_func1.eight_hour_active", DetectorSettingsSectionKind.UserConfig, group, "CFG_8HOUR_ACTIVE", baseOffset + 0, 0x04, "CFG_ZONE_COMMON_FUNC1 bit 2.");
            AddBool(list, $"{prefix}.cfg_zone_common_func1.overexp_active", DetectorSettingsSectionKind.UserConfig, group, "CFG_OVEREXP_ACTIVE", baseOffset + 0, 0x02, "CFG_ZONE_COMMON_FUNC1 bit 1.");
            AddBool(list, $"{prefix}.cfg_zone_common_func1.dlc_active", DetectorSettingsSectionKind.UserConfig, group, "CFG_DLC_ACTIVE", baseOffset + 0, 0x01, "CFG_ZONE_COMMON_FUNC1 bit 0.");
            AddEnum(
                list,
                $"{prefix}.cfg_zone_t_offpir_delay",
                group,
                "CFG_ZONE_T_OFFPIR_DELAY",
                baseOffset + 3,
                BuildDelayOptions(),
                $"Off PIR delay (s). {durationTypeHint}",
                CustomDelayOptionLabel,
                minOverride: 1,
                maxOverride: 65535,
                length: 2);
            AddEnum(
                list,
                $"{prefix}.cfg_zone_t_presence_delay",
                group,
                "CFG_ZONE_T_PRESENCE_DELAY",
                baseOffset + 5,
                BuildDelayOptions(),
                $"Presence delay (s). {durationTypeHint}",
                CustomDelayOptionLabel,
                minOverride: 1,
                maxOverride: 65535,
                length: 2);
            AddEnum(
                list,
                $"{prefix}.cfg_zone_t_non_presence_delay",
                group,
                "CFG_ZONE_T_NON_PRESENCE_DELAY",
                baseOffset + 7,
                BuildDelayOptions(),
                $"Non-presence delay (s). {durationTypeHint}",
                CustomDelayOptionLabel,
                minOverride: 1,
                maxOverride: 65535,
                length: 2);
            AddEnum(
                list,
                $"{prefix}.cfg_zone_t_orientation_delay",
                group,
                "CFG_ZONE_T_ORIENTATION_DELAY",
                baseOffset + 9,
                BuildDelayOptions(),
                $"Orientation delay (s). {durationTypeHint}",
                CustomDelayOptionLabel,
                minOverride: 0,
                maxOverride: 65535,
                length: 2);
            AddEnum(
                list,
                $"{prefix}.cfg_zone_t_on_delay",
                group,
                "CFG_ZONE_T_ON_DELAY",
                baseOffset + 11,
                BuildDelayOptions(),
                $"On delay (s). {durationTypeHint}",
                CustomDelayOptionLabel,
                minOverride: 0,
                maxOverride: 3600,
                length: 2);
            AddEnum(list, $"{prefix}.cfg_zone_type", group, "CFG_ZONE_TYPE", baseOffset + 13, BuildZoneTypeOptions(), "Zone type.");
            AddNumber(list, $"{prefix}.cfg_zone_reserved", group, "CFG_ZONE_RESERVED", baseOffset + 14, 1, 0, 255, "Reserved.");
            AddNumber(list, $"{prefix}.cfg_zone_deskdaylightfactorpct", group, "CFG_ZONE_DESKDAYLIGHTFACTORPCT", baseOffset + 15, 2, 0, 1000, "Desk daylight factor (%).");
            AddNumber(list, $"{prefix}.cfg_zone_deskmaxartificiallight", group, "CFG_ZONE_DESKMAXARTIFICIALLIGHT", baseOffset + 17, 2, 1, 2000, "Desk max artificial light (lux).");
            AddEnum(
                list,
                $"{prefix}.cfg_zone_v_setpoint",
                group,
                "CFG_ZONE_V_SETPOINT",
                baseOffset + 19,
                BuildLuxSetpointOptions(),
                $"Lux setpoint. {luxTypeHint}",
                CustomLuxOptionLabel,
                minOverride: 0,
                maxOverride: 65535,
                length: 2);
            AddNumber(list, $"{prefix}.cfg_zone_daylight", group, "CFG_ZONE_DAYLIGHT", baseOffset + 21, 2, 0, 65535, $"Daylight (lux). {luxTypeHint}");
            AddNumber(list, $"{prefix}.cfg_zone_presence_level", group, "CFG_ZONE_PRESENCE_LEVEL", baseOffset + 23, 1, 0, 100, "Presence level (%).");
            AddNumber(list, $"{prefix}.cfg_zone_non_presence_level", group, "CFG_ZONE_NON_PRESENCE_LEVEL", baseOffset + 24, 1, 0, 100, "Non-presence level (%).");
            AddNumber(list, $"{prefix}.cfg_zone_orientation_level", group, "CFG_ZONE_ORIENTATION_LEVEL", baseOffset + 25, 1, 0, 100, "Orientation level (%).");
            AddNumber(list, $"{prefix}.cfg_zone_turnon_level", group, "CFG_ZONE_TURNON_LEVEL", baseOffset + 26, 1, 1, 100, "Turn-on level (%).");
        }

        return list;
    }

    private static IReadOnlyList<DetectorFieldDefinition> BuildWiredPushButtonsFields()
    {
        var list = new List<DetectorFieldDefinition>();
        AddNumber(list, "wired.version", DetectorSettingsSectionKind.WiredPushButtons, "General", "Version", 0, 1, 0, 255, "Wired push-button list version.");

        for (var button = 1; button <= 4; button++)
        {
            var group = $"Wired PB {button}";
            var baseOffset = 1 + (button - 1) * 5;
            AddPbStructureFields(list, $"wired.pb{button}", DetectorSettingsSectionKind.WiredPushButtons, group, baseOffset);
        }

        return list;
    }

    private static IReadOnlyList<DetectorFieldDefinition> BuildBlePushButtonsFields()
    {
        var list = new List<DetectorFieldDefinition>();
        AddNumber(list, "ble.version", DetectorSettingsSectionKind.BlePushButtons, "General", "Version", 0, 1, 0, 255, "BLE push-button list version.");

        for (var button = 1; button <= 4; button++)
        {
            var group = $"BLE PB {button}";
            var entryOffset = 1 + (button - 1) * 23;
            AddHex(list, $"ble.pb{button}.mac_last3", group, "Button MAC (last 3 bytes)", entryOffset, 3, "Format: AABBCC.");
            AddPbStructureFields(list, $"ble.pb{button}.a0", DetectorSettingsSectionKind.BlePushButtons, $"{group} A0", entryOffset + 3);
            AddPbStructureFields(list, $"ble.pb{button}.a1", DetectorSettingsSectionKind.BlePushButtons, $"{group} A1", entryOffset + 8);
            AddPbStructureFields(list, $"ble.pb{button}.b0", DetectorSettingsSectionKind.BlePushButtons, $"{group} B0", entryOffset + 13);
            AddPbStructureFields(list, $"ble.pb{button}.b1", DetectorSettingsSectionKind.BlePushButtons, $"{group} B1", entryOffset + 18);
        }

        return list;
    }

    private static IReadOnlyList<DetectorFieldDefinition> BuildDaliPushButtonsFields()
    {
        var list = new List<DetectorFieldDefinition>();
        AddNumber(list, "dali.version", DetectorSettingsSectionKind.DaliPushButtons, "General", "Version", 0, 1, 0, 255, "DALI push-button list version.");

        for (var button = 1; button <= 4; button++)
        {
            var group = $"DALI PB {button}";
            var entryOffset = 1 + (button - 1) * 25;

            AddPbStructureFields(list, $"dali.pb{button}.a0", DetectorSettingsSectionKind.DaliPushButtons, $"{group} A0", entryOffset + 0);
            AddNumber(list, $"dali.pb{button}.instance0", DetectorSettingsSectionKind.DaliPushButtons, group, "Instance (0)", entryOffset + 5, 1, 0, 15, "Range 0..15.");
            AddPbStructureFields(list, $"dali.pb{button}.a1", DetectorSettingsSectionKind.DaliPushButtons, $"{group} A1", entryOffset + 6);
            AddNumber(list, $"dali.pb{button}.instance1", DetectorSettingsSectionKind.DaliPushButtons, group, "Instance (3)", entryOffset + 11, 1, 0, 15, "Range 0..15.");
            AddPbStructureFields(list, $"dali.pb{button}.b0", DetectorSettingsSectionKind.DaliPushButtons, $"{group} B0", entryOffset + 12);
            AddNumber(list, $"dali.pb{button}.instance2", DetectorSettingsSectionKind.DaliPushButtons, group, "Instance (6)", entryOffset + 17, 1, 0, 15, "Range 0..15.");
            AddPbStructureFields(list, $"dali.pb{button}.b1", DetectorSettingsSectionKind.DaliPushButtons, $"{group} B1", entryOffset + 18);
            AddNumber(list, $"dali.pb{button}.instance3", DetectorSettingsSectionKind.DaliPushButtons, group, "Instance (7)", entryOffset + 23, 1, 0, 15, "Range 0..15.");
            AddNumber(list, $"dali.pb{button}.short_address", DetectorSettingsSectionKind.DaliPushButtons, group, "Short Address", entryOffset + 24, 1, 0, 63, "Range 0..63.");
        }

        return list;
    }

    private static IReadOnlyList<DetectorFieldDefinition> BuildDaliDeviceCommonParamFields()
    {
        var list = new List<DetectorFieldDefinition>();
        const string groupLevels = "Levels";
        const string groupFade = "Fade";

        AddEnum(
            list,
            "dali.common.max_level",
            DetectorSettingsSectionKind.DaliDeviceCommonParam,
            groupLevels,
            "DaliSetDevicesMaxLevel",
            0,
            BuildDaliArcLevelOptions(includeMask: false),
            "Range 0..254. Human-readable output uses the standard DALI logarithmic dimming curve.",
            customEnumValueLabelFactory: value => FormatDaliArcLevelOption(value, includeMask: false));
        AddEnum(
            list,
            "dali.common.min_level",
            DetectorSettingsSectionKind.DaliDeviceCommonParam,
            groupLevels,
            "DaliSetDevicesMinLevel",
            1,
            BuildDaliArcLevelOptions(includeMask: false),
            "Range 0..254. Human-readable output uses the standard DALI logarithmic dimming curve.",
            customEnumValueLabelFactory: value => FormatDaliArcLevelOption(value, includeMask: false));
        AddEnum(
            list,
            "dali.common.power_on_level",
            DetectorSettingsSectionKind.DaliDeviceCommonParam,
            groupLevels,
            "DaliSetDevicesPowerOnLevel",
            2,
            BuildDaliArcLevelOptions(includeMask: true),
            "Range 0..255 (255 = MASK/no change). Human-readable output uses the standard DALI logarithmic dimming curve.",
            customEnumValueLabelFactory: value => FormatDaliArcLevelOption(value, includeMask: true));
        AddEnum(
            list,
            "dali.common.sys_fail_level",
            DetectorSettingsSectionKind.DaliDeviceCommonParam,
            groupLevels,
            "DaliSetDevicesSysFailLevel",
            3,
            BuildDaliArcLevelOptions(includeMask: true),
            "Range 0..255 (255 = MASK/no change). Human-readable output uses the standard DALI logarithmic dimming curve.",
            customEnumValueLabelFactory: value => FormatDaliArcLevelOption(value, includeMask: true));
        AddEnum(
            list,
            "dali.common.fade_time",
            DetectorSettingsSectionKind.DaliDeviceCommonParam,
            groupFade,
            "DaliSetDevicesFadeTime",
            4,
            BuildDaliFadeTimeOptions(),
            "Range 0..15. 0 means no fade time.",
            customEnumValueLabelFactory: FormatDaliFadeTimeOption);
        AddEnum(
            list,
            "dali.common.fade_rate",
            DetectorSettingsSectionKind.DaliDeviceCommonParam,
            groupFade,
            "DaliSetDevicesFadeRate",
            5,
            BuildDaliFadeRateOptions(),
            "Range 0..15. 0 means use Extended Fade Time.",
            customEnumValueLabelFactory: FormatDaliFadeRateOption);
        AddEnum(
            list,
            "dali.common.extended_fade_time",
            DetectorSettingsSectionKind.DaliDeviceCommonParam,
            groupFade,
            "DaliSetDevicesExtendedFadeTime",
            6,
            BuildDaliExtendedFadeTimeOptions(),
            "Range 0..79, encoded as 0YYYAAAA (DALI extended fade time).",
            customEnumValueLabelFactory: FormatDaliExtendedFadeTimeOption);

        return list;
    }

    private static void AddPbStructureFields(
        ICollection<DetectorFieldDefinition> list,
        string keyPrefix,
        DetectorSettingsSectionKind section,
        string group,
        int offset)
    {
        AddBool(list, $"{keyPrefix}.is_switch", section, group, "Button Type: Switch", offset, 0x01, "0=PushButton, 1=Switch.");
        AddBool(list, $"{keyPrefix}.dest_zone1", section, group, "Destination Zone/Channel 1", offset, 0x02, "Config Type bit 1.");
        AddBool(list, $"{keyPrefix}.dest_zone2", section, group, "Destination Zone/Channel 2", offset, 0x04, "Config Type bit 2.");
        AddBool(list, $"{keyPrefix}.dest_zone3", section, group, "Destination Zone 3", offset, 0x08, "Config Type bit 3.");
        AddBool(list, $"{keyPrefix}.dest_zone4", section, group, "Destination Zone 4", offset, 0x10, "Config Type bit 4.");
        AddBool(list, $"{keyPrefix}.dest_zone5", section, group, "Destination Zone 5 / Internal Relay", offset, 0x20, "Config Type bit 5.");
        AddBool(list, $"{keyPrefix}.dest_muz", section, group, "Destination MUZ", offset, 0x40, "Config Type bit 6.");
        AddEnum(list, $"{keyPrefix}.function1", section, group, "Function1", offset + 1, BuildPushButtonFunctionOptions(), "Push button function (section 4.2.3).");
        AddEnum(list, $"{keyPrefix}.function2", section, group, "Function2", offset + 2, BuildPushButtonFunctionOptions(), "Push button function (section 4.2.3).");
        AddNumber(list, $"{keyPrefix}.param1", section, group, "Param1 (Function1)", offset + 3, 1, 0, 255, "Parameter for Function1. Note: for ONLY_ON / ON_OFF / ONLY_OFF, value 1 means return to auto before command.");
        AddNumber(list, $"{keyPrefix}.param2", section, group, "Param2 (Function2)", offset + 4, 1, 0, 255, "Parameter for Function2.");
    }

    private static void AddNumber(
        ICollection<DetectorFieldDefinition> list,
        string key,
        string group,
        string label,
        int offset,
        int length,
        int min,
        int max,
        string description)
    {
        AddNumber(list, key, DetectorSettingsSectionKind.UserConfig, group, label, offset, length, min, max, description);
    }

    private static void AddNumber(
        ICollection<DetectorFieldDefinition> list,
        string key,
        DetectorSettingsSectionKind section,
        string group,
        string label,
        int offset,
        int length,
        int min,
        int max,
        string description)
    {
        list.Add(new DetectorFieldDefinition
        {
            Key = key,
            Section = section,
            Group = group,
            Label = label,
            Description = description,
            Offset = offset,
            Length = length,
            EditorKind = DetectorFieldEditorKind.Number,
            Min = min,
            Max = max
        });
    }

    private static void AddEnum(
        ICollection<DetectorFieldDefinition> list,
        string key,
        string group,
        string label,
        int offset,
        IReadOnlyList<DetectorFieldOption> options,
        string description,
        Func<int, string>? customEnumValueLabelFactory = null,
        int? minOverride = null,
        int? maxOverride = null,
        int length = 1)
    {
        AddEnum(
            list,
            key,
            DetectorSettingsSectionKind.UserConfig,
            group,
            label,
            offset,
            options,
            description,
            customEnumValueLabelFactory,
            minOverride,
            maxOverride,
            length);
    }

    private static void AddEnum(
        ICollection<DetectorFieldDefinition> list,
        string key,
        DetectorSettingsSectionKind section,
        string group,
        string label,
        int offset,
        IReadOnlyList<DetectorFieldOption> options,
        string description,
        Func<int, string>? customEnumValueLabelFactory = null,
        int? minOverride = null,
        int? maxOverride = null,
        int length = 1)
    {
        AddEnumBitField(
            list,
            key,
            section,
            group,
            label,
            offset,
            0xFF,
            0,
            options,
            description,
            customEnumValueLabelFactory,
            minOverride,
            maxOverride,
            length);
    }

    private static void AddEnumBitField(
        ICollection<DetectorFieldDefinition> list,
        string key,
        DetectorSettingsSectionKind section,
        string group,
        string label,
        int offset,
        int bitMask,
        int bitShift,
        IReadOnlyList<DetectorFieldOption> options,
        string description,
        Func<int, string>? customEnumValueLabelFactory = null,
        int? minOverride = null,
        int? maxOverride = null,
        int length = 1)
    {
        list.Add(new DetectorFieldDefinition
        {
            Key = key,
            Section = section,
            Group = group,
            Label = label,
            Description = description,
            Offset = offset,
            Length = Math.Max(1, length),
            EditorKind = DetectorFieldEditorKind.Enum,
            BitMask = bitMask,
            BitShift = bitShift,
            Min = minOverride ?? (options.Count == 0 ? 0 : options.Min(x => x.Value)),
            Max = maxOverride ?? (options.Count == 0 ? 255 : options.Max(x => x.Value)),
            Options = options,
            CustomEnumValueLabelFactory = customEnumValueLabelFactory
        });
    }

    private static void AddBool(
        ICollection<DetectorFieldDefinition> list,
        string key,
        DetectorSettingsSectionKind section,
        string group,
        string label,
        int offset,
        int bitMask,
        string description)
    {
        list.Add(new DetectorFieldDefinition
        {
            Key = key,
            Section = section,
            Group = group,
            Label = label,
            Description = description,
            Offset = offset,
            Length = 1,
            EditorKind = DetectorFieldEditorKind.Bool,
            BitMask = bitMask,
            Min = 0,
            Max = 1
        });
    }

    private static void AddHex(
        ICollection<DetectorFieldDefinition> list,
        string key,
        string group,
        string label,
        int offset,
        int length,
        string description)
    {
        list.Add(new DetectorFieldDefinition
        {
            Key = key,
            Section = DetectorSettingsSectionKind.BlePushButtons,
            Group = group,
            Label = label,
            Description = description,
            Offset = offset,
            Length = length,
            EditorKind = DetectorFieldEditorKind.Hex
        });
    }

    private static IReadOnlyList<DetectorFieldOption> BuildConfigStateOptions()
        => new[]
        {
            new DetectorFieldOption(0, "OOTB"),
            new DetectorFieldOption(1, "FS_DK"),
            new DetectorFieldOption(2, "FS_SV"),
            new DetectorFieldOption(3, "FS_NO"),
            new DetectorFieldOption(4, "FS_DE"),
            new DetectorFieldOption(5, "FS_BE"),
            new DetectorFieldOption(255, "USER")
        };

    private static IReadOnlyList<DetectorFieldOption> BuildInternalRelayModeOptions()
        => new[]
        {
            new DetectorFieldOption(0, "Not used"),
            new DetectorFieldOption(1, "Lighting"),
            new DetectorFieldOption(2, "HVAC"),
            new DetectorFieldOption(3, "Reserved (3)"),
            new DetectorFieldOption(4, "STDBY MIN"),
            new DetectorFieldOption(5, "Reserved (5)"),
            new DetectorFieldOption(6, "Reserved (6)"),
            new DetectorFieldOption(7, "Reserved (7)")
        };

    private static IReadOnlyList<DetectorFieldOption> BuildPirSensitivityOptions()
        => new[]
        {
            new DetectorFieldOption(0, "OFF"),
            new DetectorFieldOption(1, "MIN"),
            new DetectorFieldOption(2, "LOW"),
            new DetectorFieldOption(3, "HIGH"),
            new DetectorFieldOption(4, "MAX")
        };

    private static IReadOnlyList<DetectorFieldOption> BuildTunableWhiteModeOptions()
        => new[]
        {
            new DetectorFieldOption(0, "OOTB (default Kelvin)"),
            new DetectorFieldOption(1, "Tunable White Preset"),
            new DetectorFieldOption(2, "Tunable White Dynamic"),
            new DetectorFieldOption(3, "Dynamic + Preset")
        };

    private static IReadOnlyList<DetectorFieldOption> BuildDaliConfiguredOptions()
        => new[]
        {
            new DetectorFieldOption(0, "Broadcast"),
            new DetectorFieldOption(1, "Addressable")
        };

    private static IReadOnlyList<DetectorFieldOption> BuildZoneTypeOptions()
        => new[]
        {
            new DetectorFieldOption(0, "Disabled"),
            new DetectorFieldOption(1, "DLZ"),
            new DetectorFieldOption(2, "SEZ"),
            new DetectorFieldOption(3, "MUZ"),
            new DetectorFieldOption(4, "HVAC")
        };

    private static IReadOnlyList<DetectorFieldOption> BuildDaliArcLevelOptions(bool includeMask)
    {
        var max = includeMask ? 255 : 254;
        var list = new List<DetectorFieldOption>(max + 1);
        for (var value = 0; value <= max; value++)
            list.Add(new DetectorFieldOption(value, FormatDaliArcLevelOption(value, includeMask)));
        return list;
    }

    private static string FormatDaliArcLevelOption(int value, bool includeMask)
    {
        if (value == 255)
            return includeMask ? "255 - MASK (no change)" : "255 - Out of range";
        if (value <= 0)
            return "0 - OFF (0%)";
        if (value > 254)
            return $"{value} - Out of range";

        var percent = DaliArcLevelToPercent(value);
        return $"{value} - {FormatPercent(percent)}";
    }

    private static double DaliArcLevelToPercent(int arcLevel)
    {
        if (arcLevel <= 0)
            return 0;
        if (arcLevel >= 254)
            return 100;

        return 100d * Math.Pow(10d, 3d * (arcLevel - 254d) / 253d);
    }

    private static string FormatPercent(double value)
    {
        if (value <= 0)
            return "0%";
        if (value >= 100)
            return "100%";
        if (value >= 10)
            return value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        if (value >= 1)
            return value.ToString("0.00", CultureInfo.InvariantCulture) + "%";
        return value.ToString("0.000", CultureInfo.InvariantCulture) + "%";
    }

    private static IReadOnlyList<DetectorFieldOption> BuildDaliFadeTimeOptions()
    {
        var list = new List<DetectorFieldOption>(16);
        for (var value = 0; value <= 15; value++)
            list.Add(new DetectorFieldOption(value, FormatDaliFadeTimeOption(value)));
        return list;
    }

    private static string FormatDaliFadeTimeOption(int value)
    {
        if (value <= 0)
            return "0 - No fade time";
        if (value > 15)
            return $"{value} - Out of range";

        var seconds = 0.5d * Math.Pow(2d, value / 2d);
        return $"{value} - {FormatDuration(seconds)}";
    }

    private static IReadOnlyList<DetectorFieldOption> BuildDaliFadeRateOptions()
    {
        var list = new List<DetectorFieldOption>(16);
        for (var value = 0; value <= 15; value++)
            list.Add(new DetectorFieldOption(value, FormatDaliFadeRateOption(value)));
        return list;
    }

    private static string FormatDaliFadeRateOption(int value)
    {
        if (value <= 0)
            return "0 - Use Extended Fade Time";
        if (value > 15)
            return $"{value} - Out of range";

        // IEC 62386 nominal fade-rate sequence: each step is divided by sqrt(2).
        var stepsPerSecond = 357.796d / Math.Pow(Math.Sqrt(2d), value - 1d);
        var fullScaleSeconds = 254d / stepsPerSecond;
        return $"{value} - {stepsPerSecond.ToString("0.###", CultureInfo.InvariantCulture)} steps/s ({FormatDuration(fullScaleSeconds)} for 0->254)";
    }

    private static IReadOnlyList<DetectorFieldOption> BuildDaliExtendedFadeTimeOptions()
    {
        var list = new List<DetectorFieldOption>(80);
        for (var value = 0; value <= 79; value++)
            list.Add(new DetectorFieldOption(value, FormatDaliExtendedFadeTimeOption(value)));
        return list;
    }

    private static string FormatDaliExtendedFadeTimeOption(int value)
    {
        if (value < 0 || value > 79)
            return $"{value} - Out of range";

        var multiplierCode = (value >> 4) & 0x07;
        var baseIndex = value & 0x0F;
        var baseValue = baseIndex + 1;

        if (multiplierCode == 0)
            return $"{value} - 0 s (no fade)";

        var unitSeconds = multiplierCode switch
        {
            1 => 0.1d,
            2 => 1d,
            3 => 10d,
            4 => 60d,
            _ => 0d
        };

        if (unitSeconds <= 0)
            return $"{value} - Reserved";

        var totalSeconds = baseValue * unitSeconds;
        var unitLabel = multiplierCode switch
        {
            1 => "100 ms",
            2 => "1 s",
            3 => "10 s",
            4 => "1 min",
            _ => string.Empty
        };

        return $"{value} - base {baseValue} x {unitLabel} = {FormatDuration(totalSeconds)}";
    }

    private static string FormatDuration(double totalSeconds)
    {
        if (totalSeconds < 1d)
            return (totalSeconds * 1000d).ToString("0", CultureInfo.InvariantCulture) + " ms";
        if (totalSeconds < 60d)
            return totalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";

        var minutes = Math.Floor(totalSeconds / 60d);
        var seconds = totalSeconds - (minutes * 60d);
        if (seconds < 0.0001d)
            return minutes.ToString("0", CultureInfo.InvariantCulture) + " min";

        return minutes.ToString("0", CultureInfo.InvariantCulture)
               + " min "
               + seconds.ToString("0.###", CultureInfo.InvariantCulture)
               + " s";
    }

    private static IReadOnlyList<DetectorFieldOption> BuildDelayOptions()
        => new[]
        {
            new DetectorFieldOption(15, "15 sec"),
            new DetectorFieldOption(30, "30 sec"),
            new DetectorFieldOption(120, "2 min"),
            new DetectorFieldOption(300, "5 min"),
            new DetectorFieldOption(600, "10 min"),
            new DetectorFieldOption(900, "15 min"),
            new DetectorFieldOption(1200, "20 min"),
            new DetectorFieldOption(1500, "25 min"),
            new DetectorFieldOption(1800, "30 min"),
            new DetectorFieldOption(2100, "35 min"),
            new DetectorFieldOption(2400, "40 min"),
            new DetectorFieldOption(2700, "45 min"),
            new DetectorFieldOption(3000, "50 min"),
            new DetectorFieldOption(3300, "55 min"),
            new DetectorFieldOption(3600, "60 min"),
            new DetectorFieldOption(65533, "Pulse: 1s on / 20s off"),
            new DetectorFieldOption(65534, "Pulse: 1s on / 60s off"),
            new DetectorFieldOption(65535, "Disable timer")
        };

    private static IReadOnlyList<DetectorFieldOption> BuildLuxSetpointOptions()
        => new[]
        {
            new DetectorFieldOption(20, "20 lux"),
            new DetectorFieldOption(50, "50 lux"),
            new DetectorFieldOption(75, "75 lux"),
            new DetectorFieldOption(100, "100 lux"),
            new DetectorFieldOption(150, "150 lux"),
            new DetectorFieldOption(200, "200 lux"),
            new DetectorFieldOption(300, "300 lux"),
            new DetectorFieldOption(400, "400 lux"),
            new DetectorFieldOption(500, "500 lux"),
            new DetectorFieldOption(750, "750 lux"),
            new DetectorFieldOption(1000, "1000 lux"),
            new DetectorFieldOption(1500, "1500 lux")
        };

    private static string CustomDelayOptionLabel(int seconds)
    {
        if (seconds == 65533)
            return "Pulse: 1s on / 20s off";
        if (seconds == 65534)
            return "Pulse: 1s on / 60s off";
        if (seconds == 65535)
            return "Disable timer";
        if (seconds <= 0)
            return $"{seconds} sec";
        if (seconds % 60 == 0)
            return $"{seconds / 60} min ({seconds} sec)";
        return $"{seconds} sec";
    }

    private static string CustomLuxOptionLabel(int lux)
        => $"{lux} lux";

    private static IReadOnlyList<DetectorFieldOption> BuildPushButtonFunctionOptions()
    {
        var labels = new Dictionary<int, string>
        {
            [0] = "NO_FUNCTION - No function assigned",
            [1] = "ONLY_ON - Turn the light On",
            [2] = "ON_OFF - Turn the light On or Off",
            [3] = "ONLY_OFF - Turn the light Off",
            [4] = "RESERVED",
            [5] = "RESERVED",
            [6] = "TW_WARMER - Tunable white dim warmer",
            [7] = "TW_COOLER - Tunable white dim cooler",
            [8] = "CALL_SCENE - Call a scene",
            [9] = "FDOOR_OPEN - Folding door open indication",
            [10] = "FDOOR_CLOSED - Folding door closed indication",
            [11] = "TW1 - Tunable White preset 1",
            [12] = "TW2 - Tunable White preset 2",
            [13] = "TW3 - Tunable White preset 3",
            [14] = "TW4 - Tunable White preset 4",
            [15] = "RESERVED",
            [16] = "RESERVED",
            [17] = "RESERVED",
            [18] = "RESERVED",
            [19] = "AUTO - Back to automatic mode",
            [20] = "RESERVED",
            [21] = "RESERVED",
            [22] = "RESERVED",
            [23] = "RESERVED",
            [24] = "2LEVEL_ACTIVE - Switch to 2-level control",
            [25] = "DLC_ACTIVE - Switch to DLC control",
            [26] = "RESERVED",
            [27] = "LEVEL - For DLC",
            [28] = "RESERVED"
        };

        var list = new List<DetectorFieldOption>(256);
        for (var value = 0; value <= 255; value++)
        {
            if (labels.TryGetValue(value, out var label))
                list.Add(new DetectorFieldOption(value, $"{value} - {label}"));
            else
                list.Add(new DetectorFieldOption(value, $"{value} - RESERVED"));
        }

        return list;
    }
}
