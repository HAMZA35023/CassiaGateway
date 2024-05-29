using AccessAPP.Models;
using AccessAPP.Services;
using Microsoft.AspNetCore.Mvc;

namespace AccessAPP.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class CassiaController : ControllerBase
    {
        private readonly CassiaScanService _scanService;
        private readonly CassiaConnectService _connectService;
        private readonly CassiaPinCodeService _cassiaPinCodeService;

        public CassiaController(CassiaScanService scanService, CassiaConnectService connectService, CassiaPinCodeService cassiaPinCodeService)
        {
            _scanService = scanService;
            _connectService = connectService;
            _cassiaPinCodeService = cassiaPinCodeService;
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
                return StatusCode(500, $"Error starting scan: {ex.Message}");
            }
        }
        [HttpGet("scannearbydevices")]
        public async Task<IActionResult> FetchNearbyDevices([FromQuery] int minRssi = -100)
        {
            try
            {
                string gatewayIpAddress = "192.168.0.20";
                int gatewayPort = 80;

                var nearbyDevices = await _scanService.FetchNearbyDevices(gatewayIpAddress, gatewayPort, minRssi);
                return Ok(nearbyDevices);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        [HttpPost("connect")]
        public async Task<IActionResult> ConnectToBleDevice([FromBody] string macAddress)
        {
            try
            {
                string gatewayIpAddress = "192.168.0.20";
                int gatewayPort = 80;

                //before connecting to the device, try logging in to the device
                var isConnected = await _connectService.ConnectToBleDevice(gatewayIpAddress, gatewayPort, macAddress);
                return Ok($"Device {macAddress} connected: {isConnected.Status}");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message}");
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
                return StatusCode(500, $"Error: {ex.Message}");
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
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        [HttpGet("Login")]
        public async Task<IActionResult> Login([FromBody] List<LoginRequestModel> models)
        {
            try
            {
                string gatewayIpAddress = "192.168.0.20";
                int gatewayPort = 80;
                var responses = new List<LoginResponseModel>();
                foreach (var model in models)
                {
                    string macAddress = model.MacAddress;
                    string pincode = model.Pincode;

                    if (string.IsNullOrEmpty(macAddress))
                    {
                        return BadRequest(new { error = "Bad Request", message = "Missing required parameter {macAddress}" });
                    }

                    var connectResult = await _connectService.ConnectToBleDevice(gatewayIpAddress, gatewayPort, macAddress);

                    if (connectResult.Status.ToString() == "OK")
                    {
                        var loginResult = await _connectService.AttemptLogin(gatewayIpAddress, macAddress);

                        if (loginResult.ResponseBody.PincodeRequired && string.IsNullOrEmpty(pincode))
                        {
                            var disconnected = await _connectService.DisconnectFromBleDevice(gatewayIpAddress, macAddress, 3);

                            //if (disconnected.Status.ToString() == "OK")
                            //{
                            //    return StatusCode(Convert.ToInt32(loginResult.ResponseBody.Status), new { loginResult.Status, loginResult.ResponseBody });
                            //}
                        }

                        else if (loginResult.ResponseBody.PincodeRequired && !string.IsNullOrEmpty(pincode))
                        {
                            var checkPincodeResponse = await _cassiaPinCodeService.CheckPincode(gatewayIpAddress, macAddress, pincode);
                            loginResult.ResponseBody = checkPincodeResponse.ResponseBody;
                            if (!checkPincodeResponse.ResponseBody.PinCodeAccepted)
                            {
                                var disconnected = await _connectService.DisconnectFromBleDevice(gatewayIpAddress, macAddress, 3);
                            }

                        }


                        responses.Add(loginResult);
                        //return StatusCode(Convert.ToInt32(checkPincodeResponse.ResponseBody.Status), checkPincodeResponse);
                    }
                    else
                    {
                        return StatusCode(500, new { error = "Internal Server Error", message = "An unexpected error occurred" });
                    }

                }
                return Ok(responses);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return StatusCode(500, new { error = "Internal Server Error", message = "An unexpected error occurred" });
            }
        }

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
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }
        [HttpPost("pairdevices")]
        public async Task<IActionResult> PairDevices([FromBody] PairDevicesRequest pairDevicesRequest)
        {
            try
            {
                string gatewayIpAddress = "192.168.0.20";
                int gatewayPort = 80;
                var tasks = pairDevicesRequest.macAddresses.Select(async macAddress =>
                {
                    var pairRequest = new PairRequest
                    {
                        IoCapability = pairDevicesRequest.IoCapability,
                        Oob = pairDevicesRequest.Oob,
                        Timeout = pairDevicesRequest.Timeout,
                        Type = pairDevicesRequest.Type,
                        Bond = pairDevicesRequest.Bond
                    };

                    return await _connectService.PairDevice(gatewayIpAddress, gatewayPort, macAddress, pairRequest);
                });

                var results = await Task.WhenAll(tasks);

                return Ok(results);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        [HttpDelete("unpairdevices")]
        public async Task<IActionResult> UnpairDevices([FromBody] UnpairDevicesRequest unpairDevicesRequest)
        {
            try
            {
                string gatewayIpAddress = "192.168.0.20";
                int gatewayPort = 80;
                var tasks = unpairDevicesRequest.MacAddresses.Select(async macAddress =>
                {
                    return await _connectService.UnpairDevice(gatewayIpAddress, gatewayPort, macAddress);
                });

                var results = await Task.WhenAll(tasks);

                return Ok(results);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

    }
}
