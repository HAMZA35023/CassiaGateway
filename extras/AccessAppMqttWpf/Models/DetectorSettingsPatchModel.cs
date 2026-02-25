using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AccessAppMqttWpf.Models;

public sealed class DetectorSettingsPatchModel
{
    private static readonly Regex NonHexRegex = new("[^0-9A-Fa-f]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string? UserConfigHex { get; set; }
    public string? UserConfigMaskHex { get; set; }
    public string? PushButtonsHex { get; set; }
    public string? PushButtonsMaskHex { get; set; }
    public string? DaliPushButtonsHex { get; set; }
    public string? DaliPushButtonsMaskHex { get; set; }
    public string? BlePushButtonsHex { get; set; }
    public string? BlePushButtonsMaskHex { get; set; }

    public bool HasAnyValue =>
        !string.IsNullOrWhiteSpace(UserConfigHex)
        || !string.IsNullOrWhiteSpace(PushButtonsHex)
        || !string.IsNullOrWhiteSpace(DaliPushButtonsHex)
        || !string.IsNullOrWhiteSpace(BlePushButtonsHex);

    public DetectorSettingsPatchModel CloneNormalized()
    {
        return new DetectorSettingsPatchModel
        {
            UserConfigHex = NormalizeHex(UserConfigHex),
            UserConfigMaskHex = NormalizeHex(UserConfigMaskHex),
            PushButtonsHex = NormalizeHex(PushButtonsHex),
            PushButtonsMaskHex = NormalizeHex(PushButtonsMaskHex),
            DaliPushButtonsHex = NormalizeHex(DaliPushButtonsHex),
            DaliPushButtonsMaskHex = NormalizeHex(DaliPushButtonsMaskHex),
            BlePushButtonsHex = NormalizeHex(BlePushButtonsHex),
            BlePushButtonsMaskHex = NormalizeHex(BlePushButtonsMaskHex)
        };
    }

    public JsonObject ToJsonObject()
    {
        var normalized = CloneNormalized();
        var obj = new JsonObject();

        if (!string.IsNullOrWhiteSpace(normalized.UserConfigHex))
            obj["userConfigHex"] = normalized.UserConfigHex;
        if (!string.IsNullOrWhiteSpace(normalized.UserConfigMaskHex))
            obj["userConfigMaskHex"] = normalized.UserConfigMaskHex;
        if (!string.IsNullOrWhiteSpace(normalized.PushButtonsHex))
            obj["pushButtonsHex"] = normalized.PushButtonsHex;
        if (!string.IsNullOrWhiteSpace(normalized.PushButtonsMaskHex))
            obj["pushButtonsMaskHex"] = normalized.PushButtonsMaskHex;
        if (!string.IsNullOrWhiteSpace(normalized.DaliPushButtonsHex))
            obj["daliPushButtonsHex"] = normalized.DaliPushButtonsHex;
        if (!string.IsNullOrWhiteSpace(normalized.DaliPushButtonsMaskHex))
            obj["daliPushButtonsMaskHex"] = normalized.DaliPushButtonsMaskHex;
        if (!string.IsNullOrWhiteSpace(normalized.BlePushButtonsHex))
            obj["blePushButtonsHex"] = normalized.BlePushButtonsHex;
        if (!string.IsNullOrWhiteSpace(normalized.BlePushButtonsMaskHex))
            obj["blePushButtonsMaskHex"] = normalized.BlePushButtonsMaskHex;

        return obj;
    }

    public static DetectorSettingsPatchModel FromJsonNode(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return new DetectorSettingsPatchModel();

        return new DetectorSettingsPatchModel
        {
            UserConfigHex = ReadString(obj, "userConfigHex", "UserConfigHex"),
            UserConfigMaskHex = ReadString(obj, "userConfigMaskHex", "UserConfigMaskHex"),
            PushButtonsHex = ReadString(obj, "pushButtonsHex", "PushButtonsHex"),
            PushButtonsMaskHex = ReadString(obj, "pushButtonsMaskHex", "PushButtonsMaskHex"),
            DaliPushButtonsHex = ReadString(obj, "daliPushButtonsHex", "DaliPushButtonsHex"),
            DaliPushButtonsMaskHex = ReadString(obj, "daliPushButtonsMaskHex", "DaliPushButtonsMaskHex"),
            BlePushButtonsHex = ReadString(obj, "blePushButtonsHex", "BlePushButtonsHex"),
            BlePushButtonsMaskHex = ReadString(obj, "blePushButtonsMaskHex", "BlePushButtonsMaskHex")
        }.CloneNormalized();
    }

    private static string? ReadString(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetPropertyValue(name, out var value) || value == null)
                continue;

            if (value is JsonValue v)
            {
                if (v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    return s;
                if (v.TryGetValue<int>(out var i))
                    return i.ToString();
                if (v.TryGetValue<long>(out var l))
                    return l.ToString();
            }
        }

        return null;
    }

    private static string? NormalizeHex(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var compact = NonHexRegex.Replace(input.Trim(), string.Empty).ToUpperInvariant();
        return compact.Length == 0 ? null : compact;
    }
}

public sealed class DetectorSettingsProfileModel
{
    public string Version { get; set; } = "1";
    public string? Name { get; set; }
    public string? DetectorType { get; set; }
    public string? FirmwareVersion { get; set; }
    public bool WriteOnlyChanged { get; set; } = true;

    public bool ApplyUserConfig { get; set; }
    public bool ApplyPushButtons { get; set; }
    public bool ApplyDaliPushButtons { get; set; }
    public bool ApplyBlePushButtons { get; set; }

    public DetectorSettingsPatchModel Settings { get; set; } = new();
    public List<DetectorSettingsFieldOverrideModel> FieldOverrides { get; set; } = new();

    public static DetectorSettingsProfileModel? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        return JsonSerializer.Deserialize<DetectorSettingsProfileModel>(json, opts);
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
}

public sealed class DetectorSettingsFieldOverrideModel
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
