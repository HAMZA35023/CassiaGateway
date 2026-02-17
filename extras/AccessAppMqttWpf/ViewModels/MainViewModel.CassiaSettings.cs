using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.VisualBasic;
using AccessAppMqttWpf;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly object _cassiaSettingsLock = new();
    private readonly Dictionary<string, TaskCompletionSource<CassiaSettingsCommandResult>> _cassiaSettingsPending =
        new(StringComparer.OrdinalIgnoreCase);

    internal async Task<CassiaSettingsCommandResult?> RequestCassiaSettingsAsync(
        string cassiaName,
        string? gatewayIp,
        string? username,
        string? password,
        string? passwordEncrypted,
        TimeSpan? timeout = null)
    {
        var payload = new JsonObject();
        AddIfNotEmpty(payload, "gatewayIp", gatewayIp);
        AddIfNotEmpty(payload, "username", username);
        AddIfNotEmpty(payload, "password", password);
        AddIfNotEmpty(payload, "passwordEncrypted", passwordEncrypted);

        return await SendCassiaSettingsCommandAsync(
            cassiaName,
            "get-cassia-settings",
            payload,
            timeout ?? TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    }

    internal async Task<CassiaSettingsCommandResult?> SaveCassiaSettingsAsync(
        string cassiaName,
        JsonObject settingsPayload,
        string? gatewayIp,
        string? username,
        string? password,
        string? passwordEncrypted,
        TimeSpan? timeout = null)
    {
        var payload = new JsonObject
        {
            ["settings"] = settingsPayload?.DeepClone()
        };

        AddIfNotEmpty(payload, "gatewayIp", gatewayIp);
        AddIfNotEmpty(payload, "username", username);
        AddIfNotEmpty(payload, "password", password);
        AddIfNotEmpty(payload, "passwordEncrypted", passwordEncrypted);

        return await SendCassiaSettingsCommandAsync(
            cassiaName,
            "set-cassia-settings",
            payload,
            timeout ?? TimeSpan.FromSeconds(12)).ConfigureAwait(false);
    }

    private async Task<CassiaSettingsCommandResult?> SendCassiaSettingsCommandAsync(
        string cassiaName,
        string command,
        JsonObject payload,
        TimeSpan timeout)
    {
        if (!IsConnected)
        {
            ConnectionStatus = "Not connected";
            return null;
        }

        cassiaName = (cassiaName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassiaName))
            return null;

        var requestId = Guid.NewGuid().ToString("N");
        payload["requestId"] = requestId;

        var tcs = new TaskCompletionSource<CassiaSettingsCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_cassiaSettingsLock)
        {
            _cassiaSettingsPending[requestId] = tcs;
        }

        try
        {
            await _mqtt.PublishJsonAsync(
                BuildCmdTopic(cassiaName, command),
                payload,
                retain: false,
                qos: 1,
                ct: _appCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_cassiaSettingsLock)
            {
                _cassiaSettingsPending.Remove(requestId);
            }

            ConnectionStatus = $"{command} failed ({cassiaName}): {ex.Message}";
            return null;
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed == tcs.Task)
            return await tcs.Task.ConfigureAwait(false);

        lock (_cassiaSettingsLock)
        {
            _cassiaSettingsPending.Remove(requestId);
        }

        ConnectionStatus = $"{command} timed out ({cassiaName})";
        return null;
    }

    [RelayCommand]
    private void OpenCassiaSettings(string cassiaName)
    {
        cassiaName = (cassiaName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cassiaName))
            return;

        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var wnd = new CassiaSettingsWindow(this, cassiaName)
                {
                    Owner = Application.Current.MainWindow
                };
                wnd.Show();
                wnd.Activate();
            });
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Open Cassia settings failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenCassiaSettingsFirstOnline()
    {
        var source = CassiaGateways
            .FirstOrDefault(g => string.Equals(g.State, "online", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(g.Name))
            ?? CassiaGateways.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g.Name));

        if (source == null)
        {
            ConnectionStatus = "No Cassia gateway available.";
            return;
        }

        OpenCassiaSettings(source.Name);
    }

    private void HandleCassiaSettingsTele(string cassia, string payload)
    {
        CassiaSettingsCommandResult? result = null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            var requestId = ReadString(root, "requestId") ?? "";
            var action = ReadString(root, "action") ?? "";
            var message = ReadString(root, "message") ?? "";
            var gatewayIp = ReadString(root, "gatewayIp") ?? "";
            var csrf = ReadString(root, "csrf");
            var success = root.TryGetProperty("success", out var sEl) && sEl.ValueKind == JsonValueKind.True;

            JsonNode? settings = null;
            if (root.TryGetProperty("settings", out var settingsEl))
                settings = JsonNode.Parse(settingsEl.GetRawText());

            JsonNode? saveTemplate = null;
            if (root.TryGetProperty("savePayloadTemplate", out var templateEl))
                saveTemplate = JsonNode.Parse(templateEl.GetRawText());

            result = new CassiaSettingsCommandResult
            {
                Success = success,
                Action = action,
                RequestId = requestId,
                Cassia = cassia,
                GatewayIp = gatewayIp,
                Message = message,
                Csrf = csrf,
                Settings = settings,
                SavePayloadTemplate = saveTemplate,
                Raw = JsonNode.Parse(root.GetRawText())
            };
        }
        catch
        {
            // ignore malformed telemetry
        }

        if (result == null)
            return;

        if (!string.IsNullOrWhiteSpace(result.RequestId))
        {
            lock (_cassiaSettingsLock)
            {
                if (_cassiaSettingsPending.TryGetValue(result.RequestId, out var pending))
                {
                    _cassiaSettingsPending.Remove(result.RequestId);
                    pending.TrySetResult(result);
                }
            }
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            var text = string.IsNullOrWhiteSpace(result.Message)
                ? (result.Success ? "Cassia settings command completed." : "Cassia settings command failed.")
                : result.Message;

            ConnectionStatus = $"[{cassia}] {text}";
        });

        static string? ReadString(JsonElement obj, string propertyName)
        {
            if (!obj.TryGetProperty(propertyName, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }
    }

    private static void AddIfNotEmpty(JsonObject payload, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        payload[name] = value.Trim();
    }
}
