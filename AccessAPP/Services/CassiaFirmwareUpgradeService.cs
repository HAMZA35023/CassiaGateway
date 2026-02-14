using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using AccessAPP.Services.UpgradeCore;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AccessAPP.Logging;


namespace AccessAPP.Services
{
    public partial class CassiaFirmwareUpgradeService : IDeviceSettingsBleApi
    {

        private static int _inQueue;
        public static int inQueue
        {
            get => _inQueue;
            set
            {
                if (_inQueue == value)
                    return;

                var old = _inQueue;
                _inQueue = value;

                AppLog.Debug($"[QUEUE] inQueue {old} → {_inQueue} @ {DateTime.Now:HH:mm:ss.fff}");
}
        }

        public static double totalSpeed { get; set; } = 0;


        public static int GlobalnumberOfParallelThreads = 2; // runtime adjustable via MQTT (resets on restart) // Optimal setting with current Cassia Gateway HW (21:43 Min for 3 P48 with actor and sensor firmware update)
        
        private readonly HttpClient _httpClient;
        private readonly CassiaConnectService _connectService;
        private readonly CassiaPinCodeService _cassiaPinCodeService;
        private static DeviceStorageService _deviceStorageService;
        private readonly IMqttService _mqttService;
        private readonly IConfiguration _configuration;

        private readonly IDeviceSettingsBackupService _settingsBackup;

        //private ConcurrentDictionary<string, byte[]> _lastNotificationDataRead = new ConcurrentDictionary<string, byte[]>();
        //private ManualResetEvent _notificationEvent = new ManualResetEvent(false);
        //private readonly HashSet<string> _subscribedMacAddresses = new HashSet<string>();
        internal const int ERR_SUCCESS = 0;
        internal const int ERR_OPEN = 1;
        internal const int ERR_CLOSE = 2;
        internal const int ERR_READ = 3;
        internal const int ERR_WRITE = 4;

        private readonly byte[] _securityKey = { 0x49, 0xA1, 0x34, 0xB6, 0xC7, 0x79 }; // Security ID
        private readonly byte _appID = 0x00; // AppID as shown in the screenshot
        private readonly string _gatewayIpAddress;
        private readonly int _gatewayPort;

	    // Internal accessors for extracted pipeline/worker helpers (no behavioral impact).
	    internal string GatewayIpAddress => _gatewayIpAddress;
	    internal int GatewayPort => _gatewayPort;
	    internal IDeviceSettingsBackupService SettingsBackupService => _settingsBackup;
	    internal CassiaConnectService ConnectService => _connectService;

	    // Wrapper with the SAME parameter order as the original private method.
        internal Task<(bool ok, HttpStatusCode code, string msg)> ConnectOnlyWithRetryAsync_Internal(
	        int maxAttempts,
	        int delayMs,
	        string stageName,
	        string macAddress,
	        string firmwareVersion,
	        string? logId,
	        bool logSuccess = true,
	        int? discoverGattOverride = null)
	        => ConnectOnlyWithRetryAsync(maxAttempts, delayMs, stageName, macAddress, firmwareVersion, logId ?? "", logSuccess, discoverGattOverride);

	    internal async Task<(bool Success, int StatusCode, string Message)> ConnectAndLoginWithRetryForPipelineAsync(
	        string gatewayIpAddress,
	        int gatewayPort,
	        string macAddress,
	        string pincode,
	        string? logId,
	        string? firmwareVersion,
	        int maxAttempts,
	        int delayBetweenAttemptsMs)
	    {
	        var r = await ConnectAndLoginWithRetryAsync(gatewayIpAddress, gatewayPort, macAddress, pincode, logId, firmwareVersion, maxAttempts, delayBetweenAttemptsMs)
	            .ConfigureAwait(false);
	        return (r.Success, r.StatusCode, r.Message ?? "");
	    }

