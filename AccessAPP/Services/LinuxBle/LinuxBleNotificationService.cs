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

    // Per-MAC semaphore so that EnsureNotifyingAsync for device A (hci0) and device B (hci1)
    // run CONCURRENTLY.  A process-wide (1,1) semaphore serialised all StartNotifyAsync calls
    // (each up to 10 s) which caused login-notification misses when both chips were active.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _subLocksByMac =
        new(StringComparer.OrdinalIgnoreCase);

    private SemaphoreSlim GetSubLock(string macAddress) =>
        _subLocksByMac.GetOrAdd(macAddress, _ => new SemaphoreSlim(1, 1));

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
                _logger.LogDebug("LinuxBLE Notify: last handler removed for {Mac} — disposing D-Bus WatchProperties synchronously", macAddress);
                DisposeNotifySubscriptionSync(macAddress, "Unsubscribe(token)");
            }
        }
    }

    public void Unsubscribe(string macAddress)
    {
        _handlers.TryRemove(macAddress, out _);
        _logger.LogDebug("LinuxBLE Notify: Unsubscribe(all) {Mac} — clearing handlers and D-Bus subscription", macAddress);

        // IMPORTANT: Dispose the D-Bus WatchProperties subscription SYNCHRONOUSLY here.
        //
        // Background: InitializeNotificationSubscription (firmware upload) calls:
        //   1. Unsubscribe(mac)      ← clears old state
        //   2. Subscribe(mac, handler) ← registers new handler, fires EnsureNotifyingAsync
        //
        // If we fire StopNotifyingAsync as a background task (fire-and-forget), there is a
        // race with EnsureNotifyingAsync:
        //   - EnsureNotifyingAsync sees _notifySubscriptions.ContainsKey(mac) = true (stale
        //     entry from a prior session, e.g. actor firmware upload) and returns early.
        //   - StopNotifyingAsync then runs, calls sub.Dispose() — killing the D-Bus
        //     WatchPropertiesAsync callback that was still being used for the new session.
        //   - Result: the FIRST firmware notification arrives (before Dispose), all subsequent
        //     notifications are silently dropped → firmware upload hangs after chunk 1.
        //
        // Fix: remove and dispose synchronously so EnsureNotifyingAsync always sees
        // ContainsKey = false and re-creates the subscription cleanly.
        //
        // StopNotifyAsync() (BlueZ CCCD disable) is intentionally skipped: on device
        // disconnect BlueZ tears down CCCD automatically; on re-subscribe EnsureNotifyingAsync
        // will call StartNotifyAsync() again.
        if (_notifySubscriptions.TryRemove(macAddress, out var sub))
        {
            _notifyCharacteristics.TryRemove(macAddress, out _);
            try { sub.Dispose(); } catch { /* ignore — subscription may already be gone */ }
            _logger.LogDebug("LinuxBLE Notify: Unsubscribe(all) {Mac} — D-Bus WatchProperties disposed synchronously", macAddress);
        }
    }

    // ── Notify management ───────────────────────────────────────────────────

    /// <summary>
    /// Ensures StartNotify + WatchProperties are active before a caller sends a write that
    /// expects a fast response notification (for example login telegrams).
    /// </summary>
    public async Task EnsureNotifyingReadyAsync(string macAddress, CancellationToken ct = default)
    {
        await EnsureNotifyingAsync(macAddress, ct).ConfigureAwait(false);
        if (await IsNotifyingActiveAsync(macAddress, ct).ConfigureAwait(false))
            return;

        _logger.LogDebug("LinuxBLE Notify: EnsureNotifyingReady {Mac} - Notifying=false after first pass; resetting and retrying", macAddress);
        DisposeNotifySubscriptionSync(macAddress, "EnsureNotifyingReady(first-pass-notifying-false)");

        await EnsureNotifyingAsync(macAddress, ct).ConfigureAwait(false);
        if (await IsNotifyingActiveAsync(macAddress, ct).ConfigureAwait(false))
            return;

        DisposeNotifySubscriptionSync(macAddress, "EnsureNotifyingReady(final-notifying-false)");
        throw new InvalidOperationException($"LinuxBLE notify pipeline not ready for {macAddress} (Notifying=false).");
    }

    private void DisposeNotifySubscriptionSync(string macAddress, string source)
    {
        if (_notifySubscriptions.TryRemove(macAddress, out var sub))
        {
            _notifyCharacteristics.TryRemove(macAddress, out _);
            try { sub.Dispose(); } catch { /* ignore */ }
            _logger.LogDebug("LinuxBLE Notify: {Source} {Mac} - D-Bus WatchProperties disposed synchronously", source, macAddress);
        }
    }

    private async Task EnsureNotifyingAsync(string macAddress, CancellationToken ct = default)
    {
        if (_notifySubscriptions.ContainsKey(macAddress))
        {
            _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — already subscribed, skipping", macAddress);
            return;
        }

        _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — acquiring per-MAC subLock", macAddress);
        var subLock = GetSubLock(macAddress);
        var lockSw = System.Diagnostics.Stopwatch.StartNew();
        await subLock.WaitAsync(ct).ConfigureAwait(false);
        lockSw.Stop();
        _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — per-MAC subLock acquired after {Ms}ms", macAddress, lockSw.ElapsedMilliseconds);
        try
        {
            if (_notifySubscriptions.ContainsKey(macAddress))
            {
                _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — already subscribed (race), releasing per-MAC lock", macAddress);
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
                    devicePath, RuntimeVariables.LINUX_BLE_SERVICES_RESOLVED_TIMEOUT_MS, 200, ct);
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
            bool notifyActive = false;
            try
            {
                // Guard against an indefinite D-Bus hang: if BlueZ is unresponsive (e.g. after
                // rapid connect/disconnect cycles during firmware upgrade), StartNotifyAsync can
                // block forever while holding _subLock.  That would prevent every subsequent
                // EnsureNotifyingAsync call from acquiring the lock, silently breaking
                // notifications for all future login attempts.  A 10-second ceiling releases the
                // lock in the worst case and lets the retry loop reconnect cleanly.
                using var startNotifyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                startNotifyCts.CancelAfter(TimeSpan.FromSeconds(10));
                await characteristic.StartNotifyAsync().WaitAsync(startNotifyCts.Token).ConfigureAwait(false);
                startSw.Stop();
                notifyActive = await WaitForNotifyingTrueAsync(characteristic, 1500, ct).ConfigureAwait(false);
                _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — StartNotify OK ({Ms}ms)", macAddress, startSw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                startSw.Stop();
                _logger.LogWarning("LinuxBLE Notify: EnsureNotifying {Mac} — StartNotify timed out after {Ms}ms (10 s limit) — proceeding without notify; will retry on next connect", macAddress, startSw.ElapsedMilliseconds);
            }
            catch (Tmds.DBus.DBusException dbex) when (dbex.ErrorName == "org.bluez.Error.InProgress")
            {
                // BlueZ still has Notifying=True from a previous session where the device
                // disconnected before StopNotifyAsync() could clear CCCD on the device side.
                // The device reset its CCCD on disconnect, so BlueZ's state is stale.
                // Cycle StopNotify→StartNotify to resync so CCCD is re-written to the device.
                startSw.Stop();
                _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — StartNotify InProgress after {Ms}ms; cycling StopNotify→StartNotify to reset stale CCCD", macAddress, startSw.ElapsedMilliseconds);
                try
                {
                    using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stopCts.CancelAfter(TimeSpan.FromSeconds(3));
                    await characteristic.StopNotifyAsync().WaitAsync(stopCts.Token).ConfigureAwait(false);
                    using var restartCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    restartCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await characteristic.StartNotifyAsync().WaitAsync(restartCts.Token).ConfigureAwait(false);
                    notifyActive = await WaitForNotifyingTrueAsync(characteristic, 1500, ct).ConfigureAwait(false);
                    _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — StopNotify→StartNotify cycle OK", macAddress);
                }
                catch (Exception cycleEx)
                {
                    _logger.LogWarning(cycleEx, "LinuxBLE Notify: EnsureNotifying {Mac} — StopNotify→StartNotify cycle failed; notifications may not arrive", macAddress);
                }
            }
            catch (Exception ex)
            {
                startSw.Stop();
                _logger.LogWarning(ex, "LinuxBLE Notify: EnsureNotifying {Mac} — StartNotify failed after {Ms}ms (may already be notifying)", macAddress, startSw.ElapsedMilliseconds);
            }

            if (!notifyActive)
            {
                try
                {
                    notifyActive = await WaitForNotifyingTrueAsync(characteristic, 1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
            }

            if (!notifyActive)
            {
                try { sub.Dispose(); } catch { /* ignore */ }
                _logger.LogWarning("LinuxBLE Notify: EnsureNotifying {Mac} - Notifying=false after StartNotify; dropping subscription so caller can retry cleanly", macAddress);
                return;
            }

            _notifySubscriptions[macAddress] = sub;
            _notifyCharacteristics[macAddress] = characteristic;
            _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — subscription registered OK", macAddress);
        }
        finally
        {
            GetSubLock(macAddress).Release();
            _logger.LogDebug("LinuxBLE Notify: EnsureNotifying {Mac} — per-MAC subLock released", macAddress);
        }
    }

    private async Task<bool> IsNotifyingActiveAsync(string macAddress, CancellationToken ct)
    {
        if (!_notifyCharacteristics.TryGetValue(macAddress, out var characteristic))
            return false;

        return await WaitForNotifyingTrueAsync(characteristic, 1000, ct).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForNotifyingTrueAsync(IGattCharacteristic1 characteristic, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(200, timeoutMs));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pollCts.CancelAfter(TimeSpan.FromSeconds(2));
                if (await characteristic.GetAsync<bool>("Notifying").WaitAsync(pollCts.Token).ConfigureAwait(false))
                    return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Keep polling until timeout; BlueZ may transiently reject reads while reconnecting.
            }

            var remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remainingMs <= 0)
                break;

            await Task.Delay(Math.Min(100, remainingMs), ct).ConfigureAwait(false);
        }

        return false;
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

