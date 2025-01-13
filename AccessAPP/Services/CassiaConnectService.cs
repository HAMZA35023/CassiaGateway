using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System.Net;
using System.Net.Http;
using System.Text;

namespace AccessAPP.Services
{
    public class CassiaConnectService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        public int Status { get; set; }
        public object ResponseBody { get; set; }

        public CassiaConnectService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _configuration = configuration;
        }

        public async Task<ResponseModel> ConnectToBleDevice(string gatewayIpAddress, int gatewayPort, string macAddress)
        {
            //try
            //{
            //    // Step 3: Connect selected BLE devices
            //    string connectEndpoint = $"http://{gatewayIpAddress}:{gatewayPort}/gap/nodes/{macAddress}/connection";

            //    // Create the request body
            //    var body = new
            //    {
            //        timeout = "10000",
            //        type = "public",
            //        discovergatt = 0
            //    };

            //    // Serialize the body to JSON
            //    string jsonBody = JsonConvert.SerializeObject(body);
            //    var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            //    // Send POST request to connect to the BLE device
            //    HttpResponseMessage connectResponse = await _httpClient.PostAsync(connectEndpoint, content);

            //    if (connectResponse.IsSuccessStatusCode)
            //    {
            //        return Helper.CreateResponse(macAddress, connectResponse);
            //    }
            //    else
            //    {
            //        return Helper.CreateResponse(macAddress, connectResponse);
            //    }
            //}
            //catch (Exception ex)
            //{
            //    throw new Exception($"Error: {ex.Message + ex.StackTrace}");
            //}
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
                // Send the request
                var response = await client.SendAsync(request);

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
                Console.WriteLine($"Request error: {e.Message}");
                throw new Exception($"Error: {e.Message + e.StackTrace}");
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

                using (var cassiaListener = new CassiaNotificationService(_configuration))
                {
                    var responseTask = new TaskCompletionSource<DataResponseModel>();

                    cassiaListener.Subscribe(macAddress, (sender, data) =>
                    {
                        // Process the notification data
                        var response = new GenericTelegramReply(data);

                        var responseResult = response.DataResult;
                        DataResponseModel responseBody = new DataResponseModel
                        {
                            MacAddress = macAddress,
                            Data = responseResult,
                            Time = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                            Status = result.StatusCode,
                        };

                        responseTask.TrySetResult(responseBody);

                    });

                    // Wait for the response or timeout
                    var completedTask = await Task.WhenAny(responseTask.Task, Task.Delay(TimeSpan.FromSeconds(120)));

                    // Unsubscribe from notifications
                    // cassiaListener.Unsubscribe(macAddress);
                    
                    // Check if the response task completed
                    if (completedTask == responseTask.Task)
                    {
                        return await responseTask.Task;
                    }
                    else
                    {
                        // Handle timeout
                        return new DataResponseModel
                        {
                            MacAddress = macAddress,
                            Data = "Timeout",
                            Time = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                            Status = HttpStatusCode.RequestTimeout,

                        };
                    }
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
                using (var cassiaListener = new CassiaNotificationService(_configuration))
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
                    cassiaListener.Unsubscribe(macAddress);

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
                throw new Exception($"Error: {ex.Message + ex.StackTrace}");
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
