using Tmds.DBus;

namespace AccessAPP.Services.LinuxBle;

// ─── org.bluez.Adapter1 ──────────────────────────────────────────────────────
// Controls the local BLE adapter: start/stop discovery, set filters, etc.

[DBusInterface("org.bluez.Adapter1")]
public interface IAdapter1 : IDBusObject
{
    Task StartDiscoveryAsync();
    Task StopDiscoveryAsync();
    /// <summary>
    /// Set a discovery filter. Useful keys: "Transport" (string "le"), "DuplicateData" (bool).
    /// Pass an empty dictionary to clear the filter.
    /// </summary>
    Task SetDiscoveryFilterAsync(IDictionary<string, object> filter);
    Task<T> GetAsync<T>(string prop);
    Task<IDictionary<string, object>> GetAllAsync();
    Task SetAsync(string prop, object val);
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler, Action<Exception>? onError = null);
}

// ─── org.bluez.Device1 ───────────────────────────────────────────────────────
// Represents a remote BLE device: connect, disconnect, inspect advertisement data.

[DBusInterface("org.bluez.Device1")]
public interface IDevice1 : IDBusObject
{
    Task ConnectAsync();
    Task DisconnectAsync();
    /// <summary>Trigger GATT service/characteristic discovery on a connected device.</summary>
    Task DiscoverServicesAsync();
    Task<T> GetAsync<T>(string prop);
    Task<IDictionary<string, object>> GetAllAsync();
    Task SetAsync(string prop, object val);
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler, Action<Exception>? onError = null);
}

// ─── org.bluez.GattService1 ──────────────────────────────────────────────────
// Represents a GATT service on a connected device (read-only, used for enumeration).

[DBusInterface("org.bluez.GattService1")]
public interface IGattService1 : IDBusObject
{
    Task<T> GetAsync<T>(string prop);
    Task<IDictionary<string, object>> GetAllAsync();
}

// ─── org.bluez.GattCharacteristic1 ───────────────────────────────────────────
// Represents a GATT characteristic: read, write, start/stop notify.

[DBusInterface("org.bluez.GattCharacteristic1")]
public interface IGattCharacteristic1 : IDBusObject
{
    /// <summary>Read the current value. Options: {"offset": ushort}.</summary>
    Task<byte[]> ReadValueAsync(IDictionary<string, object> options);

    /// <summary>
    /// Write a value to the characteristic.
    /// Options: {"type": "command"} for write-without-response,
    ///          {"type": "request"} for write-with-response (default).
    /// </summary>
    Task WriteValueAsync(byte[] value, IDictionary<string, object> options);

    /// <summary>Enable notifications/indications for this characteristic.</summary>
    Task StartNotifyAsync();

    /// <summary>Disable notifications/indications for this characteristic.</summary>
    Task StopNotifyAsync();

    Task<T> GetAsync<T>(string prop);
    Task<IDictionary<string, object>> GetAllAsync();
    Task SetAsync(string prop, object val);

    /// <summary>
    /// Watch for property changes. The "Value" property carries notification payloads
    /// when StartNotify is active.
    /// </summary>
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler, Action<Exception>? onError = null);
}

// ─── org.freedesktop.DBus.ObjectManager ──────────────────────────────────────
// Used to enumerate and watch all BlueZ objects (adapters, devices, services,
// characteristics) under the /org/bluez tree.

[DBusInterface("org.freedesktop.DBus.ObjectManager")]
public interface IObjectManager : IDBusObject
{
    /// <summary>
    /// Returns all managed objects and their interface properties.
    /// Key = object path, Value = dict of interface name → property dict.
    /// </summary>
    Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>> GetManagedObjectsAsync();

    /// <summary>Signal fired when a new object (device, characteristic …) is added.</summary>
    Task<IDisposable> WatchInterfacesAddedAsync(
        Action<(ObjectPath objectPath, IDictionary<string, IDictionary<string, object>> interfacesAndProperties)> handler,
        Action<Exception>? onError = null);

    /// <summary>Signal fired when an object (device, characteristic …) is removed.</summary>
    Task<IDisposable> WatchInterfacesRemovedAsync(
        Action<(ObjectPath objectPath, string[] interfaces)> handler,
        Action<Exception>? onError = null);
}
