using AccessAPP.Models;
using AccessAPP.Services;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

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
                await _scanService.ScanForBleDevices(gatewayIpAddress, gatewayPort);
                return Ok("Scan started successfully.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error starting scan: {ex.Message}");
            }
        }


        [HttpPost("connect")]
        public async Task<IActionResult> ConnectToBleDevice()
        {
            try
            {
                string gatewayIpAddress = "192.168.0.20";
                int gatewayPort = 80;
                string macAddress = "10:B9:F7:0F:83:39";

                //before connecting to the device, try logging in to the device
                var isConnected = await _connectService.ConnectToBleDevice(gatewayIpAddress, gatewayPort, macAddress);
                return Ok($"Device {macAddress} connected: {isConnected.Status}");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        [HttpGet("Login")]
        public async Task<IActionResult> Login([FromBody] LoginRequestModel model)
        {
            try
            {
                string gatewayIpAddress = "192.168.0.20";
                int gatewayPort = 80;


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

                        if (disconnected.Status.ToString() == "OK")
                        {
                            return StatusCode(Convert.ToInt32(loginResult.ResponseBody.Status), new { loginResult.Status, loginResult.ResponseBody });
                        }
                    }

                    var checkPincodeResponse = await _cassiaPinCodeService.CheckPincode(gatewayIpAddress, macAddress, pincode);

                    if (!checkPincodeResponse.ResponseBody.PinCodeAccepted)
                    {
                        var disconnected = await _connectService.DisconnectFromBleDevice(gatewayIpAddress, macAddress, 3);
                    }

                    return StatusCode(Convert.ToInt32(checkPincodeResponse.ResponseBody.Status), checkPincodeResponse);
                }
                else
                {
                    return StatusCode(500, new { error = "Internal Server Error", message = "An unexpected error occurred" });
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return StatusCode(500, new { error = "Internal Server Error", message = "An unexpected error occurred" });
            }
        }

    }
}
