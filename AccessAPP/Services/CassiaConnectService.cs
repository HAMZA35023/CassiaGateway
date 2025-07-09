using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using Newtonsoft.Json;
using System.Net;
using System.Text;
using System.Threading;

namespace AccessAPP.Services
{
    public class CassiaConnectService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly CassiaNotificationService _notificationService; // ✅ Injected singleton
        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(1);

        public int Status { get; set; }
        public object ResponseBody { get; set; }

        public CassiaConnectService(HttpClient httpClient, IConfiguration configuration, CassiaNotificationService notificationService)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _notificationService = notificationService;
        }

        public async Task<ResponseModel> ConnectToBleDevice(string gatewayIpAddress, int gatewayPort, string macAddress)
        {
            var client = new HttpClient();

            // Define the request URL
            string url = $"http://{gatewayIpAddress}:{gatewayPort}/gap/nodes/{macAddress}/connection";

            // Define the JSON content
            var jsonContent = new StringContent(
                "{ \r\n    \"timeout\" : \"10000\",\r\n    \"type\" : \"public\",\r\n    \"discovergatt\" : 0\r\n}\r\n",
                Encoding.UTF8,
                "application/json"
            );

            // Create the HTTP request message
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = jsonContent
            };

            try
            {
                HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

                await semaphore.WaitAsync(); //lock connect requests

                try
                {
                    // Send the request
                    response = await client.SendAsync(request);
                    Thread.Sleep(1000);
                }
                catch(Exception e)
                {
                    throw e;
                }
                finally
                {
                    semaphore.Release();
                }

                // Ensure the request succeeded
                response.EnsureSuccessStatusCode();

                // Read and display the response content
                if (response.IsSuccessStatusCode)
                {
                    return Helper.CreateResponse(macAddress, response);
                }
                else
                {
                    return Helper.CreateResponse(macAddress, response);
                }
            }
            catch (HttpRequestException e)
            {
                return new ResponseModel
                {
                    MacAddress = macAddress,
                    Data = $"Connection failed: {e.Message}",
                    Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Retries = 0,
                    Status = HttpStatusCode.ExpectationFailed, // or use 0 if unknown
                    PincodeRequired = false,
                    PinCodeAccepted = false
                };
            }
        }

        public async Task<ResponseModel> BatchConnectDevices(string gatewayIpAddress, List<string> macAddresses)
        {
            try
            {
                // Prepare the payload for the batch connect request
                var list = macAddresses.Select(mac => new { type = "public", addr = mac }).ToList();
                var payload = new
                {
                    list = list,
                    timeout = 5000, // Optional: Timeout per device
                    per_dev_timeout = 10000 // Optional: Total timeout per device
                };

                // Batch connect endpoint
                string batchConnectEndpoint = $"http://{gatewayIpAddress}/gap/batch-connect";

                // Serialize the payload and create the request
                var jsonPayload = JsonConvert.SerializeObject(payload); // Serialize the object into JSON

                // Corrected to use a valid MediaTypeHeaderValue instead of string
                var request = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                // Send POST request to batch connect the BLE devices
                HttpResponseMessage batchConnectResponse = await _httpClient.PostAsync(batchConnectEndpoint, request);

                string responseContent = await batchConnectResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"Batch Connect Response: {batchConnectResponse.StatusCode}, Content: {responseContent}");
                // Return the response formatted as ResponseModel
                return Helper.CreateResponse("BatchConnect", batchConnectResponse);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error during batch connect: {ex.Message + ex.StackTrace}");
            }
        }



        public async Task<ResponseModel> SendControlToLight(string gatewayIpAddress, string macAddress, string hexControlValue)
        {
            try
            {
                // Define the write BLE message endpoint
                string writeEndpoint = $"http://{gatewayIpAddress}/gatt/nodes/{macAddress}/handle/19/value/{hexControlValue}?noresponse=1";

                // Send the write BLE message request
                HttpResponseMessage writeResponse = await _httpClient.GetAsync(writeEndpoint);

                // Return the response as ResponseModel
                return Helper.CreateResponse(macAddress, writeResponse);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while sending control to light: {ex.Message + ex.StackTrace}");
            }
        }



        public async Task<ConnectedDevicesView> GetConnectedBleDevices(string gatewayIpAddress, int gatewayPort)
        {
            try
            {
                // Endpoint to get connected BLE devices
                string getConnectedDevicesEndpoint = $"http://{gatewayIpAddress}:{gatewayPort}/gap/nodes?connection_state=connected";

                // Send GET request to get the list of connected BLE devices
                HttpResponseMessage getResponse = await _httpClient.GetAsync(getConnectedDevicesEndpoint);

                if (getResponse.IsSuccessStatusCode)
                {
                    string responseBody = await getResponse.Content.ReadAsStringAsync();
                    var connectedDevices = JsonConvert.DeserializeObject<ConnectedDevicesView>(responseBody);
                    return connectedDevices;
                }
                else
                {
                    throw new Exception($"Failed to get connected devices: {getResponse.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error: {ex.Message + ex.StackTrace}");
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
                    return Helper.CreateResponse(macAddress, response);
                }
                else
                {
                    // Handle unsuccessful response
                    return Helper.CreateResponse(macAddress, response);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error: {ex.Message + ex.StackTrace}");
            }
        }
        public async Task<DataResponseModel> GetDataFromBleDevice(string gatewayIpAddress, int gatewayPort, string macAddress, string value)
        {
            try
            {
                // Write BLE message
                CassiaReadWriteService cassiaReadWrite = new CassiaReadWriteService();
                var result = await cassiaReadWrite.WriteBleMessage(gatewayIpAddress, macAddress, 19, value, "?noresponse=1");

                var responseTask = new TaskCompletionSource<DataResponseModel>();

                // ✅ Subscribe to notifications using the singleton `_notificationService`
                _notificationService.Subscribe(macAddress, (sender, data) =>
                {
                    // Process the notification data
                    var response = new GenericTelegramReply(data);
                    var responseResult = response.DataResult;

                    var responseBody = new DataResponseModel
                    {
                        MacAddress = macAddress,
                        Data = responseResult,
                        Time = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                        Status = result.StatusCode,
                    };

                    responseTask.TrySetResult(responseBody);
                });

                // ✅ Wait for response or timeout
                var completedTask = await Task.WhenAny(responseTask.Task, Task.Delay(TimeSpan.FromSeconds(120)));

                if (completedTask == responseTask.Task)
                {
                    return await responseTask.Task; // ✅ Return received data
                }
                else
                {
                    // ✅ Handle timeout and unsubscribe
                    _notificationService.Unsubscribe(macAddress);
                    return new DataResponseModel
                    {
                        MacAddress = macAddress,
                        Data = "Timeout",
                        Time = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                        Status = HttpStatusCode.RequestTimeout,
                    };
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error: {ex.Message + ex.StackTrace}");
            }
        }
        public async Task<LoginResponseModel> AttemptLogin(string gatewayIpAddress, string macAddress)
        {
            try
            {
                string hexLoginValue = new LoginTelegram().Create();
                CassiaReadWriteService cassiaReadWrite = new CassiaReadWriteService();
                var result = await cassiaReadWrite.WriteBleMessage(gatewayIpAddress, macAddress, 19, hexLoginValue, "?noresponse=1");

                var loginResultTask = new TaskCompletionSource<LoginResponseModel>();

                // ✅ Subscribe to notifications using the singleton `_notificationService`
                _notificationService.Subscribe(macAddress, (sender, data) =>
                {
                    var loginReply = new LoginTelegramReply(data);
                    if (loginReply.TelegramType == "1100")
                    {
                        var loginReplyResult = loginReply.GetResult();
                        ResponseModel responseBody = Helper.CreateResponseWithMessage(macAddress, result, loginReplyResult.Msg, loginReplyResult.PincodeRequired);

                        var attemptLoginResult = new LoginResponseModel
                        {
                            Status = result.StatusCode.ToString(),
                            ResponseBody = responseBody
                        };
                        loginResultTask.TrySetResult(attemptLoginResult);
                    }
                });

                // ✅ Wait for login result or timeout
                var completedTask = await Task.WhenAny(loginResultTask.Task, Task.Delay(TimeSpan.FromSeconds(120)));

                if (completedTask == loginResultTask.Task)
                {
                    return await loginResultTask.Task; // ✅ Return successful login response
                }
                else
                {
                    // ✅ Handle timeout and unsubscribe
                    _notificationService.Unsubscribe(macAddress);
                    return new LoginResponseModel
                    {
                        Status = "Timeout",
                        ResponseBody = new ResponseModel
                        {
                            MacAddress = macAddress,
                            Data = "Login response timeout.",
                            Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            Status = HttpStatusCode.RequestTimeout
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                return new LoginResponseModel
                {
                    Status = "Error",
                    ResponseBody = new ResponseModel
                    {
                        MacAddress = macAddress,
                        Data = $"Exception: {ex.Message}",
                        Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Status = HttpStatusCode.InternalServerError
                    }
                };
            }
        }
        public PairResponse PairDevice(string gatewayIpAddress, int gatewayPort, PairDevicesRequest pairDevicesRequest)
        {
            try
            {
                // Specify the directory path
                string directoryPath = "PairRequests";

                // Check if the directory exists, create it if necessary
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }

                // Generate the file path
                string filePath = Path.Combine(directoryPath, "pairRequest.txt");

                // Join the mac addresses into a single string separated by commas
                // Append the mac addresses to the text file
                foreach (var macAddress in pairDevicesRequest.macAddresses)
                {
                    File.AppendAllText(filePath, macAddress + Environment.NewLine);
                }

                return new PairResponse { PairingStatus = "Success", Message = $"Devices paired: {string.Join(",", pairDevicesRequest.macAddresses)}" };
            }
            catch (Exception ex)
            {
                throw new Exception($"Error pairing devices: {ex.Message + ex.StackTrace}");
            }
        }

        public UnpairResponse UnpairDevice(string gatewayIpAddress, int gatewayPort, UnpairDevicesRequest unpairDevicesRequest)
        {
            try
            {
                // Specify the directory path
                string directoryPath = "PairRequests";

                // Generate the file path
                string filePath = Path.Combine(directoryPath, "pairRequest.txt");

                // Read all lines from the file
                string[] lines = File.ReadAllLines(filePath);

                // Remove the specified mac addresses from the list
                var updatedLines = lines.Where(line => !unpairDevicesRequest.MacAddresses.Contains(line.Trim())).ToList();

                // Write the updated content back to the file
                File.WriteAllLines(filePath, updatedLines);

                return new UnpairResponse
                {
                    MacAddress = string.Join(",", unpairDevicesRequest.MacAddresses),
                    Status = "Unpairing successful"
                };
            }
            catch (Exception ex)
            {
                throw new Exception($"Error unpairing devices: {ex.Message + ex.StackTrace}");
            }
        }

        public List<string> GetPairedDevices()
        {
            try
            {
                // Specify the directory path
                string directoryPath = "PairRequests";

                // Generate the file path
                string filePath = Path.Combine(directoryPath, "pairRequest.txt");

                // Check if the file exists
                if (!File.Exists(filePath))
                {
                    // If the file doesn't exist, return an empty list
                    return new List<string>();
                }

                // Read all lines from the file and return them as a list
                return File.ReadAllLines(filePath).ToList();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error getting paired devices: {ex.Message + ex.StackTrace}");
            }
        }




    }
}
