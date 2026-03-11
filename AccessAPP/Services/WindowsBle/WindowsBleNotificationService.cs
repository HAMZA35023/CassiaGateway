#if WINDOWS
using System.Collections.Concurrent;
using AccessAPP.Services.BleAbstractions;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;

namespace AccessAPP.Services.WindowsBle;

/// <summary>
/// <see cref="IBleNotificationService"/> implementation using Windows.Devices.Bluetooth WinRT APIs.
///
/// For each subscribed MAC the service:
///   1. Detects app vs bootloader mode via GATT service UUIDs.
///   2. Locates the notify characteristic by UUID.
///   3. Writes the CCCD to enable notifications (<c>WriteClientCharacteristicConfigurationDescriptorAsync</c>).
///   4. Hooks <c>GattCharacteristic.ValueChanged</c> to receive data and dispatches hex strings to handlers.
///   5. Disables notifications (CCCD) and unhooks the event when the last subscriber unsubscribes.
/// </summary>
public class WindowsBleNotificationService : IBleNotificationService
{
    private readonly ILogger<WindowsBleNotificationService> _logger;

    // Cassia-compat fields with no functional role here.
    public SemaphoreSlim? semaphore { get; set; } = null;
    public bool forcedRestartedSSE { get; set; } = false;

    // Handler registry: MAC → {token → handler}
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, EventHandler<string>>> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    // Active ValueChanged subscriptions: MAC → ValueChangedEventRegistration wrapper
    private readonly ConcurrentDictionary<string, ValueChangedRegistration> _subs =
        new(StringComparer.OrdinalIgnoreCase);

    // Per-MAC lock for subscribe/unsubscribe serialisation.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _subLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private SemaphoreSlim GetSubLock(string mac) =>
        _subLocks.GetOrAdd(mac, _ => new SemaphoreSlim(1, 1));

    public WindowsBleNotificationService(ILogger<WindowsBleNotificationService> logger)
    {
        _logger = logger;
    }

    // ── IBleNotificationService ──────────────────────────────────────────────

    /// <summary>No-op: CCCD is written in <see cref="EnsureNotifyingAsync"/>.</summary>
    public Task<bool> EnableNotificationAsync(string gatewayIpAddress, string nodeMac, bool bActor, int chip = -1)
        => Task.FromResult(true);

    public Guid Subscribe(string macAddress, EventHandler<string> handler)
    {
        var token = Guid.NewGuid();
        var map = _handlers.GetOrAdd(macAddress, _ =>
            new ConcurrentDictionary<Guid, EventHandler<string>>());
        map[token] = handler;

        _logger.LogDebug("WinBLE Notify: Subscribe {Mac} token={Token} handlers={Count}",
            macAddress, token.ToString()[..8], map.Count);

        _ = EnsureNotifyingAsync(macAddress);
        return token;
    }

    public void Unsubscribe(string macAddress, Guid token)
    {
        if (_handlers.TryGetValue(macAddress, out var map))
        {
            map.TryRemove(token, out _);
            _logger.LogDebug("WinBLE Notify: Unsubscribe(token) {Mac} token={Token} remaining={Count}",
                macAddress, token.ToString()[..8], map.Count);
            if (map.IsEmpty)
            {
                _handlers.TryRemove(macAddress, out _);
                DisposeSubSync(macAddress, "Unsubscribe(token)");
            }
        }
    }

    public void Unsubscribe(string macAddress)
    {
        _handlers.TryRemove(macAddress, out _);
        _logger.LogDebug("WinBLE Notify: Unsubscribe(all) {Mac}", macAddress);
        DisposeSubSync(macAddress, "Unsubscribe(all)");
    }

    // ── Public pre-arm helper (called by connection service before login write) ─

