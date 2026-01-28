using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;


namespace AccessAPP.Services
{
    public class CassiaFirmwareUpgradeService : IDeviceSettingsBleApi
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

                Console.WriteLine(
                    $"[QUEUE] inQueue {old} → {_inQueue} @ {DateTime.Now:HH:mm:ss.fff}"
                );
            }
        }

        public static double totalSpeed { get; set; } = 0;

        const bool _DEBUG = false;
        const bool _VERBOSE = true;

        public static int GlobalnumberOfParallelThreads = 3; // runtime adjustable via MQTT (resets on restart) // Optimal setting with current Cassia Gateway HW (21:43 Min for 3 P48 with actor and sensor firmware update)
        
        //TODO: Be moved to app-settings
        //Only for P47+P48
        const bool RebootDetectorAfterUpgrade = true;
        const bool Restore102DBAfterUpgrade = true;
        const bool RestoreSettingsAfterUpgrade = true;
        const bool AutoSetSysFailLevelUnderUpdate = true;

        private readonly HttpClient _httpClient;
        private readonly CassiaConnectService _connectService;
        private readonly CassiaPinCodeService _cassiaPinCodeService;
        private static DeviceStorageService _deviceStorageService;
        private readonly IConfiguration _configuration;
        private const int MaxPacketSize = 270;
        private const int InterPacketDelay = 0;
        private readonly string _firmwareActorFilePath = "d:\\work\\firma_vis\\niko\\app_hamza\\CassiaGateway\\AccessAPP\\FirmwareVersions\\353AP20227.cyacd";
        private readonly string _firmwareSensorFilePath4 = "d:\\work\\firma_vis\\niko\\app_hamza\\CassiaGateway\\AccessAPP\\FirmwareVersions\\353AP40227.cyacd";
        private readonly string _firmwareSensorFilePath3 = "d:\\work\\firma_vis\\niko\\app_hamza\\CassiaGateway\\AccessAPP\\FirmwareVersions\\353AP30227.cyacd";
        private readonly string _firmwareSensorFilePath1 = "d:\\work\\firma_vis\\niko\\app_hamza\\CassiaGateway\\AccessAPP\\FirmwareVersions\\353AP10227.cyacd";
        private readonly string _firmwareBootLoaderFilePath = "d:\\work\\firma_vis\\niko\\app_hamza\\CassiaGateway\\AccessAPP\\FirmwareVersions\\353BL10604.cyacd";
        private readonly IDeviceSettingsBackupService _settingsBackup;

        private ConcurrentDictionary<string, ConcurrentQueue<byte[]>> _notificationQueues = new ConcurrentDictionary<string, ConcurrentQueue<byte[]>>();
        private ConcurrentDictionary<string, ManualResetEvent> _notificationEvents = new ConcurrentDictionary<string, ManualResetEvent>();
        //private ConcurrentDictionary<string, byte[]> _lastNotificationDataRead = new ConcurrentDictionary<string, byte[]>();
        //private ManualResetEvent _notificationEvent = new ManualResetEvent(false);
        //private readonly HashSet<string> _subscribedMacAddresses = new HashSet<string>();
        internal const int ERR_SUCCESS = 0;
        internal const int ERR_OPEN = 1;
        internal const int ERR_CLOSE = 2;
        internal const int ERR_READ = 3;
        internal const int ERR_WRITE = 4;
        double progressBarProgress = 0;
        double progressBarStepSize = 5;
        private readonly byte[] _securityKey = { 0x49, 0xA1, 0x34, 0xB6, 0xC7, 0x79 }; // Security ID
        private readonly byte _appID = 0x00; // AppID as shown in the screenshot
        private readonly string _gatewayIpAddress;
        private readonly int _gatewayPort;
        private string MacAddress = "";
        private double totalRows = 0;
        private string sensorType = "";
        private static ConcurrentDictionary<string, HashSet<string>> allRows = new();
        private static ConcurrentDictionary<string, HashSet<string>> completedRows = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _macsInProgress = new(StringComparer.OrdinalIgnoreCase);

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
    try
    {
        // Release a few tokens so the worker wakes and can refill capacity.
        // (No harm if no worker is running.)
        var n = Math.Clamp(newValue, 1, 32);
        for (int i = 0; i < n; i++)
            _queueSignal.Release();
    }
    catch
    {
        // ignore
    }
}

