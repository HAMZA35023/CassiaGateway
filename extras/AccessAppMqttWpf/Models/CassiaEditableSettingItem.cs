using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;

namespace AccessAppMqttWpf.Models;

public partial class CassiaEditableSettingItem : ObservableObject
{
    [ObservableProperty] private string valueText = "";
    [ObservableProperty] private bool isVisible = true;
    [ObservableProperty] private bool isEnabled = true;

    public CassiaEditableSettingItem(
        string path,
        string label,
        JsonNode? originalNode,
        CassiaFieldEditorKind editorKind,
        IEnumerable<CassiaSelectOption>? selectOptions = null,
        bool isReadOnly = false)
    {
        Path = path ?? "";
        Label = string.IsNullOrWhiteSpace(label) ? (Path == "" ? "(Not bound)" : Path) : label.Trim();
        OriginalNode = originalNode?.DeepClone();
        OriginalValueText = ToEditableText(OriginalNode);
        ValueText = OriginalValueText;
        EditorKind = isReadOnly ? CassiaFieldEditorKind.ReadOnly : editorKind;
        ValueType = InferValueType(OriginalNode);
        SelectOptions = new ObservableCollection<CassiaSelectOption>(
            (selectOptions ?? Array.Empty<CassiaSelectOption>())
                .Where(o => o != null && !string.IsNullOrWhiteSpace(o.Value))
                .GroupBy(o => o.Value.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()));
        AllowedValues = new ObservableCollection<string>(SelectOptions.Select(o => o.Value));
        IsReadOnly = EditorKind == CassiaFieldEditorKind.ReadOnly;
        IsEnabled = !IsReadOnly && !string.IsNullOrWhiteSpace(Path);
    }

    public string Label { get; }
    public string Path { get; }
    public CassiaFieldEditorKind EditorKind { get; }
    public string ValueType { get; }
    public string OriginalValueText { get; }
    public JsonNode? OriginalNode { get; }
    public ObservableCollection<CassiaSelectOption> SelectOptions { get; }
    public ObservableCollection<string> AllowedValues { get; }
    public bool HasAllowedValues => AllowedValues.Count > 0;
    public string AllowedValuesText => string.Join(", ", AllowedValues);
    public bool IsReadOnly { get; }
    public bool IsTextEditor => EditorKind == CassiaFieldEditorKind.Text;
    public bool IsPasswordEditor => EditorKind == CassiaFieldEditorKind.Password;
    public bool IsTextAreaEditor => EditorKind == CassiaFieldEditorKind.TextArea;
    public bool IsSelectEditor => EditorKind == CassiaFieldEditorKind.Select;
    public bool IsCheckBoxEditor => EditorKind == CassiaFieldEditorKind.CheckBox;
    public bool IsEditable => !IsReadOnly && !string.IsNullOrWhiteSpace(Path);
    public bool IsChanged => !string.Equals(ValueText, OriginalValueText, StringComparison.Ordinal);

    public bool? BoolValue
    {
        get
        {
            if (TryParseBool(ValueText, out var value))
                return value;
            return false;
        }
        set
        {
            if (!value.HasValue)
                return;
            ValueText = FormatBoolForOriginal(value.Value, OriginalNode, OriginalValueText);
        }
    }

    partial void OnValueTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(BoolValue));
    }

    private static string ToEditableText(JsonNode? node)
    {
        if (node == null) return "";

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s)) return s ?? "";
            if (value.TryGetValue<bool>(out var b)) return b ? "true" : "false";
            if (value.TryGetValue<int>(out var i)) return i.ToString(CultureInfo.InvariantCulture);
            if (value.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
            if (value.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
        }

        return node.ToJsonString();
    }

    private static string InferValueType(JsonNode? node)
    {
        if (node == null) return "null";

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out _)) return "string";
            if (value.TryGetValue<bool>(out _)) return "bool";
            if (value.TryGetValue<int>(out _)) return "int";
            if (value.TryGetValue<long>(out _)) return "long";
            if (value.TryGetValue<double>(out _)) return "double";
            return "value";
        }

        if (node is JsonObject) return "object";
        if (node is JsonArray) return "array";
        return "node";
    }

    private static bool TryParseBool(string? value, out bool parsed)
    {
        parsed = false;
        var text = (value ?? "").Trim();
        if (text.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            parsed = true;
            return true;
        }

        if (text.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            parsed = false;
            return true;
        }

        return false;
    }

    private static string FormatBoolForOriginal(bool value, JsonNode? originalNode, string originalText)
    {
        if (originalNode is JsonValue originalValue)
        {
            if (originalValue.TryGetValue<bool>(out _))
                return value ? "true" : "false";
            if (originalValue.TryGetValue<int>(out _))
                return value ? "1" : "0";
            if (originalValue.TryGetValue<long>(out _))
                return value ? "1" : "0";
            if (originalValue.TryGetValue<string>(out var s))
            {
                var style = (s ?? "").Trim();
                if (style.Equals("on", StringComparison.OrdinalIgnoreCase) || style.Equals("off", StringComparison.OrdinalIgnoreCase))
                    return value ? "on" : "off";
                if (style.Equals("yes", StringComparison.OrdinalIgnoreCase) || style.Equals("no", StringComparison.OrdinalIgnoreCase))
                    return value ? "yes" : "no";
                if (style.Equals("true", StringComparison.OrdinalIgnoreCase) || style.Equals("false", StringComparison.OrdinalIgnoreCase))
                    return value ? "true" : "false";
                if (style.Equals("1", StringComparison.OrdinalIgnoreCase) || style.Equals("0", StringComparison.OrdinalIgnoreCase))
                    return value ? "1" : "0";
            }
        }

        var raw = (originalText ?? "").Trim();
        if (raw.Equals("on", StringComparison.OrdinalIgnoreCase) || raw.Equals("off", StringComparison.OrdinalIgnoreCase))
            return value ? "on" : "off";
        if (raw.Equals("yes", StringComparison.OrdinalIgnoreCase) || raw.Equals("no", StringComparison.OrdinalIgnoreCase))
            return value ? "yes" : "no";
        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Equals("false", StringComparison.OrdinalIgnoreCase))
            return value ? "true" : "false";
        return value ? "1" : "0";
    }
}
