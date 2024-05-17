using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;

namespace AccessAPP.Services
{
    public class CassiaConnectService
    {
        private readonly HttpClient _httpClient;
        public int Status { get; set; }
        public object ResponseBody { get; set; }

        public CassiaConnectService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<ResponseModel> ConnectToBleDevice(string gatewayIpAddress, int gatewayPort, string macAddress)
        {
            try
            {
                // Step 3: Connect selected BLE devices
                string connectEndpoint = $"http://{gatewayIpAddress}:{gatewayPort}/gap/nodes/{macAddress}/connection";

                // Send POST request to connect to the BLE device
                HttpResponseMessage connectResponse = await _httpClient.PostAsync(connectEndpoint, new StringContent("{}", Encoding.UTF8, "application/json"));

                if (connectResponse.IsSuccessStatusCode)
                {
                    return Helper.CreateResponse(macAddress, connectResponse);

                }
                else
                {
                    return Helper.CreateResponse(macAddress, connectResponse);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error: {ex.Message}");
            }
        }
        public async Task<ResponseModel> DisconnectFromBleDevice(string gatewayIpAddress, string macAddress, int retries)
        {
            try
            {
                string disconnectEndpoint = $"http://{gatewayIpAddress}/gap/nodes/{macAddress}/connection";

                // Configure the DELETE request
                var request = new HttpRequestMessage(HttpMethod.Delete, disconnectEndpoint);

                // Send the DELETE request
                HttpResponseMessage response = await _httpClient.SendAsync(request);

                // Check if the request was successful
                if (response.IsSuccessStatusCode)
                {
                    return  Helper.CreateResponse(macAddress, response);
                }
                else
                {
                    // Handle unsuccessful response
                    return  Helper.CreateResponse(macAddress, response);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error: {ex.Message}");
            }
        }
        public async Task<LoginResponseModel> AttemptLogin(string gatewayIpAddress, string macAddress)
        {
            try
            {
                string hexLoginValue = new LoginTelegram().Create();
                CassiaReadWriteService cassiaReadWrite = new CassiaReadWriteService();
                var result = await cassiaReadWrite.WriteBleMessage(gatewayIpAddress, macAddress, 19, hexLoginValue, "?noresponse=1");
                using (var cassiaListener = new CassiaNotificationService())
                {
                    var loginResultTask = new TaskCompletionSource<LoginResponseModel>();

                    cassiaListener.Subscribe(macAddress, (sender, data) =>
                    {
                        var loginReply = new LoginTelegramReply(data);
                        if (loginReply.TelegramType == "1100")
                        {
                            var loginReplyResult = loginReply.GetResult();
                            ResponseModel responseBody = new ResponseModel();
                            responseBody = Helper.CreateResponseWithMessage(macAddress, result, loginReplyResult.Msg, loginReplyResult.PincodeRequired);
                            
                            var attemptLoginResult = new LoginResponseModel
                            {
                                Status = result.StatusCode.ToString(),
                                ResponseBody = responseBody
                            };
                            loginResultTask.TrySetResult(attemptLoginResult);
                        }
                    });

                    // Wait for login result or timeout
                    var loginTask = loginResultTask.Task;
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(120));
                    var completedTask = await Task.WhenAny(loginTask, timeoutTask);

                    // Unsubscribe from notifications
                    //cassiaListener.Unsubscribe(macAddress);

                    // Check if the login task completed
                    if (completedTask == loginTask)
                    {
                        return await loginTask;
                    }
                    else
                    {
                        // Handle timeout
                        return new LoginResponseModel
                        {
                            Status = "Timeout",
                            ResponseBody = null
                        };
                    }
                }

            }
            catch (Exception ex)
            {
                throw new Exception($"Error: {ex.Message}");
            }
        }

    }
}