        internal async Task<bool> EnsureLoginOnConnectedSessionUnlessBootModeAsync(
            string macAddress,
            string? pincode,
            string? logId,
            string? firmwareVersion,
            string stageName = "LoggedIn",
            int maxAttempts = 3)
        {
            if (CheckIfDeviceInBootMode(_gatewayIpAddress, macAddress))
            {
                UpgradeLogger.Log(logId ?? "", macAddress, stageName, "Skipped (bootloader mode)", firmwareVersion ?? "");
                return true;
            }

            int attempts = Math.Max(1, maxAttempts);
            int loginTimeoutMs = Math.Max(2000, RuntimeVariables.UPGRADE_LOGIN_ATTEMPT_TIMEOUT_MS);
            int retryDelayMs = Math.Max(100, RuntimeVariables.UPGRADE_LOGIN_RETRY_DELAY_MS);
            int settleBeforeLoginMs = GetConnectStabilizationDelayMs();
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    if (attempt == 1 && settleBeforeLoginMs > 0)
                    {
                        AppLog.Debug($"{stageName}: waiting {settleBeforeLoginMs}ms before login on connected session for {macAddress}.");
                        await Task.Delay(settleBeforeLoginMs).ConfigureAwait(false);
                    }

                    AppLog.Debug($"{stageName}: login attempt {attempt}/{attempts} for {macAddress}.");
                    using var cts = new CancellationTokenSource(loginTimeoutMs);
                    var loginResult = await _connectService
                        .AttemptLogin(_gatewayIpAddress, macAddress, cts.Token)
                        .ConfigureAwait(false);

                    bool pinReq = loginResult.ResponseBody.PincodeRequired;

                    if (pinReq && !string.IsNullOrEmpty(pincode))
                    {
                        var check = await _cassiaPinCodeService.CheckPincode(_gatewayIpAddress, macAddress, pincode).ConfigureAwait(false);
                        loginResult.ResponseBody = check.ResponseBody;
                        loginResult.ResponseBody.PincodeRequired = pinReq;
                    }

                    var statusText = loginResult.Status?.ToString() ?? "";
                    bool statusOk = string.Equals(statusText, "OK", StringComparison.OrdinalIgnoreCase);
                    bool pinOk = !pinReq || loginResult.ResponseBody.PinCodeAccepted;

                    if (statusOk && pinOk)
                    {
                        AppLog.Debug($"{stageName}: login success for {macAddress} on attempt {attempt}/{attempts}.");
                        UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Success (attempt {attempt}/{attempts})", firmwareVersion ?? "");
                        return true;
                    }

                    AppLog.Debug($"{stageName}: login failed for {macAddress} on attempt {attempt}/{attempts}. Status={statusText}, pinRequired={pinReq}, pinAccepted={loginResult.ResponseBody.PinCodeAccepted}.");
                    UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Failed (attempt {attempt}/{attempts}) - Status={statusText}", firmwareVersion ?? "");
                }
                catch (OperationCanceledException)
                {
                    AppLog.Debug($"{stageName}: login timeout for {macAddress} on attempt {attempt}/{attempts}.");
                    UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Timeout (attempt {attempt}/{attempts}, {loginTimeoutMs / 1000}s)", firmwareVersion ?? "");
                }
                catch (Exception ex)
                {
                    AppLog.Debug($"{stageName}: login exception for {macAddress} on attempt {attempt}/{attempts}: {ex.Message}");
                    UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Exception (attempt {attempt}/{attempts}): {ex.Message}", firmwareVersion ?? "");
                }

                if (attempt < attempts)
                    await Task.Delay(retryDelayMs).ConfigureAwait(false);
            }

            // Final safety: boot mode can flip after a jump and the first check may be stale.
            // If we are actually in boot mode, treat login as "not required".
            try
            {
                if (CheckIfDeviceInBootMode(_gatewayIpAddress, macAddress))
                {
                    UpgradeLogger.Log(logId ?? "", macAddress, stageName, "Skipped (bootloader mode detected after login failure)", firmwareVersion ?? "");
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug($"{stageName}: boot-mode recheck failed for {macAddress}: {ex.Message}");
            }

            return false;
        }
        private string MacAddress = "";
        private double totalRows = 0;
        private string sensorType = "";
        private static ConcurrentDictionary<string, HashSet<string>> allRows = new();
        private static ConcurrentDictionary<string, HashSet<string>> completedRows = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _macsInProgress = new(StringComparer.OrdinalIgnoreCase);

        // Split responsibilities:
        // - ChipAllocationManager: Cassia X2000 dual-chip pinning + fair scheduling
        // - UpgradeQueueProcessor: FIFO queue worker that starts upgrades as capacity allows
        private readonly ChipAllocationManager _chipManager;
        private readonly UpgradeQueueProcessor _queue;

        internal int GetChipForMac(string mac)
            => _chipManager.GetChipForMac(mac, RuntimeVariables.DEFAULT_CASSIA_CHIP);

        private static string NormalizeMac(string? mac)
            => MacUtils.NormalizeMac(mac);

        CassiaReadWriteService cassiaReadWriteService = new CassiaReadWriteService();


        private readonly CassiaNotificationService _notificationService; // ✅ Injected singleton

        private static CassiaFirmwareUpgradeService _ownInstance = null;

// Tracks currently programming devices and their target firmware (for MQTT programming list)
private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string DetectorType, string FirmwareVersion)> _programmingTargets
    = new(StringComparer.OrdinalIgnoreCase);

