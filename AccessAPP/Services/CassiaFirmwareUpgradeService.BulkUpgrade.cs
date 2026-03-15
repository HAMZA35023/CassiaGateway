using AccessAPP.Models;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using AccessAPP.Logging;

namespace AccessAPP.Services
{
    public partial class CassiaFirmwareUpgradeService
    {

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
                        string logId = $"{request.MacAddress.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";
                        await semaphore.WaitAsync();

                        taskList.Add(Task.Run(async () =>
                        {
                            try
                            {
                                var result = await UpgradeActorAsync(request.MacAddress, request.Pincode, request.bActor, request.DetctorType, request.FirmwareVersion, logId);
                                upgradeResults.Add(result);
                                return result;
                            }
                            catch (Exception ex)
                            {
                                AppLog.Error($"Error upgrading actor {request.MacAddress}", ex);
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

        public async Task<List<ServiceResponse>> BulkUpgradeSensorAsync(List<BulkUpgradeRequest> requests)
                {
                    var taskList = new List<Task<ServiceResponse>>();
                    var upgradeResults = new ConcurrentBag<ServiceResponse>();
                    var semaphore = new SemaphoreSlim(3); // Fix comment to match concurrency limit

                    foreach (var request in requests)
                    {
                        string logId = $"{request.MacAddress.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";
                        await semaphore.WaitAsync();

                        taskList.Add(Task.Run(async () =>
                        {
                            try
                            {
                                var result = await UpgradeSensorAsync(request.MacAddress, request.Pincode, request.bActor, false, request.DetctorType, request.FirmwareVersion, logId);
                                result.MacAddress = request.MacAddress;
                                upgradeResults.Add(result);
                                return result;
                            }
                            catch (Exception ex)
                            {
                                var errorResult = new ServiceResponse
                                {
                                    Success = false,
                                    StatusCode = 500,
                                    Message = $"Error upgrading sensor {request.MacAddress}: {ex.Message}",
                                    MacAddress = request.MacAddress
                                };
                                upgradeResults.Add(errorResult);
                                return errorResult;
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }));
                    }

                    await Task.WhenAll(taskList);

                    // Now just return the full list
                    return upgradeResults.ToList();
                }

        public async Task<List<UpgradeResponse>> UpgradeBLSensorsAsync(List<BulkUpgradeRequest> devices)
                {
                    var responses = new Dictionary<string, UpgradeResponse>(); // Stores latest response for each device
                    var failedDevices = new Queue<(BulkUpgradeRequest, int)>(); // (Device, Retry Count)

                    foreach (var device in devices)
                    {
                        string logId = $"{device.MacAddress.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";
                        sensorType = device.DetctorType;
                        var response = await UpgradeBLSensorWithRetryAsync(device, 0, logId);
                        responses[device.MacAddress] = response; // Always store latest response

                        if (!response.Success)
                        {
                            failedDevices.Enqueue((device, 1)); // Initial retry count is 1
                        }

                        AppLog.Info("Next Device will be upgraded after 10 seconds");
        }

                    AppLog.Warn($"Initial upgrade completed. Retrying failed devices: {failedDevices.Count} devices.");
        while (failedDevices.Count > 0)
                    {
                        var (device, retryCount) = failedDevices.Dequeue();
                        string logId = $"{device.MacAddress.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";
                        var response = await UpgradeBLSensorWithRetryAsync(device, retryCount, logId);
                        responses[device.MacAddress] = response; // Overwrite previous responses

                        if (!response.Success && retryCount < 2) // Retry up to 2 times
                        {
                            failedDevices.Enqueue((device, retryCount + 1));
                        }
                    }

                    return responses.Values.ToList(); // Return only the latest responses
                }

        private async Task<UpgradeResponse> UpgradeBLSensorWithRetryAsync(BulkUpgradeRequest device, int retryCount, string logId)
                {
                    var response = new UpgradeResponse
                    {
                        MacAddress = device.MacAddress,
                        RetryCount = retryCount
                    };

                    try
                    {
                        Stopwatch stopwatch = new Stopwatch();
                        AppLog.Warn($"Starting bootloader upgrade for {device.MacAddress}, Attempt {retryCount + 1}");
        stopwatch.Restart();

                        // Step 1: Bootloader Upgrade
                        var bootloaderUpgradeResult = await UpgradeSensorAsync(device.MacAddress, device.Pincode, false, true, device.DetctorType, device.FirmwareVersion, logId);
                        stopwatch.Stop();
                        AppLog.Info($"Bootloader upgrade completed for {device.MacAddress}. Time taken: {stopwatch.Elapsed.TotalSeconds} seconds");
        if (!bootloaderUpgradeResult.Success)
                        {
                            AppLog.Warn($"Bootloader upgrade failed for {device.MacAddress}. Skipping sensor upgrade.");
        response.Success = false;
                            response.StatusCode = bootloaderUpgradeResult.StatusCode;
                            response.Message = $"Bootloader upgrade failed: {bootloaderUpgradeResult.Message}";
                            return response;
                        }

                        // Allow bootloader transition delay
                        await Task.Delay(10000);

                        AppLog.Warn($"Starting sensor upgrade for {device.MacAddress}, Attempt {retryCount + 1}");
        // Step 2: Sensor Upgrade (Only if Bootloader upgrade succeeded)
                        stopwatch.Restart();
                        var sensorUpgradeResult = await UpgradeSensorAsync(device.MacAddress, device.Pincode, false, false, device.DetctorType, device.FirmwareVersion, logId);
                        stopwatch.Stop();
                        AppLog.Info($"Sensor upgrade completed for {device.MacAddress}. Time taken: {stopwatch.Elapsed.TotalSeconds} seconds");
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
                        AppLog.Error($"Error during sensor and bootloader upgrade for {device.MacAddress}", ex);
        response.Success = false;
                        response.StatusCode = 500;
                        response.Message = "An unexpected error occurred during the upgrade process.";
                        return response;
                    }
                }

        public async Task<ServiceResponse> BulkUpgradeDevicesAsync(List<BulkUpgradeRequest> requests, int numberOfParallelThreads = -1)
                {
                    if (numberOfParallelThreads == -1)
                        numberOfParallelThreads = GlobalnumberOfParallelThreads;

                    var progressList = requests.Select(req => new UpgradeProgress { MacAddress = req.MacAddress, Pincode = req.Pincode, DetectotType = req.DetctorType, FirmwareVersion = req.FirmwareVersion, CurrentFirmwareVersion = req.CurrentFirmwareVersion, ForceUpdate = req.ForceUpdate }).ToList();

                    // Mark all devices as Queued immediately so the UI can show status
                    foreach (var p in progressList)
                        if (!string.IsNullOrWhiteSpace(p.MacAddress))
                            _deviceStorageService.UpdateFirmwareProgress(p.MacAddress, 0, "Queued", null, p.FirmwareVersion, p.DetectotType);

                    // Phase 1: Initial Upgrades
                    await UpgradeDevicesInParallel(progressList, numberOfParallelThreads);

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

    }
}
