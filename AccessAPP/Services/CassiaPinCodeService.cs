using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;

namespace AccessAPP.Services
{
    public class CassiaPinCodeService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly CassiaNotificationService _notificationService;
        public int Status { get; set; }
        public object ResponseBody { get; set; }

        public CassiaPinCodeService(HttpClient httpClient, IConfiguration configuration, CassiaNotificationService notificationService)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _notificationService = notificationService;
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

                var pincodeCheckResultTask = new TaskCompletionSource<PincodeCheckResponseModel>();

                // ✅ Subscribe to notifications using the singleton `_notificationService`
                _notificationService.Subscribe(macAddress, (sender, data) =>
                {
                    var checkPincodeReply = new CheckPincodeReply(data);

                    if (checkPincodeReply.TelegramType == "3201")
                    {
                        var pincodeResult = checkPincodeReply.GetResult();
                        ResponseModel responseBody = Helper.CreateResponseWithMessage(macAddress, result, pincodeResult.Msg, pincodeResult.Ack);
                        responseBody.PinCodeAccepted = true;

                        var pincodeCheckResult = new PincodeCheckResponseModel
                        {
                            Status = result.StatusCode.ToString(),
                            ResponseBody = responseBody
                        };

                        pincodeCheckResultTask.TrySetResult(pincodeCheckResult);
                    }
                });

                // ✅ Wait for pincode check result or timeout
                var completedTask = await Task.WhenAny(pincodeCheckResultTask.Task, Task.Delay(TimeSpan.FromSeconds(120)));

                if (completedTask == pincodeCheckResultTask.Task)
                {
                    return await pincodeCheckResultTask.Task; // ✅ Return successful pincode check
                }
                else
                {
                    // ✅ Handle timeout and unsubscribe
                    _notificationService.Unsubscribe(macAddress);
                    return new PincodeCheckResponseModel
                    {
                        Status = "Timeout",
                        ResponseBody = null
                    };
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error: {ex.Message + ex.StackTrace}");
            }
        }



    }
}
