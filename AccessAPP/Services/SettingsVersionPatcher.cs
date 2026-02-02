using AccessAPP.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

public static class SettingsVersionPatcher
{
    public sealed class RulesRoot
    {
        public List<Rule> Rules { get; set; } = new();
    }

    public sealed class Rule
    {
        public int From { get; set; } // e.g. 200, 218, 234
        public RuleSettings Settings { get; set; } = new();
    }

    public sealed class RuleSettings
    {
        public UserConfigRule? UserConfig { get; set; }
        public SimpleVersionRule? Dali { get; set; }
        public SimpleVersionRule? Wired { get; set; }
        public SimpleVersionRule? Ble { get; set; }
    }

    public sealed class UserConfigRule
    {
        public int? CfgVersion { get; set; } // byte value
    }

    public sealed class SimpleVersionRule
    {
        public int? Version { get; set; } // byte value
    }

    private sealed class EffectiveVersions
    {
        public int? UserCfg;
        public int? Dali;
        public int? Wired;
        public int? Ble;
    }

    /// <summary>
    /// firmwareVersion like "2.35" -> 235, "2.18" -> 218.
    /// </summary>

public static int FirmwareToInt(string firmwareVersion)
{
    if (string.IsNullOrWhiteSpace(firmwareVersion))
        return 0;

    var s = firmwareVersion.Trim();

    // Accept formats like: "v02.34", "02.34", "2.34", "FW=v02.34", "v2.34-build7"
    // Grab first "digits.digits"
    var m = Regex.Match(s, @"(?i)(\d{1,3})\.(\d{1,3})");
    if (m.Success)
    {
        int major = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int minor = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return (major * 100) + minor;
    }

    // Fallback: accept "234" meaning 2.34
    var digits = new string(s.Where(char.IsDigit).ToArray());
    if (digits.Length >= 3 && int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asInt))
        return asInt;

    return 0;
}


        public static void ApplyRestoreVersionRules(
        DeviceSettingsSnapshot snap,
        string firmwareVersion,
        RulesRoot rulesRoot)
    {
        if (snap == null) throw new ArgumentNullException(nameof(snap));
        if (rulesRoot?.Rules == null || rulesRoot.Rules.Count == 0) return;

        int target = FirmwareToInt(firmwareVersion);
        AppLog.Info($"[RestorePatch] FirmwareVersion='{firmwareVersion}' parsedTarget={target}");
// Apply all rules with From <= target, ascending (later overrides)
        var applicable = rulesRoot.Rules
            .Where(r => r != null && r.From <= target)
            .OrderBy(r => r.From)
            .ToList();
        AppLog.Info($"[RestorePatch] Applicable rules: {string.Join(", ", applicable.Select(a => a.From))}");
if (applicable.Count == 0) return;

        var eff = new EffectiveVersions();

        foreach (var r in applicable)
        {
            var s = r.Settings;
            if (s == null) continue;

            if (s.UserConfig?.CfgVersion != null) eff.UserCfg = s.UserConfig.CfgVersion;
            if (s.Dali?.Version != null) eff.Dali = s.Dali.Version;
            if (s.Wired?.Version != null) eff.Wired = s.Wired.Version;
            if (s.Ble?.Version != null) eff.Ble = s.Ble.Version;
        }
        AppLog.Info($"[RestorePatch] Effective: UserCfg={eff.UserCfg?.ToString() ?? "null"}, Wired={eff.Wired?.ToString() ?? "null"}, Dali={eff.Dali?.ToString() ?? "null"}, Ble={eff.Ble?.ToString() ?? "null"}");
// Patch first byte (first 2 hex chars) on each payload, and log changes
        if (eff.UserCfg != null)
            snap.UserConfigHex = PatchFirstByteHexWithLog(
                fieldName: nameof(snap.UserConfigHex),
                hex: snap.UserConfigHex,
                newByte: eff.UserCfg.Value,
                firmwareVersion: firmwareVersion);

        if (eff.Wired != null)
            snap.PushButtonsHex = PatchFirstByteHexWithLog(
                fieldName: nameof(snap.PushButtonsHex),
                hex: snap.PushButtonsHex,
                newByte: eff.Wired.Value,
                firmwareVersion: firmwareVersion);

        if (eff.Dali != null)
            snap.DaliPushButtonsHex = PatchFirstByteHexWithLog(
                fieldName: nameof(snap.DaliPushButtonsHex),
                hex: snap.DaliPushButtonsHex,
                newByte: eff.Dali.Value,
                firmwareVersion: firmwareVersion);

        if (eff.Ble != null)
            snap.BlePushButtonsHex = PatchFirstByteHexWithLog(
                fieldName: nameof(snap.BlePushButtonsHex),
                hex: snap.BlePushButtonsHex,
                newByte: eff.Ble.Value,
                firmwareVersion: firmwareVersion);
    }

    private static string? PatchFirstByteHexWithLog(string fieldName, string? hex, int newByte, string firmwareVersion)
    {
        var beforeHex = (hex ?? string.Empty).Trim();

        // Needs at least 1 byte = 2 hex chars
        if (string.IsNullOrWhiteSpace(beforeHex) || beforeHex.Length < 2)
        {
            AppLog.Info($"[RestorePatch] FW {firmwareVersion}: {fieldName} not patched (empty/too short: '{beforeHex}').");
return hex;
        }

        int nb = ClampByte(newByte);
        string newByteHex = nb.ToString("X2", CultureInfo.InvariantCulture);
        string oldByteHex = beforeHex.Substring(0, 2);

        if (string.Equals(oldByteHex, newByteHex, StringComparison.OrdinalIgnoreCase))
            return beforeHex; // no change

        string afterHex = newByteHex + beforeHex.Substring(2);

        AppLog.Info($"[RestorePatch] FW {firmwareVersion}: {fieldName} first byte {oldByteHex.ToUpperInvariant()} -> {newByteHex}.");
return afterHex;
    }

    private static int ClampByte(int value)
    {
        if (value < 0) return 0;
        if (value > 255) return 255;
        return value;
    }
}
