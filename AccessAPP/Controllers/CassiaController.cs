using AccessAPP.Models;
using AccessAPP.Services;
using AccessAPP.Services.HelperClasses;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Net;

namespace AccessAPP.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class CassiaController : ControllerBase
    {
        private readonly CassiaScanService _scanService;
        private readonly CassiaConnectService _connectService;
        private readonly CassiaPinCodeService _cassiaPinCodeService;
        private readonly DeviceStorageService _deviceStorageService;
        private readonly CassiaFirmwareUpgradeService _firmwareUpgradeService;
        private readonly IConfiguration _configuration;
        private readonly string _gatewayIpAddress;
        private readonly int _gatewayPort;
        private readonly CassiaNotificationService _notificationService; // ✅ Injected singleton


        public CassiaController(IConfiguration configuration, CassiaScanService scanService, CassiaConnectService connectService, CassiaPinCodeService cassiaPinCodeService, DeviceStorageService deviceStorageService, CassiaFirmwareUpgradeService firmwareUpgradeService, CassiaNotificationService notificationService)
        {
            _configuration = configuration;
            _gatewayIpAddress = _configuration.GetValue<string>("GatewayConfiguration:IpAddress");
            _gatewayPort = _configuration.GetValue<int>("GatewayConfiguration:Port");
            _scanService = scanService;
            _connectService = connectService;
            _cassiaPinCodeService = cassiaPinCodeService;
            _deviceStorageService = deviceStorageService;
            _firmwareUpgradeService = firmwareUpgradeService;
            _notificationService = notificationService;
        }

        [HttpGet("scan")]
        public async Task<IActionResult> ScanForBleDevices()
        {
            try
            {

                var scannedDevices = await _scanService.ScanForBleDevices(_gatewayIpAddress, _gatewayPort);
                return Ok(scannedDevices);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error starting scan: {ex.Message + ex.StackTrace}");
            }
        }

        [HttpGet("devices")]
        public IActionResult GetDevices()
        {
            try
            {
                var devices = _deviceStorageService.GetFilteredDevices();
                return Ok(devices);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error retrieving devices: {ex.Message}");
            }
        }

        [HttpGet("scannearbydevices")]
        public IActionResult FetchNearbyDevices([FromQuery] int minRssi = -100)
        {
            try
            {

                // Start SSE processing in the background
                Task.Run(async () => await _scanService.FetchNearbyDevices(_gatewayIpAddress, _gatewayPort, minRssi));

                // Return Ok immediately after starting the background process
                return Ok("SSE processing started in the background.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
            }
        }

        /// <summary>
        /// This API is written so that the Fetch nearby api won't stay hold and this one will help fetch list in modbus server
        /// </summary>
        /// <returns></returns>
        [HttpGet("getscannearbydevices")]
        public IActionResult GetScannedDevices()
        {
            try
            {
                // Access the list from the singleton DeviceStorageService
                var devices = _deviceStorageService.GetFilteredDevices();
                return Ok(devices);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
            }
        }



        [HttpPost("connect")]
        public async Task<IActionResult> ConnectToBleDevice([FromBody] List<string> macAddresses)
        {
            try
            {
                var responses = new List<ResponseModel>();

                foreach (var macAddress in macAddresses)
                {
                    //before connecting to the device, try logging in to the device
                    var isConnected = await _connectService.ConnectToBleDevice(_gatewayIpAddress, _gatewayPort, macAddress);
                    responses.Add(isConnected);
                    Thread.Sleep(2000);
                }
                return Ok(responses);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
            }
        }
        [HttpGet("getconnecteddevices")]
        public async Task<IActionResult> GetConnectedDevices()
        {
            try
            {

                var connectedDevicesResponse = await _connectService.GetConnectedBleDevices(_gatewayIpAddress, _gatewayPort);
                return Ok(connectedDevicesResponse);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
            }
        }


        [HttpDelete("disconnect")]
        public async Task<IActionResult> DisconnectToBleDevice([FromBody] List<string> macAddresses)
        {
            try
            {

                var responses = new List<ResponseModel>();

                //before connecting to the device, try logging in to the device
                foreach (var macAddress in macAddresses)
                {
                    var response = await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0);
                    responses.Add(response);

                }
                return Ok(responses);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
            }
        }


        [HttpPost("Login")]
        public async Task<IActionResult> Login([FromBody] List<LoginRequestModel> models)
        {
            try
            {
                var responses = new List<LoginResponseModel>();

                foreach (var model in models)
                {
                    string macAddress = model.MacAddress;
                    string pincode = model.Pincode;

                    if (string.IsNullOrEmpty(macAddress))
                    {
                        return BadRequest(new { error = "Bad Request", message = "Missing required parameter {macAddress}" });
                    }

                    var connectResult = await _connectService.ConnectToBleDevice(_gatewayIpAddress, 80, macAddress);

                    if (connectResult.Status.ToString() == "OK")
                    {
                        var loginResult = await _connectService.AttemptLogin(_gatewayIpAddress, macAddress);
                        bool pincodereq = loginResult.ResponseBody.PincodeRequired;
                        if (loginResult.ResponseBody.PincodeRequired && !string.IsNullOrEmpty(pincode))
                        {
                            var checkPincodeResponse = await _cassiaPinCodeService.CheckPincode(_gatewayIpAddress, macAddress, pincode);
                            loginResult.ResponseBody = checkPincodeResponse.ResponseBody;
                            loginResult.ResponseBody.PincodeRequired = pincodereq;
                        }

                        responses.Add(loginResult);
                    }
                    else
                    {
                        responses.Add(new LoginResponseModel { Status = HttpStatusCode.InternalServerError.ToString(), ResponseBody = connectResult });
                    }

                    await Task.Delay(5000); // Adding delay to prevent overloading
                }

                return Ok(responses);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message + ex.StackTrace);
                return StatusCode(500, new { error = "Internal Server Error", message = "An unexpected error occurred" });
            }
        }


        [HttpGet("attemptlogin")] /// This API logs in without the connect functionality
        public async Task<IActionResult> Attemptlogin([FromBody] List<LoginRequestModel> models)
        {
            var responses = new List<LoginResponseModel>();

            foreach (var model in models)
            {
                string macAddress = model.MacAddress;
                string pincode = model.Pincode;
                var loginResult = await _connectService.AttemptLogin(_gatewayIpAddress, macAddress);
                responses.Add(loginResult);
            }
            return Ok(responses);

        }

        [HttpPost("getdata")]
        public async Task<IActionResult> GetDataFromBleDevices([FromBody] List<DeviceRequest> deviceRequests)
        {
            try
            {
                var tasks = deviceRequests.Select(async request =>
                {
                    return await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, request.MacAddress, request.Value);
                });

                var results = await Task.WhenAll(tasks);

                return Ok(results);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
            }
        }
        [HttpPost("pairdevices")]
        public IActionResult PairDevices([FromBody] PairDevicesRequest pairDevicesRequest)
        {
            try
            {
                var result = _connectService.PairDevice(_gatewayIpAddress, _gatewayPort, pairDevicesRequest);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
            }
        }

        [HttpDelete("unpairdevices")]
        public IActionResult UnpairDevices([FromBody] UnpairDevicesRequest unpairDevicesRequest)
        {
            try
            {

                var result = _connectService.UnpairDevice(_gatewayIpAddress, _gatewayPort, unpairDevicesRequest);


                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
            }
        }

        [HttpGet("getpaireddevices")]
        public IActionResult GetPairedDevices()
        {
            try
            {
                var result = _connectService.GetPairedDevices();


                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
            }
        }

        [HttpPost("SensorUpgrade")]
        public async Task<IActionResult> SensorUpgrade([FromBody] FirmwareUpgradeRequest request)
        {
            string nodeMac = request.MacAddress;
            string pincode = request.Pincode;
            bool bActor = request.bActor; // if bActor=1, programming actor
            int sensorType = request.sType;
            try
            {
                // Check if an upgrade is already in progress for this device
                if (false) // Implement a check for ongoing upgrade
                {
                    return Conflict(new { message = "Firmware upgrade already in progress for this device." });
                }
                var result = await _firmwareUpgradeService.UpgradeSensorAsync(nodeMac, pincode, bActor, sensorType);

                return result.Success
                    ? Ok(new { message = result.Message })
                    : StatusCode(result.StatusCode, new { message = result.Message });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error during firmware upgrade: " + ex.Message + ex.StackTrace);
                return StatusCode(500, new { error = "Internal Server Error", message = "An unexpected error occurred." });
            }
        }

        [HttpPost("UpgradeBLSensor")]
        public async Task<IActionResult> UpgradeBLSensor([FromBody] List<BulkUpgradeRequest> request)
        {
            if (request == null || request.Count == 0)
            {
                return BadRequest(new { message = "Device list cannot be empty." });
            }

            try
            {
                var upgradeResults = await _firmwareUpgradeService.UpgradeBLSensorsAsync(request);
                return Ok(upgradeResults);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during bootloader & sensor upgrade: {ex.Message} {ex.StackTrace}");
                return StatusCode(500, new { error = "Internal Server Error", message = "An unexpected error occurred." });
            }
        }


        [HttpPost("ActorUpgrade")]
        public async Task<IActionResult> ActorUpgrade([FromBody] FirmwareUpgradeRequest request)
        {

            string nodeMac = request.MacAddress;
            string pincode = request.Pincode;
            bool bActor = request.bActor; // if bActor=1 , programming actor

            try
            {
                // Check if an upgrade is already in progress for this device
                if (false) // we will check if BLE Device already in boot mode here
                {
                    return Conflict(new { message = "Firmware upgrade already in progress for this device." });
                }

                // Step 1: Connect to the device
                var result = await _firmwareUpgradeService.UpgradeActorAsync(nodeMac, pincode, bActor);

                return result.Success
                    ? Ok(new { message = result.Message })
                    : StatusCode(result.StatusCode, new { message = result.Message });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error during firmware upgrade: " + ex.Message + ex.StackTrace);
                return StatusCode(500, new { error = "Internal Server Error", message = "An unexpected error occurred." });
            }
        }


        [HttpPost("UpgradeDevices")]
        public async Task<IActionResult> UpgradeDevices([FromBody] FirmwareUpgradeRequest request)
        {
            try
            {

                UpgradeProgress upProgress = new UpgradeProgress();
                upProgress.MacAddress = request.MacAddress;
                upProgress.Pincode = request.Pincode;
                upProgress.sType = request.sType;

                var result = await _firmwareUpgradeService.UpgradeDeviceAsync(upProgress, request.MacAddress, request.Pincode, request.sType, true, true, true);

                return result.Success
                    ? Ok(new { message = result.Message })
                    : StatusCode(result.StatusCode, new { message = result.Message });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during bulk actor upgrade: {ex.Message}");
                return StatusCode(500, new { error = "Internal Server Error", message = "An unexpected error occurred." });
            }
        }


        [HttpPost("BulkActorUpgrade")]
        public async Task<IActionResult> BulkActorUpgrade([FromBody] List<BulkUpgradeRequest> request)
        {
            try
            {
                var result = await _firmwareUpgradeService.BulkUpgradeActorsAsync(request);

                return result.Success
                    ? Ok(new { message = result.Message })
                    : StatusCode(result.StatusCode, new { message = result.Message });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during bulk actor upgrade: {ex.Message}");
                return StatusCode(500, new { error = "Internal Server Error", message = "An unexpected error occurred." });
            }
        }

        [HttpPost("BulkSensorUpgrade")]
        public async Task<IActionResult> BulkSensorUpgrade([FromBody] List<BulkUpgradeRequest> request)
        {
            try
            {
                var result = await _firmwareUpgradeService.BulkUpgradeSensorAsync(request);

                return Ok(result);
                //result.Success
                //? Ok(new { message = result.Message })
                //: StatusCode(result.StatusCode, new { message = result.Message });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during bulk actor upgrade: {ex.Message}");
                return StatusCode(500, new { error = "Internal Server Error", message = "An unexpected error occurred." });
            }
        }

        [HttpPost("BulkDeviceUpgrade")]
        public async Task<IActionResult> BulkDeviceUpgrade([FromBody] List<BulkUpgradeRequest> request)
        {
            try
            {
                var result = await _firmwareUpgradeService.BulkUpgradeDevicesAsync(request);

                return Ok(result);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during bulk actor upgrade: {ex.Message}");
                return StatusCode(500, new { error = "Internal Server Error", message = "An unexpected error occurred." });
            }
        }

        // API to connect to a BLE device and send a telegram to control the light
        [HttpPost("controlLight")]
        public async Task<IActionResult> ControlLight([FromBody] List<LightControlRequest> lightControlRequests)
        {
            try
            {
                var responses = new List<string>();

                // Loop through each device (MAC address) and send the telegram
                foreach (var lightControlRequest in lightControlRequests)
                {
                    string macAddress = lightControlRequest.MacAddress;
                    string hexLoginValue = lightControlRequest.HexLoginValue; // telegram in hex format

                    // Step 1: Connect to the BLE device
                    var connectResponse = await _connectService.ConnectToBleDevice(_gatewayIpAddress, _gatewayPort, macAddress);

                    if (connectResponse.Status.ToString() != "OK")
                    {
                        responses.Add($"Failed to connect to device: {macAddress}");
                        continue; // Skip to the next device if connection fails
                    }

                    // Step 2: Send the telegram to the BLE device using WriteBleMessage method
                    CassiaReadWriteService cassiaReadWrite = new CassiaReadWriteService();
                    var writeResponse = await cassiaReadWrite.WriteBleMessage(_gatewayIpAddress, macAddress, 19, hexLoginValue, "?noresponse=1");

                    if (writeResponse.IsSuccessStatusCode)
                    {
                        responses.Add($"Light control telegram sent successfully to device: {macAddress}");
                    }
                    else
                    {
                        responses.Add($"Failed to send telegram to device: {macAddress}, Reason: {writeResponse.ReasonPhrase}");
                    }

                    //await Task.Delay(500);
                }

                // Return all responses as a result
                return Ok(responses);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
            }
        }


        [HttpPost("batchlightcontrol")]
        public async Task<IActionResult> BatchLightControl([FromBody] BatchLightControlRequest request)
        {
            try
            {
                // Step 1: Batch connect the devices
                var batchConnectResponse = await _connectService.BatchConnectDevices(_gatewayIpAddress, request.MacAddresses);

                if (batchConnectResponse.Status.ToString() != "OK")
                {
                    return StatusCode(500, "Failed to batch connect devices.");
                }

                // Step 2: Listen to SSE stream for connection updates
                var connectedDevices = await ListenForConnectedDevices(request.MacAddresses);

                if (connectedDevices.Count == 0)
                {
                    return StatusCode(500, "No devices connected successfully.");
                }

                List<string> failedDevices = new List<string>();
                int maxAttempts = 3; // Number of retry attempts for failed devices

                // Step 3: Send control command using a **single HEX value** for all connected devices
                foreach (var macAddress in connectedDevices)
                {
                    bool success = false;

                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        var controlResponse = await _connectService.SendControlToLight(_gatewayIpAddress, macAddress, request.HexLoginValue);

                        if (controlResponse.Status.ToString() == "OK")
                        {
                            Console.WriteLine($"Attempt {attempt}: Successfully controlled light for {macAddress}");
                            success = true;
                            break; // If successful, exit the retry loop
                        }
                        else
                        {
                            Console.WriteLine($"Attempt {attempt}: Failed to control light for {macAddress}, will retry...");
                            await Task.Delay(500); // Small delay before retrying
                        }
                    }

                    if (!success)
                    {
                        failedDevices.Add(macAddress);
                    }
                }

                if (failedDevices.Count > 0)
                {
                    return StatusCode(500, $"Some devices failed: {string.Join(", ", failedDevices)}");
                }

                return Ok("Batch light control completed successfully.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
            }
        }


        [HttpPost("deviceversions")]
        public async Task<IActionResult> GetDeviceSoftwareVersions([FromBody] List<string> macAddresses)
        {
            if (macAddresses == null || macAddresses.Count == 0)
                return BadRequest("MAC address list is empty.");

            var results = new Dictionary<string, string>();

            foreach (var macAddress in macAddresses)
            {
                string parsedVersion = string.Empty;

                try
                {
                    var connectResponse = await _connectService.ConnectToBleDevice(_gatewayIpAddress, _gatewayPort, macAddress);
                    if (connectResponse.Status.ToString() == "OK")
                    {
                        var loginResponse = await _connectService.AttemptLogin(_gatewayIpAddress, macAddress);
                        if (loginResponse.Status.ToString() == "OK")
                        {
                            string testMessage = "01290107005A5E";
                            var response = await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, macAddress, testMessage);

                            if (response.Status.ToString() == "OK" && !string.IsNullOrEmpty(response.Data))
                            {
                                parsedVersion = ScanDataParser.ParseSoftwareVersionFromResponse(response.Data);
                            }
                        }

                        // Attempt disconnect even if data failed
                        await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Error with device {macAddress}: {ex.Message}");
                    // Leave parsedVersion as empty
                }

                results[macAddress] = parsedVersion;

                await Task.Delay(500); // Delay between devices
            }

            return Ok(results);
        }



        [HttpPost("connectiontest/{testCycles}")]
        public async Task<IActionResult> TestConnectionStability(int testCycles, [FromBody] List<string> macAddresses)
        {
            try
            {
                if (testCycles <= 0)
                {
                    return BadRequest("Test cycle count must be greater than zero.");
                }

                var results = new Dictionary<string, ConnectionTestResult>();

                foreach (var macAddress in macAddresses)
                {
                    var testResult = new ConnectionTestResult();

                    for (int i = 0; i < testCycles; i++)
                    {
                        var attemptDetails = new TestAttempt
                        {
                            AttemptNumber = i + 1,
                            ConnectionStatus = "Failed",
                            RequestedData = "01290107005A5E",
                            ResponseData = "No response",
                            DisconnectionStatus = "Not attempted"
                        };

                        Console.WriteLine($"Test {i + 1}/{testCycles} for {macAddress}...");

                        // Step 1: Connect to the device
                        var connectResponse = await _connectService.ConnectToBleDevice(_gatewayIpAddress, _gatewayPort, macAddress);
                        if (connectResponse.Status.ToString() == "OK")
                        {
                            attemptDetails.ConnectionStatus = "Success";

                            // Step 2: Login to the device
                            var loginResponse = await _connectService.AttemptLogin(_gatewayIpAddress, macAddress);
                            if (loginResponse.Status.ToString() == "OK")
                            {
                                // Step 3: Send BLE message (e.g., Read data)
                                string testMessage = "01290107005A5E"; // Example message
                                var response = await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, macAddress, testMessage);
                                string parsed = ScanDataParser.ParseSoftwareVersionFromResponse(response.Data);
                                attemptDetails.RequestedData = testMessage;
                                attemptDetails.ResponseData = parsed;

                                if (response.Status.ToString() == "OK")
                                {
                                    testResult.SuccessCount++;
                                    Console.WriteLine($"Successful test {i + 1}/{testCycles} for {macAddress}");
                                }
                                else
                                {
                                    testResult.FailedCount++;
                                    Console.WriteLine($"No response from {macAddress} on attempt {i + 1}");
                                }
                            }
                            else
                            {
                                attemptDetails.ConnectionStatus = "Failed (Login Issue)";
                                testResult.FailedCount++;
                                Console.WriteLine($"Failed to login to {macAddress} on attempt {i + 1}");
                            }
                        }
                        else
                        {
                            testResult.FailedCount++;
                            Console.WriteLine($"Failed to connect to {macAddress} on attempt {i + 1}");
                        }

                        // Step 4: Disconnect from the device
                        var disconnectResponse = await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0);
                        attemptDetails.DisconnectionStatus = disconnectResponse.Status.ToString() == "OK" ? "Success" : "Failed";

                        testResult.AttemptDetails.Add(attemptDetails);

                        // Small delay to avoid overwhelming the device
                        await Task.Delay(500);
                    }

                    results[macAddress] = testResult;
                }

                return Ok(results);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
            }
        }

        // Central SSE listener for connection state
        private async Task<List<string>> ListenForConnectedDevices(List<string> macAddresses)
        {
            var connectedDevices = new List<string>();
            var url = $"http://{_gatewayIpAddress}/management/nodes/connection-state";

            using (var client = new HttpClient())
            {
                var response = await client.GetStreamAsync(url);
                using (var reader = new StreamReader(response))
                {
                    var timeout = DateTime.UtcNow.AddSeconds(10); // Set fail-safe timeout
                    while (DateTime.UtcNow < timeout && connectedDevices.Count < macAddresses.Count)
                    {
                        if (reader.EndOfStream) continue;

                        string line = await reader.ReadLineAsync();
                        if (line.StartsWith("data:"))
                        {
                            var json = line.Substring(5).Trim();
                            var connectionEvent = JsonConvert.DeserializeObject<ConnectionEvent>(json);

                            if (connectionEvent.ConnectionState == "connected" && macAddresses.Contains(connectionEvent.Handle))
                            {
                                connectedDevices.Add(connectionEvent.Handle);
                            }
                        }
                    }
                }
            }

            return connectedDevices;
        }
        // These are logs for UI Logs page
        [HttpGet("logs")]
        public IActionResult GetUpgradeLogs()
        {
            try
            {
                var currentDir = Directory.GetCurrentDirectory();
                Console.WriteLine("Current Directory: " + currentDir);

                var logPath = Path.Combine(currentDir, "Logs", "upgrade_logs.txt");

                if (!System.IO.File.Exists(logPath))
                {
                    return NotFound($"Log file not found at: {logPath}");
                }

                var logText = System.IO.File.ReadAllText(logPath);
                return Content(logText, "text/plain");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error reading log file: {ex.Message}");
            }
        }

        [HttpGet("upgrade/progress")]
        public IActionResult GetAllProgress()
        {
            return Ok(_deviceStorageService.GetAllFirmwareProgress());
        }



    }
    public class ConnectionTestResult
    {
        public int SuccessCount { get; set; } = 0;
        public int FailedCount { get; set; } = 0;
        public List<TestAttempt> AttemptDetails { get; set; } = new List<TestAttempt>();
    }

    public class TestAttempt
    {
        public int AttemptNumber { get; set; }
        public string ConnectionStatus { get; set; }
        public string RequestedData { get; set; }
        public string ResponseData { get; set; }
        public string DisconnectionStatus { get; set; }
    }

}