// Tracks which firmware "type" is currently being programmed per MAC so that
// progress notifications can be published with the right stage.
// Values: "actor" | "sensor" | "bootloader".
private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _programmingStageByMac
    = new(StringComparer.OrdinalIgnoreCase);

public static int GetParallelProgrammers()
    => Volatile.Read(ref GlobalnumberOfParallelThreads);

public static int SetParallelProgrammers(int value)
{
    // Only lives until restart (not persisted). Guard against silly values.
    var v = Math.Clamp(value, 1, 32);
    Volatile.Write(ref GlobalnumberOfParallelThreads, v);

    // Wake the live queue worker so an increase in parallelism takes effect
    // immediately (otherwise it may wait for an in-flight device to complete).
    _ownInstance?.OnParallelismChanged(v);
    return v;
}

private void OnParallelismChanged(int newValue)
{
    _queue?.OnParallelismChanged(newValue);
}

public static IReadOnlyList<(string Mac, string DetectorType, string FirmwareVersion)> GetQueueListSnapshot()
{
    var inst = _ownInstance;
    if (inst is null) return Array.Empty<(string, string, string)>();

    return inst._queue
        .GetUpgradeQueueSnapshot()
        .Select(x => (x.Mac, x.DetectorType, x.TargetFw))
        .ToList();
}

