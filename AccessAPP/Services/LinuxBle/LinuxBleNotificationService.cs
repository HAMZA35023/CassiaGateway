using System.Collections.Concurrent;
using System.Net;
using AccessAPP.Services.BleAbstractions;
using Tmds.DBus;

namespace AccessAPP.Services.LinuxBle;

/// <summary>
/// IBleNotificationService implementation using BlueZ D-Bus (Linux native BLE).
///
/// For each MAC that is subscribed, the service:
///   1. Detects app vs bootloader by looking at the exposed GATT services (UUIDs).
///   2. Finds the notify characteristic by UUID.
///   2. Calls StartNotify on first subscriber.
///   3. Watches PropertiesChanged to receive Value updates and dispatches them to handlers.
///   4. Calls StopNotify when the last subscriber for a MAC unsubscribes.
///
/// The EnableNotificationAsync method is a no-op for BlueZ: writing to the CCCD is handled
/// automatically by BlueZ when StartNotify is called.
/// </summary>
public class LinuxBleNotificationService : IBleNotificationService
{
    private readonly ILogger<LinuxBleNotificationService> _logger;

    // semaphore / forcedRestartedSSE are Cassia-compat fields with no functional role here.
    public SemaphoreSlim? semaphore { get; set; } = null;
    public bool forcedRestartedSSE { get; set; } = false;

    // handler registry: mac → {token → handler}
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, EventHandler<string>>> _handlers = new();

    // D-Bus notification subscription per mac (one per mac, covers the notify characteristic)
    private readonly ConcurrentDictionary<string, IDisposable> _notifySubscriptions = new();
    private readonly ConcurrentDictionary<string, IGattCharacteristic1> _notifyCharacteristics = new();
    private readonly SemaphoreSlim _subLock = new(1, 1);

    public LinuxBleNotificationService(ILogger<LinuxBleNotificationService> logger)
    {
        _logger = logger;
    }

    // ── IBleNotificationService ──────────────────────────────────────────────

    /// <summary>No-op for BlueZ: StartNotify handles the CCCD automatically.</summary>
    public Task<bool> EnableNotificationAsync(string gatewayIpAddress, string nodeMac, bool bActor, int chip = -1)
        => Task.FromResult(true);

    public Guid Subscribe(string macAddress, EventHandler<string> handler)
    {
        var token = Guid.NewGuid();
        var map = _handlers.GetOrAdd(macAddress, _ => new ConcurrentDictionary<Guid, EventHandler<string>>());
        map[token] = handler;

        bool alreadyNotifying = _notifySubscriptions.ContainsKey(macAddress);
        _logger.LogDebug("LinuxBLE Notify: Subscribe {Mac} token={Token} handlers={Count} alreadyNotifying={Already}",
            macAddress, token.ToString()[..8], map.Count, alreadyNotifying);

        // Ensure we are listening for notifications on this device.
        _ = EnsureNotifyingAsync(macAddress);

        return token;
    }

    public void Unsubscribe(string macAddress, Guid token)
    {
        if (_handlers.TryGetValue(macAddress, out var map))
        {
            map.TryRemove(token, out _);
            _logger.LogDebug("LinuxBLE Notify: Unsubscribe(token) {Mac} token={Token} remainingHandlers={Count}",
                macAddress, token.ToString()[..8], map.Count);
            if (map.IsEmpty)
            {
                _handlers.TryRemove(macAddress, out _);
                _logger.LogDebug("LinuxBLE Notify: last handler removed for {Mac} — calling StopNotify", macAddress);
                _ = StopNotifyingAsync(macAddress);
            }
        }
    }

    public void Unsubscribe(string macAddress)
    {
        _handlers.TryRemove(macAddress, out _);
        _logger.LogDebug("LinuxBLE Notify: Unsubscribe(all) {Mac} — calling StopNotify", macAddress);
        _ = StopNotifyingAsync(macAddress);
    }

    // ── Notify management ───────────────────────────────────────────────────

