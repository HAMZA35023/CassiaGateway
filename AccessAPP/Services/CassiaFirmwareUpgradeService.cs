using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using AccessAPP.Models;
using System.Net.Mail;
using AccessAPP.Services.HelperClasses;
using System.Runtime.InteropServices;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Reflection.Metadata;
using System.Net.Sockets;
using System.Collections.Concurrent;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Bluetooth;
using Windows.Storage.Streams;
using System.Windows.Markup;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using Windows.Services.Maps;
using Windows.Devices.Sensors;

namespace AccessAPP.Services
{
    public class CassiaFirmwareUpgradeService
    {
        private readonly HttpClient _httpClient;
        private readonly CassiaConnectService _connectService;
        private readonly CassiaPinCodeService _cassiaPinCodeService;
        private readonly IConfiguration _configuration;
        private const int MaxPacketSize = 270;
        private const int InterPacketDelay = 0;
        private readonly string _firmwareActorFilePath = "C:\\Users\\HRS\\source\\repos\\AccessAPP\\AccessAPP\\FirmwareVersions\\353AP20227.cyacd";
        private readonly string _firmwareSensorFilePath4 = "C:\\Users\\HRS\\source\\repos\\AccessAPP\\AccessAPP\\FirmwareVersions\\353AP40227.cyacd";
        private readonly string _firmwareSensorFilePath3 = "C:\\Users\\HRS\\source\\repos\\AccessAPP\\AccessAPP\\FirmwareVersions\\353AP30227.cyacd";
        private readonly string _firmwareSensorFilePath1 = "C:\\Users\\HRS\\source\\repos\\AccessAPP\\AccessAPP\\FirmwareVersions\\353AP10227.cyacd";
        private readonly string _firmwareBootLoaderFilePath = "C:\\Users\\HRS\\source\\repos\\AccessAPP\\AccessAPP\\FirmwareVersions\\353BL10604.cyacd";
        
        private readonly ConcurrentQueue<byte[]> _notificationQueue = new ConcurrentQueue<byte[]>();
        private ManualResetEvent _notificationEvent = new ManualResetEvent(false);
        private readonly HashSet<string> _subscribedMacAddresses = new HashSet<string>();
        internal const int ERR_SUCCESS = 0;
        internal const int ERR_OPEN = 1;
        internal const int ERR_CLOSE = 2;
        internal const int ERR_READ = 3;
        internal const int ERR_WRITE = 4;
        double progressBarProgress = 0;
        double progressBarStepSize = 5;
        private readonly byte[] _securityKey = { 0x49, 0xA1, 0x34, 0xB6, 0xC7, 0x79 }; // Security ID
        private readonly byte _appID = 0x00; // AppID as shown in the screenshot
        private readonly string _gatewayIpAddress;
        private readonly int _gatewayPort;
        private string MacAddress = "";
        private double totalRows = 0;
        private bool bootloader = false;
        private int sensorType = 4;
        private readonly CassiaNotificationService _notificationService; // ✅ Injected singleton

        public CassiaFirmwareUpgradeService(HttpClient httpClient, CassiaConnectService connectService, CassiaPinCodeService cassiaPinCodeService, CassiaNotificationService notificationService, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _connectService = connectService;
            
            _cassiaPinCodeService = cassiaPinCodeService;
            _configuration = configuration;
            _gatewayIpAddress = _configuration.GetValue<string>("GatewayConfiguration:IpAddress");
            _gatewayPort = _configuration.GetValue<int>("GatewayConfiguration:Port");
            _notificationService = notificationService;
        }

        public async Task<ServiceResponse> UpgradeSensorAsync(string nodeMac, string pincode, bool bActor)
        {
           
            // Step 1: Connect to the device
            ServiceResponse response = null;
            var connectionResult = await _connectService.ConnectToBleDevice(_gatewayIpAddress, 80, nodeMac);
            if (connectionResult.Status != HttpStatusCode.OK)
            {
                response.Success = false;
                response.StatusCode = (int)connectionResult.Status;
                response.Message = "Failed to connect to device.";
                return response;
            }

            Console.WriteLine("Connected to device...");

            bool isAlreadyInBootMode = CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac);
            if (isAlreadyInBootMode)
            {
                Console.WriteLine("Device is already in boot mode.");
                await Task.Delay(3000);
                var serviceResponse = await ProcessingSensorUpgrade(nodeMac, bActor);
                return serviceResponse;
            }
            else
            {
                // Step 2: Attempt login if needed
                var loginResult = await _connectService.AttemptLogin(_gatewayIpAddress, nodeMac);
                if (loginResult.ResponseBody.PincodeRequired && !string.IsNullOrEmpty(pincode))
                {
                    var checkPincodeResponse = await _cassiaPinCodeService.CheckPincode(_gatewayIpAddress, nodeMac, pincode);
                    loginResult.ResponseBody = checkPincodeResponse.ResponseBody;
                }

                if (loginResult.ResponseBody.PincodeRequired && !loginResult.ResponseBody.PinCodeAccepted)
                {
                    response.Success = false;
                    response.StatusCode = 401; // Unauthorized
                    response.Message = "Failed to login to the device.";
                    return response;
                }

                Console.WriteLine("Logged into device...");

                // Send Jump to Bootloader telegram repeatedly until successful
                const int maxAttempts = 5;
                bool bootModeAchieved = false;
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    bootModeAchieved = await SendJumpToBootloader(_gatewayIpAddress, nodeMac, bActor);
                    if (bootModeAchieved)
                    {
                        Console.WriteLine($"Device entered boot mode after {attempt + 1} attempts.");
                        break;
                    }
                    Console.WriteLine($"Attempt {attempt + 1} to enter boot mode failed. Retrying...");
                    await Task.Delay(3000); // Delay between attempts
                }

                if (!bootModeAchieved)
                {
                    response.Success = false;
                    response.StatusCode = 417; // Expectation Failed
                    response.Message = "Failed to enter boot mode.";
                    return response;
                }

