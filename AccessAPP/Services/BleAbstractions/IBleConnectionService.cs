using AccessAPP.Models;

namespace AccessAPP.Services.BleAbstractions;

/// <summary>
/// Abstracts BLE device connect/disconnect/login operations.
/// The Cassia implementation calls the Cassia gateway REST API.
/// The Linux native implementation calls BlueZ via D-Bus.
/// </summary>
public interface IBleConnectionService
{
    /// <summary>Global connect semaphore (Cassia compat; used by notification service).</summary>
    SemaphoreSlim semaphore { get; }

    /// <summary>Connect to a BLE device.</summary>
    Task<ResponseModel> ConnectToBleDevice(string gatewayIpAddress, int gatewayPort, string macAddress, int chip = -1, bool useGlobalLock = true, int? discoverGattOverride = null, CancellationToken ct = default);

    /// <summary>Disconnect from a BLE device.</summary>
    Task<ResponseModel> DisconnectFromBleDevice(string gatewayIpAddress, string macAddress, int retries = 1, int chip = -1);

    /// <summary>Attempt login on an already-connected device.</summary>
    Task<LoginResponseModel> AttemptLogin(string gatewayIpAddress, string macAddress);

    /// <summary>Attempt login with cancellation support.</summary>
    Task<LoginResponseModel> AttemptLogin(string gatewayIpAddress, string macAddress, CancellationToken ct);

    /// <summary>Return the list of currently connected BLE devices reported by the backend.</summary>
    Task<ConnectedDevicesView> GetConnectedBleDevices(string gatewayIpAddress, int gatewayPort);

    /// <summary>Write a value to handle 19 and wait for the notification reply.</summary>
    Task<DataResponseModel> GetDataFromBleDevice(string gatewayIpAddress, int gatewayPort, string macAddress, string value);

    /// <summary>Send a light-control telegram to handle 19 (no-response write).</summary>
    Task<ResponseModel> SendControlToLight(string gatewayIpAddress, string macAddress, string hexControlValue);

    /// <summary>Batch-connect multiple devices.</summary>
    Task<ResponseModel> BatchConnectDevices(string gatewayIpAddress, List<string> macAddresses);

    /// <summary>Persist a pairing record locally.</summary>
    PairResponse PairDevice(string gatewayIpAddress, int gatewayPort, PairDevicesRequest pairDevicesRequest);

    /// <summary>Remove a local pairing record.</summary>
    UnpairResponse UnpairDevice(string gatewayIpAddress, int gatewayPort, UnpairDevicesRequest unpairDevicesRequest);

    /// <summary>Return locally stored paired device MAC addresses.</summary>
    List<string> GetPairedDevices();

    /// <summary>
    /// Platform-specific best-effort cleanup after a connect attempt fails.
    /// On Linux native BLE this removes the device from BlueZ's D-Bus object tree so it is
    /// re-discovered fresh; on Cassia mode this is a no-op.
    /// Errors are swallowed — callers do not need to handle failures.
    /// </summary>
    Task CleanupAfterFailedConnectAsync(string macAddress) => Task.CompletedTask;
}
