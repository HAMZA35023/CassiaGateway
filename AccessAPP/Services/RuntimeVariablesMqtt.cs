using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace AccessAPP.Services;

/// <summary>
/// Reflection-based helper to read/write <see cref="AccessAPP.RuntimeVariables"/> by name.
/// This auto-picks up new public static fields/properties added later.
/// </summary>
public static class RuntimeVariablesMqtt
{
    private static readonly Type VarsType = typeof(AccessAPP.RuntimeVariables);

    public static Dictionary<string, object?> GetAll()
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // Fields
        foreach (var f in VarsType.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            dict[f.Name] = f.GetValue(null);
        }

        // Properties (only settable/readable)
        foreach (var p in VarsType.GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (!p.CanRead) continue;
            dict[p.Name] = p.GetValue(null);
        }

        return dict;
    }

    public static bool TrySet(string name, JsonElement value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Name is empty.";
            return false;
        }

        // Field first
        var field = VarsType.GetField(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
        if (field is not null)
        {
            if (field.IsInitOnly)
            {
                error = "Field is readonly.";
                return false;
            }

            if (TryConvert(value, field.FieldType, out var converted, out error))
            {
                field.SetValue(null, converted);
                return true;
            }

            return false;
        }

        // Then property
        var prop = VarsType.GetProperty(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
        if (prop is not null)
        {
            if (!prop.CanWrite)
            {
                error = "Property is not settable.";
                return false;
            }

            if (TryConvert(value, prop.PropertyType, out var converted, out error))
            {
                prop.SetValue(null, converted);
                return true;
            }

            return false;
        }

        error = "Unknown runtime variable.";
        return false;
    }

    private static bool TryConvert(JsonElement value, Type targetType, out object? converted, out string? error)
    {
        converted = null;
        error = null;

        // Nullable<T>
        var underlying = Nullable.GetUnderlyingType(targetType);
        var effectiveType = underlying ?? targetType;

        try
        {
            if (effectiveType == typeof(string))
            {
                converted = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
                return true;
            }

            if (effectiveType == typeof(bool))
            {
                if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                {
                    converted = value.GetBoolean();
                    return true;
                }
                if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b))
                {
                    converted = b;
                    return true;
                }

                error = "Expected boolean.";
                return false;
            }

            if (effectiveType == typeof(int))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i))
                {
                    converted = i;
                    return true;
                }
                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var isv))
                {
                    converted = isv;
                    return true;
                }

                error = "Expected int.";
                return false;
            }

            if (effectiveType == typeof(long))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l))
                {
                    converted = l;
                    return true;
                }
                if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lsv))
                {
                    converted = lsv;
                    return true;
                }

                error = "Expected long.";
                return false;
            }

            if (effectiveType == typeof(double))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d))
                {
                    converted = d;
                    return true;
                }
                if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dsv))
                {
                    converted = dsv;
                    return true;
                }

                error = "Expected double.";
                return false;
            }

            if (effectiveType == typeof(float))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var f))
                {
                    converted = f;
                    return true;
                }
                if (value.ValueKind == JsonValueKind.String && float.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fsv))
                {
                    converted = fsv;
                    return true;
                }

                error = "Expected float.";
                return false;
            }

            if (effectiveType == typeof(TimeSpan))
            {
                // Support either milliseconds (number) or TimeSpan string ("00:00:01")
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var ms))
                {
                    converted = TimeSpan.FromMilliseconds(ms);
                    return true;
                }
                if (value.ValueKind == JsonValueKind.String && TimeSpan.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var ts))
                {
                    converted = ts;
                    return true;
                }

                error = "Expected TimeSpan (string) or milliseconds (number).";
                return false;
            }

            if (effectiveType.IsEnum)
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    var s = value.GetString();
                    if (Enum.TryParse(effectiveType, s, ignoreCase: true, out var ev))
                    {
                        converted = ev;
                        return true;
                    }
                }
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var ei))
                {
                    converted = Enum.ToObject(effectiveType, ei);
                    return true;
                }

                error = "Expected enum (string or number).";
                return false;
            }

            // Fallback: try deserialize to the type
            converted = JsonSerializer.Deserialize(value.GetRawText(), effectiveType);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