public static IReadOnlyList<(string Mac, string DetectorType, string FirmwareVersion)> GetProgrammingListSnapshot()
{
    var inst = _ownInstance;
    if (inst is null) return Array.Empty<(string, string, string)>();

    return inst._programmingTargets
        .Select(kvp => (NormalizeMac(kvp.Key), kvp.Value.DetectorType ?? "", kvp.Value.FirmwareVersion ?? ""))
        .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

public static int GetProgrammingCount()
{
    var inst = _ownInstance;
    return inst is null ? 0 : inst._programmingTargets.Count;
}

        // ------------------------------------------------------------
        // Queue facade (delegates to UpgradeQueueProcessor)
        // ------------------------------------------------------------

        internal bool IsMacInProgress(string mac)
            => _macsInProgress.ContainsKey(mac);

        /// <summary>
        /// Queue-enabled FIFO entrypoint. Behavior is unchanged; implementation moved into UpgradeQueueProcessor.
        /// </summary>
        private Task UpgradeDevicesInParallel(List<UpgradeProgress> devices, int numbersOfThreadsInParallel = -1)
            => _queue.EnqueueAsync(devices, numbersOfThreadsInParallel);

        public static int RemoveFromUpgradeQueuePending(string mac)
        {
            var inst = _ownInstance;
            return inst is null ? 0 : inst._queue.RemoveFromUpgradeQueue(mac);
        }



        private static readonly ConcurrentDictionary<string, object> _macLocks = new();
        private static readonly ConcurrentDictionary<string, SlidingRate10s> _macRate10s = new();
        private static readonly ConcurrentDictionary<string, double> _lastInstanceRate = new();

        // Overall / all instances

        public CassiaFirmwareUpgradeService(HttpClient httpClient, CassiaConnectService connectService, CassiaPinCodeService cassiaPinCodeService, CassiaNotificationService notificationService, DeviceStorageService deviceStorageService, IConfiguration configuration, IMqttService mqttService)
        {
            _ownInstance = this;
            _httpClient = httpClient;
            _connectService = connectService;
            _deviceStorageService = deviceStorageService;
            _cassiaPinCodeService = cassiaPinCodeService;
            _mqttService = mqttService;
            _configuration = configuration;
            _gatewayIpAddress = _configuration.GetValue<string>("GatewayConfiguration:IpAddress");
            _gatewayPort = _configuration.GetValue<int>("GatewayConfiguration:Port");
            _notificationService = notificationService;
            _settingsBackup = new DeviceSettingsBackupService(this);

            // Dedicated components (structural refactor; no behavior changes intended)
            _chipManager = new ChipAllocationManager(GetParallelProgrammers);
            _queue = new UpgradeQueueProcessor(this, _chipManager);
        }

        // ------------------------------------------------------------
        // Settings helpers (BLE command wrappers)
        // ------------------------------------------------------------

        public async Task<bool> RebootDeviceAsync(string nodeMac)
        {
            const string rebootCommand = "0113060800B68F00"; // Reboot

            try
            {
                var sensorResponse = await _connectService.GetDataFromBleDevice(
                    _gatewayIpAddress,
                    _gatewayPort,
                    nodeMac,
                    rebootCommand);

                // Device reboots immediately → no response expected
                // Treat "sent successfully" as OK
                if (sensorResponse.Status.ToString() == "OK")
                {
                    AppLog.Info($"[REBOOT] Command sent, device rebooting: MAC={nodeMac}");
return true;
                }

                AppLog.Warn($"[REBOOT] Failed to send command: MAC={nodeMac}, Status={sensorResponse.Status}");
return false;
            }
            catch (Exception ex)
            {
                // BLE stack may throw because device disappears instantly
                AppLog.Debug($"[REBOOT] Exception (expected in some cases): MAC={nodeMac}, {ex.Message}");
return true; // command was likely accepted before disconnect
            }
        }


        public async Task<bool> DaliSetDeviceSysFailLevelAsync(
    string nodeMac,
    byte sysFailLevel // e.g. 0xFF or 0xFE
)
        {
            // DaliSetDeviceSysFailLevel:
            // 01-14-04-08-00-FB-B0-<LEVEL>
            const string prefix = "0114040800FBB0";

            string levelHex = sysFailLevel.ToString("X2", CultureInfo.InvariantCulture);
            string cmd = prefix + levelHex;

            try
            {
                var sensorResponse = await _connectService.GetDataFromBleDevice(
                    _gatewayIpAddress,
                    _gatewayPort,
                    nodeMac,
                    cmd);

                if (sensorResponse.Status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(sensorResponse.Data))
                {
                    AppLog.Warn($"[DALI] SysFailLevel set failed: MAC={nodeMac}, Level=0x{levelHex}, Status={sensorResponse.Status}, RAW={sensorResponse.Data}");
                    return false;
                }

                string reply = sensorResponse.Data.Trim().ToUpperInvariant();

                // Success can be "00" or "0000"
                bool ok = reply == "00" || reply == "0000";

                AppLog.Info($"[DALI] SysFailLevel set: MAC={nodeMac}, Level=0x{levelHex}, Cmd={cmd}, Reply={reply}, OK={ok}");
                if (!ok)
                    AppLog.Warn($"[DALI] SysFailLevel set rejected: MAC={nodeMac}, Level=0x{levelHex}, Cmd={cmd}, Reply={reply}");
                return ok;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[DALI] SysFailLevel set exception: MAC={nodeMac}, Level=0x{levelHex}, {ex.Message}");
                return false;
            }
        }

        public async Task<byte?> DaliGetDeviceSysFailLevelAsync(string nodeMac)
        {
            // DaliDeviceCommonParam:
            // 01-10-04-07-00-34-6A
            const string cmd = "0110040700346A";

            try
            {
                var sensorResponse = await _connectService.GetDataFromBleDevice(
                    _gatewayIpAddress,
                    _gatewayPort,
                    nodeMac,
                    cmd);

                if (sensorResponse.Status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(sensorResponse.Data))
                {
                    AppLog.Warn($"[DALI] CommonParam read failed: MAC={nodeMac}, Status={sensorResponse.Status}, RAW={sensorResponse.Data}");
                    return null;
                }

                var raw = sensorResponse.Data.Trim();
                var byteMatches = Regex.Matches(raw, @"[0-9A-Fa-f]{2}");
                if (byteMatches.Count < 8)
                {
                    AppLog.Warn($"[DALI] CommonParam parse failed (expected >=8 bytes): MAC={nodeMac}, RAW={raw}");
                    return null;
                }

                var bytes = byteMatches
                    .Cast<Match>()
                    .Select(m => byte.Parse(m.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                    .ToArray();

                // Response layout (1 byte each):
                // [0] Status, [1] MaxLevel, [2] MinLevel, [3] PowerOnLevel,
                // [4] SysFailLevel, [5] FadeTime, [6] FadeRate, [7] ExtendedFadeTime
                var status = bytes[0];
                var sysFail = bytes[4];

                if (status != 0x00)
                {
                    AppLog.Warn($"[DALI] CommonParam status not OK: MAC={nodeMac}, Status=0x{status:X2}, RAW={raw}");
                    return null;
                }

                AppLog.Info($"[DALI] CommonParam read: MAC={nodeMac}, SysFailLevel=0x{sysFail:X2}, RAW={raw}");
                return sysFail;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[DALI] CommonParam read exception: MAC={nodeMac}, {ex.Message}");
                return null;
            }
        }

        public async Task<string> GetBLEPushButtonList(string nodeMac)
        {
            string sensorCommand = "012101070099DB"; // GetBLEPushButtonList
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

            string hex = "";
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
hex = sensorResponse.Data;
            }

            return hex;
        }

        public async Task<bool> SetBLEPushButtonList(string nodeMac, string newBlePushButtonListHex)
        {
            string sensorCommand = "0119016400CA2C"; // SetBLEPushButtonList
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand + newBlePushButtonListHex);

            bool resp = false;
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
resp = sensorResponse.Data == "00";

                if (!resp)
                {
                    AppLog.Warn("Failed to set: " + sensorCommand + newBlePushButtonListHex);
}
            }
            else
                AppLog.Warn("BLE failed: " + sensorResponse.Status.ToString() + "RAW: " + sensorResponse.Data);
return resp;
        }

        public async Task<string> GetWiredPushButtonList(string nodeMac)
        {
            string sensorCommand = "0113010700181A"; // GetWiredPushButtonList
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

            string hex = "";
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
hex = sensorResponse.Data;
            }

            return hex;
        }

        public async Task<bool> SetWiredPushButtonList(string nodeMac, string newWiredPushButtonListHex)
        {
            string sensorCommand = "0111011C00F928"; // SetWiredPushButtonList
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand + newWiredPushButtonListHex);

            bool resp = false;
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
resp = sensorResponse.Data == "00";
                if (!resp)
                {
                    AppLog.Warn("Failed to set: " + sensorCommand + newWiredPushButtonListHex);
}
            }
            else
                AppLog.Warn("BLE failed: " + sensorResponse.Status.ToString() + "RAW: " + sensorResponse.Data);
