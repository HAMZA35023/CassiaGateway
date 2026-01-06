using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;


namespace AccessAPP.Services
{
    public class CassiaFirmwareUpgradeService : IDeviceSettingsBleApi
    {
        const bool _DEBUG = false;
        const bool _VERBOSE = true;

        const int GlobalnumberOfParallelThreads = 5;

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

        public async Task<ServiceResponse> UpgradeSensorAsync(string nodeMac, string pincode, bool bActor, bool isBootloader, string DetectorType, string FirmwareVersion, string logId = null)
        {

            // Step 1: Connect to the device
            ServiceResponse response = new ServiceResponse();
            sensorType = DetectorType;
            if (logId == "")
            {
                logId = $"{nodeMac.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";
            }

            UpgradeLogger.Log(
                logId,
                nodeMac,
                isBootloader ? "Process Start Bootloader Upgrade" : "Process Start Sensor Upgrade",
                "Success",
                DetectorType
            );

            var connectionResult = await _connectService.ConnectToBleDevice(_gatewayIpAddress, 80, nodeMac);
            if (connectionResult.Status != HttpStatusCode.OK)
            {
                UpgradeLogger.Log(logId, nodeMac, "Connected", "Failed");
                response.Success = false;
                response.StatusCode = (int)connectionResult.Status;
                response.Message = "Failed to connect to device.";
                return response;
            }
            UpgradeLogger.Log(logId, nodeMac, "Connected", "Success");
            Console.WriteLine($"Connected to device...{nodeMac}");

            bool isAlreadyInBootMode = CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac);
            if (isAlreadyInBootMode)
            {
                Console.WriteLine($"Device is already in boot mode. -> {nodeMac}");
                UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Detected");
                await Task.Delay(3000);
                var serviceResponse = await ProcessingSensorUpgrade(nodeMac, bActor, isBootloader, DetectorType, FirmwareVersion, logId);
                return serviceResponse;
            }
            else
            {
                // Step 2: Attempt login if needed
                var loginResult = await _connectService.AttemptLogin(_gatewayIpAddress, nodeMac);
                bool pincodereq = loginResult.ResponseBody.PincodeRequired;
                if (loginResult.ResponseBody.PincodeRequired && !string.IsNullOrEmpty(pincode))
                {
                    var checkPincodeResponse = await _cassiaPinCodeService.CheckPincode(_gatewayIpAddress, nodeMac, pincode);
                    loginResult.ResponseBody = checkPincodeResponse.ResponseBody;
                    loginResult.ResponseBody.PincodeRequired = pincodereq;
                }

                //if (loginResult.ResponseBody.PincodeRequired && !loginResult.ResponseBody.PinCodeAccepted)
                //{
                //    UpgradeLogger.Log(logId, nodeMac, "Login", "Failed");
                //    response.Success = false;
                //    response.StatusCode = 401; // Unauthorized
                //    response.Message = "Failed to login to the device.";
                //    return response;
                //}
                UpgradeLogger.Log(logId, nodeMac, "LoggedIn", "Success");
                Console.WriteLine($"Logged into device...{nodeMac}");

                string sensorInfo = "";
                string actorInfo = "";

                //Lets get the FW version before jumping to bootloader, so we know what to update
                if (loginResult.Status.ToString() == "OK")
                {

                    // Sensor
                    string sensorCommand = "01290107005A5E";
                    var sensorResponse = await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);
                    if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
                    {
                        sensorInfo = ScanDataParser.ParseSoftwareVersionFromResponse(sensorResponse.Data);
                    }

                    // Actor
                    string actorCommand = "012B01070032B3";
                    var actorResponse = await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, nodeMac, actorCommand);
                    if (actorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(actorResponse.Data))
                    {
                        actorInfo = ScanDataParser.ParseSoftwareVersionFromResponse(actorResponse.Data);
                    }

                    Console.WriteLine($"Sensor: {sensorInfo} | Actor: {actorInfo}");
                }




