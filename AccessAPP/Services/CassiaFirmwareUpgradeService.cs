using AccessAPP.Models;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AccessAPP.Services
{
    public class CassiaFirmwareUpgradeService
    {
        private readonly HttpClient _httpClient;
        private readonly CassiaConnectService _connectService;
        private const string FirmwareFilePath = "C:\\Users\\HRS\\source\\repos\\AccessAPP\\AccessAPP\\FirmwareVersions\\353AP1M222.cyacd"; // Path to firmware file

        public CassiaFirmwareUpgradeService(HttpClient httpClient, CassiaConnectService connectService)
        {
            _httpClient = httpClient;
            _connectService = connectService;
        }


        public async Task<ResponseModel> SendJumpToBootloader(string gatewayIpAddress, string nodeMac, string application = "02")
        {
            string value = $"0101000800D9CB{application}";
            int handle = 14; // assuming handle 19 as in your Node.js example

            try
            {
                CassiaReadWriteService cassiaReadWrite = new CassiaReadWriteService();
                var response = await cassiaReadWrite.WriteBleMessage(gatewayIpAddress, nodeMac, handle, value, "?noresponse=1");

                return response.IsSuccessStatusCode
                    ? new ResponseModel { Status = HttpStatusCode.OK, Data = "Jump to bootloader successful." }
                    : new ResponseModel { Status = response.StatusCode, Data = $"Failed to jump to bootloader: {response.ReasonPhrase}" };
            }
            catch (Exception ex)
            {
                return new ResponseModel { Status = HttpStatusCode.InternalServerError, Data = $"Exception: {ex.Message}" };
            }
        }

        public async Task<bool> CheckIfDeviceInBootMode(string gatewayIpAddress, string nodeMac)
        {
            string endpoint = $"http://{gatewayIpAddress}/gatt/nodes/{nodeMac}/characteristics";

            try
            {
                var response = await _httpClient.GetAsync(endpoint);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var jsonResponse = await response.Content.ReadAsStringAsync();
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
