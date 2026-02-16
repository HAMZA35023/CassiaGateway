using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AccessAPP.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AccessAPP.Services;

/// <summary>
/// Setter for AccessAPP.RuntimeVariables (public static fields) with optional persistence.
/// If a runtime.json (or configured path) exists, it can be loaded at startup.
/// </summary>
public sealed class RuntimeVariablesStore
{
    private static readonly FieldInfo[] Fields =
        typeof(RuntimeVariables).GetFields(BindingFlags.Public | BindingFlags.Static);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DictionaryKeyPolicy = null
    };

    private readonly object _fileGate = new();

    public string FilePath { get; }

    public RuntimeVariablesStore()
    {
        FilePath = "runtime.json";
    }

    public RuntimeVariablesStore(string filePath)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath) ? "runtime.json" : filePath;
    }

    public RuntimeVariablesStore(IConfiguration cfg, IHostEnvironment env)
    {
        var path = cfg.GetValue<string>("RuntimeVariables:ConfigPath");
        if (string.IsNullOrWhiteSpace(path))
            path = "runtime.json";
        if (!Path.IsPathRooted(path))
            path = Path.Combine(env.ContentRootPath, path);

        FilePath = path;
    }

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

            // Apply side effects for specific runtime variables (e.g., logging level).
            if (string.Equals(field.Name, nameof(RuntimeVariables.LOG_MIN_LEVEL), StringComparison.OrdinalIgnoreCase))
                LoggingBootstrapper.RefreshLogLevelFromRuntime();

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
    public (List<string> applied, Dictionary<string, string> errors) SetFromJsonObject(JsonElement root, ISet<string>? ignoreNames = null)
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
            if (ignoreNames != null && ignoreNames.Contains(prop.Name))
                continue;

            if (SetSingle(prop.Name, prop.Value, out var err))
                applied.Add(prop.Name);
            else
                errors[prop.Name] = err ?? "Unknown error";
        }

        return (applied, errors);
    }

    public (List<string> applied, Dictionary<string, string> errors) LoadFromDisk(bool createIfMissing = true)
    {
        lock (_fileGate)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    if (createIfMissing)
                        SaveToDisk();
                    return (new List<string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                }

                var json = File.ReadAllText(FilePath);
                using var doc = JsonDocument.Parse(json);
                return SetFromJsonObject(doc.RootElement);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Runtime variables load failed: {ex.Message}");
                return (new List<string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["file"] = ex.Message
                });
            }
        }
    }

    public bool SaveToDisk()
    {
        lock (_fileGate)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(GetAll(), JsonOptions);
                File.WriteAllText(FilePath, json);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Runtime variables save failed: {ex.Message}");
                return false;
            }
        }
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