return resp;
        }

        public async Task<string> GetDaliPushButtonList(string nodeMac)
        {
            string sensorCommand = "013F0107006462"; // GetDaliPushButtonList
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

            string hex = "";
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
hex = sensorResponse.Data;
            }

            return hex;
        }

        public async Task<bool> SetDaliPushButtonList(string nodeMac, string newDaliPushButtonListHex)
        {
            string sensorCommand = "013D016C00DC58"; // SetDaliPushButtonList
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand + newDaliPushButtonListHex);

            bool resp = false;
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
resp = sensorResponse.Data == "00";
                if (!resp)
                {
                    AppLog.Warn("Failed to set: " + sensorCommand + newDaliPushButtonListHex);
}

            }
            else
                AppLog.Warn("BLE failed: " + sensorResponse.Status.ToString() + "RAW: " + sensorResponse.Data);
return resp;
        }

        // Optional helpers
        public async Task<bool> DaliRestore102Database(string nodeMac)
        {
            string sensorCommand = "013C0407004812"; // DaliRestore102Database
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

            bool resp = false;
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
resp = true;
            }

            return resp;
        }

        public async Task<bool> DaliRestore103Database(string nodeMac)
        {
            string sensorCommand = "013D040700FC64"; // DaliRestore103Database
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

            bool resp = false;
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
resp = true;
            }

            return resp;
        }

        public async Task<string> GetUserConfig(string nodeMac)
        {

            // Sensor
            string sensorCommand = "010B0107007C84"; //GetUserConfig 
            var sensorResponse = await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);
            string userconfig = "";
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
userconfig = sensorResponse.Data;
            }

            return userconfig;
        }

        public async Task<bool> SetUserConfig(string nodeMac, string newuserconfig)
        {

            // Sensor
            string sensorCommand = "010D01A7009BBE"; //SetUserConfig 
            var sensorResponse = await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand + newuserconfig);
            bool resp = false;
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
resp = sensorResponse.Data == "0000" || sensorResponse.Data == "00";
                if (!resp)
                {
                    AppLog.Warn("Failed to set: " + sensorCommand + newuserconfig);
}
            }
            else
                AppLog.Warn("BLE failed: " + sensorResponse.Status.ToString() + "RAW: " + sensorResponse.Data);
