using AccessAPP.Logging;
using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AccessAPP.Services
{
    public partial class CassiaFirmwareUpgradeService
    {
		public async Task<bool> EnableNotificationAsync(string gatewayIpAddress, string nodeMac, bool bActor, int chip = -1)
        {
            HttpClient _httpClientTmp = new HttpClient();
            try
            {
                string url = $"http://{gatewayIpAddress}/gatt/nodes/{nodeMac}/handle/15/value/0100";
				if (bActor)
				{
					url = $"http://{gatewayIpAddress}/gatt/nodes/{nodeMac}/handle/16/value/0100";
				}

				if (chip >= 0)
				{
					url += url.Contains('?') ? $"&chip={chip}" : $"?chip={chip}";
				}


                HttpResponseMessage response = await _httpClientTmp.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    AppLog.Info($"Notification enabled successfully. {nodeMac}");
return true;
                }

                AppLog.Warn($"Failed to enable notification. Status code: {response.StatusCode}");
return false;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Exception occurred while enabling notification: {ex.Message}");
return false;
            }
        }
    }
}
