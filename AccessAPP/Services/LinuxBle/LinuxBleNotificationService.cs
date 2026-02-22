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

        // Ensure we are listening for notifications on this device.
        _ = EnsureNotifyingAsync(macAddress);

        return token;
    }

    public void Unsubscribe(string macAddress, Guid token)
    {
        if (_handlers.TryGetValue(macAddress, out var map))
        {
            map.TryRemove(token, out _);
            if (map.IsEmpty)
            {
                _handlers.TryRemove(macAddress, out _);
                _ = StopNotifyingAsync(macAddress);
            }
        }
    }

    public void Unsubscribe(string macAddress)
    {
        _handlers.TryRemove(macAddress, out _);
        _ = StopNotifyingAsync(macAddress);
    }

    // ── Notify management ───────────────────────────────────────────────────

    private async Task EnsureNotifyingAsync(string macAddress)
    {
        if (_notifySubscriptions.ContainsKey(macAddress)) return;

        await _subLock.WaitAsync();
        try
        {
            if (_notifySubscriptions.ContainsKey(macAddress)) return;

            var devicePath = BlueZHelpers.DevicePath(BlueZHelpers.GetDeviceAdapter(macAddress), macAddress);

            var mode = await BlueZHelpers.DetectModeByGattAsync(devicePath);
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

            var (characteristic, flags) = await BlueZHelpers.GetCharacteristicByUuidAsync(devicePath, serviceUuid, notifyUuid);
            if (characteristic == null)
            {
                _logger.LogWarning("LinuxBLE notifications: notify characteristic {Uuid} not found for {Mac} (mode={Mode})", notifyUuid, macAddress, mode);
                return;
            }

            // Subscribe to PropertiesChanged before calling StartNotify so we don't miss the first event.
            var sub = await characteristic.WatchPropertiesAsync(
                changes =>
                {
                    foreach (var (key, val) in changes.Changed)
                    {
                        if (key == "Value" && val is byte[] bytes)
                        {
                            var hexValue = BlueZHelpers.BytesToHex(bytes);
                            DispatchHandlers(macAddress, hexValue);
                        }
                    }
                },
                ex => _logger.LogError(ex, "LinuxBLE notification error for {Mac}", macAddress));

            try
            {
                await characteristic.StartNotifyAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LinuxBLE: StartNotify failed for {Mac} (may already be notifying)", macAddress);
            }

            _notifySubscriptions[macAddress] = sub;
            _notifyCharacteristics[macAddress] = characteristic;
        }
        finally
        {
            _subLock.Release();
        }
    }

    private async Task StopNotifyingAsync(string macAddress)
    {
        if (!_notifySubscriptions.TryRemove(macAddress, out var sub)) return;

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
                await characteristic.StopNotifyAsync();
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
