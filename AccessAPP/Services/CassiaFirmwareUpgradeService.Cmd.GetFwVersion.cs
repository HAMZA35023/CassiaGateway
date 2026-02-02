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
        public async Task<string> GetFwVersion(string macAddress, string pincode, bool disconnect_on_finish = false)
        {
            try
            {
                var cl = await ConnectAndLoginWithRetryAsync(
                    _gatewayIpAddress, 80, macAddress, pincode, null, null,
                    maxAttempts: 3,
                    delayBetweenAttemptsMs: 2000).ConfigureAwait(false);
                if (!cl.Success)
                {
                    AppLog.Warn($" Connect+login failed for {macAddress}: {cl.Message}");
return "";
                }
                else
                {

                    //Get the FW Version

                    string sensorInfo = "";
                    string actorInfo = "";

                    // Sensor
                    string sensorCommand = "01290107005A5E";
                    var sensorResponse = await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, macAddress, sensorCommand);
                    if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
                    {
                        sensorInfo = ScanDataParser.ParseSoftwareVersionFromResponse(sensorResponse.Data);
                    }

                    // Actor
                    string actorCommand = "012B01070032B3";
                    var actorResponse = await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, macAddress, actorCommand);
                    if (actorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(actorResponse.Data))
                    {
                        actorInfo = ScanDataParser.ParseSoftwareVersionFromResponse(actorResponse.Data);
                    }

                    AppLog.Info($"{macAddress} - Get this Version: Sensor: {sensorInfo} | Actor: {actorInfo}");
return ($"Sensor: {sensorInfo} | Actor: {actorInfo}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($" GetFwVersion exception for {macAddress}: {ex}");
}
            finally
            {
                if (disconnect_on_finish)
                {
					await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, chip: GetChipForMac(macAddress));
                }
            }
            return "";
        }

        /// <summary>
        /// Disconnect a device from the Cassia gateway (best-effort).
        /// Intended for MQTT command "disconnect-devices".
        /// </summary>
    }
}