                // Send Jump to Bootloader telegram repeatedly until successful
                const int maxAttempts = 5;
                bool bootModeAchieved = false;
                bool isBootModeAchieved = false;
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    bootModeAchieved = await SendJumpToBootloader(_gatewayIpAddress, nodeMac, bActor);
                    await Task.Delay(10000);
                    var CR = await _connectService.ConnectToBleDevice(_gatewayIpAddress, 80, nodeMac);
                    if (CR.Status != HttpStatusCode.OK)
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Connected", "Failed");
                        response.Success = false;
                        response.StatusCode = (int)CR.Status;
                        response.Message = "Failed to connect to device.";
                        return response;
                    }
                    UpgradeLogger.Log(logId, nodeMac, "Connected", "Success");
                    Console.WriteLine($"Connected to device...{nodeMac}");

                    isBootModeAchieved = CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac);
                    if (isBootModeAchieved)
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Achieved");
                        Console.WriteLine($"Device entered boot mode after {attempt + 1} attempts.");
                        break;
                    }
                    Console.WriteLine($"Attempt {attempt + 1} to enter boot mode failed. Retrying...");
                    await Task.Delay(3000); // Delay between attempts
                }

                if (!isBootModeAchieved)
                {
                    UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Failed");
                    response.Success = false;
                    response.StatusCode = 417; // Expectation Failed
                    response.Message = "Failed to enter boot mode.";
                    return response;
                }

                // Disconnect and prepare for the upgrade process
                Console.WriteLine("device disconnected and will reconnect after 3s");
                var isDisconnected = await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0);
                UpgradeLogger.Log(logId, nodeMac, "Disconnected", "Success");
                await Task.Delay(3000);

                var serviceResponse = await ProcessingSensorUpgrade(nodeMac, bActor, isBootloader, DetectorType, FirmwareVersion, logId);
                return serviceResponse;
            }
        }
        public async Task<ServiceResponse> UpgradeActorAsync(string nodeMac, string pincode, bool bActor, string DetectorType, string FirmwareVersion, string logId)
        {
            UpgradeLogger.Log(logId, nodeMac, "Process Start Actor Upgrade", "Success");
            ServiceResponse response = new();
            var connectionResult = await _connectService.ConnectToBleDevice(_gatewayIpAddress, 80, nodeMac);
            if (connectionResult.Status != HttpStatusCode.OK)
            {
                UpgradeLogger.Log(logId, nodeMac, "Connected", "Failed");
                response.Success = false;
                response.StatusCode = (int)connectionResult.Status;
                response.Message = "Failed to connect to device.";
                return response;
            }
            UpgradeLogger.Log(logId, nodeMac, "Connected", "Success");
            Console.WriteLine($"Connected to device...{nodeMac}");

            bool isAlreadyInBootMode = CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac);
            if (isAlreadyInBootMode)
            {
                UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Detected");
                response.Success = false;
                response.StatusCode = 409; // Conflict
                response.Message = "Sensor is already in boot mode. It needs to be in Application mode.";
                return response;
            }
            else
            {
                UpgradeLogger.Log(logId, nodeMac, "LoggedIn", "Success");
                Console.WriteLine($"Login to device...{nodeMac}");
                //Step 2: Attempt login if needed
                var loginResult = await _connectService.AttemptLogin(_gatewayIpAddress, nodeMac);
                if (loginResult.ResponseBody.PincodeRequired && !string.IsNullOrEmpty(pincode))
                {
                    var checkPincodeResponse = await _cassiaPinCodeService.CheckPincode(_gatewayIpAddress, nodeMac, pincode);
                    loginResult.ResponseBody = checkPincodeResponse.ResponseBody;
                }

                if (loginResult.ResponseBody.PincodeRequired && !loginResult.ResponseBody.PinCodeAccepted)
                {
                    response.Success = false;
                    response.StatusCode = 401; // Unauthorized
                    response.Message = "Failed to login to the device.";
                    return response;
                }

                Console.WriteLine($"Logged into device...{nodeMac}");


                // Send Jump to Bootloader telegram
                bool jumpToBootResponse = await SendJumpToBootloader(_gatewayIpAddress, nodeMac, bActor);
                if (!jumpToBootResponse)
                {
                    UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", "Failed");
                    response.Success = false;
                    response.StatusCode = 417; // Expectation Failed
                    response.Message = "Failed to enter boot mode.";
                    return response;
                }
                UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", "Achieved");
                Console.WriteLine(jumpToBootResponse);

                // Delays for 3 seconds (3000 milliseconds) before connecting to device again
                //var isDisConnected = await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0);
                //await Task.Delay(3000);
                var serviceResponse = await ProcessingActorUpgrade(nodeMac, bActor, DetectorType, FirmwareVersion, logId);

                return serviceResponse;

            }
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
                try
                {
                    UpgradeLogger.Log(logId, macAddress, $"Connect+Login attempt {attempt}/{maxAttempts}", "Info", firmwareVersion);
                    Console.WriteLine($"[INFO] Connect+Login attempt {attempt}/{maxAttempts} for {macAddress}");

                    // 1) Connect
                    var connectionResult = await _connectService
                        .ConnectToBleDevice(gatewayIp, gatewayPort, macAddress)
                        .ConfigureAwait(false);

                    if (connectionResult.Status != HttpStatusCode.OK)
                    {
                        var msg = $"Connect failed (HTTP {(int)connectionResult.Status} {connectionResult.Status}).";
                        UpgradeLogger.Log(logId, macAddress, "Connected", "Failed", firmwareVersion);
                        Console.WriteLine($"[WARN] {msg} MAC={macAddress}");

                        if (attempt < maxAttempts)
                        {
                            await Task.Delay(delayBetweenAttemptsMs).ConfigureAwait(false);
                            continue;
                        }

                        return new ConnectLoginResult
                        {
                            Success = false,
                            StatusCode = (int)connectionResult.Status,
                            Message = msg
                        };
                    }

                    UpgradeLogger.Log(logId, macAddress, "Connected", "Success", firmwareVersion);
                    Console.WriteLine($"[INFO] Connected to device... {macAddress}");

                    // 2) Login
                    var loginResult = await _connectService
                        .AttemptLogin(gatewayIp, macAddress)
                        .ConfigureAwait(false);

                    // If pincode required and we have a pincode, check it
                    bool pincodeReq = loginResult.ResponseBody.PincodeRequired;
                    if (pincodeReq && !string.IsNullOrEmpty(pincode))
                    {
                        var checkPincodeResponse = await _cassiaPinCodeService
                            .CheckPincode(gatewayIp, macAddress, pincode)
                            .ConfigureAwait(false);

                        loginResult.ResponseBody = checkPincodeResponse.ResponseBody;
                        loginResult.ResponseBody.PincodeRequired = pincodeReq;
                    }

                    // Your code currently treats "OK" as success criterion
                    var statusText = loginResult.Status?.ToString() ?? "";
                    if (!string.Equals(statusText, "OK", StringComparison.OrdinalIgnoreCase))
                    {
                        var msg = $"Login failed (Status={statusText}).";
                        UpgradeLogger.Log(logId, macAddress, "LoggedIn", "Failed", firmwareVersion);
                        Console.WriteLine($"[WARN] {msg} MAC={macAddress}");

                        if (attempt < maxAttempts)
                        {
                            await Task.Delay(delayBetweenAttemptsMs).ConfigureAwait(false);
                            continue;
                        }

                        return new ConnectLoginResult
                        {
                            Success = false,
                            StatusCode = 401,
                            Message = msg,
                            LoginResponseBody = loginResult.ResponseBody,
                            RawStatus = statusText
                        };
                    }

                    UpgradeLogger.Log(logId, macAddress, "LoggedIn", "Success", firmwareVersion);
                    Console.WriteLine($"[INFO] Logged into device... {macAddress}");

                    return new ConnectLoginResult
                    {
                        Success = true,
                        StatusCode = 200,
                        Message = "Connected + logged in",
                        LoginResponseBody = loginResult.ResponseBody,
                        RawStatus = statusText
                    };
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    UpgradeLogger.Log(logId, macAddress, $"Connect+Login exception attempt {attempt}/{maxAttempts}: {ex.Message}", "Warn", firmwareVersion);
                    Console.WriteLine($"[WARN] Connect+Login exception attempt {attempt}/{maxAttempts} for {macAddress}: {ex}");

                    if (attempt < maxAttempts)
                        await Task.Delay(delayBetweenAttemptsMs).ConfigureAwait(false);
                }
            }

            return new ConnectLoginResult
            {
                Success = false,
                StatusCode = 500,
                Message = $"Connect+Login failed after retries. Last error: {lastEx?.Message}",
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

            bool disable_update = false; //used to test restore config without updating firmware

            // NEW: Backup/restore settings (hex strings) to a file before/after upgrade
            string? settingsBackupPath = null;

            try
            {
                UpgradeLogger.Log(logId, macAddress, "Process Start Device Async", "Success", FirmwareVersion);

                // --- NEW: Backup settings at start (best-effort) ---
                if (DetectorType == "P48" || DetectorType == "P47")
                {
                    Console.WriteLine($"Starting settings backup for {macAddress}");

                    var cl = await ConnectAndLoginWithRetryAsync(
                        _gatewayIpAddress, 80, macAddress, pincode, logId, FirmwareVersion,
                        maxAttempts: 3,
                        delayBetweenAttemptsMs: 2000).ConfigureAwait(false);

                    if (!cl.Success)
                    {
                        UpgradeLogger.Log(logId, macAddress, $"Backup connect+login failed: {cl.Message}", "Warn", FirmwareVersion);
                        Console.WriteLine($"[WARN] Backup connect+login failed for {macAddress}: {cl.Message}");
                        // Best-effort backup: do NOT return; continue with upgrade
                    }
                    else
                    {
                        try
                        {
                            var backup = await _settingsBackup
                                .BackupToFileAsync(macAddress, pincode, DetectorType, FirmwareVersion, logId)
                                .ConfigureAwait(false);

                            settingsBackupPath = backup.filePath;

                            UpgradeLogger.Log(logId, macAddress, "Settings backup saved", "Success", FirmwareVersion);
                            Console.WriteLine($"[INFO] Settings backup saved for {macAddress} to: {settingsBackupPath}");

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
                            UpgradeLogger.Log(logId, macAddress, $"Settings backup failed: {ex.Message}", "Warn", FirmwareVersion);
                            Console.WriteLine($"[WARN] Settings backup failed for {macAddress}: {ex}");
                        }
                    }
                }

                // Step 2: Upgrade the actor / bootloader / sensor
                var stopwatch = new Stopwatch();

                if (upgradeActor && !disable_update)
                {
                    Console.WriteLine($"Starting actor upgrade for {macAddress}");
                    dev.RetryCountActor++;

                    stopwatch.Restart();
                    var actorUpgradeResult = await UpgradeActorAsync(macAddress, pincode, true, DetectorType, FirmwareVersion, logId)
                                                .ConfigureAwait(false);
                    stopwatch.Stop();

                    Console.WriteLine($"Actor upgrade completed for {macAddress}. Time taken: {stopwatch.Elapsed.TotalSeconds} seconds - result: {actorUpgradeResult.Success}");

                    dev.ActorSuccess = actorUpgradeResult.Success;

                    await Task.Delay(10000).ConfigureAwait(false); // FIXED: must await
                }

                if (upgradeBootloader && !disable_update)
                {
                    dev.RetryCountBootloader++;

                    Console.WriteLine($"Starting bootloader upgrade for {macAddress}");
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

                    await Task.Delay(20000).ConfigureAwait(false); // FIXED: must await
                }

                if (upgradeSensor && !disable_update)
                {
                    Console.WriteLine($"Starting Sensor upgrade for {macAddress}");
                    dev.RetryCountSensor++;

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
                }

                if (DetectorType == "P48" || DetectorType == "P47")
                {
                    await Task.Delay(10000).ConfigureAwait(false);

                    Console.WriteLine($"Starting settings restore for {macAddress} - trying to connect and login");

                    var cl = await ConnectAndLoginWithRetryAsync(
                        _gatewayIpAddress, 80, macAddress, pincode, logId, FirmwareVersion,
                        maxAttempts: 3,
                        delayBetweenAttemptsMs: 3000).ConfigureAwait(false);

                    if (!cl.Success)
                    {
                        UpgradeLogger.Log(logId, macAddress, $"Restore connect+login failed: {cl.Message}", "Warn", FirmwareVersion);
                        Console.WriteLine($"[WARN] Restore connect+login failed for {macAddress}: {cl.Message}");
                    }
                    else
                    {
                        Console.WriteLine($"Starting settings restore for {macAddress} - upload config");

                        if (!string.IsNullOrWhiteSpace(settingsBackupPath))
                        {
                            try
                            {
                                var restore = await _settingsBackup.RestoreFromFileAsync(
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
                                    $"Settings restore: {(restore.Success ? "Success" : "Fail")} - {restore.Message}",
                                    restore.Success ? "Success" : "Failed",
                                    FirmwareVersion);

                                Console.WriteLine($"[INFO] Settings restore for {macAddress} - result: {restore.Success} - {restore.Message}");
                                dev.isConfigRestored = true;
                            }
                            catch (Exception ex)
                            {
                                UpgradeLogger.Log(logId, macAddress, $"Settings restore failed: {ex.Message}", "Warn", FirmwareVersion);
                                Console.WriteLine($"[WARN] Settings restore failed for {macAddress}: {ex}");
                            }
                        }
                        else
                        {
                            UpgradeLogger.Log(logId, macAddress, "Settings restore skipped (no backup file available)", "Warn", FirmwareVersion);
                            Console.WriteLine($"[WARN] Settings restore skipped for {macAddress} - no backup file available");
                        }
                    }

                    // Restore 102 DB after update, and then disconnect
                    var resp = await DaliRestore102Database(macAddress);
                    Console.WriteLine($"Dali Restore 102 Database response: {resp} for {macAddress}");

                    await Task.Delay(3000).ConfigureAwait(false);

                    // Prefer async instead of .Wait()
                    await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0)
                        .ConfigureAwait(false);
                }
                else
                {
                    UpgradeLogger.Log(logId, macAddress, "Settings restore skipped (not P47 or P48)", "Info", FirmwareVersion);
                }




                // Both upgrades successful
                response.Success = true;
                response.StatusCode = 200;
                response.Message = "Sensor and actor upgrades completed successfully.";
                return response;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during sensor and actor upgrade: {ex.Message}");
                response.Success = false;
                response.StatusCode = 500; // Internal Server Error
                response.Message = "An unexpected error occurred during the upgrade process.";
                return response;
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

        private async Task UpgradeDevicesInParallel(
          List<UpgradeProgress> devices,
          int numbersOfThreadsInParallel = -1)
        {

            if (numbersOfThreadsInParallel == -1)
                numbersOfThreadsInParallel = GlobalnumberOfParallelThreads;

            int maxRetriesPerComponent = 3;

            using var semaphore = new SemaphoreSlim(numbersOfThreadsInParallel);
            Interlocked.Add(ref UpgradeDevicesInProgress, devices.Count);

            var tasks = devices.Select(async dev =>
            {
                // Normalize the MAC so all lookups match
                var mac = (dev.MacAddress ?? string.Empty).Trim();

                // --- CLAIM MAC (prevents double-upgrade anywhere in the app) ---
                if (!_macsInProgress.TryAdd(mac, 0))
                {
                    Console.WriteLine($"Skipping {mac} - already upgrading this MAC in another thread/task.");
                    Interlocked.Decrement(ref UpgradeDevicesInProgress);
                    return;
                }

                await semaphore.WaitAsync().ConfigureAwait(false);
                try
                {

                    if (_VERBOSE)
                    {
                        Console.WriteLine(
                            $"[VERBOSE][{DateTime.Now:HH:mm:ss.fff}][T{Environment.CurrentManagedThreadId}] " +
                            System.Text.Json.JsonSerializer.Serialize(dev)
                        );
                    }

                    string logId = $"{mac.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";
                    dev.RetryCount = 0;
                    dev.RetryCountActor = 0;
                    dev.RetryCountBootloader = 0;
                    dev.RetryCountSensor = 0;

                    Console.WriteLine($"Starting upgrade for device {mac}");

                    dev.upgradeBootloader = FirmwareResolver.ShouldUpgradeBootloader(
                        dev.DetectotType,
                        dev.FirmwareVersion,
                        dev.CurrentFirmwareVersion
                    );

                    dev.isActorUpgradeNeeded = dev.DetectotType == "P48" || dev.DetectotType == "P47";

                    await UpgradeDeviceAsync(
                        dev, mac, dev.Pincode, dev.DetectotType, dev.FirmwareVersion,
                        dev.isActorUpgradeNeeded, dev.upgradeBootloader, true, logId
                    ).ConfigureAwait(false);

                    while (!dev.IsFullyUpgraded &&
                           (dev.RetryCountActor < 2 * maxRetriesPerComponent ||
                            dev.RetryCountBootloader < maxRetriesPerComponent ||
                            dev.RetryCountSensor < maxRetriesPerComponent))
                    {
                        await Task.Delay(10000).ConfigureAwait(false);
                        dev.RetryCount++;
                        Console.WriteLine($"Retry upgrade for device {mac} - Retry {dev.RetryCount}");

                        await UpgradeDeviceAsync(
                            dev, mac, dev.Pincode, dev.DetectotType, dev.FirmwareVersion,
                            dev.isActorUpgradeNeeded && !dev.ActorSuccess,
                            dev.upgradeBootloader && !dev.BootloaderSuccess,
                            !dev.SensorSuccess,
                            logId
                        ).ConfigureAwait(false);
                    }

                    Console.WriteLine($">>>> THREAD END - {mac} - actor: {dev.ActorSuccess}:{dev.RetryCountActor} - bootloader: {dev.BootloaderSuccess}:{dev.RetryCountBootloader} - sensor: {dev.SensorSuccess}:{dev.RetryCountSensor} - recover config : {dev.isConfigRestored}");
                }
                finally
                {
                    semaphore.Release();
                    _macsInProgress.TryRemove(mac, out _);  // --- RELEASE MAC ---
                    Interlocked.Decrement(ref UpgradeDevicesInProgress);
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
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

        private static string Pad(string value, int width)
    => value.Length >= width ? value : value.PadRight(width);

        /// <summary>
        /// Method that updates the progres bar
        /// </summary>
        /// <param name="arrayID"></param>
        /// <param name="rowNum"></param>
        public static void ProgressUpdate(byte arrayID, ushort rowNum, UInt64 customContext)
        {
            static string Pad(string value, int width) => value.Length >= width ? value : value.PadRight(width);

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

                progress = Math.Min(100.0, (completedRowsH.Count / (double)allRowsH.Count) * 100.0);
            }

            // ---- DONE: purge instance so global speed drops immediately ----
            if (progress >= 100.0 - 0.0001)
            {
                PurgeInstance(macContext);

                double globalTotalAfterPurge = 0.0;
                foreach (var rate in _lastInstanceRate.Values)
                    globalTotalAfterPurge += rate;

                Console.WriteLine(
                    $"{Pad($"[{macContext}]", 22)} " +
                    $"{Pad($"Array:{arrayID:X2}", 10)} " +
                    $"{Pad($"Row:{rowNum:X4}", 10)} | " +
                    $"{Pad($"{progress,6:F2}%", 8)} | " +
                    $"{Pad("DONE", 6)} | " +
                    $"{Pad("Global total:", 14)} {globalTotalAfterPurge,7:F2}%/min (10s avg)"
                );

                _deviceStorageService.UpdateFirmwareProgress(macContext, 100.0);
                return;
            }

            // ---- Per-instance rate (%/min, 10s avg) ----
            var tracker = _macRate10s.GetOrAdd(macContext, _ => new SlidingRate10s());
            var ratePerMinThisMac = tracker.AddAndGetRatePerMinute(progress);

            // Cache latest rate so global total is the sum of active instances
            _lastInstanceRate[macContext] = ratePerMinThisMac;

            // ---- Global total rate = sum of instance rates (%/min, 10s avg) ----
            double globalTotalRatePerMin = 0.0;
            foreach (var rate in _lastInstanceRate.Values)
                globalTotalRatePerMin += rate;

            Console.WriteLine(
                $"{Pad($"[{macContext}]", 22)} " +
                $"{Pad($"Array:{arrayID:X2}", 10)} " +
                $"{Pad($"Row:{rowNum:X4}", 10)} | " +
                $"{Pad($"{progress,6:F2}%", 8)} | " +
                $"{Pad($"{ratePerMinThisMac,7:F2}%/min", 14)} | " +
                $"{Pad("Global total:", 14)} {globalTotalRatePerMin,7:F2}%/min (10s avg)"
            );

            _deviceStorageService.UpdateFirmwareProgress(macContext, progress);
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
        public bool isConfigRestored = false;

        public bool IsFullyUpgraded => (!isActorUpgradeNeeded || ActorSuccess) && (!upgradeBootloader || BootloaderSuccess) && (SensorSuccess);
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
