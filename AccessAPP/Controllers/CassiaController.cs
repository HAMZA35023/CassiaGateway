using AccessAPP.Models;
using AccessAPP.Services;
using Microsoft.AspNetCore.Mvc;
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


        public CassiaController(CassiaScanService scanService, CassiaConnectService connectService, CassiaPinCodeService cassiaPinCodeService, DeviceStorageService deviceStorageService)
        {
            _scanService = scanService;
            _connectService = connectService;
            _cassiaPinCodeService = cassiaPinCodeService;
            _deviceStorageService = deviceStorageService;
        }

        [HttpGet("scan")]
        public async Task<IActionResult> ScanForBleDevices()
        {
            try
            {
                string gatewayIpAddress = "192.168.0.20";
                int gatewayPort = 80;
                var scannedDevices = await _scanService.ScanForBleDevices(gatewayIpAddress, gatewayPort);
                return Ok(scannedDevices);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error starting scan: {ex.Message + ex.StackTrace}");
            }
        }

        //this is older version, works fine but we wanted this api to have an effect in backend

        //[HttpGet("scannearbydevices")]   
        //public async Task<IActionResult> FetchNearbyDevices([FromQuery] int minRssi = -100)
        //{
        //    try
        //    {
        //        string gatewayIpAddress = "192.168.0.20";
        //        int gatewayPort = 80;

        //        var nearbyDevices = await _scanService.FetchNearbyDevices(gatewayIpAddress, gatewayPort, minRssi);
        //        return Ok(nearbyDevices);
        //    }
        //    catch (Exception ex)
        //    {
        //        return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
        //    }
        //}

        [HttpGet("scannearbydevices")]
        public IActionResult FetchNearbyDevices([FromQuery] int minRssi = -100)
        {
            try
            {
                string gatewayIpAddress = "192.168.0.20";
                int gatewayPort = 80;

                // Start SSE processing in the background
                Task.Run(async () => await _scanService.FetchNearbyDevices(gatewayIpAddress, gatewayPort, minRssi));

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
                string gatewayIpAddress = "192.168.0.20";
                int gatewayPort = 80;
                var responses = new List<ResponseModel>();

                foreach (var macAddress in macAddresses)
                {
                    //before connecting to the device, try logging in to the device
                    var isConnected = await _connectService.ConnectToBleDevice(gatewayIpAddress, gatewayPort, macAddress);
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
                string gatewayIpAddress = "192.168.0.20";
                int gatewayPort = 80;

                var connectedDevicesResponse = await _connectService.GetConnectedBleDevices(gatewayIpAddress, gatewayPort);
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
                string gatewayIpAddress = "192.168.0.20";

                var responses = new List<ResponseModel>();

                //before connecting to the device, try logging in to the device
                foreach (var macAddress in macAddresses)
                {
                    var response = await _connectService.DisconnectFromBleDevice(gatewayIpAddress, macAddress, 0);
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
                string gatewayIpAddress = "192.168.0.20";
                var responses = new List<LoginResponseModel>();

                foreach (var model in models)
                {
                    string macAddress = model.MacAddress;
                    string pincode = model.Pincode;

                    if (string.IsNullOrEmpty(macAddress))
                    {
                        return BadRequest(new { error = "Bad Request", message = "Missing required parameter {macAddress}" });
                    }

                    var connectResult = await _connectService.ConnectToBleDevice(gatewayIpAddress,80, macAddress);

                    if (connectResult.Status.ToString() == "OK")
                    {
                        var loginResult = await _connectService.AttemptLogin(gatewayIpAddress, macAddress);

                        if (loginResult.ResponseBody.PincodeRequired && !string.IsNullOrEmpty(pincode))
                        {
                            var checkPincodeResponse = await _cassiaPinCodeService.CheckPincode(gatewayIpAddress, macAddress, pincode);
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


        [HttpGet("attemptlogin")]
        public async Task<IActionResult> attemptlogin([FromBody] List<LoginRequestModel> models)
        {
            string gatewayIpAddress = "192.168.0.20";
            var responses = new List<LoginResponseModel>();

            foreach(var model in models)
            {
                string macAddress = model.MacAddress;
                string pincode = model.Pincode;
                var loginResult = await _connectService.AttemptLogin(gatewayIpAddress, macAddress);
                responses.Add(loginResult);
            }
            return Ok(responses);
            
        }
        
        //public async Task<IActionResult> Login([FromBody] List<LoginRequestModel> models)
        //{
        //    try
        //    {
        //        string gatewayIpAddress = "192.168.0.20";
        //        int gatewayPort = 80;
        //        var responses = new List<LoginResponseModel>();
        //        foreach (var model in models)
        //        {
        //            string macAddress = model.MacAddress;
        //            string pincode = model.Pincode;

        //            if (string.IsNullOrEmpty(macAddress))
        //            {
        //                return BadRequest(new { error = "Bad Request", message = "Missing required parameter {macAddress}" });
        //            }

        //            var connectResult = await _connectService.ConnectToBleDevice(gatewayIpAddress, gatewayPort, macAddress);

        //            if (connectResult.Status.ToString() == "OK")
        //            {
        //                var loginResult = await _connectService.AttemptLogin(gatewayIpAddress, macAddress);

        //                if (loginResult.ResponseBody.PincodeRequired && string.IsNullOrEmpty(pincode))
        //                {
        //                    var disconnected = await _connectService.DisconnectFromBleDevice(gatewayIpAddress, macAddress, 3);

        //                    //if (disconnected.Status.ToString() == "OK")
        //                    //{
        //                    //    return StatusCode(Convert.ToInt32(loginResult.ResponseBody.Status), new { loginResult.Status, loginResult.ResponseBody });
        //                    //}
        //                }

        //                else if (loginResult.ResponseBody.PincodeRequired && !string.IsNullOrEmpty(pincode))
        //                {
        //                    var checkPincodeResponse = await _cassiaPinCodeService.CheckPincode(gatewayIpAddress, macAddress, pincode);
        //                    loginResult.ResponseBody = checkPincodeResponse.ResponseBody;
        //                    if (!checkPincodeResponse.ResponseBody.PinCodeAccepted)
        //                    {
        //                        var disconnected = await _connectService.DisconnectFromBleDevice(gatewayIpAddress, macAddress, 3);
        //                    }

        //                }


        //                responses.Add(loginResult);
        //                //return StatusCode(Convert.ToInt32(checkPincodeResponse.ResponseBody.Status), checkPincodeResponse);
        //            }
        //            else
        //            {
        //                responses.Add(new LoginResponseModel { Status= HttpStatusCode.InternalServerError.ToString(), ResponseBody= connectResult});

        //            }
        //            Thread.Sleep(10000);
        //        }
        //        return Ok(responses);
        //    }
        //    catch (Exception ex)
        //    {
        //        Console.Error.WriteLine("Error: " + ex.Message + ex.StackTrace);
        //        return StatusCode(500, new { error = "Internal Server Error", message = "An unexpected error occurred" });
        //    }
        //}

        [HttpPost("getdata")]
        public async Task<IActionResult> GetDataFromBleDevices([FromBody] List<DeviceRequest> deviceRequests)
        {
            try
            {
                string gatewayIpAddress = "192.168.0.20";
                int gatewayPort = 80;
                var tasks = deviceRequests.Select(async request =>
                {
                    return await _connectService.GetDataFromBleDevice(gatewayIpAddress, gatewayPort, request.MacAddress, request.Value);
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
                string gatewayIpAddress = "192.168.0.20";
                int gatewayPort = 80;
                var result =_connectService.PairDevice(gatewayIpAddress, gatewayPort, pairDevicesRequest);
               
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
                string gatewayIpAddress = "192.168.0.20";
                int gatewayPort = 80;
                var result =  _connectService.UnpairDevice(gatewayIpAddress, gatewayPort, unpairDevicesRequest);
                

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
                string gatewayIpAddress = "192.168.0.20";
                int gatewayPort = 80;
                var result = _connectService.GetPairedDevices();


                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message + ex.StackTrace}");
            }
        }

    }
}
