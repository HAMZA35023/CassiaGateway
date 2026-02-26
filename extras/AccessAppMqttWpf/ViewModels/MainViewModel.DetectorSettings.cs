using AccessAppMqttWpf.Models;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using AccessAppMqttWpf;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel
{
    private sealed class DetectorSettingsPendingRequest
    {
        public DetectorSettingsPendingRequest(string expectedMac)
        {
            ExpectedMac = expectedMac;
            Completion = new TaskCompletionSource<DetectorSettingsCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public string ExpectedMac { get; }
        public TaskCompletionSource<DetectorSettingsCommandResult> Completion { get; }
    }

    private readonly object _detectorSettingsLock = new();
    private readonly Dictionary<string, DetectorSettingsPendingRequest> _detectorSettingsPending = new(StringComparer.OrdinalIgnoreCase);

    internal async Task<DetectorSettingsCommandResult?> RequestDetectorSettingsAsync(
        string cassiaName,
        string macAddress,
        string detectorType,
        string pincode,
        string firmwareVersion,
        TimeSpan? timeout = null)
    {
        var payload = BuildDetectorSettingsBasePayload(macAddress, detectorType, pincode, firmwareVersion);
        return await SendDetectorSettingsCommandAsync(
            cassiaName,
            "get-detector-settings",
            payload,
            expectedMac: macAddress,
            timeout: timeout ?? TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    internal async Task<DetectorSettingsCommandResult?> ApplyDetectorSettingsAsync(
        string cassiaName,
        string macAddress,
        string detectorType,
        string pincode,
        string firmwareVersion,
        DetectorSettingsPatchModel patch,
        bool writeOnlyChanged,
        TimeSpan? timeout = null)
    {
        var normalizedPatch = patch?.CloneNormalized() ?? new DetectorSettingsPatchModel();
        if (!normalizedPatch.HasAnyValue)
            return null;

        var payload = BuildDetectorSettingsBasePayload(macAddress, detectorType, pincode, firmwareVersion);
        payload["writeOnlyChanged"] = writeOnlyChanged;
        payload["settings"] = normalizedPatch.ToJsonObject();

        return await SendDetectorSettingsCommandAsync(
            cassiaName,
            "set-detector-settings",
            payload,
            expectedMac: macAddress,
            timeout: timeout ?? TimeSpan.FromSeconds(20)).ConfigureAwait(false);
    }

    internal async Task QueueDeviceAndRequestWithDetectorSettingsAsync(
        DiscoveredDevice? device,
        DetectorSettingsPatchModel patch,
        bool? runDaliAddressAllToZone1AfterUpdateOverride = null,
        bool? runDali102TotalNewScanAfterUpdateOverride = null,
        bool? runDali103TotalNewScanAfterUpdateOverride = null)
    {
        if (device == null) return;
        if (patch == null || !patch.CloneNormalized().HasAnyValue)
        {
            await QueueDeviceAndRequestAsync(
                device,
                runDaliAddressAllToZone1AfterUpdateOverride: runDaliAddressAllToZone1AfterUpdateOverride,
                runDali102TotalNewScanAfterUpdateOverride: runDali102TotalNewScanAfterUpdateOverride,
                runDali103TotalNewScanAfterUpdateOverride: runDali103TotalNewScanAfterUpdateOverride).ConfigureAwait(false);
            return;
        }

        await QueueDeviceAndRequestAsync(
            device,
            forceUpdateOverride: null,
            detectorSettings: patch,
            runDaliAddressAllToZone1AfterUpdateOverride: runDaliAddressAllToZone1AfterUpdateOverride,
            runDali102TotalNewScanAfterUpdateOverride: runDali102TotalNewScanAfterUpdateOverride,
            runDali103TotalNewScanAfterUpdateOverride: runDali103TotalNewScanAfterUpdateOverride).ConfigureAwait(false);
    }

    [RelayCommand]
    private void OpenDetectorSettings(DiscoveredDevice? device)
    {
        if (device == null) return;
        OpenDetectorSettingsWindow(device);
    }

    [RelayCommand]
    private void OpenDetectorSettingsNewProfile()
    {
        OpenDetectorSettingsWindow(device: null);
    }

    private void OpenDetectorSettingsWindow(DiscoveredDevice? device)
    {
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var wnd = new DetectorSettingsWindow(this, device)
                {
                    Owner = Application.Current.MainWindow
                };
                wnd.Show();
                wnd.Activate();
            });
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Open detector settings failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenDetectorSettingsForSelected()
    {
        OpenDetectorSettings(SelectedDevice);
    }

    private JsonObject BuildDetectorSettingsBasePayload(
        string macAddress,
        string detectorType,
        string pincode,
        string firmwareVersion)
    {
        var payload = new JsonObject();
        var sensors = new JsonArray();
        if (!string.IsNullOrWhiteSpace(macAddress))
            sensors.Add(macAddress.Trim());
        payload["sensors"] = sensors;

        AddIfNotEmpty(payload, "detectorType", detectorType);
        AddIfNotEmpty(payload, "pincode", pincode);
        AddIfNotEmpty(payload, "firmwareVersion", firmwareVersion);
        return payload;
    }

    private async Task<DetectorSettingsCommandResult?> SendDetectorSettingsCommandAsync(
        string cassiaName,
        string command,
        JsonObject payload,
        string expectedMac,
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

        var pending = new DetectorSettingsPendingRequest((expectedMac ?? "").Trim());
        lock (_detectorSettingsLock)
        {
            _detectorSettingsPending[requestId] = pending;
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
            lock (_detectorSettingsLock)
            {
                _detectorSettingsPending.Remove(requestId);
            }
            ConnectionStatus = $"{command} failed ({cassiaName}): {ex.Message}";
            return null;
        }

        var completed = await Task.WhenAny(pending.Completion.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed == pending.Completion.Task)
            return await pending.Completion.Task.ConfigureAwait(false);

        lock (_detectorSettingsLock)
        {
            _detectorSettingsPending.Remove(requestId);
        }

        ConnectionStatus = $"{command} timed out ({cassiaName})";
        return null;
    }

    private void HandleDetectorSettingsTele(string cassia, string payload)
    {
        DetectorSettingsCommandResult? result = null;
        DetectorSettingsPendingRequest? pendingRequest = null;
        string requestId = "";

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            requestId = ReadString(root, "requestId") ?? "";
            var action = ReadString(root, "action") ?? "";
            var success = root.TryGetProperty("success", out var sEl) && sEl.ValueKind == JsonValueKind.True;
            var message = ReadString(root, "message") ?? "";

            if (!string.IsNullOrWhiteSpace(requestId))
            {
                lock (_detectorSettingsLock)
                {
                    _detectorSettingsPending.TryGetValue(requestId, out pendingRequest);
                }
            }

            JsonElement selectedResult = default;
            var hasSelected = false;

            if (root.TryGetProperty("results", out var resultsEl) && resultsEl.ValueKind == JsonValueKind.Array)
            {
                var expectedMac = (pendingRequest?.ExpectedMac ?? "").Trim();
                foreach (var item in resultsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    if (!hasSelected)
                    {
                        selectedResult = item;
                        hasSelected = true;
                    }

                    var rowMac = ReadString(item, "mac") ?? "";
                    if (!string.IsNullOrWhiteSpace(expectedMac) &&
                        expectedMac.Equals(rowMac, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedResult = item;
                        hasSelected = true;
                        break;
                    }
                }
            }

            var row = hasSelected ? selectedResult : root;
            var rowSuccess = row.TryGetProperty("success", out var rs) && rs.ValueKind == JsonValueKind.True;
            var rowMessage = ReadString(row, "message") ?? message;
            var mac = ReadString(row, "mac") ?? "";
            var detectorType = ReadString(row, "detectorType") ?? "";
            var firmwareVersion = ReadString(row, "firmwareVersion") ?? "";

            JsonNode? settings = null;
            if (row.TryGetProperty("settings", out var settingsEl))
                settings = JsonNode.Parse(settingsEl.GetRawText());
            else if (root.TryGetProperty("settings", out settingsEl))
                settings = JsonNode.Parse(settingsEl.GetRawText());

            result = new DetectorSettingsCommandResult
            {
                Success = hasSelected ? rowSuccess : success,
                Action = action,
                RequestId = requestId,
                Cassia = cassia,
                Mac = mac,
                DetectorType = detectorType,
                FirmwareVersion = firmwareVersion,
                Message = rowMessage,
                Settings = settings,
                Raw = JsonNode.Parse(root.GetRawText())
            };
        }
        catch
        {
            // ignore malformed telemetry
        }

        if (result == null)
            return;

        if (!string.IsNullOrWhiteSpace(requestId))
        {
            lock (_detectorSettingsLock)
            {
                if (_detectorSettingsPending.TryGetValue(requestId, out var pending))
                {
                    _detectorSettingsPending.Remove(requestId);
                    pending.Completion.TrySetResult(result);
                }
            }
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            var text = string.IsNullOrWhiteSpace(result.Message)
                ? (result.Success ? "Detector settings command completed." : "Detector settings command failed.")
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
}