return resp;
        }

        public async Task<ServiceResponse> UpgradeSensorAsync(
            string nodeMac,
            string pincode,
            bool bActor,
            bool isBootloader,
            string DetectorType,
            string FirmwareVersion,
            string logId = null,
            bool reuseExistingConnection = false)
        {
            ServiceResponse response = new ServiceResponse();
            sensorType = DetectorType;

            if (string.IsNullOrWhiteSpace(logId))
                logId = $"{nodeMac.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";

            UpgradeLogger.Log(
                logId,
                nodeMac,
                isBootloader ? "Process Start Bootloader Upgrade" : "Process Start Sensor Upgrade",
                "Success",
                DetectorType
            );

            // ----------------------------
            // Local helpers (no new deps)
            // ----------------------------

            int connectMaxAttempts = Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS);
            const int loginMaxAttempts = 3;
            const int bootJumpMaxAttempts = 5;

            int? BootJumpDiscoverGattOverride()
            {
                int v = RuntimeVariables.UPGRADE_CONNECT_DISCOVER_GATT_AFTER_BOOT_JUMP;
                if (v < 0) return null;
                return v <= 0 ? 0 : 1;
            }

            async Task<bool> ConnectWithRetryAsync(string stepName, int? discoverGattOverride = null)
            {
                var connect = await ConnectOnlyWithRetryAsync(
                    maxAttempts: connectMaxAttempts,
                    delayMs: 2000,
                    stageName: stepName,
                    macAddress: nodeMac,
                    FirmwareVersion: FirmwareVersion,
                    logId: logId,
                    logSuccess: true,
                    discoverGattOverride: discoverGattOverride).ConfigureAwait(false);

                if (!connect.ok)
                    AppLog.Warn($"{stepName}: connect failed for {nodeMac} on chip {GetChipForMac(nodeMac)} with status {connect.code}. Message: {connect.msg}");

                return connect.ok;
            }

            async Task<bool> LoginWithRetryAsync()
            {
                return await EnsureLoginOnConnectedSessionUnlessBootModeAsync(
                    nodeMac,
                    pincode,
                    logId,
                    FirmwareVersion,
                    stageName: "LoggedIn",
                    maxAttempts: loginMaxAttempts).ConfigureAwait(false);
            }

            async Task<bool> EnsureBootModeAsync()
            {
                // Quick check first
                if (CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac))
                {
                    UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Detected");
                    return true;
                }

                // Try to jump multiple times, each time reconnect and verify
                for (int attempt = 1; attempt <= bootJumpMaxAttempts; attempt++)
                {
                    bool jumpOk = false;
                    try
                    {
                        jumpOk = await SendJumpToBootloader(_gatewayIpAddress, nodeMac, bActor);
                    }
                    catch (Exception ex)
                    {
                        UpgradeLogger.Log(logId, nodeMac, "JumpToBootloader", $"Exception (attempt {attempt}/{bootJumpMaxAttempts}): {ex.Message}");
                    }

                    if (!jumpOk)
                    {
                        UpgradeLogger.Log(logId, nodeMac, "JumpToBootloader", $"Failed (attempt {attempt}/{bootJumpMaxAttempts})");
                        await Task.Delay(3000);
                        continue;
                    }

                    UpgradeLogger.Log(logId, nodeMac, "JumpToBootloader", $"Sent (attempt {attempt}/{bootJumpMaxAttempts})");

                    // Give device time to switch to bootloader
                    int jumpDelay = 10000 + Math.Max(0, RuntimeVariables.UPGRADE_DELAY_AFTER_BOOT_JUMP_MS);
                    await Task.Delay(jumpDelay);

                    // Reconnect after jump (robust)
                    if (!await ConnectWithRetryAsync("Connect After JumpToBoot", BootJumpDiscoverGattOverride()))
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Connect After JumpToBoot", $"Failed (attempt {attempt}/{bootJumpMaxAttempts})");
                        await Task.Delay(3000);
                        continue;
                    }

                    // Verify boot mode (sometimes needs multiple checks)
                    for (int verify = 1; verify <= 5; verify++)
                    {
                        bool isBoot = false;
                        try
                        {
                            isBoot = CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac);
                        }
                        catch (Exception ex)
                        {
                            UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", $"Check exception (verify {verify}/5): {ex.Message}");
                        }

                        if (isBoot)
                        {
                            UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Achieved");
                            AppLog.Info($"Device entered boot mode after {attempt} jump attempts.");
return true;
                        }

                        UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", $"NotYet (verify {verify}/5, attempt {attempt}/{bootJumpMaxAttempts})");
                        await Task.Delay(1500);
                    }

                    // Try again
                    await Task.Delay(3000);
                }

                UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Failed");
                return false;
            }

            // ----------------------------
            // Step 1: Connect (robust)
            // ----------------------------
            if (!reuseExistingConnection)
            {
                if (!await ConnectWithRetryAsync("Connected"))
                {
                    response.Success = false;
                    response.StatusCode = 500;
                    response.Message = "Failed to connect to device.";
                    return response;
                }
            }
            else
            {
                UpgradeLogger.Log(logId, nodeMac, "Connected", "Skipped (reusing existing session)");
                AppLog.Info($"Skipping initial connect for {nodeMac} (reusing existing session).");
            }

            AppLog.Info($"Connected to device...{nodeMac}");