public static IReadOnlyList<(string Mac, string DetectorType, string FirmwareVersion)> GetQueueListSnapshot()
{
    var inst = _ownInstance;
    if (inst is null) return Array.Empty<(string, string, string)>();

    var arr = inst._upgradeQueue.ToArray();
    return arr
        .Where(d => d is not null && !string.IsNullOrWhiteSpace(d.MacAddress))
        .Select(d => (NormalizeMac(d.MacAddress), d.DetectotType ?? "", d.FirmwareVersion ?? ""))
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



        private static readonly ConcurrentDictionary<string, object> _macLocks = new();
        private static readonly ConcurrentDictionary<string, SlidingRate10s> _macRate10s = new();
        private static readonly ConcurrentDictionary<string, double> _lastInstanceRate = new();

        // Overall / all instances

        public CassiaFirmwareUpgradeService(HttpClient httpClient, CassiaConnectService connectService, CassiaPinCodeService cassiaPinCodeService, CassiaNotificationService notificationService, DeviceStorageService deviceStorageService, IConfiguration configuration)
        {
            _ownInstance = this;
            _httpClient = httpClient;
            _connectService = connectService;
            cassiaReadWriteService.semaphore = connectService.semaphore;
            _deviceStorageService = deviceStorageService;
            _cassiaPinCodeService = cassiaPinCodeService;
            _configuration = configuration;
            _gatewayIpAddress = _configuration.GetValue<string>("GatewayConfiguration:IpAddress");
            _gatewayPort = _configuration.GetValue<int>("GatewayConfiguration:Port");
            _notificationService = notificationService;
            _settingsBackup = new DeviceSettingsBackupService(this);
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
                    Console.WriteLine($"[REBOOT] Command sent, device rebooting: MAC={nodeMac}");
                    return true;
                }

                Console.WriteLine($"[REBOOT] Failed to send command: MAC={nodeMac}, Status={sensorResponse.Status}");
                return false;
            }
            catch (Exception ex)
            {
                // BLE stack may throw because device disappears instantly
                Console.WriteLine($"[REBOOT] Exception (expected in some cases): MAC={nodeMac}, {ex.Message}");
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

            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress,
                _gatewayPort,
                nodeMac,
                cmd);

            if (sensorResponse.Status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(sensorResponse.Data))
            {
                Console.WriteLine($"[DALI] SysFailLevel set failed: MAC={nodeMac}, Level=0x{levelHex}, Status={sensorResponse.Status}, RAW={sensorResponse.Data}");
                return false;
            }

            string reply = sensorResponse.Data.Trim().ToUpperInvariant();

            // Success can be "00" or "0000"
            bool ok = reply == "00" || reply == "0000";

            Console.WriteLine($"[DALI] SysFailLevel set: MAC={nodeMac}, Level=0x{levelHex}, Cmd={cmd}, Reply={reply}, OK={ok}");

            if (!ok)
                Console.WriteLine($"[DALI] SysFailLevel set rejected: MAC={nodeMac}, Level=0x{levelHex}, Cmd={cmd}, Reply={reply}");

            return ok;
        }

        public async Task<string> GetBLEPushButtonList(string nodeMac)
        {
            string sensorCommand = "012101070099DB"; // GetBLEPushButtonList
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

            string hex = "";
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                Console.WriteLine(sensorResponse.Data);
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
                Console.WriteLine(sensorResponse.Data);
                resp = sensorResponse.Data == "00";

                if (!resp)
                {
                    Console.WriteLine("Failed to set: " + sensorCommand + newBlePushButtonListHex);
                }
            }
            else
                Console.WriteLine("BLE failed: " + sensorResponse.Status.ToString() + "RAW: " + sensorResponse.Data);

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
                Console.WriteLine(sensorResponse.Data);
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
                Console.WriteLine(sensorResponse.Data);
                resp = sensorResponse.Data == "00";
                if (!resp)
                {
                    Console.WriteLine("Failed to set: " + sensorCommand + newWiredPushButtonListHex);
                }
            }
            else
                Console.WriteLine("BLE failed: " + sensorResponse.Status.ToString() + "RAW: " + sensorResponse.Data);


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
                Console.WriteLine(sensorResponse.Data);
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
                Console.WriteLine(sensorResponse.Data);
                resp = sensorResponse.Data == "00";
                if (!resp)
                {
                    Console.WriteLine("Failed to set: " + sensorCommand + newDaliPushButtonListHex);
                }

            }
            else
                Console.WriteLine("BLE failed: " + sensorResponse.Status.ToString() + "RAW: " + sensorResponse.Data);


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
                Console.WriteLine(sensorResponse.Data);
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
                Console.WriteLine(sensorResponse.Data);
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
                Console.WriteLine(sensorResponse.Data);
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
                Console.WriteLine(sensorResponse.Data);
                resp = sensorResponse.Data == "0000" || sensorResponse.Data == "00";
                if (!resp)
                {
                    Console.WriteLine("Failed to set: " + sensorCommand + newuserconfig);
                }
            }
            else
                Console.WriteLine("BLE failed: " + sensorResponse.Status.ToString() + "RAW: " + sensorResponse.Data);


            return resp;
        }

        public async Task<ServiceResponse> UpgradeSensorAsync(
            string nodeMac,
            string pincode,
            bool bActor,
            bool isBootloader,
            string DetectorType,
            string FirmwareVersion,
            string logId = null)
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

            const int connectMaxAttempts = 5;
            const int loginMaxAttempts = 3;
            const int bootJumpMaxAttempts = 5;

            async Task<bool> ConnectWithRetryAsync(string stepName)
            {
                for (int attempt = 1; attempt <= connectMaxAttempts; attempt++)
                {
                    try
                    {
                        var cr = await _connectService.ConnectToBleDevice(_gatewayIpAddress, 80, nodeMac);
                        if (cr.Status == HttpStatusCode.OK)
                        {
                            UpgradeLogger.Log(logId, nodeMac, stepName, $"Success (attempt {attempt}/{connectMaxAttempts})");
                            return true;
                        }

                        UpgradeLogger.Log(logId, nodeMac, stepName, $"Failed (attempt {attempt}/{connectMaxAttempts})");
                    }
                    catch (Exception ex)
                    {
                        UpgradeLogger.Log(logId, nodeMac, stepName, $"Exception (attempt {attempt}/{connectMaxAttempts}): {ex.Message}");
                    }

                    // Backoff (fast -> slower)
                    int delay = attempt switch
                    {
                        1 => 1500,
                        2 => 3000,
                        3 => 5000,
                        _ => 8000
                    };
                    await Task.Delay(delay);
                }

                return false;
            }

            async Task<bool> LoginWithRetryAsync()
            {
                for (int attempt = 1; attempt <= loginMaxAttempts; attempt++)
                {
                    try
                    {
                        var loginResult = await _connectService.AttemptLogin(_gatewayIpAddress, nodeMac);

                        bool pinReq = loginResult.ResponseBody.PincodeRequired;
                        if (pinReq && !string.IsNullOrEmpty(pincode))
                        {
                            var check = await _cassiaPinCodeService.CheckPincode(_gatewayIpAddress, nodeMac, pincode);
                            loginResult.ResponseBody = check.ResponseBody;
                            loginResult.ResponseBody.PincodeRequired = pinReq;
                        }

                        // NOTE: you previously commented out the "fail if not accepted".
                        // Keep your behavior: log success and continue.
                        UpgradeLogger.Log(logId, nodeMac, "LoggedIn", $"Success (attempt {attempt}/{loginMaxAttempts})");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Login", $"Exception (attempt {attempt}/{loginMaxAttempts}): {ex.Message}");
                    }

                    await Task.Delay(2000);
                }
                return false;
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
                    await Task.Delay(10000);

                    // Reconnect after jump (robust)
                    if (!await ConnectWithRetryAsync("Connect After JumpToBoot"))
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
                            Console.WriteLine($"Device entered boot mode after {attempt} jump attempts.");
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
            if (!await ConnectWithRetryAsync("Connected"))
            {
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Failed to connect to device.";
                return response;
            }

            Console.WriteLine($"Connected to device...{nodeMac}");

            // If already in boot mode, skip login/jump and go directly to processing
            if (CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac))
            {
                Console.WriteLine($"Device is already in boot mode. -> {nodeMac}");
                UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Detected");
                await Task.Delay(3000);

                return await ProcessingSensorUpgrade(nodeMac, bActor, isBootloader, DetectorType, FirmwareVersion, logId);
            }

            // ----------------------------
            // Step 2: Login (robust)
            // ----------------------------
            if (!await LoginWithRetryAsync())
            {
                response.Success = false;
                response.StatusCode = 401;
                response.Message = "Failed to login to the device.";
                UpgradeLogger.Log(logId, nodeMac, "Login", "Failed");
                return response;
            }

            Console.WriteLine($"Logged into device...{nodeMac}");

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
                Console.WriteLine("device disconnected and will reconnect after 3s");
                await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0);
                UpgradeLogger.Log(logId, nodeMac, "Disconnected", "Success");
            }
            catch (Exception ex)
            {
                UpgradeLogger.Log(logId, nodeMac, "Disconnected", $"Exception: {ex.Message}");
                // continue anyway (device might already have dropped)
            }

            await Task.Delay(3000);

            // Now do the actual programming flow
            return await ProcessingSensorUpgrade(nodeMac, bActor, isBootloader, DetectorType, FirmwareVersion, logId);
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

            const int connectMaxAttempts = 5;
            const int loginMaxAttempts = 3;
            const int bootJumpMaxAttempts = 3;

            async Task<bool> ConnectWithRetryAsync(string stepName)
            {
                for (int attempt = 1; attempt <= connectMaxAttempts; attempt++)
                {
                    try
                    {
                        var cr = await _connectService.ConnectToBleDevice(_gatewayIpAddress, 80, nodeMac);
                        if (cr.Status == HttpStatusCode.OK)
                        {
                            UpgradeLogger.Log(logId, nodeMac, stepName, $"Success (attempt {attempt}/{connectMaxAttempts})");
                            return true;
                        }

                        UpgradeLogger.Log(logId, nodeMac, stepName, $"Failed (attempt {attempt}/{connectMaxAttempts})");
                    }
                    catch (Exception ex)
                    {
                        UpgradeLogger.Log(logId, nodeMac, stepName, $"Exception (attempt {attempt}/{connectMaxAttempts}): {ex.Message}");
                    }

                    int delay = attempt switch
                    {
                        1 => 1500,
                        2 => 3000,
                        3 => 5000,
                        _ => 8000
                    };
                    await Task.Delay(delay);
                }

                return false;
            }

            async Task<bool> LoginWithRetryAsync()
            {
                for (int attempt = 1; attempt <= loginMaxAttempts; attempt++)
                {
                    try
                    {
                        var loginResult = await _connectService.AttemptLogin(_gatewayIpAddress, nodeMac);

                        if (loginResult.ResponseBody.PincodeRequired && !string.IsNullOrEmpty(pincode))
                        {
                            var check = await _cassiaPinCodeService.CheckPincode(_gatewayIpAddress, nodeMac, pincode);
                            loginResult.ResponseBody = check.ResponseBody;
                        }

                        // For actor you DO enforce pincode accepted (keeps your original behavior)
                        if (loginResult.ResponseBody.PincodeRequired && !loginResult.ResponseBody.PinCodeAccepted)
                        {
                            UpgradeLogger.Log(logId, nodeMac, "Login", $"Failed (attempt {attempt}/{loginMaxAttempts})");
                            await Task.Delay(2000);
                            continue;
                        }

                        UpgradeLogger.Log(logId, nodeMac, "LoggedIn", $"Success (attempt {attempt}/{loginMaxAttempts})");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Login", $"Exception (attempt {attempt}/{loginMaxAttempts}): {ex.Message}");
                    }

                    await Task.Delay(2000);
                }

                return false;
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

            Console.WriteLine($"Connected to device...{nodeMac}");

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
                    await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0);
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

            Console.WriteLine($"Logged into device...{nodeMac}");

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
            return await ProcessingActorUpgrade(nodeMac, bActor, DetectorType, FirmwareVersion, logId);
        }

        public async Task<ServiceResponse> BulkUpgradeActorsAsync(List<BulkUpgradeRequest> requests)
        {
            var response = new ServiceResponse
            {
                Success = true,
                StatusCode = 200,
                Message = "Bulk upgrade completed successfully."
            };

            var taskList = new List<Task<ServiceResponse>>();
            var upgradeResults = new ConcurrentBag<ServiceResponse>();
            var semaphore = new SemaphoreSlim(1); // Limit to 3 concurrent upgrades

            foreach (var request in requests)
            {
                string logId = $"{request.MacAddress.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";
                await semaphore.WaitAsync();

                taskList.Add(Task.Run(async () =>
                {
                    try
                    {
                        var result = await UpgradeActorAsync(request.MacAddress, request.Pincode, request.bActor, request.DetctorType, request.FirmwareVersion, logId);
                        upgradeResults.Add(result);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error upgrading actor {request.MacAddress}: {ex.Message}");
                        return new ServiceResponse
                        {
                            Success = false,
                            StatusCode = 500,
                            Message = $"Error upgrading actor {request.MacAddress}: {ex.Message}"
                        };
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(taskList);

            // Aggregate responses to determine overall success
            var failedUpgrades = upgradeResults.Where(r => !r.Success).ToList();
            if (failedUpgrades.Any())
            {
                response.Success = false;
                response.StatusCode = 207; // Multi-Status
                response.Message = $"Bulk upgrade completed with errors. Failed actors: {string.Join(", ", failedUpgrades.Select(r => r.Message))}";
            }

            return response;
        }

        public async Task<List<ServiceResponse>> BulkUpgradeSensorAsync(List<BulkUpgradeRequest> requests)
        {
            var taskList = new List<Task<ServiceResponse>>();
            var upgradeResults = new ConcurrentBag<ServiceResponse>();
            var semaphore = new SemaphoreSlim(3); // Fix comment to match concurrency limit

            foreach (var request in requests)
            {
                string logId = $"{request.MacAddress.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";
                await semaphore.WaitAsync();

                taskList.Add(Task.Run(async () =>
                {
                    try
                    {
                        var result = await UpgradeSensorAsync(request.MacAddress, request.Pincode, request.bActor, false, request.DetctorType, request.FirmwareVersion, logId);
                        result.MacAddress = request.MacAddress;
                        upgradeResults.Add(result);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        var errorResult = new ServiceResponse
                        {
                            Success = false,
                            StatusCode = 500,
                            Message = $"Error upgrading sensor {request.MacAddress}: {ex.Message}",
                            MacAddress = request.MacAddress
                        };
                        upgradeResults.Add(errorResult);
                        return errorResult;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(taskList);

            // Now just return the full list
            return upgradeResults.ToList();
        }

        private sealed class ConnectLoginResult
        {
            public bool Success { get; init; }
            public int StatusCode { get; init; }
            public string Message { get; init; } = "";
            public dynamic? LoginResponseBody { get; init; } // keep dynamic if you don't have a strong type here
            public string? RawStatus { get; init; }
        }


        private async Task<ConnectLoginResult> ConnectAndLoginWithRetryAsync(
            string gatewayIp,
            int gatewayPort,
            string macAddress,
            string? pincode,
            string? logId,
            string? firmwareVersion,
            int maxAttempts = 3,
            int delayBetweenAttemptsMs = 2000)
        {
            Exception? lastEx = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                try
                {
                    UpgradeLogger.Log(logId, macAddress,
                        $"Connect+Login attempt {attempt}/{maxAttempts} (timeout 10s)",
                        "Info", firmwareVersion);

                    Console.WriteLine($"[INFO] Connect+Login attempt {attempt}/{maxAttempts} for {macAddress}");

                    // ---- Run connect + login with timeout ----
                    var attemptTask = Task.Run(async () =>
                    {
                        // 1) Connect
                        var connectionResult = await _connectService
                            .ConnectToBleDevice(gatewayIp, gatewayPort, macAddress)
                            .ConfigureAwait(false);

                        if (connectionResult.Status != HttpStatusCode.OK)
                        {
                            return new ConnectLoginResult
                            {
                                Success = false,
                                StatusCode = (int)connectionResult.Status,
                                Message = $"Connect failed (HTTP {(int)connectionResult.Status} {connectionResult.Status})."
                            };
                        }

                        bool isAlreadyInBootMode = CheckIfDeviceInBootMode(_gatewayIpAddress, macAddress);
                        if (isAlreadyInBootMode)
                        {
                            await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0);
                            return new ConnectLoginResult
                            {
                                Success = false,
                                StatusCode = 409, // Conflict
                                Message = "Device is in boot mode."
                            };
                        }

                        UpgradeLogger.Log(logId, macAddress, "Connected", "Success", firmwareVersion);

                        // 2) Login
                        var loginResult = await _connectService
                            .AttemptLogin(gatewayIp, macAddress)
                            .ConfigureAwait(false);

                        bool pincodeReq = loginResult.ResponseBody.PincodeRequired;
                        if (pincodeReq && !string.IsNullOrEmpty(pincode))
                        {
                            var checkPincodeResponse = await _cassiaPinCodeService
                                .CheckPincode(gatewayIp, macAddress, pincode)
                                .ConfigureAwait(false);

                            loginResult.ResponseBody = checkPincodeResponse.ResponseBody;
                            loginResult.ResponseBody.PincodeRequired = pincodeReq;
                        }

                        var statusText = loginResult.Status?.ToString() ?? "";

                        if (!string.Equals(statusText, "OK", StringComparison.OrdinalIgnoreCase))
                        {
                            return new ConnectLoginResult
                            {
                                Success = false,
                                StatusCode = 401,
                                Message = $"Login failed (Status={statusText}).",
                                LoginResponseBody = loginResult.ResponseBody,
                                RawStatus = statusText
                            };
                        }

                        UpgradeLogger.Log(logId, macAddress, "LoggedIn", "Success", firmwareVersion);

                        return new ConnectLoginResult
                        {
                            Success = true,
                            StatusCode = 200,
                            Message = "Connected + logged in",
                            LoginResponseBody = loginResult.ResponseBody,
                            RawStatus = statusText
                        };
                    }, cts.Token);

                    var completed = await Task.WhenAny(attemptTask, Task.Delay(Timeout.Infinite, cts.Token));

                    if (completed != attemptTask)
                        throw new TimeoutException("Connect+Login timed out after 10 seconds");

                    var result = await attemptTask.ConfigureAwait(false);
                    if (result.Success)
                        return result;

                    lastEx = new Exception(result.Message);
                }
                catch (OperationCanceledException)
                {
                    lastEx = new TimeoutException("Connect+Login timed out after 10 seconds");
                    UpgradeLogger.Log(logId, macAddress,
                        $"Connect+Login timeout attempt {attempt}/{maxAttempts}",
                        "Warn", firmwareVersion);

                    _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0).Wait();
                    UpgradeLogger.Log(logId, macAddress,
                        $"Disconnected after timeout on attempt {attempt}/{maxAttempts}",
                        "Info", firmwareVersion);
                    await Task.Delay(5000);
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    UpgradeLogger.Log(logId, macAddress,
                        $"Connect+Login exception attempt {attempt}/{maxAttempts}: {ex.Message}",
                        "Warn", firmwareVersion);
                    _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0).Wait();
                    UpgradeLogger.Log(logId, macAddress,
                        $"Disconnected after exception on attempt {attempt}/{maxAttempts}",
                        "Info", firmwareVersion);
                    await Task.Delay(5000);


                }

                if (attempt < maxAttempts)
                    await Task.Delay(delayBetweenAttemptsMs).ConfigureAwait(false);
            }

            _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0).Wait();
            
            UpgradeLogger.Log(logId, macAddress,
                $"Disconnected after all Connect+Login attempts failed",
                "Info", firmwareVersion);
            await Task.Delay(5000);

            return new ConnectLoginResult
            {
                Success = false,
                StatusCode = 500,
                Message = $"Connect+Login failed after retries. Last error: {lastEx?.Message}"
            };
        }

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
            var response = new ServiceResponse();

            bool disable_update = false; // used to test restore config without updating firmware

            // NEW: Backup/restore settings (hex strings) to a file before/after upgrade
            string? settingsBackupPath = dev.SettingsBackupPath;

            // Which detector types we support settings backup/restore for.
            static bool SupportsSettingsBackup(string detectorType)
                => detectorType == "P48" || detectorType == "P47" || detectorType == "P46" || detectorType == "P49" || detectorType == "P41" || detectorType == "P42";

            // ---- Local helper: Connect only (robust) ----
            async Task<(bool ok, HttpStatusCode code, string msg)> ConnectOnlyWithRetryAsync(
                int maxAttempts,
                int delayMs,
                string stageName,
                bool logSuccess = true)
            {
                HttpStatusCode last = 0;
                string lastMsg = "Connect failed";

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        var cr = await _connectService.ConnectToBleDevice(_gatewayIpAddress, 80, macAddress).ConfigureAwait(false);
                        last = cr.Status;

                        if (cr.Status == HttpStatusCode.OK)
                        {
                            if (logSuccess)
                                UpgradeLogger.Log(logId, macAddress, stageName, $"Success (attempt {attempt}/{maxAttempts})", FirmwareVersion);
                            return (true, cr.Status, "OK");
                        }

                        UpgradeLogger.Log(logId, macAddress, stageName, $"Failed (attempt {attempt}/{maxAttempts})", FirmwareVersion);
                        lastMsg = $"Connect failed ({cr.Status})";
                    }
                    catch (Exception ex)
                    {
                        UpgradeLogger.Log(logId, macAddress, stageName, $"Exception (attempt {attempt}/{maxAttempts}): {ex.Message}", FirmwareVersion);
                        lastMsg = ex.Message;
                    }

                    // extra cooldown for 417 right after boot transitions
                    if ((int)last == 417)
                        await Task.Delay(delayMs + 4000).ConfigureAwait(false);
                    else
                        await Task.Delay(delayMs).ConfigureAwait(false);
                }

                return (false, last, lastMsg);
            }

            try
            {
                UpgradeLogger.Log(logId, macAddress, "Process Start Device Async", "Success", FirmwareVersion);

                // --------------------------------------------------------------------
                // 0) Determine boot/application mode early (best-effort + robust connect)
                // --------------------------------------------------------------------
                Console.WriteLine($"Getting current FW Verison if possible {macAddress}");

                // IMPORTANT FIX:
                // Your original connect logic was wrong (connect -> if OK connect again -> else block inverted).
                // Replace with a clean connect-only attempt.
                var connProbe = await ConnectOnlyWithRetryAsync(
                    maxAttempts: 5,
                    delayMs: 2000,
                    stageName: "Connected (probe)",
                    logSuccess: false).ConfigureAwait(false);

                if (!connProbe.ok)
                {
                    UpgradeLogger.Log(logId, macAddress, "Connected", "Failed", FirmwareVersion);
                    response.Success = false;
                    response.StatusCode = (int)(connProbe.code == 0 ? HttpStatusCode.ServiceUnavailable : connProbe.code);
                    response.Message = "Failed to connect to device.";
                    dev.LastFailureReason = response.Message;
                    dev.RetryCount++;
                    dev.shouldRetry = false;
                    return response;
                }

                // NOTE: if CheckIfDeviceInBootMode relies on Cassia state, this is now safer.
                var isInBoot = false;
                try
                {
                    isInBoot = CheckIfDeviceInBootMode(_gatewayIpAddress, macAddress);
                }
                catch (Exception ex)
                {
                    UpgradeLogger.Log(logId, macAddress, $"BootMode check exception: {ex.Message}", "Warn", FirmwareVersion);
                }

                if (isInBoot)
                {
                    Console.WriteLine($"Device is in boot mode, skipping FW version check: {macAddress}");
                    UpgradeLogger.Log(logId, macAddress, "Device in boot mode, skipping FW version check", "Info", FirmwareVersion);
                }
                else
                {
                    Console.WriteLine($"Device is in application mode, checking FW version: {macAddress}");
                    UpgradeLogger.Log(logId, macAddress, "Device in application mode, checking FW version", "Info", FirmwareVersion);
                }

                // --------------------------------------------------------------------
                // 1) Settings backup (best-effort but CRITICAL gating before FW update)
                //    (optimized based on logs: more retries + skip when file already exists)
                // --------------------------------------------------------------------
                if (RestoreSettingsAfterUpgrade && !isInBoot)
                {
                    if (SupportsSettingsBackup(DetectorType))
                    {
                        // Only take backup when we will actually update firmware, and only once per device
                        if (!upgradeActor && !upgradeBootloader && !upgradeSensor)
                        {
                            UpgradeLogger.Log(logId, macAddress, "Settings backup skipped (no FW steps in this attempt)", "Info", FirmwareVersion);
                            Console.WriteLine($"Skipping settings backup for {macAddress} - no FW steps in this attempt");
                        }
                        else
                        {
                            // If backup already exists, reuse it and DO NOT block upgrade on connect/login here.
                            if (!string.IsNullOrWhiteSpace(settingsBackupPath) && File.Exists(settingsBackupPath))
                            {
                                UpgradeLogger.Log(logId, macAddress, $"Settings backup already exists: {settingsBackupPath}", "Info", FirmwareVersion);
                                Console.WriteLine($"Settings backup already exists for {macAddress}: {settingsBackupPath}");
                            }
                            else
                            {
                                Console.WriteLine($"Starting settings backup for {macAddress}");

                                try
                                {
                                    // IMPORTANT: increased retries + delays, because logs show 417 after boot transitions.
                                    var cl = await ConnectAndLoginWithRetryAsync(
                                        _gatewayIpAddress, 80, macAddress, pincode, logId, FirmwareVersion,
                                        maxAttempts: 3,
                                        delayBetweenAttemptsMs: 5000).ConfigureAwait(false);

                                    if (!cl.Success)
                                    {
                                        UpgradeLogger.Log(logId, macAddress, $"[1] Connect+login failed: {cl.Message}", "Warn", FirmwareVersion);
                                        Console.WriteLine($"[WARN] [1] Connect+login failed for {macAddress}: {cl.Message}");

                                        // CRITICAL: Do NOT start firmware update if backup cannot be taken.
                                        response.Success = false;
                                        response.StatusCode = cl.StatusCode;
                                        response.Message = $"Settings backup blocked upgrade: {cl.Message}";
                                        dev.LastFailureReason = response.Message;
                                        dev.shouldRetry = false;
                                        return response;
                                    }

                                    if (AutoSetSysFailLevelUnderUpdate && (DetectorType == "P48" || DetectorType == "P47"))
                                    {
                                        if (await DaliSetDeviceSysFailLevelAsync(macAddress, 0xFF))
                                        {
                                            Console.WriteLine($"DALI SysFail Level set to 0xFF for {macAddress}");
                                            UpgradeLogger.Log(logId, macAddress, "DALI SysFail Level set to 0xFF", "Success", FirmwareVersion);
                                        }
                                        else
                                        {
                                            Console.WriteLine($"Failed to set DALI SysFail Level for {macAddress}");
                                            UpgradeLogger.Log(logId, macAddress, "DALI SysFail Level set failed", "Warn", FirmwareVersion);
                                        }
                                    }

                                    var backup = await _settingsBackup
                                        .BackupToFileAsync(macAddress, pincode, DetectorType, FirmwareVersion, logId)
                                        .ConfigureAwait(false);

                                    settingsBackupPath = backup.filePath;

                                    UpgradeLogger.Log(logId, macAddress, "Settings backup saved", "Success", FirmwareVersion);
                                    if (string.IsNullOrWhiteSpace(settingsBackupPath) || !File.Exists(settingsBackupPath))
                                    {
                                        response.Success = false;
                                        response.StatusCode = 500;
                                        response.Message = $"Settings backup failed (file missing): {settingsBackupPath}";
                                        UpgradeLogger.Log(logId, macAddress, response.Message, "Failed", FirmwareVersion);
                                        dev.LastFailureReason = response.Message;
                                        dev.shouldRetry = false;
                                        return response;
                                    }

                                    Console.WriteLine($"[INFO] Settings backup saved for {macAddress} to: {settingsBackupPath}");
                                    dev.SettingsBackupPath = settingsBackupPath;

                                    if (_VERBOSE)
                                    {
                                        Console.WriteLine(
                                            $"[VERBOSE] Settings backup snapshot for {macAddress}:\n" +
                                            System.Text.Json.JsonSerializer.Serialize(
                                                backup.snapshot,
                                                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
                                            )
                                        );
                                    }
                                }
                                catch (Exception ex)
                                {
                                    response.Success = false;
                                    response.StatusCode = 500;
                                    response.Message = $"Settings backup failed: {ex.Message}";
                                    UpgradeLogger.Log(logId, macAddress, response.Message, "Failed", FirmwareVersion);
                                    Console.WriteLine($"[ERROR] Settings backup failed for {macAddress}: {ex}");
                                    dev.LastFailureReason = response.Message;
                                    dev.shouldRetry = false;
                                    return response;
                                }
                            }
                        }
                    }
                    else
                    {
                        UpgradeLogger.Log(logId, macAddress, "Settings backup skipped (not P47 or P48)", "Info", FirmwareVersion);
                    }
                }

                // --------------------------------------------------------------------
                // 2) Upgrade steps (keep your flow, but add cooldown when switching modes)
                // --------------------------------------------------------------------
                var stopwatch = new Stopwatch();
                
                //No need to do this anymore, as we do it after the sensor application, and then reboots the sensor.
                
                /*
                if (upgradeActor && !disable_update && !isInBoot) // can't update actor first if in bootloader mode
                {
                    Console.WriteLine($"Starting actor upgrade for {macAddress}");
                    dev.RetryCountActor++;

                    stopwatch.Restart();
                    var actorUpgradeResult = await UpgradeActorAsync(macAddress, pincode, true, DetectorType, FirmwareVersion, logId)
                        .ConfigureAwait(false);
                    stopwatch.Stop();

                    Console.WriteLine($"Actor upgrade completed for {macAddress}. Time taken: {stopwatch.Elapsed.TotalSeconds} seconds - result: {actorUpgradeResult.Success}");
                    dev.ActorSuccess = actorUpgradeResult.Success;

                    await Task.Delay(20000).ConfigureAwait(false);
                }
                */

                if (upgradeBootloader && !disable_update)
                {
                    dev.RetryCountBootloader++;
                    Console.WriteLine($"Starting bootloader upgrade for {macAddress}");

                    // cooldown before bootloader step often helps after actor step
                    await Task.Delay(5000).ConfigureAwait(false);

                    stopwatch.Restart();
                    var bootladerUpgradeResult = await UpgradeSensorAsync(macAddress, pincode, false, true, DetectorType, FirmwareVersion, logId)
                        .ConfigureAwait(false);
                    stopwatch.Stop();

                    Console.WriteLine($"Bootloader upgrade completed for {macAddress}. Time taken: {stopwatch.Elapsed.TotalSeconds} seconds - result: {bootladerUpgradeResult.Success}");

                    if (!bootladerUpgradeResult.Success)
                    {
                        response.Success = false;
                        response.StatusCode = bootladerUpgradeResult.StatusCode;
                        response.Message = $"bootloader upgrade failed: {bootladerUpgradeResult.Message}";
                        dev.BootloaderSuccess = false;
                        return response;
                    }

                    dev.BootloaderSuccess = true;
                    await Task.Delay(20000).ConfigureAwait(false);
                }

                if (upgradeSensor && !disable_update)
                {
                    Console.WriteLine($"Starting Sensor upgrade for {macAddress}");
                    dev.RetryCountSensor++;

                    // IMPORTANT: after JumpToBootloader, Cassia often needs a longer cool-down before next Connect+Login
                    await Task.Delay(8000).ConfigureAwait(false);

                    stopwatch.Restart();
                    var sensorUpgradeResult = await UpgradeSensorAsync(macAddress, pincode, false, false, DetectorType, FirmwareVersion, logId)
                        .ConfigureAwait(false);
                    stopwatch.Stop();

                    Console.WriteLine($"Sensor upgrade completed for {macAddress}. Time taken: {stopwatch.Elapsed.TotalSeconds} seconds - result: {sensorUpgradeResult.Success}");

                    if (!sensorUpgradeResult.Success)
                    {
                        response.Success = false;
                        response.StatusCode = sensorUpgradeResult.StatusCode;
                        response.Message = $"Sensor upgrade failed: {sensorUpgradeResult.Message}";
                        dev.SensorSuccess = false;
                        return response;
                    }

                    dev.SensorSuccess = true;
                    await Task.Delay(20000).ConfigureAwait(false);
                }
                else
                {
                    // Consider sensor step satisfied when skipped (e.g., already at target FW).
                    dev.SensorSuccess = true;
                }

                // --------------------------------------------------------------------
                // 3) Restore settings right after sensor upgrade (do NOT reboot here)
                //    The rest of the flow (actor, reboot, 102 restore) stays after actor.
                // --------------------------------------------------------------------
                if (RestoreSettingsAfterUpgrade && !isInBoot && SupportsSettingsBackup(DetectorType))
                {
                    await Task.Delay(10000).ConfigureAwait(false);

                    Console.WriteLine($"Starting settings restore for {macAddress} - trying to connect and login");

                    var cl = await ConnectAndLoginWithRetryAsync(
                        _gatewayIpAddress, 80, macAddress, pincode, logId, FirmwareVersion,
                        maxAttempts: 4,
                        delayBetweenAttemptsMs: 6000).ConfigureAwait(false);

                    if (!cl.Success)
                    {
                        UpgradeLogger.Log(logId, macAddress, $"Restore connect+login failed: {cl.Message}", "Warn", FirmwareVersion);
                        Console.WriteLine($"[WARN] Restore connect+login failed for {macAddress}: {cl.Message}");

                        response.Success = false;
                        response.StatusCode = 500;
                        response.Message = "Could not connect and login to detector!";

                        if (dev.requiresConfigRestore)
                        {
                            dev.LastFailureReason = response.Message;
                            dev.shouldRetry = false;
                            return response;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Starting settings restore for {macAddress} - upload config");
                        settingsBackupPath ??= dev.SettingsBackupPath;

                        if (!string.IsNullOrWhiteSpace(settingsBackupPath))
                        {
                            try
                            {
                                ServiceResponse restore = new ServiceResponse { Success = false, StatusCode = 500, Message = "Restore not attempted" };
                                for (int attempt = 1; attempt <= 3; attempt++)
                                {
                                    restore = await _settingsBackup.RestoreFromFileAsync(
                                            macAddress,
                                            pincode,
                                            DetectorType,
                                            FirmwareVersion,
                                            settingsBackupPath,
                                            logId)
                                        .ConfigureAwait(false);

                                    UpgradeLogger.Log(
                                        logId,
                                        macAddress,
                                        $"Settings restore attempt {attempt}/3: {(restore.Success ? "Success" : "Fail")} - {restore.Message}",
                                        restore.Success ? "Success" : "Failed",
                                        FirmwareVersion);

                                    Console.WriteLine($"[INFO] Settings restore attempt {attempt}/3 for {macAddress} - result: {restore.Success} - {restore.Message}");

                                    if (restore.Success)
                                        break;

                                    await Task.Delay(4000).ConfigureAwait(false);
                                }

                                dev.isConfigRestored = restore.Success;

                                if (!restore.Success && dev.requiresConfigRestore)
                                {
                                    response.Success = false;
                                    response.StatusCode = restore.StatusCode;
                                    response.Message = $"Settings restore failed after retries: {restore.Message}";
                                    dev.LastFailureReason = response.Message;
                                    dev.shouldRetry = false;
                                    return response;
                                }
                            }
                            catch (Exception ex)
                            {
                                UpgradeLogger.Log(logId, macAddress, $"Settings restore failed: {ex.Message}", "Failed", FirmwareVersion);
                                Console.WriteLine($"[ERROR] Settings restore failed for {macAddress}: {ex}");
                                dev.isConfigRestored = false;

                                if (dev.requiresConfigRestore)
                                {
                                    response.Success = false;
                                    response.StatusCode = 500;
                                    response.Message = $"Settings restore exception: {ex.Message}";
                                    dev.LastFailureReason = response.Message;
                                    dev.shouldRetry = false;
                                    dev.finalUpgradeResult = "Warn";
                                    return response;
                                }
                            }
                        }
                        else
                        {
                            UpgradeLogger.Log(logId, macAddress, "Settings restore skipped (no backup file available)", "Failed", FirmwareVersion);
                            Console.WriteLine($"[ERROR] Settings restore skipped for {macAddress} - no backup file available");

                            if (dev.requiresConfigRestore)
                            {
                                dev.shouldRetry = false;
                                dev.finalUpgradeResult = "Warn";
                            }
                        }

                        await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0).ConfigureAwait(false);
                    }
                }

                if (dev.ActorSuccess != true && dev.isActorUpgradeNeeded)
                {
                    if (upgradeActor && !disable_update)
                    {
                        Console.WriteLine($"Starting actor upgrade for {macAddress}");
                        dev.RetryCountActor++;

                        stopwatch.Restart();
                        var actorUpgradeResult = await UpgradeActorAsync(macAddress, pincode, true, DetectorType, FirmwareVersion, logId)
                            .ConfigureAwait(false);
                        stopwatch.Stop();

                        Console.WriteLine($"Retry Actor upgrade after sensor application completed for {macAddress}. Time taken: {stopwatch.Elapsed.TotalSeconds} seconds - result: {actorUpgradeResult.Success}");
                        dev.ActorSuccess = actorUpgradeResult.Success;

                        if (!actorUpgradeResult.Success)
                        {
                            response.Success = false;
                            response.StatusCode = actorUpgradeResult.StatusCode;
                            response.Message = $"Actor upgrade failed again after sensor application completed: {actorUpgradeResult.Message}";
                            return response;
                        }

                        await Task.Delay(1000).ConfigureAwait(false);
                    }
                }

                // --------------------------------------------------------------------
                // 3) Post-actor steps (keep existing flow here)
                //    - Reboot after actor (if configured)
                //    - 102 restore (P47/P48 only)
                // --------------------------------------------------------------------
                if (SupportsSettingsBackup(DetectorType))
                {
                    // Only DALI masters need login + sysfail + 102 restore
                    var isDaliMaster = DetectorType == "P48" || DetectorType == "P47";

                    if (isDaliMaster)
                    {
                        await Task.Delay(10000).ConfigureAwait(false);

                        Console.WriteLine($"Post-actor: connect+login for {macAddress}");

                        var cl = await ConnectAndLoginWithRetryAsync(
                            _gatewayIpAddress, 80, macAddress, pincode, logId, FirmwareVersion,
                            maxAttempts: 4,
                            delayBetweenAttemptsMs: 6000).ConfigureAwait(false);

                        if (!cl.Success)
                        {
                            UpgradeLogger.Log(logId, macAddress, $"Post-actor connect+login failed: {cl.Message}", "Warn", FirmwareVersion);
                            response.Success = false;
                            response.StatusCode = 500;
                            response.Message = "Could not connect and login to detector!";
                            return response;
                        }

                        if (AutoSetSysFailLevelUnderUpdate)
                        {
                            if (await DaliSetDeviceSysFailLevelAsync(macAddress, 0xFE))
                                UpgradeLogger.Log(logId, macAddress, "DALI SysFail Level set to 0xFE", "Success", FirmwareVersion);
                            else
                                UpgradeLogger.Log(logId, macAddress, "DALI SysFail Level set failed", "Warn", FirmwareVersion);
                        }
                    }

                    if (RebootDetectorAfterUpgrade)
                    {
                        Console.WriteLine($"Rebooting device {macAddress} after actor update");
                        await RebootDeviceAsync(macAddress).ConfigureAwait(false);
                        UpgradeLogger.Log(logId, macAddress, "Device rebooted after actor update", "Success", FirmwareVersion);
                        await Task.Delay(10000).ConfigureAwait(false);

                        if (isDaliMaster)
                        {
                            await ConnectAndLoginWithRetryAsync(
                                _gatewayIpAddress, _gatewayPort, macAddress, pincode, logId, FirmwareVersion,
                                maxAttempts: 3,
                                delayBetweenAttemptsMs: 2000).ConfigureAwait(false);
                        }
                    }

                    if (isDaliMaster && Restore102DBAfterUpgrade)
                    {
                        bool resp = false;
                        for (int attempt = 1; attempt <= 3; attempt++)
                        {
                            resp = await DaliRestore102Database(macAddress).ConfigureAwait(false);
                            Console.WriteLine($"Dali Restore 102 Database attempt {attempt}/3 response: {resp} for {macAddress}");
                            UpgradeLogger.Log(logId, macAddress, $"Dali Restore 102 Database attempt {attempt}/3 response: {resp}", resp ? "Success" : "Failed", FirmwareVersion);
                            if (resp) break;
                            await Task.Delay(3000).ConfigureAwait(false);
                        }

                        dev.restore102Success = resp;
                        if (!resp && dev.requires102Restore)
                        {
                            response.Success = false;
                            response.StatusCode = 500;
                            response.Message = "DALI Restore 102 Database failed after retries";
                            dev.LastFailureReason = response.Message;
                            dev.shouldRetry = false;
                            dev.finalUpgradeResult = "Failed";
                            return response;
                        }
                    }

                    await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0).ConfigureAwait(false);
                }
                else
                {
                    UpgradeLogger.Log(logId, macAddress, $"Post-actor steps skipped (unsupported detector '{DetectorType}')", "Info", FirmwareVersion);
                }

                // --------------------------------------------------------------------
                // 4) Final success
                // --------------------------------------------------------------------
                response.Success = true;
                response.StatusCode = 200;
                response.Message = "Sensor and actor upgrades completed successfully.";
                if (dev.finalUpgradeResult != "Warn")
                    dev.finalUpgradeResult = "Success";

                UpgradeLogger.Log(logId, macAddress, "Device Upgrade Task Done.", "Success", FirmwareVersion);
                return response;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during sensor and actor upgrade: {ex.Message}");
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "An unexpected error occurred during the upgrade process.";
                UpgradeLogger.Log(logId, macAddress, $"Device Upgrade Failed: {ex.Message}", "Failed", FirmwareVersion);
                return response;
            }
            finally
            {
                await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0).ConfigureAwait(false);
                UpgradeLogger.Log(logId, macAddress, "Disconnected at the end of upgrade process", "Info", FirmwareVersion);

            }
        }


        public async Task<List<UpgradeResponse>> UpgradeBLSensorsAsync(List<BulkUpgradeRequest> devices)
        {
            var responses = new Dictionary<string, UpgradeResponse>(); // Stores latest response for each device
            var failedDevices = new Queue<(BulkUpgradeRequest, int)>(); // (Device, Retry Count)

            foreach (var device in devices)
            {
                string logId = $"{device.MacAddress.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";
                sensorType = device.DetctorType;
                var response = await UpgradeBLSensorWithRetryAsync(device, 0, logId);
                responses[device.MacAddress] = response; // Always store latest response

                if (!response.Success)
                {
                    failedDevices.Enqueue((device, 1)); // Initial retry count is 1
                }

                Console.WriteLine("Next Device will be upgraded after 10 seconds");

            }

            Console.WriteLine($"Initial upgrade completed. Retrying failed devices: {failedDevices.Count} devices.");

            while (failedDevices.Count > 0)
            {
                var (device, retryCount) = failedDevices.Dequeue();
                string logId = $"{device.MacAddress.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";
                var response = await UpgradeBLSensorWithRetryAsync(device, retryCount, logId);
                responses[device.MacAddress] = response; // Overwrite previous responses

                if (!response.Success && retryCount < 2) // Retry up to 2 times
                {
                    failedDevices.Enqueue((device, retryCount + 1));
                }
            }

            return responses.Values.ToList(); // Return only the latest responses
        }



        private async Task<UpgradeResponse> UpgradeBLSensorWithRetryAsync(BulkUpgradeRequest device, int retryCount, string logId)
        {
            var response = new UpgradeResponse
            {
                MacAddress = device.MacAddress,
                RetryCount = retryCount
            };

            try
            {
                Stopwatch stopwatch = new Stopwatch();
                Console.WriteLine($"Starting bootloader upgrade for {device.MacAddress}, Attempt {retryCount + 1}");
                stopwatch.Restart();

                // Step 1: Bootloader Upgrade
                var bootloaderUpgradeResult = await UpgradeSensorAsync(device.MacAddress, device.Pincode, false, true, device.DetctorType, device.FirmwareVersion, logId);
                stopwatch.Stop();
                Console.WriteLine($"Bootloader upgrade completed for {device.MacAddress}. Time taken: {stopwatch.Elapsed.TotalSeconds} seconds");

                if (!bootloaderUpgradeResult.Success)
                {
                    Console.WriteLine($"Bootloader upgrade failed for {device.MacAddress}. Skipping sensor upgrade.");
                    response.Success = false;
                    response.StatusCode = bootloaderUpgradeResult.StatusCode;
                    response.Message = $"Bootloader upgrade failed: {bootloaderUpgradeResult.Message}";
                    return response;
                }

                // Allow bootloader transition delay
                await Task.Delay(10000);

                Console.WriteLine($"Starting sensor upgrade for {device.MacAddress}, Attempt {retryCount + 1}");

                // Step 2: Sensor Upgrade (Only if Bootloader upgrade succeeded)
                stopwatch.Restart();
                var sensorUpgradeResult = await UpgradeSensorAsync(device.MacAddress, device.Pincode, false, false, device.DetctorType, device.FirmwareVersion, logId);
                stopwatch.Stop();
                Console.WriteLine($"Sensor upgrade completed for {device.MacAddress}. Time taken: {stopwatch.Elapsed.TotalSeconds} seconds");

                if (!sensorUpgradeResult.Success)
                {
                    response.Success = false;
                    response.StatusCode = sensorUpgradeResult.StatusCode;
                    response.Message = $"Sensor upgrade failed: {sensorUpgradeResult.Message}";
                    return response;
                }

                response.Success = true;
                response.StatusCode = 200;
                response.Message = "Sensor and bootloader upgrades completed successfully.";
                return response;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during sensor and bootloader upgrade for {device.MacAddress}: {ex.Message}");
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "An unexpected error occurred during the upgrade process.";
                return response;
            }
        }

        public async Task<ServiceResponse> BulkUpgradeDevicesAsync(List<BulkUpgradeRequest> requests, int numberOfParallelThreads = -1)
        {
            if (numberOfParallelThreads == -1)
                numberOfParallelThreads = GlobalnumberOfParallelThreads;

            var progressList = requests.Select(req => new UpgradeProgress { MacAddress = req.MacAddress, Pincode = req.Pincode, DetectotType = req.DetctorType, FirmwareVersion = req.FirmwareVersion, CurrentFirmwareVersion = req.CurrentFirmwareVersion }).ToList();

            // Phase 1: Initial Upgrades
            await UpgradeDevicesInParallel(progressList, numberOfParallelThreads);

            // Prepare Final Report
            var successfulDevices = progressList.Where(d => d.IsFullyUpgraded).Select(d => d.MacAddress).ToList();
            var failedDevices = progressList.Where(d => !d.IsFullyUpgraded).Select(d => new { d.MacAddress, d.LastFailureReason }).ToList();

            return new ServiceResponse
            {
                Success = failedDevices.Count == 0,
                StatusCode = failedDevices.Count == 0 ? 200 : 207,
                Message = failedDevices.Count == 0 ? "All devices upgraded successfully." : "Some devices failed to upgrade after retries."
                //,
                //Data = new { SuccessfulDevices = successfulDevices, FailedDevices = failedDevices }
            };
        }

        public int UpgradeDevicesInProgress = 0;

        //private async Task UpgradeDevicesInParallel(List<UpgradeProgress> devices, int numbersOfThreadsInParallel = 2)
        //{
        //    int maxRetriesPerComponent = 3;
        //    Interlocked.Add(ref UpgradeDevicesInProgress, devices.Count);

        //    if (numbersOfThreadsInParallel > 1)
        //    {
        //        Console.WriteLine("Upgrade devices - PRALLEL MODE");
        //        SmartThreadPool smartThreadPool = new SmartThreadPool();
        //        smartThreadPool.MaxThreads = numbersOfThreadsInParallel; //max flash devices in the same time

        //        foreach (var device in devices)
        //        {
        //            smartThreadPool.QueueWorkItem(async dev =>
        //            {
        //                string logId = $"{dev.MacAddress.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";
        //                dev.RetryCount = 0;
        //                dev.RetryCountActor = 0;
        //                dev.RetryCountBootloader = 0;
        //                dev.RetryCountSensor = 0;
        //                Console.WriteLine($"Starting upgrade for device {dev.MacAddress}");
        //                await UpgradeDeviceAsync(dev, dev.MacAddress, dev.Pincode, dev.DetectotType, dev.FirmwareVersion, true, true, true, logId);
        //                while (!dev.IsFullyUpgraded && (dev.RetryCountActor < 2 * maxRetriesPerComponent
        //                                            || dev.RetryCountBootloader < maxRetriesPerComponent
        //                                            || dev.RetryCountSensor < maxRetriesPerComponent))
        //                {
        //                    Task.Delay(10000).Wait();
        //                    dev.RetryCount++;
        //                    Console.WriteLine($"Retry upgrade for device {dev.MacAddress} - Retry {dev.RetryCount}");
        //                    await UpgradeDeviceAsync(dev, dev.MacAddress, dev.Pincode, dev.DetectotType, dev.FirmwareVersion, !dev.ActorSuccess, !dev.BootloaderSuccess, !dev.SensorSuccess, logId);
        //                }

        //                Console.WriteLine($">>>> THREAD END - {dev.MacAddress} - actor: {dev.ActorSuccess}:{dev.RetryCountActor} - bootloader: {dev.BootloaderSuccess}:{dev.RetryCountBootloader} - sensor: {dev.SensorSuccess}:{dev.RetryCountSensor}");

        //                Interlocked.Decrement(ref UpgradeDevicesInProgress);
        //            }, device);

        //        }

        //        smartThreadPool.WaitForIdle();
        //    }
        //    else
        //    {
        //        Console.WriteLine("Upgrade devices - SEQUENTIAL MODE");
        //        foreach (var dev in devices)
        //        {
        //            string logId = $"{dev.MacAddress.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";
        //            dev.RetryCount = 0;
        //            dev.RetryCountActor = 0;
        //            dev.RetryCountBootloader = 0;
        //            dev.RetryCountSensor = 0;
        //            Console.WriteLine($"Starting upgrade for device {dev.MacAddress}");
        //            await UpgradeDeviceAsync(dev, dev.MacAddress, dev.Pincode, dev.DetectotType, dev.FirmwareVersion, true, true, true, logId);
        //            while (!dev.IsFullyUpgraded && (dev.RetryCountActor < 2 * maxRetriesPerComponent
        //                                        || dev.RetryCountBootloader < maxRetriesPerComponent
        //                                        || dev.RetryCountSensor < maxRetriesPerComponent))
        //            {
        //                Task.Delay(10000).Wait();
        //                dev.RetryCount++;
        //                Console.WriteLine($"Retry upgrade for device {dev.MacAddress} - Retry {dev.RetryCount}");
        //                await UpgradeDeviceAsync(dev, dev.MacAddress, dev.Pincode, dev.DetectotType, dev.FirmwareVersion, !dev.ActorSuccess, !dev.BootloaderSuccess, !dev.SensorSuccess, logId);
        //            }

        //            Console.WriteLine($">>>> THREAD END - {dev.MacAddress} - actor: {dev.ActorSuccess}:{dev.RetryCountActor} - bootloader: {dev.BootloaderSuccess}:{dev.RetryCountBootloader} - sensor: {dev.SensorSuccess}:{dev.RetryCountSensor}");

        //            Interlocked.Decrement(ref UpgradeDevicesInProgress);
        //        }
        //    }
        //}

        private sealed class DeviceUpgradeSummary
        {
            public string Mac { get; init; } = "";
            public string DetectorType { get; init; } = "";
            public string TargetFw { get; init; } = "";
            public string CurrentFw { get; init; } = "";

            public bool ActorNeeded { get; init; }
            public bool BootloaderNeeded { get; init; }

            public bool ActorSuccess { get; init; }
            public bool BootloaderSuccess { get; init; }
            public bool SensorSuccess { get; init; }
            public bool IsFullyUpgraded { get; init; }
            public bool ConfigRestored { get; init; }

            public int RetryTotal { get; init; }
            public int RetryActor { get; init; }
            public int RetryBootloader { get; init; }
            public int RetrySensor { get; init; }

            public double Seconds { get; init; }
            public string Status { get; init; } = "OK"; // OK / SKIPPED / ERROR
            public string? Error { get; init; }
        }

        public async Task<string> GetFwVersion(string macAddress, string pincode, bool disconnect_on_finish = false)
        {
            try
            {
                var cl = await ConnectAndLoginWithRetryAsync(
                    _gatewayIpAddress, 80, macAddress, pincode, null, null,
                    maxAttempts: 3,
                    delayBetweenAttemptsMs: 2000).ConfigureAwait(false);
                if (!cl.Success)
                {
                    Console.WriteLine($"[WARN] Connect+login failed for {macAddress}: {cl.Message}");
                    return "";
                }
                else
                {

                    //Get the FW Version

                    string sensorInfo = "";
                    string actorInfo = "";

                    // Sensor
                    string sensorCommand = "01290107005A5E";
                    var sensorResponse = await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, macAddress, sensorCommand);
                    if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
                    {
                        sensorInfo = ScanDataParser.ParseSoftwareVersionFromResponse(sensorResponse.Data);
                    }

                    // Actor
                    string actorCommand = "012B01070032B3";
                    var actorResponse = await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, macAddress, actorCommand);
                    if (actorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(actorResponse.Data))
                    {
                        actorInfo = ScanDataParser.ParseSoftwareVersionFromResponse(actorResponse.Data);
                    }

                    Console.WriteLine($"{macAddress} - Get this Version: Sensor: {sensorInfo} | Actor: {actorInfo}");
                    return ($"Sensor: {sensorInfo} | Actor: {actorInfo}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] GetFwVersion exception for {macAddress}: {ex}");
            }
            finally
            {
                if (disconnect_on_finish)
                {
                    await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress);
                }
            }
            return "";
        }

        /// <summary>
        /// Disconnect a device from the Cassia gateway (best-effort).
        /// Intended for MQTT command "disconnect-devices".
        /// </summary>
        public async Task<bool> DisconnectDeviceAsync(string macAddress)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(macAddress)) return false;
                var resp = await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress.Trim(), 0).ConfigureAwait(false);
                // resp.Status is HttpStatusCode (non-nullable); avoid null-propagation on value type.
                return resp != null && resp.Status == HttpStatusCode.OK;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Identify a device by connecting to it, optionally checking pincode + logging in (skipped in boot mode),
        /// keeping the connection for a specified duration, then disconnecting again.
        /// This is used by the MQTT "identify" command.
        /// </summary>
        public async Task<IdentifyResult> IdentifyDeviceAsync(
            string macAddress,
            string? pincode,
            int secondsToStayConnected = 15,
            int maxConnectAttempts = 1,
            CancellationToken ct = default,
            Func<object, Task>? report = null)
        {
            var mac = (macAddress ?? string.Empty).Trim();
            var result = new IdentifyResult { Mac = mac };

            async Task SafeReportAsync(object payload)
            {
                if (report == null) return;
                try { await report(payload).ConfigureAwait(false); }
                catch { /* ignore reporting errors */ }
            }

            if (string.IsNullOrWhiteSpace(mac))
            {
                result.Success = false;
                result.Mac = macAddress ?? "";
                result.Error = "Missing mac address";
                await SafeReportAsync(new
                {
                    success = false,
                    stage = "failed",
                    mac = result.Mac,
                    errorStep = "validate",
                    error = result.Error,
                    time = DateTimeOffset.UtcNow
                }).ConfigureAwait(false);
                return result;
            }

            secondsToStayConnected = secondsToStayConnected <= 0 ? 15 : secondsToStayConnected;
            maxConnectAttempts = maxConnectAttempts <= 0 ? 1 : maxConnectAttempts;

            bool connected = false;

            try
            {
                // 1) Connect with retry
                ResponseModel? lastConnect = null;
                for (int attempt = 1; attempt <= maxConnectAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();

                    lastConnect = await _connectService
                        .ConnectToBleDevice(_gatewayIpAddress, _gatewayPort, mac)
                        .ConfigureAwait(false);

                    if (lastConnect != null && lastConnect.Status == HttpStatusCode.OK)
                    {
                        connected = true;
                        result.Connected = true;
                        await SafeReportAsync(new
                        {
                            success = true,
                            stage = "connected",
                            mac = mac,
                            time = DateTimeOffset.UtcNow,
                            connectAttempt = attempt,
                            maxConnectAttempts
                        }).ConfigureAwait(false);
                        break;
                    }

                    result.ErrorStep = "connect";
                    result.Error = lastConnect?.Data ?? "Connect failed";

                    if (attempt < maxConnectAttempts)
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                }

                if (!connected)
                {
                    result.Success = false;
                    await SafeReportAsync(new
                    {
                        success = false,
                        stage = "failed",
                        mac = mac,
                        time = DateTimeOffset.UtcNow,
                        errorStep = result.ErrorStep,
                        error = result.Error,
                        maxConnectAttempts
                    }).ConfigureAwait(false);
                    return result;
                }

                // 2) Detect boot mode
                result.IsBootMode = CheckIfDeviceInBootMode(_gatewayIpAddress, mac);

                await SafeReportAsync(new
                {
                    success = true,
                    stage = "bootmode-check",
                    mac = mac,
                    time = DateTimeOffset.UtcNow,
                    isBootMode = result.IsBootMode
                }).ConfigureAwait(false);

                // 3) Optional pincode + login (skip in boot mode)
                if (result.IsBootMode)
                {
                    result.LoginSkippedBootMode = true;
                    result.PincodeOk = true; // not applicable in boot mode

                    await SafeReportAsync(new
                    {
                        success = true,
                        stage = "login-skipped",
                        reason = "bootmode",
                        mac = mac,
                        time = DateTimeOffset.UtcNow
                    }).ConfigureAwait(false);
                }
                else
                {
                    // Login (FIRST) – determines whether a pincode is required.
                    // We must NOT try pincode checks up front, because many devices never require it.
                    var login = await _connectService.AttemptLogin(_gatewayIpAddress, mac).ConfigureAwait(false);
                    var pincodeRequired = login?.ResponseBody?.PincodeRequired == true;
                    var pinAccepted = login?.ResponseBody?.PinCodeAccepted == true;
                    result.LoggedIn = login?.ResponseBody?.Status == HttpStatusCode.OK;

                    if (result.LoggedIn)
                    {
                        // Logged in without needing pincode (or it was already accepted).
                        result.PincodeOk = true;

                        await SafeReportAsync(new
                        {
                            success = true,
                            stage = "logged-in",
                            mac = mac,
                            time = DateTimeOffset.UtcNow,
                            pincodeRequired
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        // If pincode is required and not accepted, and we have a pincode, then try pincode + login again.
                        if (pincodeRequired && !pinAccepted)
                        {
                            await SafeReportAsync(new
                            {
                                success = true,
                                stage = "pincode-required",
                                mac = mac,
                                time = DateTimeOffset.UtcNow
                            }).ConfigureAwait(false);

                            if (string.IsNullOrWhiteSpace(pincode))
                            {
                                result.Success = false;
                                result.PincodeOk = false;
                                result.ErrorStep = "pincode";
                                result.Error = "Pincode required, but no pincode was provided.";

                                await SafeReportAsync(new
                                {
                                    success = false,
                                    stage = "failed",
                                    mac = mac,
                                    time = DateTimeOffset.UtcNow,
                                    errorStep = result.ErrorStep,
                                    error = result.Error
                                }).ConfigureAwait(false);
                                return result;
                            }

                            try
                            {
                                var check = await _cassiaPinCodeService
                                    .CheckPincode(_gatewayIpAddress, mac, pincode)
                                    .ConfigureAwait(false);

                                result.PincodeOk = check?.ResponseBody?.PinCodeAccepted == true;
                                if (!result.PincodeOk)
                                {
                                    result.Success = false;
                                    result.ErrorStep = "pincode";
                                    result.Error = check?.ResponseBody?.Data ?? "Pincode not accepted";

                                    await SafeReportAsync(new
                                    {
                                        success = false,
                                        stage = "failed",
                                        mac = mac,
                                        time = DateTimeOffset.UtcNow,
                                        errorStep = result.ErrorStep,
                                        error = result.Error
                                    }).ConfigureAwait(false);
                                    return result;
                                }

                                await SafeReportAsync(new
                                {
                                    success = true,
                                    stage = "pincode-ok",
                                    mac = mac,
                                    time = DateTimeOffset.UtcNow
                                }).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                result.Success = false;
                                result.PincodeOk = false;
                                result.ErrorStep = "pincode";
                                result.Error = ex.Message;

                                await SafeReportAsync(new
                                {
                                    success = false,
                                    stage = "failed",
                                    mac = mac,
                                    time = DateTimeOffset.UtcNow,
                                    errorStep = result.ErrorStep,
                                    error = result.Error
                                }).ConfigureAwait(false);
                                return result;
                            }

                            // Retry login after successful pincode.
                            var login2 = await _connectService.AttemptLogin(_gatewayIpAddress, mac).ConfigureAwait(false);
                            result.LoggedIn = login2?.ResponseBody?.Status == HttpStatusCode.OK;
                            if (!result.LoggedIn)
                            {
                                result.Success = false;
                                result.ErrorStep = "login";
                                result.Error = login2?.ResponseBody?.Data ?? (login2?.Status ?? "Login failed");

                                await SafeReportAsync(new
                                {
                                    success = false,
                                    stage = "failed",
                                    mac = mac,
                                    time = DateTimeOffset.UtcNow,
                                    errorStep = result.ErrorStep,
                                    error = result.Error
                                }).ConfigureAwait(false);
                                return result;
                            }

                            await SafeReportAsync(new
                            {
                                success = true,
                                stage = "logged-in",
                                mac = mac,
                                time = DateTimeOffset.UtcNow,
                                pincodeRequired = true,
                                pincodeUsed = true
                            }).ConfigureAwait(false);
                        }
                        else
                        {
                            // Login failed for another reason.
                            result.Success = false;
                            result.ErrorStep = "login";
                            result.Error = login?.ResponseBody?.Data ?? (login?.Status ?? "Login failed");

                            await SafeReportAsync(new
                            {
                                success = false,
                                stage = "failed",
                                mac = mac,
                                time = DateTimeOffset.UtcNow,
                                errorStep = result.ErrorStep,
                                error = result.Error
                            }).ConfigureAwait(false);
                            return result;
                        }
                    }
                }

                // 4) Hold connection
                var remaining = TimeSpan.FromSeconds(secondsToStayConnected);
                var step = TimeSpan.FromMilliseconds(250);
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < remaining)
                {
                    ct.ThrowIfCancellationRequested();
                    var delay = remaining - sw.Elapsed;
                    if (delay <= TimeSpan.Zero) break;
                    await Task.Delay(delay < step ? delay : step, ct).ConfigureAwait(false);
                }

                result.Success = true;
                result.SecondsConnected = secondsToStayConnected;

                await SafeReportAsync(new
                {
                    success = true,
                    stage = "holding",
                    mac = mac,
                    time = DateTimeOffset.UtcNow,
                    seconds = secondsToStayConnected
                }).ConfigureAwait(false);
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorStep = result.ErrorStep ?? "canceled";
                result.Error = "Operation canceled";

                await SafeReportAsync(new
                {
                    success = false,
                    stage = "failed",
                    mac = mac,
                    time = DateTimeOffset.UtcNow,
                    errorStep = result.ErrorStep,
                    error = result.Error
                }).ConfigureAwait(false);
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorStep = result.ErrorStep ?? "exception";
                result.Error = ex.Message;

                await SafeReportAsync(new
                {
                    success = false,
                    stage = "failed",
                    mac = mac,
                    time = DateTimeOffset.UtcNow,
                    errorStep = result.ErrorStep,
                    error = result.Error
                }).ConfigureAwait(false);
                return result;
            }
            finally
            {
                // Best-effort disconnect if we managed to connect.
                if (connected)
                {
                    try
                    {
                        var resp = await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, mac, 0).ConfigureAwait(false);
                        result.Disconnected = resp != null && resp.Status == HttpStatusCode.OK;
                    }
                    catch
                    {
                        result.Disconnected = false;
                    }

                    await SafeReportAsync(new
                    {
                        success = result.Disconnected,
                        stage = "disconnected",
                        mac = mac,
                        time = DateTimeOffset.UtcNow
                    }).ConfigureAwait(false);
                }
            }
        }

        public sealed class IdentifyResult
        {
            public bool Success { get; set; }
            public string Mac { get; set; } = "";
            public bool Connected { get; set; }
            public bool LoggedIn { get; set; }
            public bool LoginSkippedBootMode { get; set; }
            public bool IsBootMode { get; set; }
            public bool PincodeOk { get; set; } = true;
            public bool Disconnected { get; set; }
            public int SecondsConnected { get; set; }
            public string? ErrorStep { get; set; }
            public string? Error { get; set; }
        }

        // ---------- QUEUE STATE ----------
        private readonly ConcurrentQueue<UpgradeProgress> _upgradeQueue = new();
        private readonly ConcurrentDictionary<string, byte> _queuedMacs = new(); // pending membership set
        private readonly object _upgradeQueueGate = new();

        // Guards mutations of the underlying ConcurrentQueue when we need
        // stronger-than-eventual-consistency operations (e.g. remove single item).
        private readonly object _queueEditGate = new();

        // Wake-up signal for worker when new items arrive
        private readonly SemaphoreSlim _queueSignal = new(0, int.MaxValue);

        private bool _upgradeQueueWorkerRunning;
        private Task? _upgradeQueueWorkerTask;

        private static string NormalizeMac(string? mac)
            => (mac ?? string.Empty).Trim().ToUpperInvariant();

        /// <summary>
        /// Queue-enabled FIFO entrypoint.
        /// First call starts the worker; subsequent calls only enqueue and return quickly.
        /// Dedup: won't enqueue same MAC twice while pending or running.
        /// </summary>
        private Task UpgradeDevicesInParallel(List<UpgradeProgress> devices, int numbersOfThreadsInParallel = -1)
        {
            if (devices == null || devices.Count == 0)
                return Task.CompletedTask;

            int queued = 0;
            int skippedDuplicate = 0;
            int skippedInvalid = 0;

            foreach (var dev in devices)
            {
                var mac = NormalizeMac(dev?.MacAddress);

                if (string.IsNullOrWhiteSpace(mac))
                {
                    skippedInvalid++;
                    continue;
                }

                // Already running anywhere?
                if (_macsInProgress.ContainsKey(mac))
                {
                    skippedDuplicate++;
                    Console.WriteLine($"[UPGRADE QUEUE] SKIP add (already running): {mac}");
                    continue;
                }

                // Already queued pending?
                if (!_queuedMacs.TryAdd(mac, 0))
                {
                    skippedDuplicate++;
                    Console.WriteLine($"[UPGRADE QUEUE] SKIP add (already queued): {mac}");
                    continue;
                }

                dev.MacAddress = mac;

                lock (_queueEditGate)
                {
                    _upgradeQueue.Enqueue(dev);
                }
                inQueue = _upgradeQueue.Count;
                queued++;

                Console.WriteLine(
                    $"[UPGRADE QUEUE] ADDED: {mac} " +
                    $"type:{dev.DetectotType ?? ""} " +
                    $"tgt:{dev.FirmwareVersion ?? ""}");

                // Wake the worker so it can start this immediately (if there is capacity)
                _queueSignal.Release();
            }

            Console.WriteLine(
                $"[UPGRADE QUEUE] Add request: in={devices.Count}, added={queued}, dup/ignored={skippedDuplicate}, invalid={skippedInvalid}, pending={_upgradeQueue.Count}");

            lock (_upgradeQueueGate)
            {
                // If the previous worker task died/completed but the flag didn't get cleared due to a race,
                // allow restart safely.
                if (_upgradeQueueWorkerRunning && _upgradeQueueWorkerTask is { IsCompleted: false })
                    return Task.CompletedTask;

                if (_upgradeQueue.IsEmpty)
                    return Task.CompletedTask;

                _upgradeQueueWorkerRunning = true;

                // Pass -1 to indicate "follow GlobalnumberOfParallelThreads dynamically".
                // If a caller provides a fixed value, keep it fixed for the run.
                int threadsParam = numbersOfThreadsInParallel;
                int threadsEffective = threadsParam == -1 ? GlobalnumberOfParallelThreads : threadsParam;

                Console.WriteLine($"[UPGRADE QUEUE] Worker START (threads={threadsEffective})");

                _upgradeQueueWorkerTask = Task.Run(() => UpgradeQueueWorkerLiveAsync(threadsParam));
                return _upgradeQueueWorkerTask;
            }
        }

        /// <summary>
        /// Live queue worker: starts new device upgrades as soon as there is capacity,
        /// without waiting for the current "batch" to finish.
        /// </summary>
        private async Task UpgradeQueueWorkerLiveAsync(int numbersOfThreadsInParallel)
        {
            // Per-run summary (run = from first enqueue until queue+running becomes empty)
            var globalSw = Stopwatch.StartNew();
            long totalDeviceMs = 0;
            int totalDevicesProcessed = 0;

            var summaries = new ConcurrentBag<DeviceUpgradeSummary>();

            // If the worker was started with a fixed value, keep it fixed.
            // If started with a value derived from GlobalnumberOfParallelThreads (normal case),
            // allow runtime changes to take effect.
            bool fixedParallel = numbersOfThreadsInParallel != -1;

            int CurrentMaxThreads()
            {
                if (fixedParallel) return Math.Max(1, numbersOfThreadsInParallel);
                return Math.Max(1, Volatile.Read(ref GlobalnumberOfParallelThreads));
            }

            var running = new List<Task>(capacity: Math.Max(1, CurrentMaxThreads()));

            try
            {
                while (true)
                {
                    // Fill capacity immediately from FIFO
                    var maxThreads = CurrentMaxThreads();
                    while (running.Count < maxThreads)
                    {
                        UpgradeProgress? dev;
                        lock (_queueEditGate)
                        {
                            if (!_upgradeQueue.TryDequeue(out dev))
                                break;
                        }
                        inQueue = _upgradeQueue.Count;
                        var mac = NormalizeMac(dev?.MacAddress);
                        if (!string.IsNullOrWhiteSpace(mac))
                            _queuedMacs.TryRemove(mac, out _); // leaving pending queue

                        if (dev == null)
                            continue;

                        totalDevicesProcessed++;

                        // Start immediately
                        running.Add(ProcessSingleDeviceUpgradeAsync(dev, summaries, ms => Interlocked.Add(ref totalDeviceMs, ms)));
                        inQueue = _upgradeQueue.Count;
                    }

                    // Clean out completed tasks
                    for (int i = running.Count - 1; i >= 0; i--)
                    {
                        if (running[i].IsCompleted)
                            running.RemoveAt(i);
                    }

                    // If nothing queued and nothing running -> stop the run,
                    // BUT avoid a race where an enqueue happens right as we decide to stop.
                    // We "linger" briefly waiting for a signal.
                    if (_upgradeQueue.IsEmpty && running.Count == 0)
                    {
                        // If someone enqueued right now, they release _queueSignal.
                        // Wait a tiny bit to catch it.
                        bool gotLateSignal = await _queueSignal.WaitAsync(250).ConfigureAwait(false);

                        if (gotLateSignal)
                        {
                            // New work arrived during shutdown window; continue loop and drain queue.
                            continue;
                        }

                        lock (_upgradeQueueGate)
                        {
                            // Double-check again under the gate before stopping.
                            if (_upgradeQueue.IsEmpty)
                            {
                                _upgradeQueueWorkerRunning = false;
                                _upgradeQueueWorkerTask = null;
                                break;
                            }
                        }
                    }



                    // Wait for either:
                    // - a running device completes (freeing capacity)
                    // - a new device is enqueued (signal)
                    //
                    // IMPORTANT:
                    // We must never block on the signal if the queue already contains items,
                    // because the signal tokens can be consumed earlier while we were at capacity.
                    // In that case, relying on the signal would stall the worker even though work is queued.
					// If we have queued work AND available worker capacity, start it immediately.
					// This is critical when multiple items were enqueued while we were at capacity,
					// because the signal tokens can be consumed earlier. We must not stall here.
					var maxNow = CurrentMaxThreads();
					if (!_upgradeQueue.IsEmpty && running.Count < maxNow)
						continue;

					if (running.Count > 0)
                    {
                        Task completion = Task.WhenAny(running);
                        Task signal = _queueSignal.WaitAsync();
                        await Task.WhenAny(completion, signal).ConfigureAwait(false);
                    }
                    else
                    {
                        // No running tasks. If we already have queued work, loop immediately and start it.
                        if (!_upgradeQueue.IsEmpty)
                            continue;

                        // Otherwise wait until something is enqueued.
                        await _queueSignal.WaitAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UPGRADE QUEUE] Worker crashed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                globalSw.Stop();
                Console.WriteLine($"[UPGRADE QUEUE] Worker STOP (run complete). wall={globalSw.Elapsed.TotalSeconds:F2}s, processed={totalDevicesProcessed}, pending={_upgradeQueue.Count}");

                // Optional: print run summary at end (same style as before)
                // Use current (dynamic) configured threads for summary readability.
                var finalThreads = fixedParallel ? numbersOfThreadsInParallel : Math.Max(1, Volatile.Read(ref GlobalnumberOfParallelThreads));
                PrintUpgradeRunSummary(summaries, totalDevicesProcessed, totalDeviceMs, finalThreads, globalSw);
            }
        }

        /// <summary>
        /// Clears only pending items in the FIFO queue (does not cancel in-progress upgrades).
        /// Returns how many were removed.
        /// </summary>
        public int ClearUpgradeQueue()
        {
            int removed = 0;

            lock (_queueEditGate)
            {
                while (_upgradeQueue.TryDequeue(out var dev))
                {
                    removed++;
                    var mac = NormalizeMac(dev?.MacAddress);
                    if (!string.IsNullOrWhiteSpace(mac))
                        _queuedMacs.TryRemove(mac, out _);
                }
            }
            inQueue = _upgradeQueue.Count;
            Console.WriteLine($"[UPGRADE QUEUE] Cleared {removed} queued device(s). Pending now: {_upgradeQueue.Count}");
            return removed;
        }

        /// <summary>
        /// Removes a single MAC from the pending FIFO queue (does not cancel in-progress upgrades).
        /// Returns 1 if removed, 0 if not found/pending.
        /// </summary>
        public int RemoveFromUpgradeQueue(string mac)
        {
            mac = NormalizeMac(mac);
            if (string.IsNullOrWhiteSpace(mac)) return 0;

            int removed = 0;

            lock (_queueEditGate)
            {
                // Quick exit if it isn't marked as queued
                if (!_queuedMacs.ContainsKey(mac))
                    return 0;

                var items = new List<UpgradeProgress>(capacity: Math.Max(16, _upgradeQueue.Count));
                while (_upgradeQueue.TryDequeue(out var dev))
                {
                    if (dev is null)
                        continue;

                    var m = NormalizeMac(dev.MacAddress);
                    if (string.Equals(m, mac, StringComparison.OrdinalIgnoreCase) && removed == 0)
                    {
                        removed = 1;
                        // do not re-enqueue
                        continue;
                    }

                    items.Add(dev);
                }

                _queuedMacs.Clear();

                foreach (var dev in items)
                {
                    var m = NormalizeMac(dev.MacAddress);
                    if (!string.IsNullOrWhiteSpace(m))
                        _queuedMacs.TryAdd(m, 0);
                    _upgradeQueue.Enqueue(dev);
                }

                if (removed == 1)
                    _queuedMacs.TryRemove(mac, out _);
            }

            inQueue = _upgradeQueue.Count;
            if (removed == 1)
                Console.WriteLine($"[UPGRADE QUEUE] Removed pending device: {mac}. Pending now: {_upgradeQueue.Count}");

            return removed;
        }

        public static int RemoveFromUpgradeQueuePending(string mac)
        {
            var inst = _ownInstance;
            return inst is null ? 0 : inst.RemoveFromUpgradeQueue(mac);
        }

        public List<(string Mac, string DetectorType, string TargetFw)> GetUpgradeQueueSnapshot()
        {
            return _upgradeQueue
                .ToArray()
                .Select(d => (NormalizeMac(d.MacAddress), d.DetectotType ?? "", d.FirmwareVersion ?? ""))
                .ToList();
        }

        // ------------------------------------------------------------------------------------
        // Per-device logic extracted from your original tasks delegate (same behavior)
        // ------------------------------------------------------------------------------------
        private async Task ProcessSingleDeviceUpgradeAsync(
            UpgradeProgress dev,
            ConcurrentBag<DeviceUpgradeSummary> summaries,
            Action<long> addDeviceMs)
        {
            var deviceSw = Stopwatch.StartNew();
            var mac = NormalizeMac(dev.MacAddress);

            // --- CLAIM MAC (prevents double-upgrade anywhere in the app) ---
            if (!_macsInProgress.TryAdd(mac, 0))
            {
                Console.WriteLine($"[SKIP] {mac} already upgrading in another task");
                deviceSw.Stop();

                summaries.Add(new DeviceUpgradeSummary
                {
                    Mac = mac,
                    DetectorType = dev.DetectotType ?? "",
                    TargetFw = dev.FirmwareVersion ?? "",
                    CurrentFw = dev.CurrentFirmwareVersion ?? "",
                    Seconds = deviceSw.Elapsed.TotalSeconds,
                    Status = "SKIPPED"
                });

                return;
            }

            Interlocked.Increment(ref UpgradeDevicesInProgress);

            
// Track programming list for MQTT (mac + target fw)
_programmingTargets[mac] = (dev.DetectotType ?? "", dev.FirmwareVersion ?? "");
try
            {
                if (_VERBOSE)
                {
                    Console.WriteLine(
                        $"[VERBOSE][{DateTime.Now:HH:mm:ss.fff}][T{Environment.CurrentManagedThreadId}] " +
                        System.Text.Json.JsonSerializer.Serialize(dev));
                }

                if (!CheckIfDeviceInBootMode(_gatewayIpAddress, mac))
                {
                    // FW read can be flaky; retry a few times before giving up.
                    for (int i = 1; i <= 3; i++)
                    {
                        dev.CurrentFirmwareVersion = await GetFwVersion(mac, dev.Pincode);
                        if (!string.IsNullOrWhiteSpace(dev.CurrentFirmwareVersion))
                            break;
                       
                        await Task.Delay(2000).ConfigureAwait(false);
                    }
                }

                string logId = $"{mac.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";
                UpgradeLogger.Log(logId, mac, "Current FW Version:", dev.CurrentFirmwareVersion, dev.FirmwareVersion);

                dev.RetryCount = 0;
                dev.RetryCountActor = 0;
                dev.RetryCountBootloader = 0;
                dev.RetryCountSensor = 0;

                Console.WriteLine($"[START] {mac}");

                dev.upgradeBootloader = FirmwareResolver.ShouldUpgradeBootloader(
                    dev.DetectotType,
                    dev.FirmwareVersion,
                    dev.CurrentFirmwareVersion
                );

                // Skip sensor upgrade when current App FW matches target, unless forced.
                dev.upgradeSensor = dev.ForceUpdate || !FirmwareResolver.IsSameAppVersion(dev.CurrentFirmwareVersion, dev.FirmwareVersion);
                if (!dev.upgradeSensor)
                {
                    UpgradeLogger.Log(logId, mac, "Sensor upgrade skipped (FW already matches target)", "Info", dev.FirmwareVersion);
                    Console.WriteLine($"[SKIP] Sensor upgrade for {mac} - current matches target and ForceUpdate=false");
                }

                dev.isActorUpgradeNeeded = dev.DetectotType == "P48" || dev.DetectotType == "P47";

                // Requirements for success reporting
                dev.requiresConfigRestore = RestoreSettingsAfterUpgrade && (dev.DetectotType == "P48" || dev.DetectotType == "P47" || dev.DetectotType == "P46" || dev.DetectotType == "P49" || dev.DetectotType == "P41" || dev.DetectotType == "P42");
                dev.requires102Restore = Restore102DBAfterUpgrade && (dev.DetectotType == "P48" || dev.DetectotType == "P47");

                await UpgradeDeviceAsync(
                    dev, mac, dev.Pincode, dev.DetectotType, dev.FirmwareVersion,
                    dev.isActorUpgradeNeeded, dev.upgradeBootloader, dev.upgradeSensor, logId
                ).ConfigureAwait(false);

                int maxRetriesPerComponent = 5;

                bool CanRetryNow()
                {
                    // IMPORTANT: RetryCountActor/Sensor/Bootloader are incremented inside UpgradeDeviceAsync before each attempt.
                    // So we only *check* here, we do NOT increment here (avoids double-counting and off-by-one behavior).
                    bool actorOk =
                        !dev.isActorUpgradeNeeded || dev.ActorSuccess || dev.RetryCountActor < 2 * maxRetriesPerComponent;

                    bool bootOk =
                        !dev.upgradeBootloader || dev.BootloaderSuccess || dev.RetryCountBootloader < maxRetriesPerComponent;

                    bool sensorOk =
                        dev.SensorSuccess || dev.RetryCountSensor < maxRetriesPerComponent;

                    return actorOk && bootOk && sensorOk && dev.shouldRetry && dev.RetryCount <= 10;
                }

                while (!dev.IsFullyUpgraded)
                {
                    if (!CanRetryNow())
                    {
                        Console.WriteLine(
                            $"[RETRY STOP] {mac} - retries exhausted. " +
                            $"actor:{dev.RetryCountActor} boot:{dev.RetryCountBootloader} sensor:{dev.RetryCountSensor}");
                        UpgradeLogger.Log(logId, mac, "Retries exhausted.", "Info");
                        break;
                    }

                    await Task.Delay(10_000).ConfigureAwait(false);

                    dev.RetryCount++; // total retry rounds (for summary/reporting)

                    var resp = await UpgradeDeviceAsync(
                        dev, mac, dev.Pincode, dev.DetectotType, dev.FirmwareVersion,
                        dev.isActorUpgradeNeeded && !dev.ActorSuccess,
                        dev.upgradeBootloader && !dev.BootloaderSuccess,
                        !dev.SensorSuccess,
                        logId
                    ).ConfigureAwait(false);

                    Console.WriteLine($"[RETRY RESULT] {mac} - {resp.StatusCode} - {resp.Message}");
                    UpgradeLogger.Log(logId, mac, $"Retry result: {resp.StatusCode} - {resp.Message}", resp.Success ? "Success" : "Failed");

                    // HARD FAIL: firmware missing / path issues -> never retry
                    if (!resp.Success && resp.Message != null &&
                        (resp.Message.Contains("Could not find a part of the path", StringComparison.OrdinalIgnoreCase) ||
                         resp.Message.Contains("Firmware file missing", StringComparison.OrdinalIgnoreCase)))
                    {
                        dev.LastFailureReason = resp.Message;
                        UpgradeLogger.Log(logId, mac, "Hard failure (firmware missing). Stopping retries.", "Failed");
                        break;
                    }
                }


                deviceSw.Stop();
                addDeviceMs(deviceSw.ElapsedMilliseconds);

                Console.WriteLine(
                    $">>>> THREAD END - {mac} " +
                    $"actor:{dev.ActorSuccess}:{dev.RetryCountActor} " +
                    $"bootloader:{dev.BootloaderSuccess}:{dev.RetryCountBootloader} " +
                    $"sensor:{dev.SensorSuccess}:{dev.RetryCountSensor} " +
                    $"restore:{dev.isConfigRestored} " +
                    $"time:{deviceSw.Elapsed.TotalSeconds:F2}s");

                summaries.Add(new DeviceUpgradeSummary
                {
                    Mac = mac,
                    DetectorType = dev.DetectotType ?? "",
                    TargetFw = dev.FirmwareVersion ?? "",
                    CurrentFw = dev.CurrentFirmwareVersion ?? "",

                    ActorNeeded = dev.isActorUpgradeNeeded,
                    BootloaderNeeded = dev.upgradeBootloader,

                    ActorSuccess = dev.ActorSuccess,
                    BootloaderSuccess = dev.BootloaderSuccess,
                    SensorSuccess = dev.SensorSuccess,
                    IsFullyUpgraded = dev.IsFullyUpgraded,
                    ConfigRestored = dev.isConfigRestored,

                    RetryTotal = dev.RetryCount,
                    RetryActor = dev.RetryCountActor,
                    RetryBootloader = dev.RetryCountBootloader,
                    RetrySensor = dev.RetryCountSensor,

                    Seconds = deviceSw.Elapsed.TotalSeconds,
                    Status = "OK"
                });

                UpgradeLogger.Log(logId, mac, "Device Upgrade Completed.", dev.finalUpgradeResult);
            }
            catch (Exception ex)
            {
                deviceSw.Stop();
                addDeviceMs(deviceSw.ElapsedMilliseconds);

                Console.WriteLine($"[ERROR] {mac} - {ex.GetType().Name}: {ex.Message}");

                summaries.Add(new DeviceUpgradeSummary
                {
                    Mac = mac,
                    DetectorType = dev.DetectotType ?? "",
                    TargetFw = dev.FirmwareVersion ?? "",
                    CurrentFw = dev.CurrentFirmwareVersion ?? "",

                    ActorNeeded = dev.isActorUpgradeNeeded,
                    BootloaderNeeded = dev.upgradeBootloader,

                    ActorSuccess = dev.ActorSuccess,
                    BootloaderSuccess = dev.BootloaderSuccess,
                    SensorSuccess = dev.SensorSuccess,
                    IsFullyUpgraded = dev.IsFullyUpgraded,
                    ConfigRestored = dev.isConfigRestored,

                    RetryTotal = dev.RetryCount,
                    RetryActor = dev.RetryCountActor,
                    RetryBootloader = dev.RetryCountBootloader,
                    RetrySensor = dev.RetryCountSensor,

                    Seconds = deviceSw.Elapsed.TotalSeconds,
                    Status = "ERROR",
                    Error = ex.ToString()
                });
            }
            finally
            {
                _macsInProgress.TryRemove(mac, out _);

                
_programmingTargets.TryRemove(mac, out _);
Interlocked.Decrement(ref UpgradeDevicesInProgress);
            }
        }

        private void PrintUpgradeRunSummary(
            ConcurrentBag<DeviceUpgradeSummary> summaries,
            int totalDevicesProcessed,
            long totalDeviceMs,
            int numbersOfThreadsInParallel,
            Stopwatch globalSw)
        {
            var ordered = summaries
                .OrderBy(s => s.Status == "ERROR" ? 0 : s.Status == "OK" ? 1 : 2)
                .ThenByDescending(s => s.Seconds)
                .ThenBy(s => s.Mac)
                .ToList();

            Console.WriteLine("[DEVICE SUMMARY]");
            foreach (var s in ordered)
            {
                string a = s.ActorNeeded ? (s.ActorSuccess ? "A:OK" : "A:FAIL") : "A:-";
                string b = s.BootloaderNeeded ? (s.BootloaderSuccess ? "B:OK" : "B:FAIL") : "B:-";
                string se = s.SensorSuccess ? "S:OK" : "S:FAIL";
                string full = s.IsFullyUpgraded ? "FULL:OK" : "FULL:FAIL";
                string cfg = s.ConfigRestored ? "CFG:OK" : "CFG:-";

                Console.WriteLine(
                    $"  {s.Mac,-17} {s.DetectorType,-3} " +
                    $"cur:{s.CurrentFw,-8} -> tgt:{s.TargetFw,-8} " +
                    $"{a} {b} {se} {cfg} {full} " +
                    $"retry:{s.RetryTotal} (a:{s.RetryActor},b:{s.RetryBootloader},s:{s.RetrySensor}) " +
                    $"time:{s.Seconds,8:F2}s " +
                    $"status:{s.Status}");
            }

            int okCount = ordered.Count(x => x.Status == "OK");
            int errCount = ordered.Count(x => x.Status == "ERROR");
            int skipCount = ordered.Count(x => x.Status == "SKIPPED");

            double avgSecondsPerDevice =
                totalDevicesProcessed > 0 ? (totalDeviceMs / 1000.0) / totalDevicesProcessed : 0.0;

            double wallPerDevice =
                totalDevicesProcessed > 0 ? globalSw.Elapsed.TotalSeconds / totalDevicesProcessed : 0.0;

            Console.WriteLine(
                $"[UPGRADE SUMMARY]\n" +
                $"  Devices processed  : {totalDevicesProcessed}\n" +
                $"  OK / ERROR / SKIP  : {okCount} / {errCount} / {skipCount}\n" +
                $"  Parallel threads   : {numbersOfThreadsInParallel}\n" +
                $"  Total wall time    : {globalSw.Elapsed.TotalSeconds:F2}s\n" +
                $"  Avg exec / device  : {avgSecondsPerDevice:F2}s\n" +
                $"  Wall / device      : {wallPerDevice:F2}s"
            );
        }


        public async Task<ServiceResponse> ProcessingSensorUpgrade(string nodeMac, bool bActor, bool isBootloader, string DetectorType, string FirmwareVersion, string logId) // should be moved to firmware services
        {
            Console.WriteLine($"Processing Sensor Upgrade started->{nodeMac}");
            var response = new ServiceResponse();
            var isConnected = await _connectService.ConnectToBleDevice(_gatewayIpAddress, 80, nodeMac);
            if (isConnected.Status != HttpStatusCode.OK)
            {
                UpgradeLogger.Log(logId, nodeMac, "ReConnected", "Failed");
                Console.WriteLine("Failed to connect to device.");
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Failed to connect to device.";
                return response;
            }
            UpgradeLogger.Log(logId, nodeMac, "ReConnected", "Success");

            bool isAlreadyInBootMode = CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac);

            //var notificationService = new CassiaNotificationService(_configuration);
            if (isAlreadyInBootMode)
            {
                //await Task.Delay(3000);

                bool notificationEnabled = await _notificationService.EnableNotificationAsync(_gatewayIpAddress, nodeMac, bActor);

                if (!notificationEnabled)
                {
                    response.Success = false;
                    response.StatusCode = 500;
                    response.Message = "Error Enabling Notifications";
                    return response;
                }
                UpgradeLogger.Log(logId, nodeMac, "NotificationEnabled", "Success");
                Console.WriteLine($"bootloader mode achieved and Notification enabled status: {notificationEnabled} -> {nodeMac}");

            }
            else
            {
                const int maxAttempts = 5;
                bool bootModeAchieved = false;
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    bootModeAchieved = await SendJumpToBootloader(_gatewayIpAddress, nodeMac, bActor);
                    if (bootModeAchieved)
                    {
                        UpgradeLogger.Log(logId, nodeMac, "BootMode", "Achieved");
                        Console.WriteLine($"Device entered boot mode after {attempt + 1} attempts.");
                        break;
                    }
                    Console.WriteLine($"Attempt {attempt + 1} to enter boot mode failed. Retrying...");
                    await Task.Delay(3000); // Delay between attempts
                }

                if (!bootModeAchieved)
                {
                    UpgradeLogger.Log(logId, nodeMac, "BootMode", "Failed");
                    response.Success = false;
                    response.StatusCode = 417; // Expectation Failed
                    response.Message = "Failed to enter boot mode.";
                    return response;
                }

            }

            //Step 3: Start Programming the Sensor
            bool programmingResult = ProgramDevice(_gatewayIpAddress, nodeMac, _notificationService, DetectorType, FirmwareVersion, bActor, isBootloader);

            if (programmingResult)
            {

                UpgradeLogger.Log(logId, nodeMac, isBootloader ? "BootLoaderProgrammingComplete" : "SensorProgrammingComplete", "Success");
                response.Success = true;
                response.StatusCode = 200;
                response.Message = "Programming Complete";
                return response;
            }
            else
            {
                UpgradeLogger.Log(logId, nodeMac, isBootloader ? "BootLoaderProgrammingComplete" : "SensorProgrammingComplete", "Failed");
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Programming Failed";
                return response;
            }

        }

        public async Task<ServiceResponse> ProcessingActorUpgrade(string nodeMac, bool bActor, string DetectorType, string FirmwareVersion, string logId) // should be moved to firmware services
        {
            var response = new ServiceResponse();
            const int maxRetryAttempts = 3; // Maximum number of retries to put the actor into boot mode
            const int delayBetweenRetries = 5000; // Delay between retries (in milliseconds)
            int retryCount = 0;

            // Step 1: Check if the actor is in boot mode
            while (retryCount < maxRetryAttempts)
            {
                var isActorInBootMode = await ActorBootCheck(_gatewayIpAddress, nodeMac);

                if (isActorInBootMode)
                {
                    Console.WriteLine($"Actor {nodeMac} is in boot mode.");
                    break; // Exit the loop if the actor is already in boot mode
                }
                else
                {
                    retryCount++;
                    Console.WriteLine($"Actor {nodeMac} is not in boot mode. Attempting to put it into boot mode. Retry {retryCount}/{maxRetryAttempts}");

                    // Send a command to put the actor into boot mode
                    var jumpToBootloaderSuccess = await SendJumpToBootloader(_gatewayIpAddress, nodeMac, bActor);

                    if (!jumpToBootloaderSuccess)
                    {
                        Console.WriteLine($"Failed to send jump-to-bootloader command for {nodeMac}. Retrying...");
                    }

                    // Wait for a while before retrying
                    await Task.Delay(delayBetweenRetries);
                }
            }

            // If after max retries the actor is still not in boot mode, return an error response
            if (retryCount >= maxRetryAttempts)
            {
                UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", "Failed");
                Console.WriteLine($"Failed to put actor {nodeMac} into boot mode after {maxRetryAttempts} attempts.");
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Failed to put actor into boot mode.";
                return response;
            }

            // Step 2: Enable notifications

            Console.WriteLine($"Bootloader mode achieved for {nodeMac}.");

            // Step 3: Start programming the actor
            var programmingResult = ProgramDevice(_gatewayIpAddress, nodeMac, _notificationService, DetectorType, FirmwareVersion, bActor, false);

            if (programmingResult)
            {
                UpgradeLogger.Log(logId, nodeMac, "ActorProgrammingComplete", "Success");
                response.Success = true;
                response.StatusCode = 200;
                response.Message = "Programming Complete";
                return response;
            }
            else
            {
                UpgradeLogger.Log(logId, nodeMac, "ActorProgrammingComplete", "Success");
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Programming Failed";
                return response;
            }
        }

        public static UInt64 MacToInt64(string macAddress)
        {
            string hex = macAddress.Replace(":", "");
            return Convert.ToUInt64(hex, 16);
        }

        public static string MacToString(UInt64 macAddress)
        {
            return string.Join(":",
                                BitConverter.GetBytes(macAddress).Reverse()
                                .Select(b => b.ToString("X2"))).Substring(6);
        }
        Bootloader_Utils.CyBtldr_ProgressUpdate Upd = new Bootloader_Utils.CyBtldr_ProgressUpdate(CassiaFirmwareUpgradeService.ProgressUpdate);

        public bool ProgramDevice(string gatewayIpAddress, string nodeMac, CassiaNotificationService cassiaNotificationService, string DetectorType, string FirmwareVersion, bool bActor, bool isBootloader)
        {
            Console.WriteLine($"Actor is going to be programmed? : {bActor}");
            try
            {
                // Remember what we are programming so ProgressUpdate can include the stage
                // in MQTT progress messages.
                if (_ownInstance != null)
                {
                    var stage = bActor ? "actor" : (isBootloader ? "bootloader" : "sensor");
                    _ownInstance._programmingStageByMac[nodeMac] = stage;
                }

                InitializeNotificationSubscription(nodeMac, cassiaNotificationService);
                MacAddress = nodeMac;


                Bootloader_Utils.CyBtldr_CommunicationsData m_comm_data = new Bootloader_Utils.CyBtldr_CommunicationsData();
                m_comm_data.OpenConnection = OpenConnection;
                m_comm_data.CloseConnection = CloseConnection;
                m_comm_data.CustomContext = MacToInt64(nodeMac);
                ReturnCodes local_status = 0x00;
                string firmwarePath = "";

                // Phase 1 - Return relative path string
                firmwarePath = FirmwareResolver.ResolveFirmwareFile(DetectorType, FirmwareVersion, bActor, isBootloader);
                Console.WriteLine($"Firmware path resolved: {firmwarePath}");
                if (!File.Exists(firmwarePath))
                {
                    Console.WriteLine($"[FATAL] Firmware file missing: {firmwarePath}");
                    return false;
                }
                if (bActor)
                {
                    Console.WriteLine($"Programming Actor  - {nodeMac}");
                    m_comm_data.WriteData = WriteActorData;
                    m_comm_data.ReadData = ReadActorData;
                    m_comm_data.MaxTransferSize = 72;
                }
                else if (isBootloader)
                {
                    Console.WriteLine($"Programming Bootloader  - {nodeMac}");
                    m_comm_data.WriteData = WriteSensorData;
                    m_comm_data.ReadData = ReadData;
                    m_comm_data.MaxTransferSize = 265;
                }
                else
                {

                    Console.WriteLine($"Programming Sensor - {nodeMac}");
                    m_comm_data.WriteData = WriteSensorData;
                    m_comm_data.ReadData = ReadData;
                    m_comm_data.MaxTransferSize = 265;
                }

                // Load all expected rows
                HashSet<string> allRowsH = new HashSet<string>();
                HashSet<string> tmpH = null;
                allRows.TryRemove(nodeMac, out tmpH);
                completedRows.TryRemove(nodeMac, out tmpH);
                tmpH = new HashSet<string>();
                completedRows.TryAdd(nodeMac, tmpH);

                foreach (string line in File.ReadAllLines(firmwarePath).Skip(1)) // skip CYACD header
                {
                    if (line.StartsWith(":"))
                    {
                        string arrayId = line.Substring(1, 2);
                        string rowNumber = line.Substring(3, 4);
                        string key = $"{arrayId}:{rowNumber}";
                        allRowsH.Add(key);
                    }
                }

                allRows.TryAdd(nodeMac, allRowsH);


                // Call programming function
                local_status = bActor
                    ? (ReturnCodes)Bootloader_Utils.CyBtldr_Program(firmwarePath, null, _appID, ref m_comm_data, Upd)
                    : (ReturnCodes)Bootloader_Utils.CyBtldr_Program(firmwarePath, _securityKey, _appID, ref m_comm_data, Upd);

                // Handle failure
                if (local_status != ReturnCodes.CYRET_SUCCESS)
                {
                    Console.WriteLine("Programming failed - status: " + local_status);
                    _deviceStorageService.MarkFirmwareFailed(nodeMac);
                }

                return local_status == ReturnCodes.CYRET_SUCCESS;
            }
            finally
            {
                // Clear stage mapping when this programming call finishes (success or fail)
                _ownInstance?._programmingStageByMac.TryRemove(nodeMac, out _);
                //UnsubscribeNotification(nodeMac, cassiaNotificationService);
            }
        }


        public int ReadData(IntPtr buffer, int size, UInt64 customContext)
        {
            string macContext = MacToString(customContext);
            ManualResetEvent _notificationEvent = null;
            //Console.WriteLine("ReadData called here for actor and sensor | maccontext: " + macContext);
            try
            {
                // Wait for notification data to be available

                if (_notificationEvents.TryGetValue(macContext, out _notificationEvent) && _notificationEvent != null)
                {
                    //if (!_notificationEvent.WaitOne(TimeSpan.FromSeconds(15)))
                    //{
                    //   var resultEnable = _ownInstance._notificationService.EnableNotificationAsync("192.168.100.90", macContext, false);
                    //   resultEnable.Wait();
                    //   if (!resultEnable.Result)
                    //   {
                    //        Thread.Sleep(10000);
                    //        resultEnable = _ownInstance._notificationService.EnableNotificationAsync("192.168.100.90", macContext, false);
                    //        resultEnable.Wait();
                    //   }
                    //}

                    if (!_notificationEvent.WaitOne(TimeSpan.FromSeconds(20)))
                    {
                        Console.WriteLine("ReadData timeout waiting for notification");

                        //byte[] lastReadNotif = null;
                        //if (_ownInstance._lastNotificationDataRead.TryGetValue(macContext, out lastReadNotif) && lastReadNotif != null)
                        //{
                        //    Console.WriteLine($"Read data BACKUP {macContext} - " + BitConverter.ToString(lastReadNotif).Replace("-", ""));

                        //    // Copy the notification data into the provided buffer
                        //    int bytesToCopy = Math.Min(size, lastReadNotif.Length);
                        //    Marshal.Copy(lastReadNotif, 0, buffer, bytesToCopy);

                        //    _ownInstance._lastNotificationDataRead.TryRemove(macContext, out _);

                        //    Thread.Sleep(5000);

                        //    //Console.WriteLine($"ReadData succeeded, bytes read: {bytesToCopy}");
                        //    return ERR_SUCCESS; // Success
                        //}
                        //else
                        {
                            return ERR_READ; // Timeout or no data available
                        }
                    }
                }
                else
                {
                    return ERR_READ; // Timeout or no data available
                }

                ConcurrentQueue<byte[]> _notificationQueue = null;
                if (_notificationQueues.TryGetValue(macContext, out _notificationQueue) && _notificationQueue != null)
                {


                    // Dequeue the notification data
                    if (_notificationQueue.TryDequeue(out var notificationData))
                    {
                        //_ownInstance._lastNotificationDataRead.TryRemove(macContext, out _);
                        if (_DEBUG)
                            Console.WriteLine($"Read data queue process {macContext} - size: {size} - " + BitConverter.ToString(notificationData).Replace("-", ""));

                        // Copy the notification data into the provided buffer
                        int bytesToCopy = Math.Min(size, notificationData.Length);
                        Marshal.Copy(notificationData, 0, buffer, bytesToCopy);

                        //_ownInstance._lastNotificationDataRead.TryAdd(macContext, notificationData);

                        //Console.WriteLine($"ReadData succeeded, bytes read: {bytesToCopy}");
                        return ERR_SUCCESS; // Success
                    }
                    else
                    {
                        Console.WriteLine("ReadData failed: No data available in queue");
                        return ERR_READ; // No data available
                    }
                }
                else
                {
                    Console.WriteLine("ReadData failed: No notfication queue");
                    return ERR_READ; // No data available
                }
            }
            finally
            {
                // Reset the event so it can wait for the next notification
                if (_notificationEvent != null)
                {
                    _notificationEvent.Reset();
                }
            }

        }

        public static int ReadActorData(IntPtr buffer, int size, UInt64 customContext)
        {
            string macContext = MacToString(customContext);
            ManualResetEvent _notificationEvent = null;
            //Console.WriteLine("ReadData called here for actor and sensor | maccontext: " + macContext);
            try
            {
                // Wait for notification data to be available
                if (_ownInstance._notificationEvents.TryGetValue(macContext, out _notificationEvent) && _notificationEvent != null)
                {
                    //if (!_notificationEvent.WaitOne(TimeSpan.FromSeconds(15)))
                    //{
                    //    var resultEnable = _ownInstance._notificationService.EnableNotificationAsync("192.168.100.90", macContext, true);
                    //    resultEnable.Wait();
                    //    if (!resultEnable.Result)
                    //    {
                    //        Thread.Sleep(10000);
                    //        resultEnable = _ownInstance._notificationService.EnableNotificationAsync("192.168.100.90", macContext, true);
                    //        resultEnable.Wait();
                    //    }
                    //}

                    if (!_notificationEvent.WaitOne(TimeSpan.FromSeconds(20)))
                    {
                        byte[] lastReadNotif = null;
                        //if (_ownInstance._lastNotificationDataRead.TryGetValue(macContext, out lastReadNotif) && lastReadNotif != null)
                        //{
                        //    Console.WriteLine($"Read ACTOR BACKUP process {macContext} - " + BitConverter.ToString(lastReadNotif).Replace("-", ""));

                        //    int bytesToSkip = 7;
                        //    int bytesToCopy = Math.Min(size, lastReadNotif.Length - bytesToSkip);

                        //    // Ensure there are enough bytes to skip
                        //    if (lastReadNotif.Length > bytesToSkip)
                        //    {
                        //        Marshal.Copy(lastReadNotif, bytesToSkip, buffer, bytesToCopy);
                        //        _ownInstance._lastNotificationDataRead.TryRemove(macContext, out _);
                        //        // Console.WriteLine($"Skipped {bytesToSkip} bytes and copied {bytesToCopy} bytes.");

                        //        Thread.Sleep(5000);
                        //        return ERR_SUCCESS;
                        //    }
                        //    else
                        //    {
                        //        Console.WriteLine($"Not enough data to skip {bytesToSkip} bytes. Copy operation skipped.");
                        //        return ERR_READ; // Return an appropriate error code
                        //    }
                        //}
                        //else
                        {
                            Console.WriteLine("ReadData timeout waiting for notification");
                            return ERR_READ; // Timeout or no data available
                        }
                    }
                }
                else
                {
                    return ERR_READ; // Timeout or no data available
                }

                ConcurrentQueue<byte[]> _notificationQueue = null;
                if (_ownInstance._notificationQueues.TryGetValue(macContext, out _notificationQueue) && _notificationQueue != null)
                {

                    // Dequeue the notification data
                    if (_notificationQueue.TryDequeue(out var notificationData))
                    {
                        //_ownInstance._lastNotificationDataRead.TryRemove(macContext, out _);
                        if (_DEBUG)
                            Console.WriteLine($"Read ACTOR data queue process {macContext} - size {size} - " + BitConverter.ToString(notificationData).Replace("-", ""));
                        // Copy the notification data into the provided buffer
                        int bytesToSkip = 7;
                        int bytesToCopy = Math.Min(size, notificationData.Length - bytesToSkip);

                        // Ensure there are enough bytes to skip
                        if (notificationData.Length > bytesToSkip)
                        {
                            Marshal.Copy(notificationData, bytesToSkip, buffer, bytesToCopy);
                            //_ownInstance._lastNotificationDataRead.TryAdd(macContext, notificationData);
                            // Console.WriteLine($"Skipped {bytesToSkip} bytes and copied {bytesToCopy} bytes.");
                        }
                        else
                        {
                            Console.WriteLine($"Not enough data to skip {bytesToSkip} bytes. Copy operation skipped.");
                            return ERR_READ; // Return an appropriate error code
                        }


                        //Console.WriteLine($"ReadData succeeded, bytes read: {bytesToCopy}");
                        return ERR_SUCCESS; // Success
                    }
                    else
                    {
                        Console.WriteLine("ReadData failed: No data available in queue");
                        return ERR_READ; // No data available
                    }
                }
                else
                {
                    Console.WriteLine("ReadData failed: No notfication queue");
                    return ERR_READ; // No data available
                }
            }
            finally
            {
                // Reset the event so it can wait for the next notification
                if (_notificationEvent != null)
                {
                    _notificationEvent.Reset();
                }
            }

        }

        /// <summary>
        /// Method that writes to the USB device
        /// </summary>
        /// <param name="buffer">Pointer to an array where data written to USB device is stored </param>
        /// <param name="size"> Size of the Buffer </param>
        /// <returns></returns>

        ///Sensor Programming



        public static int WriteSensorData(IntPtr buffer, int size, UInt64 customContext)
        {
            bool status = false;
            byte[] data = new byte[size];
            Marshal.Copy(buffer, data, 0, size);


            if (GetHidDevice())
            {
                string hexData = BitConverter.ToString(data).Replace("-", "");


                string macContext = MacToString(customContext);

                try
                {

                    //Console.WriteLine($"Data Sent: {hexData} | macContext: {macContext}");
                    //Console.WriteLine($"size of buffer: {size}");
                    //SendMessage(data);
                    _ownInstance.cassiaReadWriteService.WriteBleMessage(_ownInstance._gatewayIpAddress, macContext, 14, hexData, "");
                    Thread.Sleep(100);

                    status = true;
                }
                catch
                {
                }

                //second try
                if (!status)
                {
                    Thread.Sleep(2000);

                    try
                    {

                        //Console.WriteLine($"Data Sent: {hexData} | macContext: {macContext}");
                        //Console.WriteLine($"size of buffer: {size}");
                        //SendMessage(data);
                        _ownInstance.cassiaReadWriteService.WriteBleMessage(_ownInstance._gatewayIpAddress, macContext, 14, hexData, "");
                        Thread.Sleep(100);

                        status = true;
                    }
                    catch
                    {
                    }
                }

                if (status)
                    return ERR_SUCCESS;
                else
                    return ERR_WRITE;
            }
            else
                return ERR_WRITE;
        }

        ///Actor Programming
        public static int WriteActorData(IntPtr buffer, int size, UInt64 customContext)
        {
            bool status = false;
            byte[] data = new byte[size];
            Marshal.Copy(buffer, data, 0, size);

            // Log the data being written
            //Console.WriteLine($"WriteData called: Buffer size={size} Data={BitConverter.ToString(data)}");

            if (GetHidDevice())
            {
                // Prepare and send BLE message for actor
                BleMessage bleMessage = new BleMessage
                {
                    _BleMessageType = BleMessage.BleMsgId.ActorBootPacket,
                    _BleMessageDataBuffer = data
                };

                string macContext = MacToString(customContext);

                try
                {
                    // Encode the message
                    if (!bleMessage.EncodeGetBleTelegram())
                        throw new Exception("Failed to encode BLE telegram.");


                    //Console.WriteLine($"macContext: {macContext}");
                    // Send the BLE message asynchronously
                    SendBleMessageAsync(bleMessage, macContext).GetAwaiter().GetResult();

                    status = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in WriteData: {ex.Message}");
                }

                if (!status)
                {
                    Thread.Sleep(2000);
                    try
                    {
                        // Encode the message
                        if (!bleMessage.EncodeGetBleTelegram())
                            throw new Exception("Failed to encode BLE telegram.");

                        //Console.WriteLine($"macContext: {macContext}");
                        // Send the BLE message asynchronously
                        SendBleMessageAsync(bleMessage, macContext).GetAwaiter().GetResult();

                        status = true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error in WriteData: {ex.Message}");
                    }
                }

                return status ? ERR_SUCCESS : ERR_WRITE;
            }
            else
            {
                return ERR_WRITE;
            }
        }

        private static async Task SendBleMessageAsync(BleMessage message, string macAddress)
        {
            //Console.WriteLine($"Sending BLE message of size {message._BleMessageBuffer.Length}");

            if (message._BleMessageBuffer.Length > 80) // Assuming 251 is the MTU size
            {
                int bytesSent = 0;
                int remainingBytes = message._BleMessageBuffer.Length;

                while (remainingBytes > 0)
                {
                    int chunkSize = Math.Min(80, remainingBytes);
                    byte[] chunk = new byte[chunkSize];
                    Array.Copy(message._BleMessageBuffer, bytesSent, chunk, 0, chunkSize);

                    await SendChunk(chunk, macAddress);
                    bytesSent += chunkSize;
                    remainingBytes -= chunkSize;

                    //Console.WriteLine($"Sent chunk of size {chunkSize}. Remaining: {remainingBytes}");
                    if (remainingBytes > 0)
                    {
                        Thread.Sleep(1000);
                    }
                    else
                    {
                        Thread.Sleep(200);
                    }

                }
            }
            else
            {
                await SendChunk(message._BleMessageBuffer, macAddress);
                //await Task.Delay(100); // Adjust delay as needed
                Thread.Sleep(200);
            }
        }

        private static async Task SendChunk(byte[] chunk, string macAddress)
        {
            // Actual sending logic (e.g., via BLE GATT write)
            //CassiaReadWriteService cassiaReadWriteService = new CassiaReadWriteService();
            string hexData = BitConverter.ToString(chunk).Replace("-", "");
            //Console.WriteLine($"Data Sent: {hexData} -> mac: {macAddress}");

            await _ownInstance.cassiaReadWriteService.WriteBleMessage(_ownInstance._gatewayIpAddress, macAddress, 19, hexData, "?noresponse=1");

        }


        public async Task<bool> SendJumpToBootloader(string gatewayIpAddress, string nodeMac, bool bActor)
        {
            //var cassiaReadWrite = new CassiaReadWriteService();
            string value = "0101000800D9CB01";
            if (bActor)
            {
                value = "0101000800D9CB02";
            }

            var response = await cassiaReadWriteService.WriteBleMessage(gatewayIpAddress, nodeMac, 19, value, "?noresponse=1");

            return response.IsSuccessStatusCode;
        }

        public bool CheckIfDeviceInBootMode(string gatewayIpAddress, string nodeMac)
        {
            string endpoint = $"http://{gatewayIpAddress}/gatt/nodes/{nodeMac}/characteristics";

            HttpClient _httpClientTmp = new HttpClient();
            try
            {
                // Use synchronous version of HttpClient with GetAwaiter().GetResult()
                var response = _httpClientTmp.GetAsync(endpoint).GetAwaiter().GetResult();

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var jsonResponse = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var characteristics = JsonConvert.DeserializeObject<List<CharacteristicModel>>(jsonResponse);

                    // Check if the characteristic UUID is present
                    return characteristics.Any(charac => charac.Uuid == "00060001-f8ce-11e4-abf4-0002a5d5c51b");
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking boot mode for {nodeMac}: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> ActorBootCheck(string gatewayIpAddress, string nodeMac)
        {
            try
            {
                string hexData = "0117000700D9E7"; // Command to trigger boot mode check
                //CassiaReadWriteService cassiaReadWriteService = new CassiaReadWriteService();

                using (var cassiaListener = _notificationService)
                {
                    var bootCheckResultTask = new TaskCompletionSource<bool>();

                    // Subscribe to notifications
                    cassiaListener.Subscribe(nodeMac, (sender, data) =>
                    {
                        //Console.WriteLine($"Notification received for {nodeMac}: {data}");

                        // Parse notification data
                        byte[] notificationData = ParseHexStringToByteArray(data);

                        // Logic to verify boot mode based on the received data
                        if (notificationData != null && notificationData.Length > 0)
                        {
                            // Convert notification data to a string for comparison
                            string receivedHex = BitConverter.ToString(notificationData).Replace("-", "");

                            if (receivedHex == "0118000800092301") // Actor is in boot mode
                            {
                                Console.WriteLine($"Actor {nodeMac} is in boot mode.");
                                bootCheckResultTask.TrySetResult(true);
                            }
                            else if (receivedHex == "0118000800092300") // Actor is not in boot mode
                            {
                                Console.WriteLine($"Actor {nodeMac} is not in boot mode.");
                                bootCheckResultTask.TrySetResult(false);
                            }
                            else
                            {
                                Console.WriteLine($"Unexpected response received: {receivedHex}");
                            }
                        }
                    });

                    // Send the write message to trigger the notification
                    await cassiaReadWriteService.WriteBleMessage(gatewayIpAddress, nodeMac, 19, hexData, "?noresponse=1");

                    // Wait for the boot check result or timeout
                    var bootCheckTask = bootCheckResultTask.Task;
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(120));
                    var completedTask = await Task.WhenAny(bootCheckTask, timeoutTask);

                    // Unsubscribe from notifications
                    cassiaListener.Unsubscribe(nodeMac);

                    // Check if the boot check task completed
                    if (completedTask == bootCheckTask)
                    {
                        return await bootCheckTask;
                    }
                    else
                    {
                        // Handle timeout
                        Console.WriteLine($"ActorBootCheck timed out for {nodeMac}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in ActorBootCheck: {ex.Message}");
                return false;
            }
        }

        private static void PurgeInstance(string macContext)
        {
            _lastInstanceRate.TryRemove(macContext, out _);
            _macRate10s.TryRemove(macContext, out _);

            // Optional: if you want to release lock objects too
            _macLocks.TryRemove(macContext, out _);
        }


        /// <summary>
        /// Method that updates the progres bar
        /// </summary>
        /// <param name="arrayID"></param>
        /// <param name="rowNum"></param>
        public static void ProgressUpdate(byte arrayID, ushort rowNum, UInt64 customContext)
        {
            static string Pad(string value, int width)
                => value.Length >= width ? value : value.PadRight(width);

            string key = $"{arrayID:X2}:{rowNum:X4}";
            string macContext = MacToString(customContext);

            var gate = _macLocks.GetOrAdd(macContext, _ => new object());

            double progress;

            lock (gate)
            {
                if (!completedRows.TryGetValue(macContext, out var completedRowsH) || completedRowsH == null)
                    return;

                if (!allRows.TryGetValue(macContext, out var allRowsH) || allRowsH == null || allRowsH.Count == 0)
                    return;

                // Only advance on first-seen row
                if (!completedRowsH.Add(key))
                    return;

                progress = Math.Min(
                    100.0,
                    (completedRowsH.Count / (double)allRowsH.Count) * 100.0
                );
            }

            // ---- DONE: purge instance so global speeds drop immediately ----
            if (progress >= 100.0 - 0.0001)
            {
                PurgeInstance(macContext);

                double globalTotalAfterPurge = 0.0;
                foreach (var rate in _lastInstanceRate.Values)
                    globalTotalAfterPurge += rate;

                double globalAvgAfterPurge =
                    _lastInstanceRate.Count > 0
                        ? globalTotalAfterPurge / _lastInstanceRate.Count
                        : 0.0;

                Console.WriteLine(
                    $"{Pad($"[{macContext}]", 22)} " +
                    $"{Pad($"Array:{arrayID:X2}", 10)} " +
                    $"{Pad($"Row:{rowNum:X4}", 10)} | " +
                    $"{Pad($"{progress,6:F2}%", 8)} | " +
                    $"{Pad("DONE", 6)} | " +
                    $"{Pad("Total:", 8)} {globalTotalAfterPurge,7:F2}%/min | " +
                    $"{Pad("Avg:", 6)} {globalAvgAfterPurge,7:F2}%/min (10s avg)"
                );

                var stage = _ownInstance?._programmingStageByMac.TryGetValue(macContext, out var s1) == true
                    ? s1
                    : "";
                var msg = string.IsNullOrWhiteSpace(stage) ? "Programming" : $"Programming {stage}";
                _deviceStorageService.UpdateFirmwareProgress(macContext, 100.0, msg);
                totalSpeed = Math.Round(globalTotalAfterPurge, 2);
                return;
            }

            // ---- Per-instance rate (%/min, 10s avg) ----
            var tracker = _macRate10s.GetOrAdd(macContext, _ => new SlidingRate10s());
            var ratePerMinThisMac = tracker.AddAndGetRatePerMinute(progress);

            // Cache latest rate so global speeds are correct
            _lastInstanceRate[macContext] = ratePerMinThisMac;

            // ---- Global total + average rates ----
            double globalTotalRatePerMin = 0.0;
            foreach (var rate in _lastInstanceRate.Values)
                globalTotalRatePerMin += rate;

            double globalAvgRatePerMin =
                _lastInstanceRate.Count > 0
                    ? globalTotalRatePerMin / _lastInstanceRate.Count
                    : 0.0;

            Console.WriteLine(
                $"{Pad($"[{macContext}]", 22)} " +
                $"{Pad($"Array:{arrayID:X2}", 10)} " +
                $"{Pad($"Row:{rowNum:X4}", 10)} | " +
                $"{Pad($"{progress,6:F2}%", 8)} | " +
                $"{Pad($"{ratePerMinThisMac,7:F2}%/min", 14)} | " +
                $"{Pad("Total:", 8)} {globalTotalRatePerMin,7:F2}%/min | " +
                $"{Pad("Avg:", 6)} {globalAvgRatePerMin,7:F2}%/min (10s avg)"
            );

            var stage2 = _ownInstance?._programmingStageByMac.TryGetValue(macContext, out var s2) == true
                ? s2
                : "";
            var msg2 = string.IsNullOrWhiteSpace(stage2) ? "Programming" : $"Programming {stage2}";
            _deviceStorageService.UpdateFirmwareProgress(macContext, progress, msg2);
            totalSpeed = Math.Round(globalTotalRatePerMin, 2);

        }


        public void SetTotalRows(int rows)
        {
            totalRows = rows > 0 ? rows : 1; // Avoid division by zero
        }


        public static bool GetHidDevice()
        {
            return (true);
        }

        /// <summary>
        /// Checks if the USB device is connected and opens if it is present
        /// Returns a success or failure
        /// </summary>
        public static int OpenConnection(UInt64 customContext)
        {
            int status = 0;
            status = GetHidDevice() ? ERR_SUCCESS : ERR_OPEN;

            return status;
        }

        /// <summary>
        /// Closes the previously opened USB device and returns the status
        /// </summary>
        public static int CloseConnection(UInt64 customContext)
        {
            int status = 0;
            return status;

        }

        public void InitializeNotificationSubscription(string macAddress, CassiaNotificationService cassiaNotificationService)
        {
            // Unsubscribe from all previous subscriptions
            //foreach (var subscribedMac in _subscribedMacAddresses)
            //{
            //    Console.WriteLine($"Unsubscribing from notifications for {subscribedMac}");
            //    cassiaNotificationService.Unsubscribe(subscribedMac);
            //}

            cassiaNotificationService.Unsubscribe(macAddress);

            ConcurrentQueue<byte[]> _tmpCheck = null;

            //if (_notificationQueues.TryGetValue(macAddress, out _tmpCheck) && _tmpCheck != null)
            {
                _notificationEvents.TryRemove(macAddress, out _);
                _notificationQueues.TryRemove(macAddress, out _);
                //_lastNotificationDataRead.TryRemove(macAddress, out _);
            }


            _notificationQueues.TryAdd(macAddress, new ConcurrentQueue<byte[]>());

            _notificationEvents.TryAdd(macAddress, new ManualResetEvent(false));


            //// Clear the list of subscribed MAC addresses
            //_subscribedMacAddresses.Clear();

            //// Add the new MAC address to the subscribed set
            //_subscribedMacAddresses.Add(macAddress);

            // Subscribe to notifications for the new MAC address
            cassiaNotificationService.Subscribe(macAddress, (sender, data) =>
            {
                if (_DEBUG)
                    Console.WriteLine($"Notification received for {macAddress}: {data}");

                // Parse the notification data into a byte array
                byte[] parsedData = ParseHexStringToByteArray(data);

                // Enqueue the data into the notification queue
                ConcurrentQueue<byte[]> _notificationQueue = null;
                if (_notificationQueues.TryGetValue(macAddress, out _notificationQueue) && _notificationQueue != null)
                {
                    _notificationQueue.Enqueue(parsedData);
                }

                // Signal that new data is available
                ManualResetEvent _notificationEvent = null;
                if (_notificationEvents.TryGetValue(macAddress, out _notificationEvent) && _notificationEvent != null)
                {
                    _notificationEvent.Set();
                }
            });
        }

        public void UnsubscribeNotification(string macAddress, CassiaNotificationService cassiaNotificationService)
        {
            // Check if the MAC address is subscribed
            ConcurrentQueue<byte[]> _tmpCheck = null;

            if (_notificationQueues.TryGetValue(macAddress, out _tmpCheck) && _tmpCheck != null)
            //if (_subscribedMacAddresses.Contains(macAddress))
            {
                Console.WriteLine($"Unsubscribing from notifications for {macAddress}");
                cassiaNotificationService.Unsubscribe(macAddress);
                //_subscribedMacAddresses.Remove(macAddress);
                _notificationQueues.TryRemove(macAddress, out _tmpCheck);
                ManualResetEvent evt = null;
                _notificationEvents.TryRemove(macAddress, out evt);
                //_lastNotificationDataRead.TryRemove(macAddress, out _);
            }
        }


        private byte[] ParseHexStringToByteArray(string hexString)
        {
            int numberOfBytes = hexString.Length / 2;
            byte[] bytes = new byte[numberOfBytes];
            for (int i = 0; i < numberOfBytes; i++)
            {
                bytes[i] = Convert.ToByte(hexString.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        public async Task<bool> EnableNotificationAsync(string gatewayIpAddress, string nodeMac, bool bActor)
        {
            HttpClient _httpClientTmp = new HttpClient();
            try
            {
                string url = $"http://{gatewayIpAddress}/gatt/nodes/{nodeMac}/handle/15/value/0100";
                if (bActor)
                {
                    url = $"http://{gatewayIpAddress}/gatt/nodes/{nodeMac}/handle/16/value/0100";
                }


                HttpResponseMessage response = await _httpClientTmp.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Notification enabled successfully. {nodeMac}");
                    return true;
                }

                Console.WriteLine($"Failed to enable notification. Status code: {response.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception occurred while enabling notification: {ex.Message}");
                return false;
            }
        }
        // Public entrypoint used by MQTT start-update handler
        public Task EnqueueUpgradesAsync(IEnumerable<UpgradeProgress> devices, int numbersOfThreadsInParallel = -1)
        {
            var list = devices?.Where(d => d != null).ToList() ?? new List<UpgradeProgress>();
            if (list.Count == 0) return Task.CompletedTask;

            // IMPORTANT: mark queued immediately (publishes progress "Queued")
            foreach (var d in list)
            {
                if (string.IsNullOrWhiteSpace(d.MacAddress)) continue;
                _deviceStorageService.UpdateFirmwareProgress(d.MacAddress, 0, "Queued");
            }

            // Reuse your existing queue-aware parallel upgrader
            // (UpgradeDevicesInParallel is already the place you wanted to feed)
            _ = UpgradeDevicesInParallel(list, numbersOfThreadsInParallel);

            return Task.CompletedTask;
        }



        /// <summary>
        /// Method that performs Read operation from USB Device
        /// </summary>
        /// <param name="buffer"> Pointer to an array where data read from USB device is copied to </param>
        /// <param name="size"> Size of the Buffer </param>
        /// <returns></returns>

    }
    public class UpgradeProgress
    {
        public string MacAddress { get; set; }
        public string Pincode { get; set; }

        public string DetectotType { get; set; }
        public string FirmwareVersion { get; set; }
        public string CurrentFirmwareVersion { get; set; }
        public string? SettingsBackupPath { get; set; }

        // If true, forces re-programming even when current FW matches target.
        public bool ForceUpdate { get; set; } = false;
        public bool BootloaderSuccess { get; set; } = false;
        public bool SensorSuccess { get; set; } = false;
        public bool ActorSuccess { get; set; } = false;
        public int RetryCount { get; set; } = 0;
        public int RetryCountActor { get; set; } = 0;
        public int RetryCountBootloader { get; set; } = 0;
        public int RetryCountSensor { get; set; } = 0;
        public string LastFailureReason { get; set; } = string.Empty;
        public bool isActorUpgradeNeeded = true;

        public bool upgradeBootloader = true;
        public bool upgradeSensor = true;
        public bool isConfigRestored = false;
        public bool requiresConfigRestore = false;
        public bool requires102Restore = false;
        public bool restore102Success = false;
        public bool shouldRetry = true;

        public string finalUpgradeResult = "Failed";

        public bool IsFullyUpgraded
            => (!isActorUpgradeNeeded || ActorSuccess)
               && (!upgradeBootloader || BootloaderSuccess)
               && (SensorSuccess)
               && (!requiresConfigRestore || isConfigRestored)
               && (!requires102Restore || restore102Success);
    }
    public class UpgradeResponse
    {
        public string MacAddress { get; set; }
        public bool Success { get; set; }
        public int StatusCode { get; set; }
        public string Message { get; set; }
        public int RetryCount { get; set; } // How many retries were used (0 = first attempt)
    }

}
