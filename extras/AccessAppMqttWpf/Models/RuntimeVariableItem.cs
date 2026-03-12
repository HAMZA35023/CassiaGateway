using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AccessAppMqttWpf.Models;

public sealed class RuntimeVariableChoice
{
    public RuntimeVariableChoice(object? value, string display)
    {
        Value = value;
        Display = display ?? "";
    }

    public object? Value { get; }
    public string Display { get; }
    public override string ToString() => Display;
}

public enum RuntimeVariableKind
{
    Bool,
    Number,
    String,
    Unknown
}

public sealed class RuntimeVariableValue
{
    public RuntimeVariableValue(string name, RuntimeVariableKind kind, object? value)
    {
        Name = name ?? "";
        Kind = kind;
        Value = value;
    }

    public string Name { get; }
    public RuntimeVariableKind Kind { get; }
    public object? Value { get; }
}

public partial class RuntimeVariableItem : ObservableObject
{
    public RuntimeVariableItem(string name, RuntimeVariableKind kind)
    {
        Name = name ?? "";
        Kind = kind;
    }

    public string Name { get; }
    public RuntimeVariableKind Kind { get; }
    public string Description { get; set; } = "";
    public IReadOnlyList<RuntimeVariableChoice>? Choices { get; set; }
    public bool HasChoices => Choices is { Count: > 0 };

    [ObservableProperty] private bool boolValue;
    [ObservableProperty] private string? textValue;
    [ObservableProperty] private RuntimeVariableChoice? selectedChoice;

    public static RuntimeVariableItem FromValue(RuntimeVariableValue v)
    {
        var item = new RuntimeVariableItem(v.Name, v.Kind);
        switch (v.Kind)
        {
            case RuntimeVariableKind.Bool:
                item.BoolValue = v.Value is bool b && b;
                item.TextValue = item.BoolValue ? "true" : "false";
                break;
            case RuntimeVariableKind.Number:
                item.TextValue = Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? "0";
                break;
            case RuntimeVariableKind.String:
            case RuntimeVariableKind.Unknown:
                item.TextValue = v.Value?.ToString() ?? "";
                break;
        }
        return item;
    }

    public bool TryGetValue(out object? value, out string? error)
    {
        error = null;
        value = null;

        if (HasChoices)
        {
            if (SelectedChoice == null)
            {
                error = "No option selected.";
                return false;
            }
            value = SelectedChoice.Value;
            return true;
        }

        if (Kind == RuntimeVariableKind.Bool)
        {
            value = BoolValue;
            return true;
        }

        if (Kind == RuntimeVariableKind.Number)
        {
            var raw = (TextValue ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "Missing number.";
                return false;
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            {
                value = i;
                return true;
            }

            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                value = l;
                return true;
            }

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                value = d;
                return true;
            }

            error = $"Invalid number '{raw}'.";
            return false;
        }

        value = TextValue ?? "";
        return true;
    }
}
