using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace AccessAPP.Services;

/// <summary>
/// Runtime-only setter for AccessAPP.RuntimeVariables (public static fields).
/// NOTE: This does NOT persist values. RuntimeVariables always start at their coded defaults after restart.
/// </summary>
public sealed class RuntimeVariablesStore
{
    private static readonly FieldInfo[] Fields =
        typeof(RuntimeVariables).GetFields(BindingFlags.Public | BindingFlags.Static);

    public Dictionary<string, object?> GetAll()
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Fields)
            dict[f.Name] = f.GetValue(null);
        return dict;
    }

    public bool SetSingle(string name, JsonElement value, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Missing variable name.";
            return false;
        }

        var field = Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (field == null)
        {
            error = $"Unknown runtime variable '{name}'.";
            return false;
        }

        try
        {
            var converted = ConvertJsonElement(value, field.FieldType);
            if (converted == null && field.FieldType.IsValueType && Nullable.GetUnderlyingType(field.FieldType) == null)
            {
                error = $"Cannot set '{field.Name}' to null.";
                return false;
            }

            field.SetValue(null, converted);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to set '{field.Name}': {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Apply variables from a JSON object payload: { "WRITE_SLEEP_MS": 50, "FOO": true }
    /// </summary>
    public (List<string> applied, Dictionary<string, string> errors) SetFromJsonObject(JsonElement root)
    {
        var applied = new List<string>();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (root.ValueKind != JsonValueKind.Object)
        {
            errors["payload"] = "Expected a JSON object.";
            return (applied, errors);
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (SetSingle(prop.Name, prop.Value, out var err))
                applied.Add(prop.Name);
            else
                errors[prop.Name] = err ?? "Unknown error";
        }

        return (applied, errors);
    }

    private static object? ConvertJsonElement(JsonElement el, Type targetType)
    {
        // Nullable<T>
        var underlying = Nullable.GetUnderlyingType(targetType);
        if (underlying != null)
        {
            if (el.ValueKind == JsonValueKind.Null) return null;
            targetType = underlying;
        }

        if (targetType == typeof(string))
            return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();

        if (targetType == typeof(int))
            return el.ValueKind == JsonValueKind.Number ? el.GetInt32() : int.Parse(el.ToString());

        if (targetType == typeof(long))
            return el.ValueKind == JsonValueKind.Number ? el.GetInt64() : long.Parse(el.ToString());

        if (targetType == typeof(double))
            return el.ValueKind == JsonValueKind.Number ? el.GetDouble() : double.Parse(el.ToString());

        if (targetType == typeof(bool))
        {
            if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) return el.GetBoolean();
            return bool.Parse(el.ToString());
        }

        // Fallback: deserialize to target type
        return JsonSerializer.Deserialize(el.GetRawText(), targetType);
    }
}