    public async Task EnsureNotifyingReadyAsync(string macAddress, CancellationToken ct = default)
    {
        await EnsureNotifyingAsync(macAddress, ct).ConfigureAwait(false);

        if (_subs.ContainsKey(macAddress))
            return;

        _logger.LogDebug("WinBLE Notify: EnsureNotifyingReady {Mac} - not subscribed after first pass; retrying", macAddress);
        DisposeSubSync(macAddress, "EnsureNotifyingReady(retry)");
        await EnsureNotifyingAsync(macAddress, ct).ConfigureAwait(false);

        if (!_subs.ContainsKey(macAddress))
        {
            DisposeSubSync(macAddress, "EnsureNotifyingReady(final-fail)");
            throw new InvalidOperationException($"WinBLE notify pipeline not ready for {macAddress}.");
        }
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private void DisposeSubSync(string macAddress, string source)
    {
        if (_subs.TryRemove(macAddress, out var reg))
        {
            try { reg.Dispose(); } catch { /* best-effort */ }
            _logger.LogDebug("WinBLE Notify: {Source} {Mac} - ValueChanged unhooked", source, macAddress);
        }
    }

    private async Task EnsureNotifyingAsync(string macAddress, CancellationToken ct = default)
    {
        if (_subs.ContainsKey(macAddress))
        {
            _logger.LogDebug("WinBLE Notify: EnsureNotifying {Mac} — already subscribed", macAddress);
            return;
        }

        var subLock = GetSubLock(macAddress);
        await subLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_subs.ContainsKey(macAddress))
                return;

            var device = await WindowsBleHelpers.GetOrOpenDeviceAsync(macAddress, ct).ConfigureAwait(false);
            if (device == null)
            {
                _logger.LogWarning("WinBLE Notify: EnsureNotifying {Mac} — device not found", macAddress);
                return;
            }

            var mode = await WindowsBleHelpers.GetCachedModeAsync(macAddress, device, ct).ConfigureAwait(false);
            _logger.LogDebug("WinBLE Notify: EnsureNotifying {Mac} mode={Mode}", macAddress, mode);

            var serviceUuid = mode == WindowsBleHelpers.BleMode.Bootloader
                ? WindowsBleHelpers.BootServiceUuid : WindowsBleHelpers.AppServiceUuid;
            var notifyUuid = mode == WindowsBleHelpers.BleMode.Bootloader
                ? WindowsBleHelpers.BootNotifyUuid : WindowsBleHelpers.AppNotifyUuid;

            var characteristic = await WindowsBleHelpers.GetCharacteristicAsync(
                macAddress, device, serviceUuid, notifyUuid, ct).ConfigureAwait(false);

            if (characteristic == null)
            {
                _logger.LogWarning("WinBLE Notify: EnsureNotifying {Mac} — notify char not found (mode={Mode})", macAddress, mode);
                return;
            }

            // Write CCCD to enable notifications.
            var cccdResult = await characteristic
                .WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify)
                .AsTask(ct).ConfigureAwait(false);

            if (cccdResult != GattCommunicationStatus.Success)
            {
                _logger.LogWarning("WinBLE Notify: EnsureNotifying {Mac} — CCCD write failed ({Status})", macAddress, cccdResult);
                return;
            }

            // Hook ValueChanged.
            TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler =
                (sender, args) =>
                {
                    try
                    {
                        var bytes = args.CharacteristicValue.ToArray();
                        var hex = WindowsBleHelpers.BytesToHex(bytes);
                        _logger.LogDebug("WinBLE Notify: ValueChanged {Mac} ({Bytes} bytes)", macAddress, bytes.Length);
                        DispatchHandlers(macAddress, hex);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "WinBLE Notify: ValueChanged handler error for {Mac}", macAddress);
                    }
                };

            characteristic.ValueChanged += handler;
            _subs[macAddress] = new ValueChangedRegistration(characteristic, handler);

            _logger.LogDebug("WinBLE Notify: EnsureNotifying {Mac} — subscription registered OK", macAddress);
        }
        finally
        {
            subLock.Release();
        }
    }

    private void DispatchHandlers(string macAddress, string hexValue)
    {
        if (!_handlers.TryGetValue(macAddress, out var map)) return;
        foreach (var kv in map)
        {
            try { kv.Value?.Invoke(this, hexValue); }
            catch (Exception ex) { _logger.LogError(ex, "WinBLE notification handler error for {Mac}", macAddress); }
        }
    }

    // ── ValueChanged registration wrapper ────────────────────────────────────

    private sealed class ValueChangedRegistration : IDisposable
    {
        private readonly GattCharacteristic _characteristic;
        private readonly TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> _handler;
        private bool _disposed;

        public ValueChangedRegistration(
            GattCharacteristic characteristic,
            TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler)
        {
            _characteristic = characteristic;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _characteristic.ValueChanged -= _handler; } catch { /* best-effort */ }
            // Best-effort CCCD clear (device may already be disconnected).
            _ = _characteristic
                .WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None)
                .AsTask();
        }
    }
}
#endif