                // Disconnect and prepare for the upgrade process
                Console.WriteLine("device disconnected and will reconnect after 3s");
                var isDisconnected = await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0);
                await Task.Delay(3000);

                var serviceResponse = await ProcessingSensorUpgrade(nodeMac, bActor);
                return serviceResponse;
            }
        }
        public async Task<ServiceResponse> UpgradeActorAsync(string nodeMac, string pincode, bool bActor)
        {
            ServiceResponse response = new();
            var connectionResult = await _connectService.ConnectToBleDevice(_gatewayIpAddress, 80, nodeMac);
            if (connectionResult.Status != HttpStatusCode.OK)
            {
                response.Success = false;
                response.StatusCode = (int)connectionResult.Status;
                response.Message = "Failed to connect to device.";
                return response;
            }

            Console.WriteLine("Connected to device...");

            bool isAlreadyInBootMode = CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac);
            if (isAlreadyInBootMode)
            {
                response.Success = false;
                response.StatusCode = 409; // Conflict
                response.Message = "Sensor is already in boot mode. It needs to be in Application mode.";
                return response;
            }
            else
            {
                //Step 2: Attempt login if needed
                var loginResult = await _connectService.AttemptLogin(_gatewayIpAddress, nodeMac);
                if (loginResult.ResponseBody.PincodeRequired && !string.IsNullOrEmpty(pincode))
                {
                    var checkPincodeResponse = await _cassiaPinCodeService.CheckPincode(_gatewayIpAddress, nodeMac, pincode);
                    loginResult.ResponseBody = checkPincodeResponse.ResponseBody;
                }

                if (loginResult.ResponseBody.PincodeRequired && !loginResult.ResponseBody.PinCodeAccepted)
                {
                    response.Success = false;
                    response.StatusCode = 401; // Unauthorized
                    response.Message = "Failed to login to the device.";
                    return response;
                }

                Console.WriteLine("Logged into device...");


                // Send Jump to Bootloader telegram
                bool jumpToBootResponse = await SendJumpToBootloader(_gatewayIpAddress, nodeMac, bActor);
                if (!jumpToBootResponse)
                {
                    response.Success = false;
                    response.StatusCode = 417; // Expectation Failed
                    response.Message = "Failed to enter boot mode.";
                    return response;
                }

                Console.WriteLine(jumpToBootResponse);

                // Delays for 3 seconds (3000 milliseconds) before connecting to device again
                //var isDisConnected = await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0);
                //await Task.Delay(3000);
                var serviceResponse = await ProcessingActorUpgrade(nodeMac, bActor);

                return serviceResponse;

            }
        }

        public async Task<ServiceResponse> BulkUpgradeActorsAsync(List<BulkUpgradeRequest> requests)
        {
            var response = new ServiceResponse
            {
                Success = true,
                StatusCode = 200,
                Message = "Bulk upgrade completed successfully."
            };

            var taskList = new List<Task<ServiceResponse>>();
            var upgradeResults = new ConcurrentBag<ServiceResponse>();
            var semaphore = new SemaphoreSlim(1); // Limit to 3 concurrent upgrades

            foreach (var request in requests)
            {
                await semaphore.WaitAsync();

                taskList.Add(Task.Run(async () =>
                {
                    try
                    {
                        var result = await UpgradeActorAsync(request.MacAddress, request.Pincode, request.bActor);
                        upgradeResults.Add(result);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error upgrading actor {request.MacAddress}: {ex.Message}");
                        return new ServiceResponse
                        {
                            Success = false,
                            StatusCode = 500,
                            Message = $"Error upgrading actor {request.MacAddress}: {ex.Message}"
                        };
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(taskList);

            // Aggregate responses to determine overall success
            var failedUpgrades = upgradeResults.Where(r => !r.Success).ToList();
            if (failedUpgrades.Any())
            {
                response.Success = false;
                response.StatusCode = 207; // Multi-Status
                response.Message = $"Bulk upgrade completed with errors. Failed actors: {string.Join(", ", failedUpgrades.Select(r => r.Message))}";
            }

            return response;
        }

        public async Task<ServiceResponse> BulkUpgradeSensorAsync(List<BulkUpgradeRequest> requests)
        {
            var response = new ServiceResponse
            {
                Success = true,
                StatusCode = 200,
                Message = "Bulk upgrade completed successfully."
            };

            var taskList = new List<Task<ServiceResponse>>();
            var upgradeResults = new ConcurrentBag<ServiceResponse>();
            var semaphore = new SemaphoreSlim(1); // Limit to 3 concurrent upgrades

            foreach (var request in requests)
            {
                await semaphore.WaitAsync();

                taskList.Add(Task.Run(async () =>
                {
                    try
                    {
                        var result = await UpgradeSensorAsync(request.MacAddress, request.Pincode, request.bActor);
                        upgradeResults.Add(result);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error upgrading sensor {request.MacAddress}: {ex.Message}");
                        return new ServiceResponse
                        {
                            Success = false,
                            StatusCode = 500,
                            Message = $"Error upgrading sensor {request.MacAddress}: {ex.Message}"
                        };
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(taskList);

            // Aggregate responses to determine overall success
            var failedUpgrades = upgradeResults.Where(r => !r.Success).ToList();
            if (failedUpgrades.Any())
            {
                response.Success = false;
                response.StatusCode = 207; // Multi-Status
                response.Message = $"Bulk upgrade completed with errors. Failed actors: {string.Join(", ", failedUpgrades.Select(r => r.Message))}";
            }

            return response;
        }

        public async Task<ServiceResponse> UpgradeDeviceAsync(string macAddress, string pincode)
        {
            var response = new ServiceResponse();

            try
            {
                // Step 2: Upgrade the actor
                Stopwatch stopwatch = new Stopwatch();

                Console.WriteLine($"Starting actor upgrade for {macAddress}");
   
                stopwatch.Start();
                var actorUpgradeResult = await UpgradeActorAsync(macAddress, pincode, true);
                stopwatch.Stop();
                Console.WriteLine($"Actor upgrade completed for {macAddress}. Time taken: {stopwatch.Elapsed.TotalSeconds} seconds");
                if (!actorUpgradeResult.Success)
                {
                    response.Success = false;
                    response.StatusCode = actorUpgradeResult.StatusCode;
                    response.Message = $"Actor upgrade failed: {actorUpgradeResult.Message}";
                    return response; // Stop if actor upgrade fails
                }

                Console.WriteLine($"Actor upgrade completed for {macAddress}");

                Task.Delay(10000);
                bootloader = true;
                Console.WriteLine($"Starting bootloader upgrade for {macAddress}");
                stopwatch.Restart();
                // Step 1: Upgrade the sensor
                var bootladerUpgradeResult = await UpgradeSensorAsync(macAddress, pincode, false);
                stopwatch.Stop();
                Console.WriteLine($"Bootloader upgrade completed for {macAddress}. Time taken: {stopwatch.Elapsed.TotalSeconds} seconds");

                if (!bootladerUpgradeResult.Success)
                {
                    response.Success = false;
                    response.StatusCode = bootladerUpgradeResult.StatusCode;
                    response.Message = $"bootloader upgrade failed: {bootladerUpgradeResult.Message}";
                    return response; // Stop if sensor upgrade fails
                }

                Console.WriteLine($"bootloader upgrade completed for {macAddress}");


                Console.WriteLine($"Starting Sensor upgrade for {macAddress}");
                Task.Delay(10000);
                bootloader = false;
                

                // Step 1: Upgrade the sensor
                stopwatch.Restart();
                var sensorUpgradeResult = await UpgradeSensorAsync(macAddress, pincode, false);
                stopwatch.Stop();
                Console.WriteLine($"Sensor upgrade completed for {macAddress}. Time taken: {stopwatch.Elapsed.TotalSeconds} seconds");
                if (!sensorUpgradeResult.Success)
                {
                    response.Success = false;
                    response.StatusCode = sensorUpgradeResult.StatusCode;
                    response.Message = $"Sensor upgrade failed: {sensorUpgradeResult.Message}";
                    return response; // Stop if sensor upgrade fails
                }

                Console.WriteLine($"Sensor upgrade completed for {macAddress}");

                Console.WriteLine("delay for 1 minute");

               

                // Both upgrades successful
                response.Success = true;
                response.StatusCode = 200;
                response.Message = "Sensor and actor upgrades completed successfully.";
                return response;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during sensor and actor upgrade: {ex.Message}");
                response.Success = false;
                response.StatusCode = 500; // Internal Server Error
                response.Message = "An unexpected error occurred during the upgrade process.";
                return response;
            }
        }

        public async Task<List<UpgradeResponse>> UpgradeBLSensorsAsync(List<BulkUpgradeRequest> devices)
        {
            var responses = new Dictionary<string, UpgradeResponse>(); // Stores latest response for each device
            var failedDevices = new Queue<(BulkUpgradeRequest, int)>(); // (Device, Retry Count)

            foreach (var device in devices)
            {
                sensorType = device.sType;
                var response = await UpgradeBLSensorWithRetryAsync(device, 0);
                responses[device.MacAddress] = response; // Always store latest response

                if (!response.Success)
                {
                    failedDevices.Enqueue((device, 1)); // Initial retry count is 1
                }

                Console.WriteLine("Next Device will be upgraded after 10 seconds");
                
            }

            Console.WriteLine($"Initial upgrade completed. Retrying failed devices: {failedDevices.Count} devices.");

            while (failedDevices.Count > 0)
            {
                var (device, retryCount) = failedDevices.Dequeue();
                var response = await UpgradeBLSensorWithRetryAsync(device, retryCount);
                responses[device.MacAddress] = response; // Overwrite previous responses

                if (!response.Success && retryCount < 2) // Retry up to 2 times
                {
                    failedDevices.Enqueue((device, retryCount + 1));
                }
            }

            return responses.Values.ToList(); // Return only the latest responses
        }



        private async Task<UpgradeResponse> UpgradeBLSensorWithRetryAsync(BulkUpgradeRequest device, int retryCount)
        {
            var response = new UpgradeResponse
            {
                MacAddress = device.MacAddress,
                RetryCount = retryCount
            };

            try
            {
                Stopwatch stopwatch = new Stopwatch();
                bootloader = true;
                Console.WriteLine($"Starting bootloader upgrade for {device.MacAddress}, Attempt {retryCount + 1}");
                stopwatch.Restart();

                // Step 1: Bootloader Upgrade
                var bootloaderUpgradeResult = await UpgradeSensorAsync(device.MacAddress, device.Pincode, false);
                stopwatch.Stop();
                Console.WriteLine($"Bootloader upgrade completed for {device.MacAddress}. Time taken: {stopwatch.Elapsed.TotalSeconds} seconds");

                if (!bootloaderUpgradeResult.Success)
                {
                    Console.WriteLine($"Bootloader upgrade failed for {device.MacAddress}. Skipping sensor upgrade.");
                    response.Success = false;
                    response.StatusCode = bootloaderUpgradeResult.StatusCode;
                    response.Message = $"Bootloader upgrade failed: {bootloaderUpgradeResult.Message}";
                    return response;
                }

                // Allow bootloader transition delay
                await Task.Delay(10000);
                bootloader = false;

                Console.WriteLine($"Starting sensor upgrade for {device.MacAddress}, Attempt {retryCount + 1}");

                // Step 2: Sensor Upgrade (Only if Bootloader upgrade succeeded)
                stopwatch.Restart();
                var sensorUpgradeResult = await UpgradeSensorAsync(device.MacAddress, device.Pincode, false);
                stopwatch.Stop();
                Console.WriteLine($"Sensor upgrade completed for {device.MacAddress}. Time taken: {stopwatch.Elapsed.TotalSeconds} seconds");

                if (!sensorUpgradeResult.Success)
                {
                    response.Success = false;
                    response.StatusCode = sensorUpgradeResult.StatusCode;
                    response.Message = $"Sensor upgrade failed: {sensorUpgradeResult.Message}";
                    return response;
                }

                response.Success = true;
                response.StatusCode = 200;
                response.Message = "Sensor and bootloader upgrades completed successfully.";
                return response;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during sensor and bootloader upgrade for {device.MacAddress}: {ex.Message}");
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "An unexpected error occurred during the upgrade process.";
                return response;
            }
        }



        //public async Task<ServiceResponse> BulkUpgradeDevicesAsync(List<BulkUpgradeRequest> requests)
        //{
        //    var response = new ServiceResponse
        //    {
        //        Success = true,
        //        StatusCode = 200,
        //        Message = "Bulk device upgrade completed successfully."
        //    };

        //    Stopwatch stopwatch = new Stopwatch();
        //    stopwatch.Restart();
        //    var upgradeResults = new ConcurrentBag<ServiceResponse>();
        //    var semaphore = new SemaphoreSlim(1); // Limit to 1 concurrent upgrade (adjust as needed)

        //    // Phase 1: Upgrade Bootloader and Sensor for all devices
        //    var phase1TaskList = new List<Task<ServiceResponse>>();
        //    foreach (var request in requests)
        //    {
        //        await semaphore.WaitAsync();

        //        phase1TaskList.Add(Task.Run(async () =>
        //        {
        //            try
        //            {
        //                var result = await UpgradeBLSensorAsync(request.MacAddress, request.Pincode);
        //                upgradeResults.Add(result);
        //                return result;
        //            }
        //            catch (Exception ex)
        //            {
        //                Console.WriteLine($"Error upgrading device {request.MacAddress}: {ex.Message}");
        //                return new ServiceResponse
        //                {
        //                    Success = false,
        //                    StatusCode = 500,
        //                    Message = $"Error upgrading device {request.MacAddress}: {ex.Message}"
        //                };
        //            }
        //            finally
        //            {
        //                semaphore.Release();
        //            }
        //        }));

        //        await Task.Delay(TimeSpan.FromSeconds(5)); // Delay between starting tasks
        //    }

        //    // Wait for all Phase 1 tasks to complete
        //    await Task.WhenAll(phase1TaskList);

        //    // Log Phase 1 results
        //    foreach (var result in phase1TaskList)
        //    {
        //        Console.WriteLine($"Phase 1 Result: {result.Result.Message}");
        //    }

        //    await Task.Delay(TimeSpan.FromSeconds(10));

        //    Console.WriteLine("Delay Introduced before Actor program");

        //    // Phase 2: Upgrade Actors for all devices
        //    var phase2TaskList = new List<Task<ServiceResponse>>();
        //    foreach (var request in requests)
        //    {
        //        await semaphore.WaitAsync();

        //        phase2TaskList.Add(Task.Run(async () =>
        //        {
        //            try
        //            {
        //                var result = await UpgradeActorAsync(request.MacAddress, request.Pincode, true);
        //                upgradeResults.Add(result);
        //                return result;
        //            }
        //            catch (Exception ex)
        //            {
        //                Console.WriteLine($"Error upgrading actor for {request.MacAddress}: {ex.Message}");
        //                return new ServiceResponse
        //                {
        //                    Success = false,
        //                    StatusCode = 500,
        //                    Message = $"Error upgrading actor for {request.MacAddress}: {ex.Message}"
        //                };
        //            }
        //            finally
        //            {
        //                semaphore.Release();
        //            }
        //        }));

        //        await Task.Delay(TimeSpan.FromSeconds(5)); // Delay between starting tasks
        //    }

        //    // Wait for all Phase 2 tasks to complete
        //    await Task.WhenAll(phase2TaskList);

        //    // Log Phase 2 results
        //    foreach (var result in phase2TaskList)
        //    {
        //        Console.WriteLine($"Phase 2 Result: {result.Result.Message}");
        //    }

        //    stopwatch.Stop();
        //    Console.WriteLine($"All devices got upgraded. Time taken: {stopwatch.Elapsed.TotalSeconds} seconds");

        //    // Aggregate responses to determine overall success
        //    var failedUpgrades = upgradeResults.Where(r => !r.Success).ToList();
        //    if (failedUpgrades.Any())
        //    {
        //        response.Success = false;
        //        response.StatusCode = 207; // Multi-Status
        //        response.Message = $"Bulk device upgrade completed with errors. Failed devices: {string.Join(", ", failedUpgrades.Select(r => r.Message))}";
        //    }

        //    return response;
        //}

        public async Task<ServiceResponse> BulkUpgradeDevicesAsync(List<BulkUpgradeRequest> requests)
        {
            var progressList = requests.Select(req => new UpgradeProgress { MacAddress = req.MacAddress, Pincode = req.Pincode }).ToList();

            // Phase 1: Initial Upgrades
            await UpgradeDevicesSequentially(progressList);

            // Phase 2: Retry Failed Devices (up to 3 times)
            Console.WriteLine("wait for 1 minute before retrying");
            Task.Delay(10000).Wait();
            await RetryFailedDevices(progressList, 3);

            // Prepare Final Report
            var successfulDevices = progressList.Where(d => d.IsFullyUpgraded).Select(d => d.MacAddress).ToList();
            var failedDevices = progressList.Where(d => !d.IsFullyUpgraded).Select(d => new { d.MacAddress, d.LastFailureReason }).ToList();

            return new ServiceResponse
            {
                Success = failedDevices.Count == 0,
                StatusCode = failedDevices.Count == 0 ? 200 : 207,
                Message = failedDevices.Count == 0 ? "All devices upgraded successfully." : "Some devices failed to upgrade after retries."
                //,
                //Data = new { SuccessfulDevices = successfulDevices, FailedDevices = failedDevices }
            };
        }

        private async Task UpgradeDevicesSequentially(List<UpgradeProgress> devices)
        {
            foreach (var device in devices)
            {
                Console.WriteLine($"Starting upgrade for device {device.MacAddress}");


                await UpgradeDeviceAsync(device.MacAddress, device.Pincode);
            }
        }

        private async Task RetryFailedDevices(List<UpgradeProgress> devices, int maxRetries)
        {
            bool retryRequired;
            do
            {
                retryRequired = false;

                foreach (var device in devices.Where(d => !d.IsFullyUpgraded && d.RetryCount < maxRetries).ToList())
                {
                    Console.WriteLine($"Retrying upgrade for {device.MacAddress}, Attempt {device.RetryCount + 1}");

                    if (!device.BootloaderSuccess)
                    {
                        var bootloaderResponse = await UpgradeSensorAsync(device.MacAddress, device.Pincode,false);
                        if (bootloaderResponse.Success)
                            device.BootloaderSuccess = true;
                        else
                        {
                            device.LastFailureReason = $"Bootloader Retry Failed: {bootloaderResponse.Message}";
                            device.RetryCount++;
                            retryRequired = true;
                            continue;
                        }
                    }
                    if (!device.SensorSuccess)
                    {
                        var sensorResponse = await UpgradeSensorAsync(device.MacAddress, device.Pincode, false);
                        if (sensorResponse.Success)
                            device.SensorSuccess = true;
                        else
                        {
                            device.LastFailureReason = $"Sensor Retry Failed: {sensorResponse.Message}";
                            device.RetryCount++;
                            retryRequired = true;
                            continue;
                        }
                    }
                    if (!device.ActorSuccess)
                    {
                        var actorResponse = await UpgradeActorAsync(device.MacAddress, device.Pincode, true);
                        if (actorResponse.Success)
                            device.ActorSuccess = true;
                        else
                        {
                            device.LastFailureReason = $"Actor Retry Failed: {actorResponse.Message}";
                            device.RetryCount++;
                            retryRequired = true;
                            continue;
                        }
                    }
                }
            } while (retryRequired && devices.Any(d => !d.IsFullyUpgraded && d.RetryCount < maxRetries));
        }

        public async Task<ServiceResponse> ProcessingSensorUpgrade(string nodeMac, bool bActor) // should be moved to firmware services
        {
            Console.WriteLine("Processing Sensor Upgrade started");
            var response = new ServiceResponse();
            var isConnected = await _connectService.ConnectToBleDevice(_gatewayIpAddress, 80, nodeMac);
            if (isConnected.Status != HttpStatusCode.OK)
            {
                Console.WriteLine("Failed to connect to device.");
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Failed to connect to device.";
                return response;
            }

            bool isAlreadyInBootMode = CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac);

            //var notificationService = new CassiaNotificationService(_configuration);
            if (isAlreadyInBootMode)
            {
                //await Task.Delay(3000);

                bool notificationEnabled = await _notificationService.EnableNotificationAsync(_gatewayIpAddress, nodeMac, bActor);

                if (!notificationEnabled)
                {
                    response.Success = false;
                    response.StatusCode = 500;
                    response.Message = "Error Enabling Notifications";
                    return response;
                }

                Console.WriteLine("bootloader mode achieved and Notification enabled status:", notificationEnabled);

            }
            else
            {
                const int maxAttempts = 5;
                bool bootModeAchieved = false;
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    bootModeAchieved = await SendJumpToBootloader(_gatewayIpAddress, nodeMac, bActor);
                    if (bootModeAchieved)
                    {
                        Console.WriteLine($"Device entered boot mode after {attempt + 1} attempts.");
                        break;
                    }
                    Console.WriteLine($"Attempt {attempt + 1} to enter boot mode failed. Retrying...");
                    await Task.Delay(3000); // Delay between attempts
                }

                if (!bootModeAchieved)
                {
                    response.Success = false;
                    response.StatusCode = 417; // Expectation Failed
                    response.Message = "Failed to enter boot mode.";
                    return response;
                }
                
            }

            //Step 3: Start Programming the Sensor
            bool programmingResult = ProgramDevice(_gatewayIpAddress, nodeMac, _notificationService, bActor);

            if (programmingResult)
            {
                response.Success = true;
                response.StatusCode = 200;
                response.Message = "Programming Complete";
                return response;
            }
            else
            {
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Programming Failed";
                return response;
            }

        }

        public async Task<ServiceResponse> ProcessingActorUpgrade(string nodeMac, bool bActor) // should be moved to firmware services
        {
            var response = new ServiceResponse();
            const int maxRetryAttempts = 3; // Maximum number of retries to put the actor into boot mode
            const int delayBetweenRetries = 5000; // Delay between retries (in milliseconds)
            int retryCount = 0;

            // Step 1: Check if the actor is in boot mode
            while (retryCount < maxRetryAttempts)
            {
                var isActorInBootMode = await ActorBootCheck(_gatewayIpAddress, nodeMac);

                if (isActorInBootMode)
                {
                    Console.WriteLine($"Actor {nodeMac} is in boot mode.");
                    break; // Exit the loop if the actor is already in boot mode
                }
                else
                {
                    retryCount++;
                    Console.WriteLine($"Actor {nodeMac} is not in boot mode. Attempting to put it into boot mode. Retry {retryCount}/{maxRetryAttempts}");

                    // Send a command to put the actor into boot mode
                    var jumpToBootloaderSuccess = await SendJumpToBootloader(_gatewayIpAddress, nodeMac, bActor);

                    if (!jumpToBootloaderSuccess)
                    {
                        Console.WriteLine($"Failed to send jump-to-bootloader command for {nodeMac}. Retrying...");
                    }

                    // Wait for a while before retrying
                    await Task.Delay(delayBetweenRetries);
                }
            }

            // If after max retries the actor is still not in boot mode, return an error response
            if (retryCount >= maxRetryAttempts)
            {
                Console.WriteLine($"Failed to put actor {nodeMac} into boot mode after {maxRetryAttempts} attempts.");
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Failed to put actor into boot mode.";
                return response;
            }

            // Step 2: Enable notifications
  
            Console.WriteLine($"Bootloader mode achieved for {nodeMac}.");

            // Step 3: Start programming the actor
            var programmingResult = ProgramDevice(_gatewayIpAddress, nodeMac, _notificationService, bActor);

            if (programmingResult)
            {
                response.Success = true;
                response.StatusCode = 200;
                response.Message = "Programming Complete";
                return response;
            }
            else
            {
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Programming Failed";
                return response;
            }
        }
        public bool ProgramDevice(string gatewayIpAddress, string nodeMac, CassiaNotificationService cassiaNotificationService, bool bActor)
        {
            Console.WriteLine($"Actor is going to be programmed? : {bActor}");
            try
            {
                InitializeNotificationSubscription(nodeMac, cassiaNotificationService);
                int lines;
                MacAddress = nodeMac;
                Bootloader_Utils.CyBtldr_ProgressUpdate Upd = new Bootloader_Utils.CyBtldr_ProgressUpdate(ProgressUpdate);
                Bootloader_Utils.CyBtldr_CommunicationsData m_comm_data = new Bootloader_Utils.CyBtldr_CommunicationsData();
                m_comm_data.OpenConnection = OpenConnection;
                m_comm_data.CloseConnection = CloseConnection;
                ReturnCodes local_status = 0x00;
                if (bActor)
                {
                    Console.WriteLine("Programming Actor");
                    lines = File.ReadAllLines(_firmwareActorFilePath).Length - 1; //Don't count header
                    var progressBarStepSize = 100.0 / lines;
                    m_comm_data.WriteData = WriteActorData;
                    m_comm_data.ReadData = ReadActorData;
                    m_comm_data.MaxTransferSize = 72;
                    local_status = (ReturnCodes)Bootloader_Utils.CyBtldr_Program(_firmwareActorFilePath, null, _appID, ref m_comm_data, Upd);
                }
                else
                {
                    if (bootloader)
                    {
                        Console.WriteLine("Programming Bootloader");
                        lines = File.ReadAllLines(_firmwareBootLoaderFilePath).Length - 1; //Don't count header
                        var progressBarStepSize = 100.0 / lines;
                        m_comm_data.WriteData = WriteSensorData;
                        m_comm_data.ReadData = ReadData;
                        m_comm_data.MaxTransferSize = 265;
                        local_status = (ReturnCodes)Bootloader_Utils.CyBtldr_Program(_firmwareBootLoaderFilePath, _securityKey, _appID, ref m_comm_data, Upd);
                    }
                    else
                    {
                        var FP = "";
                        if (sensorType == 4) { FP = _firmwareSensorFilePath4; } else if (sensorType == 3) { FP = _firmwareSensorFilePath3; } else { FP = _firmwareSensorFilePath1; }
                        Console.WriteLine("Programming Sensor");
                        lines = File.ReadAllLines(FP).Length - 1; //Don't count header
                        var progressBarStepSize = 100.0 / lines;
                        m_comm_data.WriteData = WriteSensorData;
                        m_comm_data.ReadData = ReadData;
                        m_comm_data.MaxTransferSize = 265;
                        local_status = (ReturnCodes)Bootloader_Utils.CyBtldr_Program(FP, _securityKey, _appID, ref m_comm_data, Upd);
                    }

                }

                if(local_status == ReturnCodes.CYRET_SUCCESS)
                {
                    return true;
                }
                else
                { return false; }

            }
            finally
            {
                //UnsubscribeNotification(nodeMac, cassiaNotificationService);
            }

        }


        public int ReadData(IntPtr buffer, int size)
        {

            Console.WriteLine("ReadData called here for actor and sensor");

            try
            {
                // Wait for notification data to be available
                if (!_notificationEvent.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    Console.WriteLine("ReadData timeout waiting for notification");
                    return ERR_READ; // Timeout or no data available
                }

                // Dequeue the notification data
                if (_notificationQueue.TryDequeue(out var notificationData))
                {
                    // Copy the notification data into the provided buffer
                    int bytesToCopy = Math.Min(size, notificationData.Length);
                    Marshal.Copy(notificationData, 0, buffer, bytesToCopy);

                    Console.WriteLine($"ReadData succeeded, bytes read: {bytesToCopy}");
                    return ERR_SUCCESS; // Success
                }
                else
                {
                    Console.WriteLine("ReadData failed: No data available in queue");
                    return ERR_READ; // No data available
                }
            }
            finally
            {
                // Reset the event so it can wait for the next notification
                _notificationEvent.Reset();
            }

        }

        public int ReadActorData(IntPtr buffer, int size)
        {

            Console.WriteLine("ReadData called here for actor and sensor");

            try
            {
                // Wait for notification data to be available
                if (!_notificationEvent.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    Console.WriteLine("ReadData timeout waiting for notification");
                    return ERR_READ; // Timeout or no data available
                }

                // Dequeue the notification data
                if (_notificationQueue.TryDequeue(out var notificationData))
                {
                    // Copy the notification data into the provided buffer
                    int bytesToSkip = 7;
                    int bytesToCopy = Math.Min(size, notificationData.Length - bytesToSkip);

                    // Ensure there are enough bytes to skip
                    if (notificationData.Length > bytesToSkip)
                    {
                        Marshal.Copy(notificationData, bytesToSkip, buffer, bytesToCopy);
                        Console.WriteLine($"Skipped {bytesToSkip} bytes and copied {bytesToCopy} bytes.");
                    }
                    else
                    {
                        Console.WriteLine($"Not enough data to skip {bytesToSkip} bytes. Copy operation skipped.");
                        return ERR_READ; // Return an appropriate error code
                    }


                    Console.WriteLine($"ReadData succeeded, bytes read: {bytesToCopy}");
                    return ERR_SUCCESS; // Success
                }
                else
                {
                    Console.WriteLine("ReadData failed: No data available in queue");
                    return ERR_READ; // No data available
                }
            }
            finally
            {
                // Reset the event so it can wait for the next notification
                _notificationEvent.Reset();
            }

        }

        /// <summary>
        /// Method that writes to the USB device
        /// </summary>
        /// <param name="buffer">Pointer to an array where data written to USB device is stored </param>
        /// <param name="size"> Size of the Buffer </param>
        /// <returns></returns>

        ///Sensor Programming
        public int WriteSensorData(IntPtr buffer, int size)
        {
            bool status = false;
            byte[] data = new byte[size];
            Marshal.Copy(buffer, data, 0, size);
            CassiaReadWriteService cassiaReadWriteService = new CassiaReadWriteService();

            if (GetHidDevice())
            {
                try
                {
                    string hexData = BitConverter.ToString(data).Replace("-", "");

                    Console.WriteLine($"Data Sent: {hexData}");
                    //SendMessage(data);
                    cassiaReadWriteService.WriteBleMessage("192.168.40.1", MacAddress, 14, hexData, "");

                    status = true;
                }
                catch
                {
                }
                if (status)
                    return ERR_SUCCESS;
                else
                    return ERR_WRITE;
            }
            else
                return ERR_WRITE;
        }

        ///Actor Programming
        public int WriteActorData(IntPtr buffer, int size)
        {
            bool status = false;
            byte[] data = new byte[size];
            Marshal.Copy(buffer, data, 0, size);

            // Log the data being written
            //Console.WriteLine($"WriteData called: Buffer size={size} Data={BitConverter.ToString(data)}");

            if (GetHidDevice())
            {
                try
                {

                    // Prepare and send BLE message for actor
                    BleMessage bleMessage = new BleMessage
                    {
                        _BleMessageType = BleMessage.BleMsgId.ActorBootPacket,
                        _BleMessageDataBuffer = data
                    };

                    // Encode the message
                    if (!bleMessage.EncodeGetBleTelegram())
                        throw new Exception("Failed to encode BLE telegram.");

                    // Send the BLE message asynchronously
                    SendBleMessageAsync(bleMessage).GetAwaiter().GetResult();



                    status = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in WriteData: {ex.Message}");
                }

                return status ? ERR_SUCCESS : ERR_WRITE;
            }
            else
            {
                return ERR_WRITE;
            }
        }

        private async Task SendBleMessageAsync(BleMessage message)
        {
            //Console.WriteLine($"Sending BLE message of size {message._BleMessageBuffer.Length}");

            if (message._BleMessageBuffer.Length > 80) // Assuming 251 is the MTU size
            {
                int bytesSent = 0;
                int remainingBytes = message._BleMessageBuffer.Length;

                while (remainingBytes > 0)
                {
                    int chunkSize = Math.Min(80, remainingBytes);
                    byte[] chunk = new byte[chunkSize];
                    Array.Copy(message._BleMessageBuffer, bytesSent, chunk, 0, chunkSize);

                    await SendChunk(chunk);
                    bytesSent += chunkSize;
                    remainingBytes -= chunkSize;

                    //Console.WriteLine($"Sent chunk of size {chunkSize}. Remaining: {remainingBytes}");
                    await Task.Delay(50); // Adjust delay as needed
                }
            }
            else
            {
                await SendChunk(message._BleMessageBuffer);
            }
        }

        private async Task SendChunk(byte[] chunk)
        {
            // Actual sending logic (e.g., via BLE GATT write)
            CassiaReadWriteService cassiaReadWriteService = new CassiaReadWriteService();
            string hexData = BitConverter.ToString(chunk).Replace("-", "");
            Console.WriteLine($"Data Sent: {hexData}");

            await cassiaReadWriteService.WriteBleMessage("192.168.40.1", MacAddress, 19, hexData, "?noresponse=1");

        }


        public async Task<bool> SendJumpToBootloader(string gatewayIpAddress, string nodeMac, bool bActor)
        {
            var cassiaReadWrite = new CassiaReadWriteService();
            string value = "0101000800D9CB01";
            if (bActor)
            {
                value = "0101000800D9CB02";
            }

            var response = await cassiaReadWrite.WriteBleMessage(gatewayIpAddress, nodeMac, 19, value, "?noresponse=1");

            return response.IsSuccessStatusCode;
        }

        public bool CheckIfDeviceInBootMode(string gatewayIpAddress, string nodeMac)
        {
            string endpoint = $"http://{gatewayIpAddress}/gatt/nodes/{nodeMac}/characteristics";

            try
            {
                // Use synchronous version of HttpClient with GetAwaiter().GetResult()
                var response = _httpClient.GetAsync(endpoint).GetAwaiter().GetResult();

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var jsonResponse = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var characteristics = JsonConvert.DeserializeObject<List<CharacteristicModel>>(jsonResponse);

                    // Check if the characteristic UUID is present
                    return characteristics.Any(charac => charac.Uuid == "00060001-f8ce-11e4-abf4-0002a5d5c51b");
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking boot mode for {nodeMac}: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> ActorBootCheck(string gatewayIpAddress, string nodeMac)
        {
            try
            {
                string hexData = "0117000700D9E7"; // Command to trigger boot mode check
                CassiaReadWriteService cassiaReadWriteService = new CassiaReadWriteService();

                using (var cassiaListener = _notificationService)
                {
                    var bootCheckResultTask = new TaskCompletionSource<bool>();

                    // Subscribe to notifications
                    cassiaListener.Subscribe(nodeMac, (sender, data) =>
                    {
                        Console.WriteLine($"Notification received for {nodeMac}: {data}");

                        // Parse notification data
                        byte[] notificationData = ParseHexStringToByteArray(data);

                        // Logic to verify boot mode based on the received data
                        if (notificationData != null && notificationData.Length > 0)
                        {
                            // Convert notification data to a string for comparison
                            string receivedHex = BitConverter.ToString(notificationData).Replace("-", "");

                            if (receivedHex == "0118000800092301") // Actor is in boot mode
                            {
                                Console.WriteLine($"Actor {nodeMac} is in boot mode.");
                                bootCheckResultTask.TrySetResult(true);
                            }
                            else if (receivedHex == "0118000800092300") // Actor is not in boot mode
                            {
                                Console.WriteLine($"Actor {nodeMac} is not in boot mode.");
                                bootCheckResultTask.TrySetResult(false);
                            }
                            else
                            {
                                Console.WriteLine($"Unexpected response received: {receivedHex}");
                            }
                        }
                    });

                    // Send the write message to trigger the notification
                    await cassiaReadWriteService.WriteBleMessage(gatewayIpAddress, nodeMac, 19, hexData, "?noresponse=1");

                    // Wait for the boot check result or timeout
                    var bootCheckTask = bootCheckResultTask.Task;
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(120));
                    var completedTask = await Task.WhenAny(bootCheckTask, timeoutTask);

                    // Unsubscribe from notifications
                    cassiaListener.Unsubscribe(nodeMac);

                    // Check if the boot check task completed
                    if (completedTask == bootCheckTask)
                    {
                        return await bootCheckTask;
                    }
                    else
                    {
                        // Handle timeout
                        Console.WriteLine($"ActorBootCheck timed out for {nodeMac}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in ActorBootCheck: {ex.Message}");
                return false;
            }
        }


        /// <summary>
        /// Method that updates the progres bar
        /// </summary>
        /// <param name="arrayID"></param>
        /// <param name="rowNum"></param>
        public void ProgressUpdate(byte arrayID, ushort rowNum)
        {
            // Calculate progress percentage based on the current row and total rows
            progressBarProgress = (rowNum / totalRows) * 100.0;

            // Ensure progress does not exceed 100%
            progressBarProgress = Math.Min(progressBarProgress, 100);

            // Log the progress
            Console.WriteLine($"Progress: {progressBarProgress:F2}% - Array ID: {arrayID}, Row: {rowNum}");


        }
        public void SetTotalRows(int rows)
        {
            totalRows = rows > 0 ? rows : 1; // Avoid division by zero
        }


        public bool GetHidDevice()
        {
            return (true);
        }

        /// <summary>
        /// Checks if the USB device is connected and opens if it is present
        /// Returns a success or failure
        /// </summary>
        public int OpenConnection()
        {
            int status = 0;
            status = GetHidDevice() ? ERR_SUCCESS : ERR_OPEN;

            return status;
        }

        /// <summary>
        /// Closes the previously opened USB device and returns the status
        /// </summary>
        public int CloseConnection()
        {
            int status = 0;
            return status;

        }

        public void InitializeNotificationSubscription(string macAddress, CassiaNotificationService cassiaNotificationService)
        {
            // Unsubscribe from all previous subscriptions
            foreach (var subscribedMac in _subscribedMacAddresses)
            {
                Console.WriteLine($"Unsubscribing from notifications for {subscribedMac}");
                cassiaNotificationService.Unsubscribe(subscribedMac);
            }

            // Clear the list of subscribed MAC addresses
            _subscribedMacAddresses.Clear();

            // Add the new MAC address to the subscribed set
            _subscribedMacAddresses.Add(macAddress);

            // Subscribe to notifications for the new MAC address
            cassiaNotificationService.Subscribe(macAddress, (sender, data) =>
            {
                Console.WriteLine($"Notification received for {macAddress}: {data}");

                // Parse the notification data into a byte array
                byte[] parsedData = ParseHexStringToByteArray(data);

                // Enqueue the data into the notification queue
                _notificationQueue.Enqueue(parsedData);

                // Signal that new data is available
                _notificationEvent.Set();
            });
        }

        public void UnsubscribeNotification(string macAddress, CassiaNotificationService cassiaNotificationService)
        {
            // Check if the MAC address is subscribed
            if (_subscribedMacAddresses.Contains(macAddress))
            {
                Console.WriteLine($"Unsubscribing from notifications for {macAddress}");
                cassiaNotificationService.Unsubscribe(macAddress);
                _subscribedMacAddresses.Remove(macAddress);
            }
        }


        private byte[] ParseHexStringToByteArray(string hexString)
        {
            int numberOfBytes = hexString.Length / 2;
            byte[] bytes = new byte[numberOfBytes];
            for (int i = 0; i < numberOfBytes; i++)
            {
                bytes[i] = Convert.ToByte(hexString.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        public async Task<bool> EnableNotificationAsync(string gatewayIpAddress, string nodeMac,bool bActor)
    {
        try
        {
            string url = $"http://{gatewayIpAddress}/gatt/nodes/{nodeMac}/handle/15/value/0100";
            if (bActor)
            {
                url = $"http://{gatewayIpAddress}/gatt/nodes/{nodeMac}/handle/16/value/0100";
            }
            
            
            HttpResponseMessage response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("Notification enabled successfully.");
                return true;
            }

            Console.WriteLine($"Failed to enable notification. Status code: {response.StatusCode}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception occurred while enabling notification: {ex.Message}");
            return false;
        }
    }

        /// <summary>
        /// Method that performs Read operation from USB Device
        /// </summary>
        /// <param name="buffer"> Pointer to an array where data read from USB device is copied to </param>
        /// <param name="size"> Size of the Buffer </param>
        /// <returns></returns>

    }
    public class UpgradeProgress
    {
        public string MacAddress { get; set; }
        public string Pincode { get; set; }
        public bool BootloaderSuccess { get; set; } = false;
        public bool SensorSuccess { get; set; } = false;
        public bool ActorSuccess { get; set; } = false;
        public int RetryCount { get; set; } = 0;
        public string LastFailureReason { get; set; } = string.Empty;

        public bool IsFullyUpgraded => BootloaderSuccess && SensorSuccess && ActorSuccess;
    }
    public class UpgradeResponse
    {
        public string MacAddress { get; set; }
        public bool Success { get; set; }
        public int StatusCode { get; set; }
        public string Message { get; set; }
        public int RetryCount { get; set; } // How many retries were used (0 = first attempt)
    }

}