// If already in boot mode, skip login/jump and go directly to processing
            if (CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac))
            {
                AppLog.Info($"Device is already in boot mode. -> {nodeMac}");
UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Detected");
                await Task.Delay(3000);

                return await ProcessingSensorUpgrade(nodeMac, bActor, isBootloader, DetectorType, FirmwareVersion, logId, pincode);
            }

            // ----------------------------
            // Step 2: Login (robust)
            // ----------------------------
            if (!await LoginWithRetryAsync())
            {
                if (reuseExistingConnection)
                {
                    UpgradeLogger.Log(logId, nodeMac, "Login", "Failed on reused session; reconnecting");
                    if (!await ConnectWithRetryAsync("Connected (fallback after reused-session login fail)"))
                    {
                        response.Success = false;
                        response.StatusCode = 500;
                        response.Message = "Failed to connect to device.";
                        return response;
                    }
                    if (!await LoginWithRetryAsync())
                    {
                        response.Success = false;
                        response.StatusCode = 401;
                        response.Message = "Failed to login to the device.";
                        UpgradeLogger.Log(logId, nodeMac, "Login", "Failed");
                        return response;
                    }
                }
                else
                {
                response.Success = false;
                response.StatusCode = 401;
                response.Message = "Failed to login to the device.";
                UpgradeLogger.Log(logId, nodeMac, "Login", "Failed");
                return response;
                }
            }

            AppLog.Info($"Logged into device...{nodeMac}");
// ----------------------------
            // Step 3: Jump to bootloader + verify (robust)
            // ----------------------------
            if (!await EnsureBootModeAsync())
            {
                response.Success = false;
                response.StatusCode = 417;
                response.Message = "Failed to enter boot mode.";
                return response;
            }

            // ----------------------------
            // Disconnect and prepare for upgrade process
            // ----------------------------
            try
            {
                AppLog.Info("device disconnected and will reconnect after 3s");
await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0, chip: GetChipForMac(nodeMac));
                UpgradeLogger.Log(logId, nodeMac, "Disconnected", "Success");
            }
            catch (Exception ex)
            {
                UpgradeLogger.Log(logId, nodeMac, "Disconnected", $"Exception: {ex.Message}");
                // continue anyway (device might already have dropped)
            }

            int bootJumpDelayMs = RuntimeVariables.UPGRADE_DELAY_AFTER_BOOT_JUMP_MS;
            int postDisconnectDelay = 3000 + Math.Max(0, bootJumpDelayMs);
            await Task.Delay(postDisconnectDelay);

            // Now do the actual programming flow
            return await ProcessingSensorUpgrade(nodeMac, bActor, isBootloader, DetectorType, FirmwareVersion, logId, pincode);
        }
        public async Task<ServiceResponse> UpgradeActorAsync(
            string nodeMac,
            string pincode,
            bool bActor,
            string DetectorType,
            string FirmwareVersion,
            string logId)
        {
            UpgradeLogger.Log(logId, nodeMac, "Process Start Actor Upgrade", "Success");
            ServiceResponse response = new();

            int connectMaxAttempts = Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS);
            const int loginMaxAttempts = 3;
            const int bootJumpMaxAttempts = 3;

            async Task<bool> ConnectWithRetryAsync(string stepName)
            {
                var connect = await ConnectOnlyWithRetryAsync(
                    maxAttempts: connectMaxAttempts,
                    delayMs: 2000,
                    stageName: stepName,
                    macAddress: nodeMac,
                    FirmwareVersion: FirmwareVersion,
                    logId: logId,
                    logSuccess: true).ConfigureAwait(false);

                if (!connect.ok)
                    AppLog.Warn($"{stepName}: connect failed for {nodeMac} on chip {GetChipForMac(nodeMac)} with status {connect.code}. Message: {connect.msg}");

                return connect.ok;
            }

            async Task<bool> LoginWithRetryAsync()
            {
                return await EnsureLoginOnConnectedSessionUnlessBootModeAsync(
                    nodeMac,
                    pincode,
                    logId,
                    FirmwareVersion,
                    stageName: "LoggedIn",
                    maxAttempts: loginMaxAttempts).ConfigureAwait(false);
            }

            async Task<bool> JumpActorToBootModeAsync()
            {
                for (int attempt = 1; attempt <= bootJumpMaxAttempts; attempt++)
                {
                    bool jumpOk = false;
                    try
                    {
                        jumpOk = await SendJumpToBootloader(_gatewayIpAddress, nodeMac, bActor);
                    }
                    catch (Exception ex)
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", $"Jump exception (attempt {attempt}/{bootJumpMaxAttempts}): {ex.Message}");
                    }

                    if (jumpOk)
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", $"JumpSent (attempt {attempt}/{bootJumpMaxAttempts})");
                        return true;
                    }

                    UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", $"JumpFailed (attempt {attempt}/{bootJumpMaxAttempts})");
                    await Task.Delay(5000);
                }

                return false;
            }

            // ----------------------------
            // Step 1: Connect (robust)
            // ----------------------------
            if (!await ConnectWithRetryAsync("Connected"))
            {
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Failed to connect to device.";
                return response;
            }

            AppLog.Info($"Connected to device...{nodeMac}");