    private async Task EnsureNotifyingAsync(string macAddress)
    {
        if (_notifySubscriptions.ContainsKey(macAddress))
        {
            _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — already subscribed, skipping", macAddress);
            return;
        }

        _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — acquiring _subLock", macAddress);
        var lockSw = System.Diagnostics.Stopwatch.StartNew();
        await _subLock.WaitAsync();
        lockSw.Stop();
        _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — _subLock acquired after {Ms}ms", macAddress, lockSw.ElapsedMilliseconds);
        try
        {
            if (_notifySubscriptions.ContainsKey(macAddress))
            {
                _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — already subscribed (race), releasing lock", macAddress);
                return;
            }

            var devicePath = BlueZHelpers.DevicePath(BlueZHelpers.GetDeviceAdapter(macAddress), macAddress);

            var mode = await BlueZHelpers.DetectModeByGattAsync(devicePath);
            _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — mode={Mode}", macAddress, mode);

            if (mode == BlueZHelpers.BleMode.Unknown)
            {
                // ServicesResolved is not yet true — wait for GATT re-discovery to finish
                // (common after a firmware reboot or mode switch) before choosing the UUIDs.
                _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — mode Unknown, waiting for ServicesResolved (max {Timeout}ms)", macAddress, RuntimeVariables.LINUX_BLE_SERVICES_RESOLVED_TIMEOUT_MS);
                var servicesReady = await BlueZHelpers.WaitForServicesResolvedAsync(
                    devicePath, RuntimeVariables.LINUX_BLE_SERVICES_RESOLVED_TIMEOUT_MS, 200, CancellationToken.None);
                if (servicesReady)
                    mode = await BlueZHelpers.DetectModeByGattAsync(devicePath);

                if (mode == BlueZHelpers.BleMode.Unknown)
                    _logger.LogWarning("LinuxBLE Notify: EnsureNotifying {Mac} — GATT mode still Unknown after wait — defaulting to Application", macAddress);
                else
                    _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — mode resolved to {Mode} after ServicesResolved wait", macAddress, mode);
            }

            string serviceUuid;
            string notifyUuid;
            if (mode == BlueZHelpers.BleMode.Bootloader)
            {
                serviceUuid = BlueZHelpers.BootServiceUuid;
                notifyUuid = BlueZHelpers.BootNotifyUuid;
            }
            else
            {
                serviceUuid = BlueZHelpers.AppServiceUuid;
                notifyUuid = BlueZHelpers.AppNotifyUuid;
            }

            _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — looking up notify char {Uuid} (mode={Mode})", macAddress, notifyUuid[..8], mode);
            var (characteristic, flags) = await BlueZHelpers.GetCharacteristicByUuidAsync(devicePath, serviceUuid, notifyUuid);
            if (characteristic == null)
            {
                _logger.LogWarning("LinuxBLE Notify: EnsureNotifying {Mac} — notify char {Uuid} not found (mode={Mode})", macAddress, notifyUuid, mode);
                return;
            }

            // Subscribe to PropertiesChanged before calling StartNotify so we don't miss the first event.
            _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — registering WatchProperties", macAddress);
            var sub = await characteristic.WatchPropertiesAsync(
                changes =>
                {
                    foreach (var (key, val) in changes.Changed)
                    {
                        if (key == "Value" && val is byte[] bytes)
                        {
                            var hexValue = BlueZHelpers.BytesToHex(bytes);
                            _logger.LogDebug("LinuxBLE Notify: PropertiesChanged Value for {Mac} ({Bytes} bytes)", macAddress, bytes.Length);
                            DispatchHandlers(macAddress, hexValue);
                        }
                    }
                },
                ex => _logger.LogError(ex, "LinuxBLE notification error for {Mac}", macAddress));

            _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — calling StartNotify (mode={Mode})", macAddress, mode);
            var startSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // Guard against an indefinite D-Bus hang: if BlueZ is unresponsive (e.g. after
                // rapid connect/disconnect cycles during firmware upgrade), StartNotifyAsync can
                // block forever while holding _subLock.  That would prevent every subsequent
                // EnsureNotifyingAsync call from acquiring the lock, silently breaking
                // notifications for all future login attempts.  A 10-second ceiling releases the
                // lock in the worst case and lets the retry loop reconnect cleanly.
                using var startNotifyCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await characteristic.StartNotifyAsync().WaitAsync(startNotifyCts.Token);
                startSw.Stop();
                _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — StartNotify OK ({Ms}ms)", macAddress, startSw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                startSw.Stop();
                _logger.LogWarning("LinuxBLE Notify: EnsureNotifying {Mac} — StartNotify timed out after {Ms}ms (10 s limit) — proceeding without notify; will retry on next connect", macAddress, startSw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                startSw.Stop();
                _logger.LogWarning(ex, "LinuxBLE Notify: EnsureNotifying {Mac} — StartNotify failed after {Ms}ms (may already be notifying)", macAddress, startSw.ElapsedMilliseconds);
            }

            _notifySubscriptions[macAddress] = sub;
            _notifyCharacteristics[macAddress] = characteristic;
            _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — subscription registered OK", macAddress);
        }
        finally
        {
            _subLock.Release();
            _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — _subLock released", macAddress);
        }
    }

    private async Task StopNotifyingAsync(string macAddress)
    {
        if (!_notifySubscriptions.TryRemove(macAddress, out var sub))
        {
            _logger.LogDebug("LinuxBLE Notify: StopNotify {Mac} — no active subscription, nothing to stop", macAddress);
            return;
        }

        _logger.LogDebug("LinuxBLE Notify: StopNotify {Mac} — disposing PropertiesChanged subscription", macAddress);
        sub.Dispose();

        _notifyCharacteristics.TryRemove(macAddress, out var cachedChr);

        try
        {
            // Prefer cached proxy (no enumeration). If missing, attempt UUID lookup.
            var characteristic = cachedChr;
            if (characteristic == null)
            {
                    var devicePath = BlueZHelpers.DevicePath(BlueZHelpers.GetDeviceAdapter(macAddress), macAddress);
                var mode = await BlueZHelpers.DetectModeByGattAsync(devicePath);
                var serviceUuid = mode == BlueZHelpers.BleMode.Bootloader ? BlueZHelpers.BootServiceUuid : BlueZHelpers.AppServiceUuid;
                var notifyUuid = mode == BlueZHelpers.BleMode.Bootloader ? BlueZHelpers.BootNotifyUuid : BlueZHelpers.AppNotifyUuid;
                (characteristic, _) = await BlueZHelpers.GetCharacteristicByUuidAsync(devicePath, serviceUuid, notifyUuid);
            }

            if (characteristic != null)
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await characteristic.StopNotifyAsync().WaitAsync(stopCts.Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LinuxBLE: StopNotify failed for {Mac} (device may already be disconnected)", macAddress);
        }
    }

    private void DispatchHandlers(string macAddress, string hexValue)
    {
        if (!_handlers.TryGetValue(macAddress, out var map)) return;
        foreach (var kv in map)
        {
            try { kv.Value?.Invoke(this, hexValue); }
            catch (Exception ex) { _logger.LogError(ex, "LinuxBLE notification handler error for {Mac}", macAddress); }
        }
    }
}
