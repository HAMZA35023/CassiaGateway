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
        public async Task<bool> DisconnectDeviceAsync(string macAddress)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(macAddress)) return false;
				var resp = await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress.Trim(), 0, chip: GetChipForMac(macAddress)).ConfigureAwait(false);
                // resp.Status is HttpStatusCode (non-nullable); avoid null-propagation on value type.
                return resp != null && resp.Status == HttpStatusCode.OK;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Identify a device by connecting to it, optionally checking pincode + logging in (skipped in boot mode),
        /// keeping the connection for a specified duration, then disconnecting again.
        /// This is used by the MQTT "identify" command.
        /// </summary>
    }
}