// If sensor is already in boot mode -> actor upgrade cannot proceed
            if (CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac))
            {
                UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Detected");

                response.Success = false;
                response.StatusCode = 409;
                response.Message = "Sensor is already in boot mode. It needs to be in Application mode.";

                UpgradeLogger.Log(logId, nodeMac, "Disconnected as sensor is in bootmode", "Info");

                try
                {
					await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0, chip: GetChipForMac(nodeMac));
                }
                catch { /* ignore */ }

                await Task.Delay(5000);
                return response;
            }

            // ----------------------------
            // Step 2: Login (robust)
            // ----------------------------
            if (!await LoginWithRetryAsync())
            {
                response.Success = false;
                response.StatusCode = 401;
                response.Message = "Failed to login to the device.";
                return response;
            }

            AppLog.Info($"Logged into device...{nodeMac}");
// ----------------------------
            // Step 3: Jump actor to bootloader (robust)
            // ----------------------------
            if (!await JumpActorToBootModeAsync())
            {
                UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", "Failed");
                response.Success = false;
                response.StatusCode = 417;
                response.Message = "Failed to enter boot mode.";
                return response;
            }

            UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", "Achieved");

            // Proceed to programming step (existing flow)
            if (RuntimeVariables.UPGRADE_DELAY_AFTER_BOOT_JUMP_MS > 0)
                await Task.Delay(RuntimeVariables.UPGRADE_DELAY_AFTER_BOOT_JUMP_MS);

            return await ProcessingActorUpgrade(
                nodeMac,
                bActor,
                DetectorType,
                FirmwareVersion,
                logId,
                skipBootModeValidation: RuntimeVariables.UPGRADE_OPTIMIZE_RECONNECT_FLOW);
        }

        

        

        // Split out: ConnectLoginResult + ConnectAndLoginWithRetryAsync -> CassiaFirmwareUpgradeService.ConnectLogin.cs
        public async Task<ServiceResponse> UpgradeDeviceAsync(
            UpgradeProgress dev,
            string macAddress,
            string pincode,
            string DetectorType,
            string FirmwareVersion,
            bool upgradeActor,
            bool upgradeBootloader,
            bool upgradeSensor,
            string logId = null)
        {
            var ctx = new UpgradePipeline.DeviceUpgradeContext(
                svc: this,
                dev: dev,
                macAddress: macAddress,
                pincode: pincode,
                detectorType: DetectorType,
                firmwareVersion: FirmwareVersion,
                upgradeActor: upgradeActor,
                upgradeBootloader: upgradeBootloader,
                upgradeSensor: upgradeSensor,
                logId: logId);

            try
            {
                // preserve previous behavior by running the same stages in the same order
                var pipeline = new UpgradePipeline.DeviceUpgradePipeline();
                await pipeline.ExecuteAsync(ctx).ConfigureAwait(false);
                return ctx.Response;
            }
            catch (Exception ex)
            {
                AppLog.Error("Error during sensor and actor upgrade", ex);

                ctx.Response.Success = false;
                ctx.Response.StatusCode = 500;
                ctx.Response.Message = "An unexpected error occurred during the upgrade process.";
                UpgradeLogger.Log(logId, macAddress, $"Device Upgrade Failed: {ex.Message}", "Failed", FirmwareVersion);
                return ctx.Response;
            }
            finally
            {
                // Always disconnect at the end, identical to the original implementation.
                await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0, chip: ctx.ChipId).ConfigureAwait(false);
                UpgradeLogger.Log(logId, macAddress, "Disconnected at the end of upgrade process", "Info", FirmwareVersion);
                if (RuntimeVariables.UPGRADE_DELAY_AFTER_END_DISCONNECT_MS > 0)
                    await Task.Delay(RuntimeVariables.UPGRADE_DELAY_AFTER_END_DISCONNECT_MS).ConfigureAwait(false);
            }
        }


        



        

        

        public int UpgradeDevicesInProgress = 0;


        // NOTE: command/helper methods moved to CassiaFirmwareUpgradeService.Commands.cs
        // NOTE: DeviceUpgradeSummary moved to CassiaFirmwareUpgradeService.Models.cs
    }
}
