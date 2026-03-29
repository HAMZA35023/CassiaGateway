using AccessAppMqttWpf.Models;
using AccessAppMqttWpf.Services;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel
{
    // ── Pending read requests keyed by MAC (upper-case) ───────────────────────
    private readonly Dictionary<string, TaskCompletionSource<DaliDbTelePayload>>
        _pendingDaliDbReads = new(StringComparer.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────────
    // Commands
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the DALI DB editor for a device.
    /// Triggers a dali-db-read command and waits for the response, then opens the editor.
    /// Developer-mode only (enforced in XAML visibility binding).
    /// </summary>
    [RelayCommand]
    private async Task OpenDaliDbEditor(DiscoveredDevice? device)
    {
        if (device is null) return;
        if (!IsConnected) { ConnectionStatus = "Not connected to MQTT."; return; }

        var mac    = (device.Mac    ?? "").Trim().ToUpperInvariant();
        var cassia = (device.AssignedCassia ?? device.BestCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(mac) || string.IsNullOrWhiteSpace(cassia)) return;

        ConnectionStatus = $"Reading DALI DB for {mac}…";

        // Register a pending TCS before publishing (avoid race)
        var tcs = new TaskCompletionSource<DaliDbTelePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingDaliDbReads) { _pendingDaliDbReads[mac] = tcs; }

        try
        {
            var topic = BuildCmdTopic(cassia, "dali-db-read");
            await _mqtt.PublishJsonAsync(topic,
                new { sensor = mac },
                retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);

            // Wait up to 60 s (full DB read can take ~10-20 s over BLE)
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(-1, cts.Token)).ConfigureAwait(false);

            if (completed != tcs.Task)
            {
                ConnectionStatus = $"DALI DB read timeout for {mac}.";
                return;
            }

            var payload = await tcs.Task;
            if (payload.Status == "error")
            {
                ConnectionStatus = $"DALI DB read error: {payload.Error}";
                return;
            }

            if (payload.Db is null)
            {
                ConnectionStatus = $"DALI DB: empty response for {mac}.";
                return;
            }

            ConnectionStatus = $"DALI DB read OK for {mac}.";

            // Build observable VM from raw model and open editor on UI thread
            var vm = DaliDbMapper.ToVm(payload.Db);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var win = new DaliDbEditorWindow(this, device, vm);
                win.Owner = Application.Current.MainWindow;
                win.ShowDialog();
            });
        }
        finally
        {
            lock (_pendingDaliDbReads) { _pendingDaliDbReads.Remove(mac); }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Called by DaliDbEditorViewModel when the user saves
    // ─────────────────────────────────────────────────────────────────────────

    internal async Task SendDaliDbWriteAsync(DiscoveredDevice device, DaliDbSnapshotVm vm)
    {
        var mac    = (device.Mac    ?? "").Trim().ToUpperInvariant();
        var cassia = (device.AssignedCassia ?? device.BestCassia ?? "").Trim();
        if (string.IsNullOrWhiteSpace(mac) || string.IsNullOrWhiteSpace(cassia)) return;

        var tcs = new TaskCompletionSource<DaliDbTelePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingDaliDbReads) { _pendingDaliDbReads[mac] = tcs; }

        try
        {
            var db    = DaliDbMapper.FromVm(vm);
            var topic = BuildCmdTopic(cassia, "dali-db-write");
            await _mqtt.PublishJsonAsync(topic,
                new { sensor = mac, db },
                retain: false, qos: 1, ct: _appCts.Token).ConfigureAwait(false);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(-1, cts.Token)).ConfigureAwait(false);

            ConnectionStatus = completed == tcs.Task
                ? (await tcs.Task).Status == "write-ok"
                    ? $"DALI DB write OK for {mac}."
                    : $"DALI DB write error: {(await tcs.Task).Error}"
                : $"DALI DB write timeout for {mac}.";
        }
        finally
        {
            lock (_pendingDaliDbReads) { _pendingDaliDbReads.Remove(mac); }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Telemetry handler — called from MainViewModel.Mqtt.cs
    // ─────────────────────────────────────────────────────────────────────────

    internal void HandleDaliDbTele(string cassia, string payload)
    {
        try
        {
            var p = JsonSerializer.Deserialize<DaliDbTelePayload>(payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (p is null) return;

            var mac = (p.Mac ?? "").Trim().ToUpperInvariant();
            TaskCompletionSource<DaliDbTelePayload>? tcs;
            lock (_pendingDaliDbReads) { _pendingDaliDbReads.TryGetValue(mac, out tcs); }
            tcs?.TrySetResult(p);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[DaliDb] HandleDaliDbTele parse error: {ex.Message}");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Payload DTO for the tele/*/dali-db response
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class DaliDbTelePayload
{
    public string? Mac       { get; set; }
    public string  Status    { get; set; } = "";
    public string? RequestId { get; set; }
    public string? Error     { get; set; }

    // Full DB snapshot returned on a read response
    public AccessAPP.Models.DaliDbSnapshot? Db { get; set; }
}
