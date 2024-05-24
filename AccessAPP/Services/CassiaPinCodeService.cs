using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace AccessAPP.Services
{
    public class CassiaPinCodeService
    {
        private readonly HttpClient _httpClient;
        public int Status { get; set; }
        public object ResponseBody { get; set; }

        public CassiaPinCodeService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<PincodeCheckResponseModel> CheckPincode(string gatewayIpAddress, string macAddress, string pincode)
        {
            try
            {
                // Create the check pincode telegram bytes
                string checkPincodeBytes = new CheckPincodeTelegram(pincode).Create();

                // Write the message to the BLE device
                CassiaReadWriteService cassiaReadWrite = new CassiaReadWriteService();
                var result = await cassiaReadWrite.WriteBleMessage(gatewayIpAddress, macAddress, 19, checkPincodeBytes, "?noresponse=1");

                using (var cassiaListener = new CassiaNotificationService())
                {
                    var pincodeCheckResultTask = new TaskCompletionSource<PincodeCheckResponseModel>();

                    cassiaListener.Subscribe(macAddress, (sender, data) =>
                    {
                        var checkPincodeReply = new CheckPincodeReply(data);

                        if (checkPincodeReply.TelegramType == "3201")
                        {
                            var pincodeResult = checkPincodeReply.GetResult();
                            ResponseModel responseBody = Helper.CreateResponseWithMessage(macAddress, result, pincodeResult.Msg,  pincodeResult.Ack);
                            responseBody.PinCodeAccepted = true;
                            var pincodeCheckResult = new PincodeCheckResponseModel
                            {
                                Status = result.StatusCode.ToString(),
                                ResponseBody = responseBody
                            };
                            pincodeCheckResultTask.TrySetResult(pincodeCheckResult);
                        }
                    });

                    // Wait for pincode check result or timeout
                    var pincodeCheckTask = pincodeCheckResultTask.Task;
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(120));
                    var completedTask = await Task.WhenAny(pincodeCheckTask, timeoutTask);

                    // Check if the pincode check task completed
                    if (completedTask == pincodeCheckTask)
                    {
                        return await pincodeCheckTask;
                    }
                    else
                    {
                        // Handle timeout
                        return new PincodeCheckResponseModel
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
