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
        internal async Task<string> GetFwVersionOnConnectedSessionAsync(
            string macAddress,
            string pincode,
            string? logId = null,
            string? firmwareVersion = null)
        {
            try
            {
                var loginResult = await _connectService.AttemptLogin(_gatewayIpAddress, macAddress).ConfigureAwait(false);

                bool pinReq = loginResult.ResponseBody.PincodeRequired;
                if (pinReq && !string.IsNullOrEmpty(pincode))
                {
                    var check = await _cassiaPinCodeService.CheckPincode(_gatewayIpAddress, macAddress, pincode).ConfigureAwait(false);
                    loginResult.ResponseBody = check.ResponseBody;
                    loginResult.ResponseBody.PincodeRequired = pinReq;
                }

                if (pinReq && !loginResult.ResponseBody.PinCodeAccepted)
                {
                    AppLog.Warn($" Login failed on connected session for {macAddress}: pincode required/invalid");
                    UpgradeLogger.Log(logId ?? "", macAddress, "LoggedIn (precheck FW read)", "Failed (pincode required/invalid)", firmwareVersion ?? "");
                    return "";
                }

                UpgradeLogger.Log(logId ?? "", macAddress, "LoggedIn (precheck FW read)", "Success", firmwareVersion ?? "");
                var fw = await ReadFirmwareVersionAsync(macAddress).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(fw))
                    UpgradeLogger.Log(logId ?? "", macAddress, "FW Read (precheck)", "Failed (empty response)", firmwareVersion ?? "");
                else
                    UpgradeLogger.Log(logId ?? "", macAddress, "FW Read (precheck)", "Success", firmwareVersion ?? "");

                return fw;
            }
            catch (Exception ex)
            {
                AppLog.Error($" GetFwVersionOnConnectedSession exception for {macAddress}: {ex}");
                UpgradeLogger.Log(logId ?? "", macAddress, "LoggedIn/FW Read (precheck)", $"Exception: {ex.Message}", firmwareVersion ?? "");
                return "";
            }
        }

        private async Task<string> ReadFirmwareVersionAsync(string macAddress)
        {
            string sensorInfo = "";
            string actorInfo = "";

            // Sensor
            string sensorCommand = "01290107005A5E";
            var sensorResponse = await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, macAddress, sensorCommand).ConfigureAwait(false);
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                sensorInfo = ScanDataParser.ParseSoftwareVersionFromResponse(sensorResponse.Data);
            }

            // Actor
            string actorCommand = "012B01070032B3";
            var actorResponse = await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, macAddress, actorCommand).ConfigureAwait(false);
            if (actorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(actorResponse.Data))
            {
                actorInfo = ScanDataParser.ParseSoftwareVersionFromResponse(actorResponse.Data);
            }

            AppLog.Info($"{macAddress} - Get this Version: Sensor: {sensorInfo} | Actor: {actorInfo}");
            if (string.IsNullOrWhiteSpace(sensorInfo) && string.IsNullOrWhiteSpace(actorInfo))
                return "";
            return $"Sensor: {sensorInfo} | Actor: {actorInfo}";
        }

        public async Task<string> GetFwVersion(string macAddress, string pincode, bool disconnect_on_finish = false)
        {
            try
            {
                var cl = await ConnectAndLoginWithRetryAsync(
                    _gatewayIpAddress, 80, macAddress, pincode, null, null,
                    maxAttempts: Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS),
                    delayBetweenAttemptsMs: 2000).ConfigureAwait(false);
                if (!cl.Success)
                {
                    AppLog.Warn($" Connect+login failed for {macAddress}: {cl.Message}");
return "";
                }
                else
                {
                    return await ReadFirmwareVersionAsync(macAddress).ConfigureAwait(false);
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
