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

namespace AccessAPP.Services
{
    public class CassiaFirmwareUpgradeService
    {
        private readonly HttpClient _httpClient;
        private readonly CassiaConnectService _connectService;
        private const int MaxPacketSize = 270;
        private const int InterPacketDelay = 0;
        private readonly string _firmwareFilePath = "C:\\Users\\HRS\\source\\repos\\AccessAPP\\AccessAPP\\FirmwareVersions\\353AP10227.cyacd";
        private readonly CassiaNotificationService _notificationService;
        private readonly ConcurrentQueue<byte[]> _notificationQueue = new ConcurrentQueue<byte[]>();
        private ManualResetEvent _notificationEvent = new ManualResetEvent(false);
        internal const int ERR_SUCCESS = 0;
        internal const int ERR_OPEN = 1;
        internal const int ERR_CLOSE = 2;
        internal const int ERR_READ = 3;
        internal const int ERR_WRITE = 4;
        double progressBarProgress = 0;
        double progressBarStepSize = 5;

        private readonly byte[] _securityKey = { 0x49, 0xA1, 0x34, 0xB6, 0xC7, 0x79 }; // Security ID
        private readonly byte _appID = 0x00; // AppID as shown in the screenshot
        private string _gatewayIpAddress = "";
        private string MacAddress = "";
        public CassiaFirmwareUpgradeService(HttpClient httpClient, CassiaConnectService connectService,CassiaNotificationService notificationService)
        {
            _httpClient = httpClient;
            _connectService = connectService;
            _notificationService = notificationService;
        }

        public async Task<bool> ProgramSensor(string gatewayIpAddress, string nodeMac)
        {
            
            
            InitializeNotificationSubscription(nodeMac);
            int lines;

            lines = File.ReadAllLines(_firmwareFilePath).Length - 1; //Don't count header
            var progressBarStepSize = 100.0 / lines;
            
            MacAddress = nodeMac;
            Bootloader_Utils.CyBtldr_ProgressUpdate Upd = new Bootloader_Utils.CyBtldr_ProgressUpdate(ProgressUpdate);
            Bootloader_Utils.CyBtldr_CommunicationsData m_comm_data = new Bootloader_Utils.CyBtldr_CommunicationsData();
            m_comm_data.OpenConnection = OpenConnection;
            m_comm_data.CloseConnection = CloseConnection;
            m_comm_data.ReadData = ReadData;
            m_comm_data.WriteData = WriteData;
            m_comm_data.MaxTransferSize = 216;


            var local_status = (ReturnCodes)Bootloader_Utils.CyBtldr_Program(_firmwareFilePath, _securityKey, _appID, ref m_comm_data, Upd);



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

        public void InitializeNotificationSubscription(string macAddress)
        {
            // Subscribe to notifications for the given MAC address
            _notificationService.Subscribe(macAddress, (sender, data) =>
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

            Console.WriteLine("ReadData called");

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

        /// <summary>
        /// Method that writes to the USB device
        /// </summary>
        /// <param name="buffer">Pointer to an array where data written to USB device is stored </param>
        /// <param name="size"> Size of the Buffer </param>
        /// <returns></returns>
        public int WriteData(IntPtr buffer, int size)
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
                    Console.WriteLine(hexData);
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

        /// <summary>
        /// Method that returns the maximum transfer size
        /// </summary>
        public uint MaxTransferSize
        {
            get
            {
                return (uint)(MaxPacketSize);
            }
        }

        /// <summary>
        /// Method that updates the progres bar
        /// </summary>
        /// <param name="arrayID"></param>
        /// <param name="rowNum"></param>
        public void ProgressUpdate(byte arrayID, ushort rowNum)
        {
            progressBarProgress += progressBarStepSize;

            Console.WriteLine("Progress update: " + arrayID.ToString() + " " + rowNum.ToString());

        }


        public async Task<bool> SendJumpToBootloader(string gatewayIpAddress, string nodeMac)
        {
            var cassiaReadWrite = new CassiaReadWriteService();
            string value = "0101000800D9CB01"; // Jump to bootloader command
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

        
    }
}
