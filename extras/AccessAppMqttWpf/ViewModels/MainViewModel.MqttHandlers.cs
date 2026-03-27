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
        private void RequestDevicesRefresh()
        {
            if (_pendingDevicesRefresh) return;
            _pendingDevicesRefresh = true;

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _pendingDevicesRefresh = false;

                // preserve selection
                var selectedMac = SelectedDevice?.Mac;

                FilteredDevices.Refresh();

                if (!string.IsNullOrWhiteSpace(selectedMac))
                SelectedDevice = _devices.FirstOrDefault(d => d.Mac.Equals(selectedMac, StringComparison.OrdinalIgnoreCase));
            }, DispatcherPriority.Background);
        }
        void RequestQueueRefresh()
        {
            if (_pendingQueueRefresh) return;
            _pendingQueueRefresh = true;

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _pendingQueueRefresh = false;

                // preserve selection
                var selectedMac = SelectedQueueItem?.Mac;

                try
                {
                    // Refresh the collection view (do NOT call RequestQueueRefresh recursively)
                    QueueView?.Refresh();
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(selectedMac))
                SelectedQueueItem = QueueItems.FirstOrDefault(d => d.Mac.Equals(selectedMac, StringComparison.OrdinalIgnoreCase));
            }, DispatcherPriority.Background);
        }




        private void MaybeAutoRequestFirmwareManifestAfterStatus(CassiaGateway gw)
        {
            if (!IsConnected) return;
            if (!string.Equals(gw.StateLower, "online", StringComparison.OrdinalIgnoreCase)) return;

            // Only do this once per connection per gateway.
            if (_fwManifestRequestedForGw.Contains(gw.Name)) return;

            // If we already have a manifest received after this connect, don't re-request automatically.
            var needs = !gw.HasFwManifest || gw.FwManifestLastSeenUtc < _connectedAtUtc;
            if (!needs) return;

            _fwManifestRequestedForGw.Add(gw.Name);
            _ = RequestFirmwareManifestAsync(gw.Name, manual: false);
        }

        private void MaybeAutoRequestRuntimeStateAfterStatus(CassiaGateway gw)
        {
            if (!IsConnected) return;
            if (!string.Equals(gw.StateLower, "online", StringComparison.OrdinalIgnoreCase)) return;
            if (string.IsNullOrWhiteSpace(gw.Name)) return;

            // Only request once per connect per gateway.
            if (_runtimeStateRequestedForGw.Contains(gw.Name)) return;
            _runtimeStateRequestedForGw.Add(gw.Name);

            _ = RequestQueueListAsync(gw.Name);
            _ = RequestProgrammingListAsync(gw.Name);
            _ = RequestParallelProgrammersAsync(gw.Name);
        }



        private void MaybeAutoRequestDeviceListAfterStatus(CassiaGateway gw)
        {
            if (!IsConnected) return;
            if (!string.Equals(gw.StateLower, "online", StringComparison.OrdinalIgnoreCase)) return;
            if (string.IsNullOrWhiteSpace(gw.Name)) return;

            // Only request once per connection.
            if (_deviceListRequestedAfterConnect) return;

            // Wait until we have at least one status after connect.
            if (_connectedAtUtc != DateTimeOffset.MinValue && (DateTimeOffset.UtcNow - _connectedAtUtc) > TimeSpan.FromMinutes(10))
            return;

            _deviceListRequestedAfterConnect = true;
            _ = RequestDeviceListAsync("all");
        }

        private Task RequestDeviceListAsync(string target)
        => _mqtt.PublishJsonAsync(BuildCmdTopic(target, "get-device-list"), new { requestId = Guid.NewGuid().ToString("N") }, retain: false, qos: 1, ct: _appCts.Token);

        private Task ClearDeviceListAsync(string target)
        => _mqtt.PublishJsonAsync(BuildCmdTopic(target, "clear-device-list"), new { requestId = Guid.NewGuid().ToString("N") }, retain: false, qos: 1, ct: _appCts.Token);

        public Task RemoveFromQueueAsync(string target, IEnumerable<string> macAddresses)
        {
            if (string.IsNullOrWhiteSpace(target)) target = "all";
            var macs = (macAddresses ?? Array.Empty<string>()).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (macs.Length == 0) return Task.CompletedTask;

            // Backend accepts many payload shapes; we always use the object form.
            object payload = macs.Length == 1
            ? new { macAddress = macs[0] }
            : new { macAddresses = macs };

            return _mqtt.PublishJsonAsync(BuildCmdTopic(target, "remove-from-queue"), payload, retain: false, qos: 1, ct: _appCts.Token);
        }


        public async Task MoveQueueItemToCassiaAsync(QueueItem qi, string newCassia)
        {
            if (qi == null) return;
            var mac = (qi.Mac ?? "").Trim();
            if (string.IsNullOrWhiteSpace(mac)) return;
            newCassia = (newCassia ?? "").Trim();
            if (string.IsNullOrWhiteSpace(newCassia)) return;

            // Step 1: remove from pending queue on the current Cassia (best effort)
            var fromCassia = string.IsNullOrWhiteSpace(qi.Cassia) ? "all" : qi.Cassia.Trim();
            await RemoveFromQueueAsync(fromCassia, new[] { mac }).ConfigureAwait(false);

            // Step 2: queue on the new Cassia
            var model = ResolveDetectorTypeForMac(mac, (qi.DetectorType ?? "").Trim());
            var fw = (qi.FirmwareVersion ?? "").Trim();

            if (string.IsNullOrWhiteSpace(model))
            {
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    qi.Status = "Cannot move: unknown model";
                    qi.LastUpdateUtc = DateTimeOffset.UtcNow;
                    MirrorQueueToDevice(qi);
                    RequestQueueRefresh();
                }, DispatcherPriority.Background);
                try { MessageBox.Show($"Cannot move {mac} to {newCassia} because detector model (P4x) could not be resolved. The backend requires DetectorType. Please refresh discovery so the model is known, or re-add the device.", "Unknown model", MessageBoxButton.OK, MessageBoxImage.Warning); } catch { }
                return;
            }

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                qi.Cassia = newCassia;
                qi.Status = "Requested update";
                qi.Progress = 0;
                qi.Notes = "";
                qi.LastUpdateUtc = DateTimeOffset.UtcNow;
                qi.DetectorType = model; // IMPORTANT: carry model to the new Cassia
                UpdateQueueRssiForMac(mac);
                MirrorQueueToDevice(qi);
                RequestQueueRefresh();
            }, DispatcherPriority.Background);

            // Before queueing: send disconnect to /all to ensure no gateway is stuck on this device.
            try
            {
                await _mqtt.PublishJsonAsync(BuildCmdTopic("all", "disconnect"), new { sensors = new[] { mac } }, retain: false, qos: 1, ct: _appCts.Token)
                .ConfigureAwait(false);
            }
            catch { /* best-effort */ }

            await PublishStartUpdateAsync(newCassia, mac, model, fw).ConfigureAwait(false);
            await AutoAdjustParallelProgrammersAsync().ConfigureAwait(false);
        }

        private Task PublishStartUpdateAsync(string cassia, string mac, string model, string fw)
        {
            cassia = (cassia ?? "").Trim();
            model = NormalizeDetectorModel(model);
            var topic = BuildCmdTopic(cassia, DefaultCommand);
            var runDaliAddressAllToZone1AfterUpdate = false;
            var runDali102TotalNewScanAfterUpdate = RunDali102TotalNewScanAfterUpdateEnabled;
            var runDali103TotalNewScanAfterUpdate = RunDali103TotalNewScanAfterUpdateEnabled;

            if (TryResolveModelProfilePatch(
                    model,
                    out _,
                    out var profileRunDaliAddressAllToZone1,
                    out var profileRunDali102,
                    out var profileRunDali103,
                    out _))
            {
                runDaliAddressAllToZone1AfterUpdate = runDaliAddressAllToZone1AfterUpdate || profileRunDaliAddressAllToZone1;
                runDali102TotalNewScanAfterUpdate = runDali102TotalNewScanAfterUpdate || profileRunDali102;
                runDali103TotalNewScanAfterUpdate = runDali103TotalNewScanAfterUpdate || profileRunDali103;
            }

            // Keep legacy start-update shape (raw request array) for maximum backend compatibility.
            // Include routing hints inside each request so newer backends can use them.
            var payload = new[]
            {
                new
                {
                    DetectorType = model,
                    FirmwareVersion = fw,
                    MacAddress = mac,
                    Pincode = "",
                    forceUpdate = ForceUpdateEnabled,
                    runDaliAddressAllToZone1AfterUpdate,
                    runDali102TotalNewScanAfterUpdate,
                    runDali103TotalNewScanAfterUpdate
                }
            };

            return _mqtt.PublishJsonAsync(topic, payload, retain: false, qos: 1, ct: _appCts.Token);
        }

        partial void OnAutoSetWorkersByModelEnabledChanged(bool value)
        {
            if (_isInitializing) return;

            _lastAutoParallelProgrammersSent = int.MinValue;

            try
            {
                var s = _store.Load();
                _store.Save(BuildSettingsSnapshot(s));
            }
            catch
            {
                // best effort
            }
        }

        partial void OnProductionUpdateEnabledChanged(bool value)
        {
            if (_isInitializing) return;

            try
            {
                var s = _store.Load();
                _store.Save(BuildSettingsSnapshot(s));
            }
            catch
            {
                // best effort
            }

            // Only on uncheck: restore default post-upgrade runtime behavior.
            if (value) return;
            if (!IsConnected) return;

            _ = SetRuntimeForAllCassiasAsync(BuildProductionUpdateResetPayload());
        }

        public void AssignDeviceToCassia(DiscoveredDevice device, string cassia)
        {
            if (device == null) return;
            if (string.IsNullOrWhiteSpace(cassia)) return;
            device.AssignedCassia = cassia.Trim();
            RecalculateAssignmentCounts();
            RequestDevicesRefresh();
        }

        private Task RequestQueueListAsync(string cassiaName)
        => _mqtt.PublishJsonAsync(BuildCmdTopic(cassiaName, "get-queue-list"), new { }, retain: false, qos: 1, ct: _appCts.Token);

        private Task RequestProgrammingListAsync(string cassiaName)
        => _mqtt.PublishJsonAsync(BuildCmdTopic(cassiaName, "get-programming-list"), new { }, retain: false, qos: 1, ct: _appCts.Token);

        private Task RequestParallelProgrammersAsync(string cassiaName)
        => _mqtt.PublishJsonAsync(BuildCmdTopic(cassiaName, "get-parallel-programmers"), new { }, retain: false, qos: 1, ct: _appCts.Token);

        private Task SetParallelProgrammersAsync(string cassiaName, int value)
        => _mqtt.PublishJsonAsync(BuildCmdTopic(cassiaName, "set-parallel-programmers"), new { value }, retain: false, qos: 1, ct: _appCts.Token);

        internal async Task<IReadOnlyDictionary<string, RuntimeVariableValue>?> RequestRuntimeVariablesAsync(string cassiaName, TimeSpan? timeout = null)
        {
            if (!IsConnected)
            {
                ConnectionStatus = "Not connected";
                return null;
            }

            cassiaName = (cassiaName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cassiaName)) return null;

            var tcs = new TaskCompletionSource<IReadOnlyDictionary<string, RuntimeVariableValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_runtimeVarsLock)
            {
                if (_runtimeVarsPending.TryGetValue(cassiaName, out var prev))
                prev.TrySetCanceled();
                _runtimeVarsPending[cassiaName] = tcs;
            }

            try
            {
                await _mqtt.PublishJsonAsync(BuildCmdTopic(cassiaName, "get-runtime"), new { }, retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ConnectionStatus = $"get-runtime failed ({cassiaName}): {ex.Message}";
                lock (_runtimeVarsLock) { _runtimeVarsPending.Remove(cassiaName); }
                return null;
            }

            var wait = Task.Delay(timeout ?? TimeSpan.FromSeconds(5));
            var completed = await Task.WhenAny(tcs.Task, wait).ConfigureAwait(false);
            if (completed == tcs.Task)
            return await tcs.Task.ConfigureAwait(false);

            lock (_runtimeVarsLock) { _runtimeVarsPending.Remove(cassiaName); }
            if (_runtimeVarsByGw.TryGetValue(cassiaName, out var cached))
            return cached;

            ConnectionStatus = $"get-runtime timed out ({cassiaName})";
            return null;
        }



        [RelayCommand]
        private async Task ClearDeviceSettingsBackupsForCassia(string cassiaName)
        {
            if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
            cassiaName = (cassiaName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cassiaName)) return;

            try
            {
                // cmd -> clear-device-settings-backups payload {}
                await _mqtt.PublishJsonAsync(BuildCmdTopic(cassiaName, "clear-device-settings-backups"), new { }, retain: false, qos: 1, ct: _appCts.Token)
                .ConfigureAwait(false);
                ConnectionStatus = $"Sent clear-device-settings-backups to {cassiaName}";
            }
            catch (Exception ex)
            {
                ConnectionStatus = $"Clear backups failed ({cassiaName}): {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task ClearDeviceListForCassia(string cassiaName)
        {
            if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
            cassiaName = (cassiaName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cassiaName)) return;

            try
            {
                await ClearDeviceListAsync(cassiaName).ConfigureAwait(false);
                await Task.Delay(800, _appCts.Token).ConfigureAwait(false);
                await RequestDeviceListAsync(cassiaName).ConfigureAwait(false);
                ConnectionStatus = $"Requested clear-device-list + rescan on {cassiaName}";
            }
            catch (Exception ex)
            {
                ConnectionStatus = $"Clear device-list failed ({cassiaName}): {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task GetParallelProgrammersForCassia(string cassiaName)
        {
            if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
            if (string.IsNullOrWhiteSpace(cassiaName)) return;
            await RequestParallelProgrammersAsync(cassiaName).ConfigureAwait(false);
        }

        [RelayCommand]
        private async Task SetParallelProgrammersForCassia(object? cassiaGateway)
        {
            if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
            if (cassiaGateway is not CassiaGateway gw) return;
            if (string.IsNullOrWhiteSpace(gw.Name)) return;

            var value = gw.ParallelProgrammersDesired;
            if (value <= 0) return;
            await SetParallelProgrammersAsync(gw.Name, value).ConfigureAwait(false);
        }

        [RelayCommand]
        private async Task GetParallelProgrammersForAllCassias()
        {
            if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
            foreach (var gw in CassiaGateways.ToList())
            {
                if (string.IsNullOrWhiteSpace(gw?.Name)) continue;
                await RequestParallelProgrammersAsync(gw.Name).ConfigureAwait(false);
            }
        }

        [RelayCommand]
        private async Task SetParallelProgrammersForAllCassias()
        {
            if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
            var value = ParallelProgrammersAllDesired;
            if (value <= 0) return;

            foreach (var gw in CassiaGateways.ToList())
            {
                if (string.IsNullOrWhiteSpace(gw?.Name)) continue;
                await SetParallelProgrammersAsync(gw.Name, value).ConfigureAwait(false);
            }
        }

        [RelayCommand]
        private async Task SetParallelProgrammersForAllCassiasPrompt()
        {
            if (!IsConnected) { ConnectionStatus = "Not connected"; return; }

            var valueText = Interaction.InputBox(
            "Set parallel programmers for ALL Cassias:",
            "Set parallel (all)",
            ParallelProgrammersAllDesired.ToString());

            if (string.IsNullOrWhiteSpace(valueText)) return;
            if (!int.TryParse(valueText.Trim(), out var value) || value <= 0)
            {
                try { MessageBox.Show("Please enter a positive integer value.", "Set parallel (all)", MessageBoxButton.OK, MessageBoxImage.Warning); } catch { }
                return;
            }

            ParallelProgrammersAllDesired = value;
            await SetParallelProgrammersForAllCassias().ConfigureAwait(false);
        }

        [RelayCommand]
        private async Task SetParallelProgrammersForCassiaPrompt(object? cassiaGateway)
        {
            if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
            if (cassiaGateway is not CassiaGateway gw) return;
            if (string.IsNullOrWhiteSpace(gw.Name)) return;

            var valueText = Interaction.InputBox(
            $"Set parallel programmers for {gw.Name}:",
            "Set parallel",
            gw.ParallelProgrammersDesired.ToString());

            if (string.IsNullOrWhiteSpace(valueText)) return;
            if (!int.TryParse(valueText.Trim(), out var value) || value <= 0)
            {
                try { MessageBox.Show("Please enter a positive integer value.", "Set parallel", MessageBoxButton.OK, MessageBoxImage.Warning); } catch { }
                return;
            }

            gw.ParallelProgrammersDesired = value;
            await SetParallelProgrammersAsync(gw.Name, value).ConfigureAwait(false);
        }

        [RelayCommand]
        private async Task RefreshFwManifestForCassia(string cassiaName)
        {
            if (string.IsNullOrWhiteSpace(cassiaName)) return;
            await RequestFirmwareManifestAsync(cassiaName, manual: true).ConfigureAwait(false);
        }

        [RelayCommand]
        private async Task RefreshFwManifestForAllCassias()
        {
            if (!IsConnected) { ConnectionStatus = "Not connected"; return; }
            foreach (var gw in CassiaGateways.ToList())
            {
                if (string.IsNullOrWhiteSpace(gw?.Name)) continue;
                await RequestFirmwareManifestAsync(gw.Name, manual: true).ConfigureAwait(false);
            }
        }

        [RelayCommand]
        private async Task ClearDeviceSettingsBackupsForAllCassias()
        {
            if (!IsConnected) { ConnectionStatus = "Not connected"; return; }

            var confirm = false;
            try
            {
                var result = MessageBox.Show(
                "Clear device settings backups on ALL Cassias?\n\nThis cannot be undone.",
                "Clear backups (all)",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
                confirm = result == MessageBoxResult.Yes;
            }
            catch { }

            if (!confirm) return;

            foreach (var gw in CassiaGateways.ToList())
            {
                if (string.IsNullOrWhiteSpace(gw?.Name)) continue;
                await ClearDeviceSettingsBackupsForCassia(gw.Name).ConfigureAwait(false);
            }
        }

        private async Task RequestFirmwareManifestAsync(string? cassiaName, bool manual)
        {
            try
            {
                if (!IsConnected) return;

                // Reset state for a fresh run
                _fwManifestTimeoutArmed = true;
                _fwManifestTimeoutTimer.Stop();
                _fwManifestTimeoutTimer.Start();

                // Ask target gateway (preferred), plus fall back to all aggregator (if present)
                // Examples:
                //   accessapp/{net}/cmd/cassia-01/get-fw-manifest : {}
                //   accessapp/{net}/cmd/all/get-fw-manifest : {}

                if (!string.IsNullOrWhiteSpace(cassiaName))
                {
                    var perGwTopic = $"accessapp/{NetworkId}/cmd/{cassiaName}/get-fw-manifest";
                    await _mqtt.PublishJsonAsync(perGwTopic, new { }, retain: false, qos: 1).ConfigureAwait(false);
                }

                //var aggTopic = $"accessapp/{NetworkId}/cmd/all/get-fw-manifest";
                //await _mqtt.PublishJsonAsync(aggTopic, new { }, retain: false, qos: 1).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                MessageBox.Show($"Failed to request firmware manifest.\n\n{ex.Message}", "FW manifest", MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }

        private void HandleFwManifestTele(string cassia, string payload)
        {
            try
            {
                var resp = JsonSerializer.Deserialize<FirmwareManifestTele>(payload, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (resp?.FirmwareManifest == null || resp.FirmwareManifest.Count == 0)
                return;

                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var gw = CassiaGateways.FirstOrDefault(x => x.Name.Equals(cassia, StringComparison.OrdinalIgnoreCase));
                    if (gw == null)
                    {
                        gw = new CassiaGateway { Name = cassia, NetworkId = NetworkId };
                        CassiaGateways.Add(gw);
                        SortCassiaGatewaysByName();
                    }

                    gw.FwManifestLastSeenUtc = DateTimeOffset.UtcNow;
                    gw.FirmwareManifest = new Dictionary<string, string[]>(resp.FirmwareManifest, StringComparer.OrdinalIgnoreCase);

                    // Debounced validate + update dropdowns
                    _fwManifestValidateTimer.Stop();
                    _fwManifestValidateTimer.Start();
                }, DispatcherPriority.Background);
            }
            catch
            {
                // ignore malformed payloads
            }
        }


        private void HandleDeviceListTele(string cassia, string payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;

                if (!root.TryGetProperty("deviceList", out var listEl) || listEl.ValueKind != JsonValueKind.Array)
                return;

                var now = DateTimeOffset.UtcNow;

                // Extract all device data while the JsonDocument is still in scope.
                var deviceData = new List<(string Mac, int Rssi, string Name, string ProductNumber, string Family, string Type, DateTimeOffset LastSeen)>();
                foreach (var devEl in listEl.EnumerateArray())
                {
                    if (devEl.ValueKind != JsonValueKind.Object) continue;

                    var mac = devEl.TryGetProperty("macAddress", out var macEl) ? (macEl.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(mac))
                        mac = devEl.TryGetProperty("mac", out var macEl2) ? (macEl2.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(mac)) continue;
                    mac = mac.Trim();

                    int rssi = int.MinValue;
                    if (devEl.TryGetProperty("rssi", out var rssiEl))
                    {
                        if (rssiEl.ValueKind == JsonValueKind.Number) rssi = rssiEl.GetInt32();
                        else if (rssiEl.ValueKind == JsonValueKind.String && int.TryParse(rssiEl.GetString(), out var rv)) rssi = rv;
                    }

                    var detectorType   = devEl.TryGetProperty("detectorType",   out var dtEl) ? (dtEl.GetString() ?? "") : "";
                    var detectorFamily = devEl.TryGetProperty("detectorFamily", out var dfEl) ? (dfEl.GetString() ?? "") : "";
                    var productNumber  = devEl.TryGetProperty("productNumber",  out var pnEl) ? (pnEl.GetString() ?? "") : "";
                    var name           = devEl.TryGetProperty("name",           out var nEl)  ? (nEl.GetString()  ?? "") : "";
                    var lastSeenUtc    = now;
                    if (devEl.TryGetProperty("lastSeenUtc", out var lsEl) && lsEl.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(lsEl.GetString(), out var dto))
                        lastSeenUtc = dto;

                    deviceData.Add((mac, rssi, name, productNumber, detectorFamily, detectorType, lastSeenUtc));
                }

                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var (mac, rssi, name, productNumber, detectorFamily, detectorType, lastSeenUtc) in deviceData)
                    {
                        if (!_deviceByMac.TryGetValue(mac, out var d))
                        {
                            d = new DiscoveredDevice { Mac = mac };
                            WireDeviceAssignmentHooks(d);
                            _deviceByMac[mac] = d;
                            _devices.Add(d);
                        }

                        ApplyDeviceNameWithGuards(d, name);
                        d.ProductNumber = string.IsNullOrWhiteSpace(productNumber) ? d.ProductNumber : productNumber;
                        d.DetectorFamily = string.IsNullOrWhiteSpace(detectorFamily) ? d.DetectorFamily : detectorFamily;
                        d.DetectorType = string.IsNullOrWhiteSpace(detectorType) ? d.DetectorType : detectorType;

                        // SensorModel: prefer detectorType if it looks like Pxx
                        if (!string.IsNullOrWhiteSpace(detectorType) && detectorType.Trim().StartsWith("P", StringComparison.OrdinalIgnoreCase))
                            d.SensorModel = detectorType.Trim().ToUpperInvariant();
                        else if (!string.IsNullOrWhiteSpace(d.ProductNumber) && _productToModel.TryGetValue(d.ProductNumber, out var m))
                            d.SensorModel = m;

                        if (rssi != int.MinValue)
                            d.UpdateFromCassia(cassia, rssi, lastSeenUtc);
                        else
                            d.LastSeenUtc = lastSeenUtc;

                        UpdateQueueRssiForMac(mac);

                        ApplyCachedStatusToDevice(d);
                        EnsureStickyAssignment(d);
                    }

                    RecalculateAssignmentCounts();
                    RequestDevicesRefresh();
                }, DispatcherPriority.Background);
            }
            catch { }
        }

        private void HandleClearDeviceListTele(string cassia, string payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;

                var success = root.TryGetProperty("success", out var sEl) && sEl.ValueKind == JsonValueKind.True;
                var removed = root.TryGetProperty("removed", out var rEl) && rEl.ValueKind == JsonValueKind.Number ? rEl.GetInt32() : 0;
                var message = root.TryGetProperty("message", out var mEl) ? (mEl.GetString() ?? "") : "";

                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ConnectionStatus = success
                        ? $"[{cassia}] cleared device-list ({removed})"
                        : $"[{cassia}] clear-device-list failed: {message}";
                }, DispatcherPriority.Background);
            }
            catch { }
        }

        private void UpdateQueueRssiForMac(string? mac)
        {
            mac = (mac ?? "").Trim();
            if (string.IsNullOrWhiteSpace(mac)) return;

            var qi = QueueItems.FirstOrDefault(x => x.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
            if (qi == null) return;

            if (_deviceByMac.TryGetValue(mac, out var d))
            {
                qi.UpdateRssiEntries(d.CassiaRssi, qi.Cassia);
            }
        }

        private void HandleQueueRemoveTele(string cassia, string payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;

                var success = root.TryGetProperty("success", out var sEl) && sEl.ValueKind == JsonValueKind.True;
                if (!success) return;

                var requested = new List<string>();
                if (root.TryGetProperty("requested", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var x in reqEl.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String)
                    requested.Add(x.GetString() ?? "");
                }

                if (requested.Count == 0) return;

                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var macRaw in requested)
                    {
                        var mac = (macRaw ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(mac)) continue;

                        var qi = QueueItems.FirstOrDefault(q => q != null && mac.Equals((q.Mac ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                        if (qi != null)
                        QueueItems.Remove(qi);

                        if (_deviceByMac.TryGetValue(mac, out var dev))
                        {
                            dev.IsInQueue = false;
                            if (dev.ProcessProgress == 0)
                            dev.ProcessStatus = "";
                        }

                        var cs = GetOrCreateCache(mac);
                        cs.IsInQueue = false;
                    }

                    RequestQueueRefresh();
                    RequestDevicesRefresh();
                }, DispatcherPriority.Background);
            }
            catch { }
        }

        private void HandleQueueListTele(string cassia, string payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (!root.TryGetProperty("queueList", out var listEl) || listEl.ValueKind != JsonValueKind.Array)
                return;

                var now = DateTimeOffset.UtcNow;

                // Extract item data while the JsonDocument is still in scope.
                var queueData = new List<(string Mac, string DetectorType, string Fw)>();
                foreach (var item in listEl.EnumerateArray())
                {
                    var mac = item.TryGetProperty("mac", out var m) ? (m.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(mac)) continue;
                    var detectorType = item.TryGetProperty("detectorType", out var dt) ? (dt.GetString() ?? "") : "";
                    var fw = item.TryGetProperty("firmwareVersion", out var fv) ? (fv.GetString() ?? "") : "";
                    queueData.Add((mac, detectorType, fw));
                }

                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var (mac, detectorType, fw) in queueData)
                    {
                        var qi = QueueItems.FirstOrDefault(q => q.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
                        if (qi == null)
                        {
                            qi = new QueueItem
                            {
                                Mac = mac,
                                Cassia = cassia,
                                Command = DefaultCommand,
                                Status = "Queued",
                                Progress = 0,
                                FirmwareVersion = fw,
                                DetectorType = detectorType,
                                LastUpdateUtc = now
                            };
                            QueueItems.Add(qi);
                        }
                        else
                        {
                            qi.Cassia = cassia;
                            qi.Status = "Queued";
                            qi.DetectorType = string.IsNullOrWhiteSpace(qi.DetectorType) ? detectorType : qi.DetectorType;
                            if (!string.IsNullOrWhiteSpace(fw)) qi.FirmwareVersion = fw;
                            qi.LastUpdateUtc = now;
                        }

                        UpdateQueueRssiForMac(mac);
                        MirrorQueueToDevice(qi);
                    }

                    RequestQueueRefresh();
                    RequestDevicesRefresh();
                }, DispatcherPriority.Background);

            }
            catch { }
        }

        [RelayCommand]
        private async Task ResyncAsync()
        {
            if (!IsConnected)
            {
                // Still clear the UI so user starts from a clean slate.
                ClearAllUiAndState(ShouldResetSpeedHistoryForCurrentScope());
                ConnectionStatus = "Not connected";
                return;
            }

            var clearUpgradeLog = true;
            if (UpgradeLogGroups.Count > 0)
            {
                var res = MessageBox.Show(
                    "Clear the local upgrade log?\n\n" +
                    "If a device was offline during this session, you may have missed some entries.\n" +
                    "Choose 'No' to keep your local copy and merge in any new entries from gateways.",
                    "Clear upgrade log?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                clearUpgradeLog = res == MessageBoxResult.Yes;
            }

            await ResyncCoreAsync(ShouldResetSpeedHistoryForCurrentScope(), clearUi: true, clearUpgradeLog: clearUpgradeLog).ConfigureAwait(false);
        }

        /// <summary>
        /// Clears all UI collections and internal caches.
        /// </summary>
        private void ClearAllUiAndState(bool resetSpeedHistory, bool clearUpgradeLog = true)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (resetSpeedHistory)
                    _speedHistoryByGateway.Clear();
                else
                    CaptureSpeedHistorySnapshot();

                // Devices
                _devices.Clear();
                _deviceByMac.Clear();
                _cachedStatusByMac.Clear();

                // Queue / programming
                QueueItems.Clear();

                // Gateways + dropdowns
                CassiaGateways.Clear();
                CassiaNameOptions.Clear();

                if (clearUpgradeLog)
                {
                    // Upgrade log views
                    UpgradeLogLines.Clear();
                    UpgradeLogGroups.Clear();
                    _upgradeLogGroupByKey.Clear();
                    UpgradeLogText = "";
                    _upgradeLogSb.Clear();
                    UpgradeLogReceivedLines = 0;
                    UpgradeLogTotalLines = 0;
                    UpgradeLogStatus = "";
                }

                // Filters/selections that commonly keep stale selection pointers
                SelectedDevice = null;
                SelectedQueueItem = null;
                // (no SelectedCassia property in this project; gateway selections are re-initialized as data arrives)
                SelectedLogGateway = null;
                SelectedSpeedGateway = null;
            });

            // Internal trackers
            if (clearUpgradeLog)
                _latestUpgradeLogIdByMac.Clear();
            _progressByMac.Clear();
            _gwSeenMacs.Clear();
            _deviceAssignmentWired.Clear();
            _requestedUpgradeLogCassias.Clear(); // always reset so we re-request fresh log from gateways

            _fwManifestRequestedForGw.Clear();
            _runtimeStateRequestedForGw.Clear();
            _runtimeVarsByGw.Clear();
            _runtimeVarsPending.Clear();
            _deviceListRequestedForGw.Clear();
            _deviceListRequestedAfterConnect = false;
            _connectedAtUtc = DateTimeOffset.UtcNow;

            _lastFwManifestMissingHash = "";
            _fwManifestTimeoutArmed = false;
        }

        /// <summary>
        /// Clears UI/state and requests fresh snapshots the same way as on a new connect.
        /// </summary>
        private async Task ResyncCoreAsync(bool resetSpeedHistory, bool clearUi, bool clearUpgradeLog = true)
        {
            if (clearUi)
                ClearAllUiAndState(resetSpeedHistory, clearUpgradeLog);
            else if (resetSpeedHistory)
                _speedHistoryByGateway.Clear();

            // Ensure subscriptions exist for the current NetworkId.
            try
            {
                var net = (NetworkId ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(net) && !string.Equals(_lastSubscribedNetworkId, net, StringComparison.OrdinalIgnoreCase))
                {
                    await _mqtt.SubscribeAsync($"accessapp/{net}/tele/#").ConfigureAwait(false);
                    await _mqtt.SubscribeAsync($"accessapp/{net}/tele/+/upgrade-log", qos: 1).ConfigureAwait(false);
                    await _mqtt.SubscribeAsync($"accessapp/{net}/cmd/#").ConfigureAwait(false);
                    _lastSubscribedNetworkId = net;
                }
            }
            catch
            {
                // best effort
            }

            // Kick off a full device-list request immediately (backend supports target="all").
            try { _ = RequestDeviceListAsync("all"); } catch { }

            // The rest of the snapshots are auto-requested when we receive each gateway's status:
            // - FW manifest
            // - queue/programming/parallel programmers
            // - upgrade log
        }

        private void HandleProgrammingListTele(string cassia, string payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (!root.TryGetProperty("programmingList", out var listEl) || listEl.ValueKind != JsonValueKind.Array)
                return;

                var now = DateTimeOffset.UtcNow;

                // Extract item data while the JsonDocument is still in scope.
                var programmingData = new List<(string Mac, string DetectorType, string Fw)>();
                foreach (var item in listEl.EnumerateArray())
                {
                    var mac = item.TryGetProperty("mac", out var m) ? (m.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(mac)) continue;
                    var detectorType = item.TryGetProperty("detectorType", out var dt) ? (dt.GetString() ?? "") : "";
                    var fw = item.TryGetProperty("firmwareVersion", out var fv) ? (fv.GetString() ?? "") : "";
                    programmingData.Add((mac, detectorType, fw));
                }

                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var (mac, detectorType, fw) in programmingData)
                    {
                        var qi = QueueItems.FirstOrDefault(q => q.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
                        if (qi == null)
                        {
                            qi = new QueueItem
                            {
                                Mac = mac,
                                Cassia = cassia,
                                Command = DefaultCommand,
                                Status = "Programming",
                                Progress = 1,
                                FirmwareVersion = fw,
                                DetectorType = detectorType,
                                LastUpdateUtc = now
                            };
                            QueueItems.Add(qi);
                        }
                        else
                        {
                            qi.Cassia = cassia;
                            qi.Status = "Programming";
                            if (qi.Progress <= 0) qi.Progress = 1;
                            qi.DetectorType = string.IsNullOrWhiteSpace(qi.DetectorType) ? detectorType : qi.DetectorType;
                            if (!string.IsNullOrWhiteSpace(fw)) qi.FirmwareVersion = fw;
                            qi.LastUpdateUtc = now;
                        }

                        MirrorQueueToDevice(qi);
                    }
                }, DispatcherPriority.Background);
            }
            catch { }
        }

        private void HandleParallelProgrammersTele(string cassia, string payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                int value = 0;
                if (root.ValueKind == JsonValueKind.Number)
                value = root.GetInt32();
                else if (root.TryGetProperty("value", out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var vi)) value = vi;
                    else if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var vsi)) value = vsi;
                }

                if (value <= 0) return;

                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var gw = CassiaGateways.FirstOrDefault(x => x.Name.Equals(cassia, StringComparison.OrdinalIgnoreCase));
                    if (gw == null)
                    {
                        gw = new CassiaGateway { Name = cassia, NetworkId = NetworkId };
                        CassiaGateways.Add(gw);
                        SortCassiaGatewaysByName();
                        EnsureCassiaOption(cassia);
                    }

                    gw.ParallelProgrammers = value;
                    gw.ParallelProgrammersDesired = value;
                }, DispatcherPriority.Background);
            }
            catch { }
        }

        private void HandleRuntimeVariablesTele(string cassia, string payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                var varsEl = root;
                if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("variables", out var varsProp))
                {
                    varsEl = varsProp;
                }

                if (varsEl.ValueKind != JsonValueKind.Object)
                return;

                var dict = new Dictionary<string, RuntimeVariableValue>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in varsEl.EnumerateObject())
                {
                    var kind = RuntimeVariableKind.Unknown;
                    object? value = null;

                    switch (prop.Value.ValueKind)
                    {
                        case JsonValueKind.True:
                        case JsonValueKind.False:
                        kind = RuntimeVariableKind.Bool;
                        value = prop.Value.GetBoolean();
                        break;
                        case JsonValueKind.Number:
                        kind = RuntimeVariableKind.Number;
                        if (prop.Value.TryGetInt64(out var l))
                        value = l;
                        else if (prop.Value.TryGetDouble(out var d))
                        value = d;
                        break;
                        case JsonValueKind.String:
                        kind = RuntimeVariableKind.String;
                        value = prop.Value.GetString() ?? "";
                        break;
                        case JsonValueKind.Null:
                        kind = RuntimeVariableKind.String;
                        value = "";
                        break;
                        default:
                        kind = RuntimeVariableKind.Unknown;
                        value = prop.Value.GetRawText();
                        break;
                    }

                    dict[prop.Name] = new RuntimeVariableValue(prop.Name, kind, value);
                }

                lock (_runtimeVarsLock)
                {
                    _runtimeVarsByGw[cassia] = dict;
                    if (_runtimeVarsPending.TryGetValue(cassia, out var tcs))
                    {
                        _runtimeVarsPending.Remove(cassia);
                        tcs.TrySetResult(dict);
                    }
                }

                RuntimeVariablesReceived?.Invoke(cassia, dict);
            }
            catch { }
        }

}
