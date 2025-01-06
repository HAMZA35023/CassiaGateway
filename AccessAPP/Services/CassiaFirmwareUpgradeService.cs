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

namespace AccessAPP.Services
{
    public class CassiaFirmwareUpgradeService
    {
        private readonly HttpClient _httpClient;
        private readonly CassiaConnectService _connectService;
        private readonly IConfiguration _configuration;
        private const int MaxPacketSize = 270;
        private const int InterPacketDelay = 0;
        private readonly string _firmwareActorFilePath = "C:\\Users\\HRS\\source\\repos\\AccessAPP\\AccessAPP\\FirmwareVersions\\353AP20227.cyacd";
        private readonly string _firmwareSensorFilePath = "C:\\Users\\HRS\\source\\repos\\AccessAPP\\AccessAPP\\FirmwareVersions\\353AP10227.cyacd";
        private readonly CassiaNotificationService _notificationService;
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
        public CassiaFirmwareUpgradeService(HttpClient httpClient, CassiaConnectService connectService,CassiaNotificationService notificationService, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _connectService = connectService;
            _notificationService = notificationService;
            _configuration = configuration;
            _gatewayIpAddress = _configuration.GetValue<string>("GatewayConfiguration:IpAddress");
            _gatewayPort = _configuration.GetValue<int>("GatewayConfiguration:Port");
        }


        public async Task<ServiceResponse> ProcessingSensorUpgrade(string nodeMac, bool bActor) // should be moved to firmware services
        {
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

            var notificationService = new CassiaNotificationService(_configuration);
            if (isAlreadyInBootMode)
            {
                //await Task.Delay(3000);

                bool notificationEnabled = await notificationService.EnableNotificationAsync(_gatewayIpAddress, nodeMac, bActor);

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
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Bootloader mode not achieved";
                return response;
            }

            //Step 3: Start Programming the Sensor
            bool programmingResult = await ProgramDevice(_gatewayIpAddress, nodeMac, notificationService, bActor);

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
            var notificationService = new CassiaNotificationService(_configuration);
            Console.WriteLine($"Bootloader mode achieved for {nodeMac}.");

            // Step 3: Start programming the actor
            var programmingResult = await ProgramDevice(_gatewayIpAddress, nodeMac, notificationService, bActor);

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
        public async Task<bool> ProgramDevice(string gatewayIpAddress, string nodeMac, CassiaNotificationService cassiaNotificationService,bool bActor)
        {
            Console.WriteLine($"Actor is going to be programmed? : {bActor}" );

            InitializeNotificationSubscription(nodeMac, cassiaNotificationService);
            int lines;
            MacAddress = nodeMac;
            Bootloader_Utils.CyBtldr_ProgressUpdate Upd = new Bootloader_Utils.CyBtldr_ProgressUpdate(ProgressUpdate);
            Bootloader_Utils.CyBtldr_CommunicationsData m_comm_data = new Bootloader_Utils.CyBtldr_CommunicationsData();
            m_comm_data.OpenConnection = OpenConnection;
            m_comm_data.CloseConnection = CloseConnection;
            
            if (bActor) 
            {
                
                lines = File.ReadAllLines(_firmwareActorFilePath).Length - 1; //Don't count header
                var progressBarStepSize = 100.0 / lines;
                m_comm_data.WriteData = WriteActorData;
                m_comm_data.ReadData = ReadActorData;
                m_comm_data.MaxTransferSize = 72;
                var local_status = (ReturnCodes)Bootloader_Utils.CyBtldr_Program(_firmwareActorFilePath, null, _appID, ref m_comm_data, Upd);
            }
            else 
            {
                
                lines = File.ReadAllLines(_firmwareSensorFilePath).Length - 1; //Don't count header
                var progressBarStepSize = 100.0 / lines;
                m_comm_data.WriteData = WriteSensorData;
                m_comm_data.ReadData = ReadData;
                m_comm_data.MaxTransferSize = 265;
                var local_status = (ReturnCodes)Bootloader_Utils.CyBtldr_Program(_firmwareSensorFilePath, _securityKey, _appID, ref m_comm_data, Upd);
            }        

            return true;
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
        /// <summary>
        /// Method that performs Read operation from USB Device
        /// </summary>
        /// <param name="buffer"> Pointer to an array where data read from USB device is copied to </param>
        /// <param name="size"> Size of the Buffer </param>
        /// <returns></returns>
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
            Console.WriteLine($"WriteData called: Buffer size={size} Data={BitConverter.ToString(data)}");

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
            Console.WriteLine($"Sending BLE message of size {message._BleMessageBuffer.Length}");

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

                    Console.WriteLine($"Sent chunk of size {chunkSize}. Remaining: {remainingBytes}");
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

        public async Task<bool> SendJumpToBootloader(string gatewayIpAddress, string nodeMac,bool bActor)
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

                using (var cassiaListener = new CassiaNotificationService(_configuration))
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


    }
}
