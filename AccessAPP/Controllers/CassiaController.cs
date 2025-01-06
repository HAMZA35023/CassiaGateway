using AccessAPP.Models;
using AccessAPP.Services;
using Amazon.Runtime.Internal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Mail;

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


        public CassiaController(IConfiguration configuration, CassiaScanService scanService, CassiaConnectService connectService, CassiaPinCodeService cassiaPinCodeService, DeviceStorageService deviceStorageService, CassiaFirmwareUpgradeService firmwareUpgradeService)
        {
            _configuration = configuration;
            _gatewayIpAddress = _configuration.GetValue<string>("GatewayConfiguration:IpAddress");
            _gatewayPort = _configuration.GetValue<int>("GatewayConfiguration:Port");
            _scanService = scanService;
            _connectService = connectService;
            _cassiaPinCodeService = cassiaPinCodeService;
            _deviceStorageService = deviceStorageService;
            _firmwareUpgradeService = firmwareUpgradeService;
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
                    string pincode = null;

                    if (string.IsNullOrEmpty(macAddress))
                    {
                        return BadRequest(new { error = "Bad Request", message = "Missing required parameter {macAddress}" });
                    }

                    var connectResult = await _connectService.ConnectToBleDevice(_gatewayIpAddress, 80, macAddress);

                    if (connectResult.Status.ToString() == "OK")
                    {
                        var loginResult = await _connectService.AttemptLogin(_gatewayIpAddress, macAddress);

                        if (loginResult.ResponseBody.PincodeRequired && !string.IsNullOrEmpty(pincode))
                        {
                            var checkPincodeResponse = await _cassiaPinCodeService.CheckPincode(_gatewayIpAddress, macAddress, pincode);
                            loginResult.ResponseBody = checkPincodeResponse.ResponseBody;
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
        public async Task<IActionResult> StartSensorUpgrade([FromBody] FirmwareUpgradeRequest request)
        {
            string nodeMac = request.MacAddress;
            string pincode = request.Pincode;
            bool bActor = request.bActor; // if bActor=1, programming actor

            try
            {
                // Check if an upgrade is already in progress for this device
                if (false) // Implement a check for ongoing upgrade
                {
                    return Conflict(new { message = "Firmware upgrade already in progress for this device." });
                }
                var result = await _firmwareUpgradeService.UpgradeSensorAsync(nodeMac, pincode, bActor);

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

        [HttpPost("BulkActorUpgrade")]
        public async Task<IActionResult> BulkActorUpgrade([FromBody] BulkUpgradeRequest request)
        {
            try
            {
                var result = await _firmwareUpgradeService.BulkUpgradeActorsAsync(request.MacAddresses, request.Pincode, request.bActor);

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

                    await Task.Delay(500);
                }

                // Return all responses as a result
                return Ok(responses);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
            }
        }


        //[HttpPost("batchlightcontrol")]
        //public async Task<IActionResult> BatchLightControl([FromBody] List<LightControlRequest> lightControlRequests)
        //{
        //    try
        //    {
        //        string gatewayIpAddress = "192.168.0.24";
        //        // Fetch the list of MacAddresses
        //        List<string> macAddresses = lightControlRequests.Select(r => r.MacAddress).ToList();
        //        // Step 1: Call the batch connect method
        //        var batchConnectResponse = await _connectService.BatchConnectDevices(gatewayIpAddress, macAddresses);

        //        if (batchConnectResponse.Status.ToString() != "OK")
        //        {
        //            return StatusCode(500, "Failed to batch connect devices.");
        //        }

        //        // Step 2: Initialize SSE listener and wait for connected devices
        //        var connectedDevices = new List<string>();
        //        using (var cassiaNotificationService = new CassiaNotificationService())
        //        {
        //            foreach (var macAddress in macAddresses)
        //            {
        //                // Subscribe to each MAC address for connection events
        //                cassiaNotificationService.Subscribe(macAddress, (sender, eventData) =>
        //                {
        //                    connectedDevices.Add(macAddress);
        //                });
        //            }

        //            // Wait for devices to connect (adjust the delay as necessary)
        //            await Task.Delay(10000);

        //            // Step 3: Send control command to each connected device
        //            foreach (var macAddress in connectedDevices)
        //            {
        //                var controlResponse = await _connectService.SendControlToLight(gatewayIpAddress, macAddress, hexControlValue);

        //                if (controlResponse.Status.ToString() != "OK")
        //                {
        //                    return StatusCode(500, $"Failed to control light for device {macAddress}");
        //                }
        //            }
        //        }

        //        return Ok("Batch light control completed successfully.");
        //    }
        //    catch (Exception ex)
        //    {
        //        return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
        //    }
        //}

    }

    public class ServiceResponse
    {
        public bool Success { get; set; }
        public int StatusCode { get; set; }
        public string Message { get; set; }
    }
    // Request model for the control light API
}
