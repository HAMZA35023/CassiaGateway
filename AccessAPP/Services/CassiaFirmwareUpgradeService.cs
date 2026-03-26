using AccessAPP.Models;
using AccessAPP.Services.HelperClasses;
using AccessAPP.Services.UpgradeCore;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AccessAPP.Logging;


namespace AccessAPP.Services
{
    public partial class CassiaFirmwareUpgradeService : IDeviceSettingsBleApi
    {

        private static int _inQueue;
        public static int inQueue
        {
            get => _inQueue;
            set
            {
                if (_inQueue == value)
                    return;

                var old = _inQueue;
                _inQueue = value;

                AppLog.Debug($"[QUEUE] inQueue {old} → {_inQueue} @ {DateTime.Now:HH:mm:ss.fff}");
}
        }

        public static double totalSpeed { get; set; } = 0;
        public static double totalSpeedAvg10s { get; set; } = 0;


        public static int GlobalnumberOfParallelThreads = 2; // runtime adjustable via MQTT (resets on restart) // Optimal setting with current Cassia Gateway HW (21:43 Min for 3 P48 with actor and sensor firmware update)
        
        private readonly HttpClient _httpClient;
        private readonly Services.BleAbstractions.IBleConnectionService _connectService;
        private readonly CassiaPinCodeService _cassiaPinCodeService;
        private static DeviceStorageService _deviceStorageService;
        private readonly IMqttService _mqttService;
        private readonly IConfiguration _configuration;

        private readonly IDeviceSettingsBackupService _settingsBackup;

        //private ConcurrentDictionary<string, byte[]> _lastNotificationDataRead = new ConcurrentDictionary<string, byte[]>();
        //private ManualResetEvent _notificationEvent = new ManualResetEvent(false);
        //private readonly HashSet<string> _subscribedMacAddresses = new HashSet<string>();
        internal const int ERR_SUCCESS = 0;
        internal const int ERR_OPEN = 1;
        internal const int ERR_CLOSE = 2;
        internal const int ERR_READ = 3;
        internal const int ERR_WRITE = 4;

        private readonly byte[] _securityKey = { 0x49, 0xA1, 0x34, 0xB6, 0xC7, 0x79 }; // Security ID
        private readonly byte _appID = 0x00; // AppID as shown in the screenshot
        private readonly string _gatewayIpAddress;
        private readonly int _gatewayPort;

	    // Internal accessors for extracted pipeline/worker helpers (no behavioral impact).
	    internal string GatewayIpAddress => _gatewayIpAddress;
	    internal int GatewayPort => _gatewayPort;
	    internal IDeviceSettingsBackupService SettingsBackupService => _settingsBackup;
	    internal Services.BleAbstractions.IBleConnectionService ConnectService => _connectService;

	    // Wrapper with the SAME parameter order as the original private method.
        internal Task<(bool ok, HttpStatusCode code, string msg)> ConnectOnlyWithRetryAsync_Internal(
	        int maxAttempts,
	        int delayMs,
	        string stageName,
	        string macAddress,
	        string firmwareVersion,
	        string? logId,
	        bool logSuccess = true,
	        int? discoverGattOverride = null,
            int? connectAttemptTimeoutMsOverride = null)
	        => ConnectOnlyWithRetryAsync(
                maxAttempts,
                delayMs,
                stageName,
                macAddress,
                firmwareVersion,
                logId ?? "",
                logSuccess,
                discoverGattOverride,
                connectAttemptTimeoutMsOverride);

	    internal async Task<(bool Success, int StatusCode, string Message)> ConnectAndLoginWithRetryForPipelineAsync(
	        string gatewayIpAddress,
	        int gatewayPort,
	        string macAddress,
	        string pincode,
	        string? logId,
	        string? firmwareVersion,
	        int maxAttempts,
	        int delayBetweenAttemptsMs,
	        bool bootModeIsRetryable = false)
	    {
	        var r = await ConnectAndLoginWithRetryAsync(gatewayIpAddress, gatewayPort, macAddress, pincode, logId, firmwareVersion, maxAttempts, delayBetweenAttemptsMs, bootModeIsRetryable)
	            .ConfigureAwait(false);
	        return (r.Success, r.StatusCode, r.Message ?? "");
	    }

        internal async Task<bool> EnsureLoginOnConnectedSessionUnlessBootModeAsync(
            string macAddress,
            string? pincode,
            string? logId,
            string? firmwareVersion,
            string stageName = "LoggedIn",
            int maxAttempts = 3,
            bool preferBootOnAmbiguous = false)
        {
            if (CheckIfDeviceInBootMode(_gatewayIpAddress, macAddress, preferBootOnAmbiguous))
            {
                UpgradeLogger.Log(logId ?? "", macAddress, stageName, "Skipped (bootloader mode)", firmwareVersion ?? "");
                return true;
            }

            int attempts = Math.Max(1, maxAttempts);
            int loginTimeoutMs = Math.Max(2000, RuntimeVariables.UPGRADE_LOGIN_ATTEMPT_TIMEOUT_MS);
            int retryDelayMs = Math.Max(100, RuntimeVariables.UPGRADE_LOGIN_RETRY_DELAY_MS);
            int settleBeforeLoginMs = GetConnectStabilizationDelayMs();
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    // Mode can flip between attempts (especially right after jump/reconnect).
                    // Re-check every round so we stop trying login as soon as boot mode is active.
                    if (CheckIfDeviceInBootMode(_gatewayIpAddress, macAddress, preferBootOnAmbiguous))
                    {
                        UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Skipped (bootloader mode, attempt {attempt}/{attempts})", firmwareVersion ?? "");
                        return true;
                    }

                    if (attempt == 1 && settleBeforeLoginMs > 0)
                    {
                        AppLog.Debug($"{stageName}: waiting {settleBeforeLoginMs}ms before login on connected session for {macAddress}.");
                        await Task.Delay(settleBeforeLoginMs).ConfigureAwait(false);
                    }

                    AppLog.Debug($"{stageName}: login attempt {attempt}/{attempts} for {macAddress}.");
                    using var cts = new CancellationTokenSource(loginTimeoutMs);
                    var loginResult = await _connectService
                        .AttemptLogin(_gatewayIpAddress, macAddress, cts.Token)
                        .ConfigureAwait(false);

                    bool pinReq = loginResult.ResponseBody.PincodeRequired;

                    if (pinReq && !string.IsNullOrEmpty(pincode))
                    {
                        var check = await _cassiaPinCodeService.CheckPincode(_gatewayIpAddress, macAddress, pincode).ConfigureAwait(false);
                        loginResult.ResponseBody = check.ResponseBody;
                        loginResult.ResponseBody.PincodeRequired = pinReq;
                    }

                    var statusText = loginResult.Status?.ToString() ?? "";
                    bool statusOk = string.Equals(statusText, "OK", StringComparison.OrdinalIgnoreCase);
                    bool pinOk = !pinReq || loginResult.ResponseBody.PinCodeAccepted;
                    string responseData = "";
                    try
                    {
                        responseData = loginResult.ResponseBody?.Data?.ToString() ?? "";
                    }
                    catch { /* ignore dynamic binding issues */ }

                    if (statusOk && pinOk)
                    {
                        AppLog.Debug($"{stageName}: login success for {macAddress} on attempt {attempt}/{attempts}.");
                        UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Success (attempt {attempt}/{attempts})", firmwareVersion ?? "");
                        return true;
                    }

                    AppLog.Debug($"{stageName}: login failed for {macAddress} on attempt {attempt}/{attempts}. Status={statusText}, pinRequired={pinReq}, pinAccepted={loginResult.ResponseBody.PinCodeAccepted}, data='{responseData}'.");
                    if (!string.IsNullOrWhiteSpace(responseData))
                        UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Failed (attempt {attempt}/{attempts}) - Status={statusText} - {responseData}", firmwareVersion ?? "");
                    else
                        UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Failed (attempt {attempt}/{attempts}) - Status={statusText}", firmwareVersion ?? "");

                    bool stopSameSessionRetries =
                        _connectService is LinuxBle.LinuxBleConnectionService &&
                        string.Equals(statusText, "Canceled", StringComparison.OrdinalIgnoreCase);
                    if (stopSameSessionRetries)
                    {
                        AppLog.Debug($"{stageName}: status=Canceled for {macAddress} on attempt {attempt}/{attempts}; stopping same-session retries and forcing reconnect path.");
                        break;
                    }

                    // If login failed because device transitioned to boot mode, do not keep retrying.
                    if (CheckIfDeviceInBootMode(_gatewayIpAddress, macAddress, preferBootOnAmbiguous))
                    {
                        UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Skipped (bootloader mode detected after failed login attempt {attempt}/{attempts})", firmwareVersion ?? "");
                        return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    AppLog.Debug($"{stageName}: login timeout for {macAddress} on attempt {attempt}/{attempts}.");
                    UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Timeout (attempt {attempt}/{attempts}, {loginTimeoutMs / 1000}s)", firmwareVersion ?? "");

                    if (_connectService is LinuxBle.LinuxBleConnectionService)
                    {
                        AppLog.Debug($"{stageName}: timeout for {macAddress} on attempt {attempt}/{attempts}; stopping same-session retries and forcing reconnect path.");
                        break;
                    }

                    if (CheckIfDeviceInBootMode(_gatewayIpAddress, macAddress, preferBootOnAmbiguous))
                    {
                        UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Skipped (bootloader mode detected after timeout attempt {attempt}/{attempts})", firmwareVersion ?? "");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Debug($"{stageName}: login exception for {macAddress} on attempt {attempt}/{attempts}: {ex.Message}");
                    UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Exception (attempt {attempt}/{attempts}): {ex.Message}", firmwareVersion ?? "");

                    if (CheckIfDeviceInBootMode(_gatewayIpAddress, macAddress, preferBootOnAmbiguous))
                    {
                        UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Skipped (bootloader mode detected after exception attempt {attempt}/{attempts})", firmwareVersion ?? "");
                        return true;
                    }
                }

                if (attempt < attempts)
                    await Task.Delay(retryDelayMs).ConfigureAwait(false);
            }

            // Final safety: boot mode can flip after a jump and the first check may be stale.
            // If we are actually in boot mode, treat login as "not required".
            try
            {
                if (CheckIfDeviceInBootMode(_gatewayIpAddress, macAddress, preferBootOnAmbiguous))
                {
                    UpgradeLogger.Log(logId ?? "", macAddress, stageName, "Skipped (bootloader mode detected after login failure)", firmwareVersion ?? "");
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug($"{stageName}: boot-mode recheck failed for {macAddress}: {ex.Message}");
            }

            return false;
        }
        private string MacAddress = "";
        private double totalRows = 0;
        private string sensorType = "";
        private static ConcurrentDictionary<string, HashSet<string>> allRows = new();
        private static ConcurrentDictionary<string, HashSet<string>> completedRows = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _macsInProgress = new(StringComparer.OrdinalIgnoreCase);

        // Split responsibilities:
        // - ChipAllocationManager: Cassia X2000 dual-chip pinning + fair scheduling
        // - UpgradeQueueProcessor: FIFO queue worker that starts upgrades as capacity allows
        private readonly ChipAllocationManager _chipManager;
        private readonly UpgradeQueueProcessor _queue;

        internal int GetChipForMac(string mac)
            => _chipManager.GetChipForMac(mac, RuntimeVariables.DEFAULT_CASSIA_CHIP);

        private static string NormalizeMac(string? mac)
            => MacUtils.NormalizeMac(mac);

        Services.BleAbstractions.IBleReadWriteService cassiaReadWriteService;

        private readonly Services.BleAbstractions.IBleNotificationService _notificationService; // Injected singleton

        private static CassiaFirmwareUpgradeService _ownInstance = null;

// Tracks currently programming devices and their target firmware (for MQTT programming list)
private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string DetectorType, string FirmwareVersion)> _programmingTargets
    = new(StringComparer.OrdinalIgnoreCase);

// Tracks which firmware "type" is currently being programmed per MAC so that
// progress notifications can be published with the right stage.
// Values: "actor" | "sensor" | "bootloader".
private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _programmingStageByMac
    = new(StringComparer.OrdinalIgnoreCase);

public static int GetParallelProgrammers()
    => Volatile.Read(ref GlobalnumberOfParallelThreads);

public static int SetParallelProgrammers(int value)
{
    // Only lives until restart (not persisted). Guard against silly values.
    var v = Math.Clamp(value, 1, 32);
    Volatile.Write(ref GlobalnumberOfParallelThreads, v);

    // Wake the live queue worker so an increase in parallelism takes effect
    // immediately (otherwise it may wait for an in-flight device to complete).
    _ownInstance?.OnParallelismChanged(v);
    return v;
}

private void OnParallelismChanged(int newValue)
{
    _queue?.OnParallelismChanged(newValue);
}

public static IReadOnlyList<(string Mac, string DetectorType, string FirmwareVersion)> GetQueueListSnapshot()
{
    var inst = _ownInstance;
    if (inst is null) return Array.Empty<(string, string, string)>();

    return inst._queue
        .GetUpgradeQueueSnapshot()
        .Select(x => (x.Mac, x.DetectorType, x.TargetFw))
        .ToList();
}

public static IReadOnlyList<(string Mac, string DetectorType, string FirmwareVersion)> GetProgrammingListSnapshot()
{
    var inst = _ownInstance;
    if (inst is null) return Array.Empty<(string, string, string)>();

    return inst._programmingTargets
        .Select(kvp => (NormalizeMac(kvp.Key), kvp.Value.DetectorType ?? "", kvp.Value.FirmwareVersion ?? ""))
        .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

public static int GetProgrammingCount()
{
    var inst = _ownInstance;
    return inst is null ? 0 : inst._programmingTargets.Count;
}

        // ------------------------------------------------------------
        // Queue facade (delegates to UpgradeQueueProcessor)
        // ------------------------------------------------------------

        internal bool IsMacInProgress(string mac)
            => _macsInProgress.ContainsKey(mac);

        /// <summary>
        /// Queue-enabled FIFO entrypoint. Behavior is unchanged; implementation moved into UpgradeQueueProcessor.
        /// </summary>
        private Task UpgradeDevicesInParallel(List<UpgradeProgress> devices, int numbersOfThreadsInParallel = -1)
            => _queue.EnqueueAsync(devices, numbersOfThreadsInParallel);

        public static int RemoveFromUpgradeQueuePending(string mac)
        {
            var inst = _ownInstance;
            return inst is null ? 0 : inst._queue.RemoveFromUpgradeQueue(mac);
        }

        /// <summary>
        /// Force-remove a MAC from the active in-progress set.
        /// Use when a previous upgrade task became stuck (e.g. due to a hung D-Bus call before
        /// the per-call timeout fixes) and the process was not restarted.  Removing the MAC
        /// here allows a fresh start-update request to proceed.  If the old task eventually
        /// wakes up it will call TryRemove on a key that no longer exists — which is a safe no-op.
        /// </summary>
        public static bool ForceRemoveFromInProgress(string mac)
        {
            mac = MacUtils.NormalizeMac(mac);
            if (string.IsNullOrWhiteSpace(mac)) return false;
            var inst = _ownInstance;
            if (inst is null) return false;
            bool removed = inst._macsInProgress.TryRemove(mac, out _);
            if (removed)
                AppLog.Info($"[UPGRADE QUEUE] Force-removed '{mac}' from in-progress set (was stuck).");
            return removed;
        }



        private static readonly ConcurrentDictionary<string, object> _macLocks = new();
        private static readonly ConcurrentDictionary<string, SlidingRate10s> _macRate10s = new();
        private static readonly ConcurrentDictionary<string, double> _lastInstanceRate = new();

        public static (double TotalPctPerMin, double TotalAvg10sPctPerMin, int WorkersWithSpeed) GetSpeedSnapshot()
        {
            double total = 0.0;
            foreach (var rate in _lastInstanceRate.Values)
                total += rate;

            var rounded = Math.Round(total, 2);
            return (rounded, rounded, _lastInstanceRate.Count);
        }

        // Overall / all instances

        public CassiaFirmwareUpgradeService(HttpClient httpClient, Services.BleAbstractions.IBleConnectionService connectService, CassiaPinCodeService cassiaPinCodeService, Services.BleAbstractions.IBleNotificationService notificationService, DeviceStorageService deviceStorageService, IConfiguration configuration, IMqttService mqttService, Services.BleAbstractions.IBleReadWriteService readWriteService)
        {
            _ownInstance = this;
            _httpClient = httpClient;
            _connectService = connectService;
            _deviceStorageService = deviceStorageService;
            _cassiaPinCodeService = cassiaPinCodeService;
            _mqttService = mqttService;
            _configuration = configuration;
            _gatewayIpAddress = _configuration.GetValue<string>("GatewayConfiguration:IpAddress");
            _gatewayPort = _configuration.GetValue<int>("GatewayConfiguration:Port");
            _notificationService = notificationService;
            cassiaReadWriteService = readWriteService;
            _settingsBackup = new DeviceSettingsBackupService(this);

            // Dedicated components (structural refactor; no behavior changes intended)
            _chipManager = new ChipAllocationManager(GetParallelProgrammers);
            _queue = new UpgradeQueueProcessor(this, _chipManager);
        }

        // ------------------------------------------------------------
        // Settings helpers (BLE command wrappers)
        // ------------------------------------------------------------

        public async Task<bool> RebootDeviceAsync(string nodeMac)
        {
            const string rebootCommand = "0113060800B68F00"; // Reboot

            try
            {
                var sensorResponse = await _connectService.GetDataFromBleDevice(
                    _gatewayIpAddress,
                    _gatewayPort,
                    nodeMac,
                    rebootCommand);

                // Device reboots immediately → no response expected
                // Treat "sent successfully" as OK
                if (sensorResponse.Status.ToString() == "OK")
                {
                    AppLog.Info($"[REBOOT] Command sent, device rebooting: MAC={nodeMac}");
return true;
                }

                AppLog.Warn($"[REBOOT] Failed to send command: MAC={nodeMac}, Status={sensorResponse.Status}");
return false;
            }
            catch (Exception ex)
            {
                // BLE stack may throw because device disappears instantly
                AppLog.Debug($"[REBOOT] Exception (expected in some cases): MAC={nodeMac}, {ex.Message}");
return true; // command was likely accepted before disconnect
            }
        }

        private const string DaliGetDeviceCommonParamCmd = "0110040700346A";
        private const string DaliSetDeviceMaxLevelPrefix = "0111040800BE0C";
        private const string DaliSetDeviceMinLevelPrefix = "01120408006297";
        private const string DaliSetDevicePowerOnLevelPrefix = "0113040800D6E1";
        private const string DaliSetDeviceSysFailLevelPrefix = "0114040800FBB0";
        private const string DaliSetDeviceFadeTimePrefix = "01150409007EF5"; // CRC over header bytes 01 15 04 09 00
        private const string DaliSetDeviceFadeRatePrefix = "0116040800935D";

        public async Task<bool> DaliSetDeviceSysFailLevelAsync(
            string nodeMac,
            byte sysFailLevel)
        {
            return await SendDaliCommonParamSetAsync(
                    nodeMac,
                    DaliSetDeviceSysFailLevelPrefix + sysFailLevel.ToString("X2", CultureInfo.InvariantCulture),
                    $"SysFailLevel=0x{sysFailLevel:X2}")
                .ConfigureAwait(false);
        }

        public async Task<byte?> DaliGetDeviceSysFailLevelAsync(string nodeMac)
        {
            var current = await ReadDaliDeviceCommonParamAsync(nodeMac).ConfigureAwait(false);
            if (current == null || current.Length < 4)
                return null;

            return current[3];
        }

        public async Task<string> GetDaliDeviceCommonParam(string nodeMac)
        {
            var values = await ReadDaliDeviceCommonParamAsync(nodeMac).ConfigureAwait(false);
            if (values == null || values.Length != 7)
                return string.Empty;

            return Convert.ToHexString(values);
        }

        public async Task<bool> SetDaliDeviceCommonParam(
            string nodeMac,
            string newDaliDeviceCommonParamHex,
            string? currentDaliDeviceCommonParamHex = null)
        {
            if (!TryParseFixedHexSection(newDaliDeviceCommonParamHex, expectedBytes: 7, out var target))
            {
                AppLog.Warn($"[DALI] Set common param rejected: invalid target hex '{newDaliDeviceCommonParamHex}'. Expected exactly 7 bytes.");
                return false;
            }

            byte[]? baseline = null;
            if (TryParseFixedHexSection(currentDaliDeviceCommonParamHex, expectedBytes: 7, out var parsedBaseline))
                baseline = parsedBaseline;

            if (baseline == null)
                baseline = await ReadDaliDeviceCommonParamAsync(nodeMac).ConfigureAwait(false);

            if (baseline == null || baseline.Length != 7)
            {
                AppLog.Warn($"[DALI] Set common param failed: unable to resolve current baseline for MAC={nodeMac}.");
                return false;
            }

            var ok = true;

            if (target[0] != baseline[0])
                ok &= await SendDaliCommonParamSetAsync(nodeMac, DaliSetDeviceMaxLevelPrefix + target[0].ToString("X2", CultureInfo.InvariantCulture), $"MaxLevel=0x{target[0]:X2}").ConfigureAwait(false);

            if (target[1] != baseline[1])
                ok &= await SendDaliCommonParamSetAsync(nodeMac, DaliSetDeviceMinLevelPrefix + target[1].ToString("X2", CultureInfo.InvariantCulture), $"MinLevel=0x{target[1]:X2}").ConfigureAwait(false);

            if (target[2] != baseline[2])
                ok &= await SendDaliCommonParamSetAsync(nodeMac, DaliSetDevicePowerOnLevelPrefix + target[2].ToString("X2", CultureInfo.InvariantCulture), $"PowerOnLevel=0x{target[2]:X2}").ConfigureAwait(false);

            if (target[3] != baseline[3])
                ok &= await SendDaliCommonParamSetAsync(nodeMac, DaliSetDeviceSysFailLevelPrefix + target[3].ToString("X2", CultureInfo.InvariantCulture), $"SysFailLevel=0x{target[3]:X2}").ConfigureAwait(false);

            if (target[4] != baseline[4] || target[6] != baseline[6])
            {
                var fadePayload = target[4].ToString("X2", CultureInfo.InvariantCulture) + target[6].ToString("X2", CultureInfo.InvariantCulture);
                ok &= await SendDaliCommonParamSetAsync(nodeMac, DaliSetDeviceFadeTimePrefix + fadePayload, $"FadeTime=0x{target[4]:X2},ExtendedFade=0x{target[6]:X2}").ConfigureAwait(false);
            }

            if (target[5] != baseline[5])
                ok &= await SendDaliCommonParamSetAsync(nodeMac, DaliSetDeviceFadeRatePrefix + target[5].ToString("X2", CultureInfo.InvariantCulture), $"FadeRate=0x{target[5]:X2}").ConfigureAwait(false);

            AppLog.Info($"[DALI] CommonParam set completed: MAC={nodeMac}, AnyChanged={target.Where((v, i) => v != baseline[i]).Any()}, OK={ok}");
            return ok;
        }

        private async Task<byte[]?> ReadDaliDeviceCommonParamAsync(string nodeMac)
        {
            try
            {
                var sensorResponse = await GetDataWithSysFailTimeoutAsync(nodeMac, DaliGetDeviceCommonParamCmd, "DaliCommonParam read").ConfigureAwait(false);
                if (sensorResponse == null)
                    return null;

                if (sensorResponse.Status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(sensorResponse.Data))
                {
                    AppLog.Warn($"[DALI] CommonParam read failed: MAC={nodeMac}, Status={sensorResponse.Status}, RAW={sensorResponse.Data}");
                    return null;
                }

                if (!TryParseDaliCommonParamPayload(sensorResponse.Data, out var values, out var parseError))
                {
                    AppLog.Warn($"[DALI] CommonParam parse failed: MAC={nodeMac}, Error={parseError}, RAW={sensorResponse.Data}");
                    return null;
                }

                AppLog.Info($"[DALI] CommonParam read: MAC={nodeMac}, Max={values[0]}, Min={values[1]}, PowerOn={values[2]}, SysFail={values[3]}, FadeTime={values[4]}, FadeRate={values[5]}, ExtFade={values[6]}");
                return values;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[DALI] CommonParam read exception: MAC={nodeMac}, {ex.Message}");
                return null;
            }
        }

        private async Task<bool> SendDaliCommonParamSetAsync(string nodeMac, string cmd, string label)
        {
            try
            {
                var sensorResponse = await GetDataWithSysFailTimeoutAsync(nodeMac, cmd, $"DaliCommonParam set ({label})").ConfigureAwait(false);
                if (sensorResponse == null)
                    return false;

                if (sensorResponse.Status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(sensorResponse.Data))
                {
                    if (sensorResponse.Status == HttpStatusCode.ServiceUnavailable)
                        throw new BleDeviceUnreachableException(nodeMac);
                    AppLog.Warn($"[DALI] CommonParam set failed: MAC={nodeMac}, Label={label}, Status={sensorResponse.Status}, RAW={sensorResponse.Data}, Cmd={cmd}");
                    return false;
                }

                var reply = NormalizeHex(sensorResponse.Data);
                var ok = reply == "00" || reply == "0000";
                if (!ok)
                    AppLog.Warn($"[DALI] CommonParam set rejected: MAC={nodeMac}, Label={label}, Reply={reply}, Cmd={cmd}");

                return ok;
            }
            catch (BleDeviceUnreachableException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[DALI] CommonParam set exception: MAC={nodeMac}, Label={label}, {ex.Message}");
                return false;
            }
        }

        private static bool TryParseDaliCommonParamPayload(string? rawPayload, out byte[] values, out string error)
        {
            values = Array.Empty<byte>();
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(rawPayload))
            {
                error = "Empty payload.";
                return false;
            }

            var byteMatches = Regex.Matches(rawPayload, @"[0-9A-Fa-f]{2}");
            if (byteMatches.Count < 8)
            {
                error = $"Expected at least 8 bytes (status + 7 params), got {byteMatches.Count}.";
                return false;
            }

            var bytes = new byte[byteMatches.Count];
            for (var i = 0; i < byteMatches.Count; i++)
            {
                bytes[i] = byte.Parse(byteMatches[i].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            var status = bytes[0];
            if (status != 0x00)
            {
                error = $"NACK status 0x{status:X2}.";
                return false;
            }

            values = new byte[7];
            Array.Copy(bytes, 1, values, 0, 7);
            return true;
        }

        private static bool TryParseFixedHexSection(string? hex, int expectedBytes, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            var clean = NormalizeHex(hex);
            if (clean.Length != expectedBytes * 2)
                return false;

            try
            {
                bytes = Convert.FromHexString(clean);
                return bytes.Length == expectedBytes;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeHex(string? value)
            => new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

        private static int GetSysFailTimeoutMs()
        {
            int configured = RuntimeVariables.UPGRADE_DALI_SYSFAIL_TIMEOUT_MS;
            if (configured <= 0)
                configured = 10000;

            return Math.Clamp(configured, 1000, 30000);
        }

        private async Task<DataResponseModel?> GetDataWithSysFailTimeoutAsync(
            string nodeMac,
            string cmd,
            string label)
        {
            int timeoutMs = GetSysFailTimeoutMs();
            var task = _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, nodeMac, cmd, timeoutMs);
            var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (completed != task)
            {
                AppLog.Warn($"[DALI] {label} timed out after {timeoutMs}ms: MAC={nodeMac}");
                return null;
            }

            return await task.ConfigureAwait(false);
        }

        public async Task<string> GetBLEPushButtonList(string nodeMac)
        {
            string sensorCommand = "012101070099DB"; // GetBLEPushButtonList
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);
            if (sensorResponse.Status == System.Net.HttpStatusCode.ServiceUnavailable)
                throw new BleDeviceUnreachableException(nodeMac);

            string hex = "";
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
hex = sensorResponse.Data;
            }

            return hex;
        }

        public async Task<bool> SetBLEPushButtonList(string nodeMac, string newBlePushButtonListHex)
        {
            string sensorCommand = "0119016400CA2C"; // SetBLEPushButtonList
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand + newBlePushButtonListHex);

            if (sensorResponse.Status == HttpStatusCode.ServiceUnavailable)
                throw new BleDeviceUnreachableException(nodeMac);

            bool resp = false;
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
resp = sensorResponse.Data == "00";

                if (!resp)
                {
                    AppLog.Warn("Failed to set: " + sensorCommand + newBlePushButtonListHex);
}
            }
            else
                AppLog.Warn("BLE failed: " + sensorResponse.Status.ToString() + "RAW: " + sensorResponse.Data);
return resp;
        }

        private const ushort GetSetTunableWhiteListTelegramType = 0x0155;
        private const ushort GetSetTunableWhitePresetTelegramType = 0x0157;
        private const ushort GetSetTunableWhiteDefaultKelvinTelegramType = 0x0159;
        private const ushort UnixTimeTelegramType = 0x0150;
        private const byte TunableWhitePresetPreferredVersion = 0x02;
        private const byte TunableWhitePresetFallbackVersion = 0x01;

        public async Task<string> GetTunableWhiteList(string nodeMac)
        {
            var sensorCommand = BuildSensorCommandHex(GetSetTunableWhiteListTelegramType, new byte[] { 0x00 });
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

            if (sensorResponse.Status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(sensorResponse.Data))
            {
                if (sensorResponse.Status == HttpStatusCode.ServiceUnavailable)
                    throw new BleDeviceUnreachableException(nodeMac);
                AppLog.Warn($"[TW] Get list failed: status={sensorResponse.Status}, raw={sensorResponse.Data}");
                return string.Empty;
            }

            if (!TryNormalizeTunableWhiteListSetPayload(sensorResponse.Data, out var setPayload, out var error))
            {
                AppLog.Warn($"[TW] Get list parse failed: {error}; raw={sensorResponse.Data}");
                if (TryParseTunableWhiteListResult(sensorResponse.Data, out var listResultCode) && listResultCode == 0x07)
                    throw new BleFeatureNotSupportedBySensorException("TunableWhiteList");
                return string.Empty;
            }

            var hex = Convert.ToHexString(setPayload);
            AppLog.Debug($"[TW] Get list OK ({setPayload.Length} bytes payload).");
            return hex;
        }

        public async Task<bool> SetTunableWhiteList(string nodeMac, string tunableWhiteListHex)
        {
            if (!TryNormalizeTunableWhiteListSetPayload(tunableWhiteListHex, out var payload, out var normalizeError))
            {
                AppLog.Warn($"[TW] Set list rejected: {normalizeError}");
                return false;
            }

            payload[0] = 0x01; // enforce Set
            var sensorCommand = BuildSensorCommandHex(GetSetTunableWhiteListTelegramType, payload);
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

            if (sensorResponse.Status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(sensorResponse.Data))
            {
                if (sensorResponse.Status == HttpStatusCode.ServiceUnavailable)
                    throw new BleDeviceUnreachableException(nodeMac);
                AppLog.Warn($"[TW] Set list write failed: status={sensorResponse.Status}, raw={sensorResponse.Data}");
                return false;
            }

            if (!TryParseTunableWhiteListResult(sensorResponse.Data, out var resultCode))
            {
                AppLog.Warn($"[TW] Set list reply parse failed: raw={sensorResponse.Data}");
                return false;
            }

            if (resultCode != 0x00)
            {
                AppLog.Warn($"[TW] Set list rejected: result=0x{resultCode:X2} ({DescribeTunableWhiteResult(resultCode)})");
                return false;
            }

            var unixOk = await SetUnixTimeWithRuntimeOffsetAsync(nodeMac).ConfigureAwait(false);
            if (!unixOk)
            {
                AppLog.Warn($"[TW] List was written but UnixTime sync failed for {nodeMac}.");
                return false;
            }

            AppLog.Info($"[TW] List written and UnixTime synced for {nodeMac}.");
            return true;
        }

        public async Task<string> GetTunableWhitePreset(string nodeMac)
        {
            // Some firmware variants validate Version during GET and reject zero/default requests.
            // Try preferred version first, then fallback.
            foreach (var version in EnumeratePresetVersions(TunableWhitePresetPreferredVersion))
            {
                foreach (var payload in BuildTunableWhitePresetGetPayloadCandidates(version))
                {
                    var sensorCommand = BuildSensorCommandHex(GetSetTunableWhitePresetTelegramType, payload);
                    var sensorResponse = await _connectService.GetDataFromBleDevice(
                        _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

                    if (sensorResponse.Status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(sensorResponse.Data))
                    {
                        if (sensorResponse.Status == HttpStatusCode.ServiceUnavailable)
                            throw new BleDeviceUnreachableException(nodeMac);
                        AppLog.Warn($"[TW] Get preset failed (version=0x{version:X2}, payload={payload.Length}): status={sensorResponse.Status}, raw={sensorResponse.Data}");
                        continue;
                    }

                    if (TryNormalizeTunableWhitePresetSetPayload(sensorResponse.Data, out var setPayload, out _))
                        return Convert.ToHexString(setPayload);

                    if (TryParseTunableWhitePresetResult(sensorResponse.Data, out var resultCode))
                    {
                        AppLog.Warn($"[TW] Get preset rejected (version=0x{version:X2}, payload={payload.Length}): result=0x{resultCode:X2} ({DescribeTunableWhiteResult(resultCode)}), raw={sensorResponse.Data}");
                        if (resultCode == 0x07)
                            throw new BleFeatureNotSupportedBySensorException("TunableWhitePreset");
                        continue;
                    }

                    AppLog.Warn($"[TW] Get preset parse failed (version=0x{version:X2}, payload={payload.Length}): raw={sensorResponse.Data}");
                }
            }

            return string.Empty;
        }

        public async Task<bool> SetTunableWhitePreset(string nodeMac, string tunableWhitePresetHex)
        {
            if (!TryNormalizeTunableWhitePresetSetPayload(tunableWhitePresetHex, out var normalizedPayload, out var normalizeError))
            {
                AppLog.Warn($"[TW] Set preset rejected: {normalizeError}");
                return false;
            }

            byte requestedVersion = normalizedPayload.Length > 1 ? normalizedPayload[1] : TunableWhitePresetPreferredVersion;
            foreach (var version in EnumeratePresetVersions(requestedVersion))
            {
                foreach (var payload in BuildTunableWhitePresetSetPayloadCandidates(normalizedPayload, version))
                {
                    var sensorCommand = BuildSensorCommandHex(GetSetTunableWhitePresetTelegramType, payload);
                    var sensorResponse = await _connectService.GetDataFromBleDevice(
                        _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

                    if (sensorResponse.Status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(sensorResponse.Data))
                    {
                        if (sensorResponse.Status == HttpStatusCode.ServiceUnavailable)
                            throw new BleDeviceUnreachableException(nodeMac);
                        AppLog.Warn($"[TW] Set preset write failed (version=0x{version:X2}, payload={payload.Length}): status={sensorResponse.Status}, raw={sensorResponse.Data}");
                        continue;
                    }

                    if (!TryParseTunableWhitePresetResult(sensorResponse.Data, out var resultCode))
                    {
                        AppLog.Warn($"[TW] Set preset reply parse failed (version=0x{version:X2}, payload={payload.Length}): raw={sensorResponse.Data}");
                        continue;
                    }

                    if (resultCode == 0x00)
                        return true;

                    AppLog.Warn($"[TW] Set preset rejected (version=0x{version:X2}, payload={payload.Length}): result=0x{resultCode:X2} ({DescribeTunableWhiteResult(resultCode)}), raw={sensorResponse.Data}");
                }
            }

            return false;
        }

        public async Task<string> GetTunableWhiteDefaultKelvin(string nodeMac)
        {
            var sensorCommand = BuildSensorCommandHex(GetSetTunableWhiteDefaultKelvinTelegramType, new byte[] { 0x00 });
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

            if (sensorResponse.Status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(sensorResponse.Data))
            {
                if (sensorResponse.Status == HttpStatusCode.ServiceUnavailable)
                    throw new BleDeviceUnreachableException(nodeMac);
                AppLog.Warn($"[TW] Get default kelvin failed: status={sensorResponse.Status}, raw={sensorResponse.Data}");
                return string.Empty;
            }

            if (!TryNormalizeTunableWhiteDefaultKelvinSetPayload(sensorResponse.Data, out var setPayload, out var error))
            {
                AppLog.Warn($"[TW] Get default kelvin parse failed: {error}; raw={sensorResponse.Data}");
                if (TryParseTunableWhiteDefaultKelvinResult(sensorResponse.Data, out var kelvinResultCode) && kelvinResultCode == 0x07)
                    throw new BleFeatureNotSupportedBySensorException("TunableWhiteDefaultKelvin");
                return string.Empty;
            }

            var kelvinValue = (ushort)(setPayload[2] | (setPayload[3] << 8));
            AppLog.Debug($"[TW] Get default kelvin OK: version=0x{setPayload[1]:X2}, kelvin={kelvinValue}K, raw={sensorResponse.Data}");
            return Convert.ToHexString(setPayload);
        }

        public async Task<bool> SetTunableWhiteDefaultKelvin(string nodeMac, string tunableWhiteDefaultKelvinHex)
        {
            if (!TryNormalizeTunableWhiteDefaultKelvinSetPayload(tunableWhiteDefaultKelvinHex, out var normalizedPayload, out var normalizeError))
            {
                AppLog.Warn($"[TW] Set default kelvin rejected: {normalizeError}");
                return false;
            }

            byte requestedVersion = normalizedPayload.Length > 1 ? normalizedPayload[1] : TunableWhitePresetPreferredVersion;
            foreach (var version in EnumeratePresetVersions(requestedVersion))
            {
                var payload = (byte[])normalizedPayload.Clone();
                payload[0] = 0x01; // enforce Set
                payload[1] = version;

                var kelvinValue = (ushort)(payload[2] | (payload[3] << 8));
                AppLog.Debug($"[TW] Set default kelvin sending: version=0x{version:X2}, kelvin={kelvinValue}K");

                var sensorCommand = BuildSensorCommandHex(GetSetTunableWhiteDefaultKelvinTelegramType, payload);
                var sensorResponse = await _connectService.GetDataFromBleDevice(
                    _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

                if (sensorResponse.Status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(sensorResponse.Data))
                {
                    if (sensorResponse.Status == HttpStatusCode.ServiceUnavailable)
                        throw new BleDeviceUnreachableException(nodeMac);
                    AppLog.Warn($"[TW] Set default kelvin write failed (version=0x{version:X2}): status={sensorResponse.Status}, raw={sensorResponse.Data}");
                    continue;
                }

                if (!TryParseTunableWhiteDefaultKelvinResult(sensorResponse.Data, out var resultCode))
                {
                    AppLog.Warn($"[TW] Set default kelvin reply parse failed (version=0x{version:X2}): raw={sensorResponse.Data}");
                    continue;
                }

                if (resultCode == 0x00)
                {
                    AppLog.Info($"[TW] Default kelvin written (version=0x{version:X2}, kelvin={kelvinValue}K) for {nodeMac}.");
                    return true;
                }

                AppLog.Warn($"[TW] Set default kelvin rejected (version=0x{version:X2}, kelvin={kelvinValue}K): result=0x{resultCode:X2} ({DescribeTunableWhiteResult(resultCode)}), raw={sensorResponse.Data}");
            }

            return false;
        }

        private async Task<bool> SetUnixTimeWithRuntimeOffsetAsync(string nodeMac)
        {
            try
            {
                var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + RuntimeVariables.TUNABLE_WHITE_UNIX_TIME_OFFSET_SECONDS;
                if (seconds < 0)
                    seconds = 0;

                var payload = new byte[9];
                payload[0] = 0x01; // write
                var unixBytes = BitConverter.GetBytes(seconds);
                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(unixBytes);
                Array.Copy(unixBytes, 0, payload, 1, 8);

                var sensorCommand = BuildSensorCommandHex(UnixTimeTelegramType, payload);
                var sensorResponse = await _connectService.GetDataFromBleDevice(
                    _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

                if (sensorResponse.Status != HttpStatusCode.OK || string.IsNullOrWhiteSpace(sensorResponse.Data))
                {
                    AppLog.Warn($"[TW] UnixTime set failed: status={sensorResponse.Status}, raw={sensorResponse.Data}");
                    return false;
                }

                if (!TryParseHexBytes(sensorResponse.Data, out var replyBytes) || replyBytes.Length < 1)
                {
                    AppLog.Warn($"[TW] UnixTime reply parse failed: raw={sensorResponse.Data}");
                    return false;
                }

                if (replyBytes[0] != 0x01)
                {
                    AppLog.Warn($"[TW] UnixTime reply indicates non-write mode: 0x{replyBytes[0]:X2}");
                    return false;
                }

                AppLog.Info($"[TW] UnixTime set to {seconds} (offset={RuntimeVariables.TUNABLE_WHITE_UNIX_TIME_OFFSET_SECONDS}s) for {nodeMac}.");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[TW] UnixTime set exception for {nodeMac}: {ex.Message}");
                return false;
            }
        }

        private static bool TryNormalizeTunableWhiteListSetPayload(string? inputHex, out byte[] payload, out string error)
        {
            payload = Array.Empty<byte>();
            error = string.Empty;

            if (!TryParseHexBytes(inputHex, out var bytes))
            {
                error = "Empty or invalid hex.";
                return false;
            }

            // Reply payload form: setRead, result, version, command, hour[24]*4 (100 bytes).
            if (bytes.Length >= 100 && bytes[0] <= 0x01)
            {
                if (bytes[1] != 0x00)
                {
                    error = $"Sensor returned NACK result 0x{bytes[1]:X2} ({DescribeTunableWhiteResult(bytes[1])}).";
                    return false;
                }

                payload = new byte[99];
                payload[0] = 0x01;
                payload[1] = bytes[2];
                payload[2] = bytes[3];
                Array.Copy(bytes, 4, payload, 3, 96);
                return true;
            }

            // Set payload form: getSet, version, command, hour[24]*4 (99 bytes).
            if (bytes.Length >= 99 && bytes[0] <= 0x01)
            {
                payload = new byte[99];
                Array.Copy(bytes, 0, payload, 0, 99);
                payload[0] = 0x01;
                return true;
            }

            // Semantic form: version, command, hour[24]*4 (98 bytes).
            if (bytes.Length >= 98)
            {
                payload = new byte[99];
                payload[0] = 0x01;
                Array.Copy(bytes, 0, payload, 1, 98);
                return true;
            }

            error = $"Unsupported Tunable White list payload length: {bytes.Length} byte(s).";
            return false;
        }

        private static bool TryNormalizeTunableWhitePresetSetPayload(string? inputHex, out byte[] payload, out string error)
        {
            payload = Array.Empty<byte>();
            error = string.Empty;

            if (!TryParseHexBytes(inputHex, out var bytes))
            {
                error = "Empty or invalid hex.";
                return false;
            }

            byte version;
            int presetOffset;

            // Reply payload form (firmware variants):
            // - setRead, version, result, preset[4]*4
            // - setRead, result, version, preset[4]*4
            if (TryParseTunableWhitePresetReplyHeader(bytes, out version, out var replyResult, out presetOffset))
            {
                if (replyResult != 0x00)
                {
                    error = $"Sensor returned NACK result 0x{replyResult:X2} ({DescribeTunableWhiteResult(replyResult)}).";
                    return false;
                }
            }
            // Set payload variant: getSet, version, reserved, preset[4]*4 (19 bytes).
            else if (bytes.Length >= 19 && bytes[0] <= 0x01)
            {
                version = bytes[1];
                presetOffset = 3;
            }
            // Set payload form: getSet, version, preset[4]*4 (18 bytes).
            else if (bytes.Length >= 18 && bytes[0] <= 0x01)
            {
                version = bytes[1];
                presetOffset = 2;
            }
            // Semantic payload variant: version, reserved, preset[4]*4 (18 bytes).
            else if (bytes.Length >= 18)
            {
                version = bytes[0];
                presetOffset = 2;
            }
            // Semantic form: version, preset[4]*4 (17 bytes).
            else if (bytes.Length >= 17)
            {
                version = bytes[0];
                presetOffset = 1;
            }
            else
            {
                error = $"Unsupported Tunable White preset payload length: {bytes.Length} byte(s).";
                return false;
            }

            payload = new byte[18];
            payload[0] = 0x01;
            payload[1] = version;
            Array.Copy(bytes, presetOffset, payload, 2, 16);
            return true;
        }

        private static bool TryNormalizeTunableWhiteDefaultKelvinSetPayload(string? inputHex, out byte[] payload, out string error)
        {
            payload = Array.Empty<byte>();
            error = string.Empty;

            if (!TryParseHexBytes(inputHex, out var bytes))
            {
                error = "Empty or invalid hex.";
                return false;
            }

            byte version;
            byte kelvinLsb;
            byte kelvinMsb;

            // Reply payload form: setRead, result, version, kelvin(2) (5 bytes).
            if (bytes.Length >= 5 && bytes[0] <= 0x01)
            {
                if (bytes[1] != 0x00)
                {
                    error = $"Sensor returned NACK result 0x{bytes[1]:X2} ({DescribeTunableWhiteResult(bytes[1])}).";
                    return false;
                }

                version = bytes[2];
                kelvinLsb = bytes[3];
                kelvinMsb = bytes[4];
            }
            // Set payload form: getSet, version, kelvin(2) (4 bytes).
            else if (bytes.Length >= 4 && bytes[0] <= 0x01)
            {
                version = bytes[1];
                kelvinLsb = bytes[2];
                kelvinMsb = bytes[3];
            }
            // Semantic form: version, kelvin(2) (3 bytes).
            else if (bytes.Length >= 3)
            {
                version = bytes[0];
                kelvinLsb = bytes[1];
                kelvinMsb = bytes[2];
            }
            else
            {
                error = $"Unsupported Tunable White default kelvin payload length: {bytes.Length} byte(s).";
                return false;
            }

            payload = new byte[4];
            payload[0] = 0x01;
            payload[1] = version;
            payload[2] = kelvinLsb;
            payload[3] = kelvinMsb;
            return true;
        }

        private static bool TryParseTunableWhiteListResult(string? responseHex, out byte resultCode)
        {
            resultCode = 0xFF;
            if (!TryParseHexBytes(responseHex, out var bytes) || bytes.Length == 0)
                return false;

            if (bytes.Length >= 2 && bytes[0] <= 0x01)
            {
                resultCode = bytes[1];
                return true;
            }

            resultCode = bytes[0];
            return true;
        }

        private static bool TryParseTunableWhitePresetResult(string? responseHex, out byte resultCode)
        {
            resultCode = 0xFF;
            if (!TryParseHexBytes(responseHex, out var bytes) || bytes.Length == 0)
                return false;

            if (TryParseTunableWhitePresetReplyHeader(bytes, out _, out var parsedResult, out _))
            {
                resultCode = parsedResult;
                return true;
            }

            if (bytes.Length >= 2 && bytes[0] <= 0x01)
            {
                resultCode = bytes[1];
                return true;
            }

            resultCode = bytes[0];
            return true;
        }

        private static IEnumerable<byte[]> BuildTunableWhitePresetGetPayloadCandidates(byte version)
        {
            // Protocol 0x0157: request frame is always 25 bytes total (7-byte header + 18-byte payload).
            // Byte 7=Get/Set, Byte 8=Version, Bytes 9-24=4 presets × 4 bytes.
            // The reply (0x0158) is 26 bytes (extra Result byte at position 9) — the request has no Result byte.
            var payload = new byte[18];
            payload[0] = 0x00; // Get
            payload[1] = version;
            WriteDefaultPresetValues(payload, presetOffset: 2);
            yield return payload;
        }

        private static IEnumerable<byte[]> BuildTunableWhitePresetSetPayloadCandidates(byte[] normalizedPayload, byte version)
        {
            // Protocol 0x0157: request frame is always 25 bytes total (18-byte payload).
            var payload = (byte[])normalizedPayload.Clone();
            payload[0] = 0x01; // Set
            payload[1] = version;
            yield return payload;
        }

        private static void WriteDefaultPresetValues(byte[] payload, int presetOffset)
        {
            for (var i = 0; i < 4; i++)
            {
                var offset = presetOffset + (i * 4);
                WriteUInt16Le(payload, offset, 4000); // Kelvin
                WriteUInt16Le(payload, offset + 2, 500); // Lux
            }
        }

        private static bool TryParseTunableWhitePresetReplyHeader(
            byte[] bytes,
            out byte version,
            out byte resultCode,
            out int presetOffset)
        {
            version = 0;
            resultCode = 0xFF;
            presetOffset = 3;

            if (bytes.Length < 19 || bytes[0] > 0x01)
                return false;

            var candA = (version: bytes[1], result: bytes[2], score: ScorePresetReplyCandidate(bytes[1], bytes[2]));
            var candB = (version: bytes[2], result: bytes[1], score: ScorePresetReplyCandidate(bytes[2], bytes[1]));
            var chosen = candA.score >= candB.score ? candA : candB;

            if (chosen.score < 0)
                return false;

            version = chosen.version;
            resultCode = chosen.result;
            return true;
        }

        private static int ScorePresetReplyCandidate(byte version, byte result)
        {
            if (!IsKnownTunableWhiteResult(result))
                return -100;

            var score = 0;
            score += 2; // known result code
            if (version == 0x01 || version == 0x02)
                score += 2;
            if (result == 0x00)
                score += 1;
            if (version != 0x00)
                score += 1;
            return score;
        }

        private static bool IsKnownTunableWhiteResult(byte result)
            => result == 0x00
            || result == 0x01
            || result == 0x02
            || result == 0x03
            || result == 0x04
            || result == 0x07;

        private static IEnumerable<byte> EnumeratePresetVersions(byte first)
        {
            var yielded = new HashSet<byte>();
            if (first > 0 && yielded.Add(first))
                yield return first;
            if (yielded.Add(TunableWhitePresetPreferredVersion))
                yield return TunableWhitePresetPreferredVersion;
            if (yielded.Add(TunableWhitePresetFallbackVersion))
                yield return TunableWhitePresetFallbackVersion;
        }

        private static void WriteUInt16Le(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value & 0xFF);
            target[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static bool TryParseTunableWhiteDefaultKelvinResult(string? responseHex, out byte resultCode)
            => TryParseTunableWhiteListResult(responseHex, out resultCode);

        private static string DescribeTunableWhiteResult(byte resultCode)
            => resultCode switch
            {
                0x00 => "ACK",
                0x01 => "NACK_RANGE_CHECK",
                0x02 => "NACK_NVM",
                0x03 => "NACK_FRAME_SIZE_ERROR",
                0x04 => "NACK_OPENPERIOD",
                0x07 => "NOT_AVAILABLE_IN_PROFILE",
                _ => "UNKNOWN"
            };

        private static string BuildSensorCommandHex(ushort telegramType, byte[] payload)
        {
            payload ??= Array.Empty<byte>();
            ushort totalLength = (ushort)(7 + payload.Length);

            var message = new byte[7 + payload.Length];
            message[0] = 0x01; // protocol version
            message[1] = (byte)(telegramType & 0xFF); // telegram type little-endian
            message[2] = (byte)((telegramType >> 8) & 0xFF);
            message[3] = (byte)(totalLength & 0xFF); // total length little-endian
            message[4] = (byte)((totalLength >> 8) & 0xFF);

            var crc16 = CalcSensorCrc16(message.AsSpan(0, 5));
            message[5] = (byte)(crc16 & 0xFF);
            message[6] = (byte)((crc16 >> 8) & 0xFF);

            if (payload.Length > 0)
                Array.Copy(payload, 0, message, 7, payload.Length);

            return Convert.ToHexString(message);
        }

        private static ushort CalcSensorCrc16(ReadOnlySpan<byte> data, ushort crc = 0x8005, ushort poly = 0x1021)
        {
            for (var i = 0; i < data.Length; i++)
            {
                crc ^= (ushort)(data[i] << 8);
                for (var j = 0; j < 8; j++)
                {
                    crc = (crc & 0x8000) != 0
                        ? (ushort)(((crc << 1) ^ poly) & 0xFFFF)
                        : (ushort)(crc << 1);
                }
            }

            return crc;
        }

        private static bool TryParseHexBytes(string? value, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            var clean = NormalizeHex(value);
            if (string.IsNullOrWhiteSpace(clean))
                return false;

            if ((clean.Length & 1) != 0)
                clean = "0" + clean;

            try
            {
                bytes = Convert.FromHexString(clean);
                return bytes.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public async Task<string> GetWiredPushButtonList(string nodeMac)
        {
            string sensorCommand = "0113010700181A"; // GetWiredPushButtonList
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);
            if (sensorResponse.Status == System.Net.HttpStatusCode.ServiceUnavailable)
                throw new BleDeviceUnreachableException(nodeMac);

            string hex = "";
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
hex = sensorResponse.Data;
            }

            return hex;
        }

        public async Task<bool> SetWiredPushButtonList(string nodeMac, string newWiredPushButtonListHex)
        {
            string sensorCommand = "0111011C00F928"; // SetWiredPushButtonList
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand + newWiredPushButtonListHex);

            if (sensorResponse.Status == HttpStatusCode.ServiceUnavailable)
                throw new BleDeviceUnreachableException(nodeMac);

            bool resp = false;
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
resp = sensorResponse.Data == "00";
                if (!resp)
                {
                    AppLog.Warn("Failed to set: " + sensorCommand + newWiredPushButtonListHex);
}
            }
            else
                AppLog.Warn("BLE failed: " + sensorResponse.Status.ToString() + "RAW: " + sensorResponse.Data);
return resp;
        }

        public async Task<string> GetDaliPushButtonList(string nodeMac)
        {
            string sensorCommand = "013F0107006462"; // GetDaliPushButtonList
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);
            if (sensorResponse.Status == System.Net.HttpStatusCode.ServiceUnavailable)
                throw new BleDeviceUnreachableException(nodeMac);

            string hex = "";
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
hex = sensorResponse.Data;
            }

            return hex;
        }

        public async Task<bool> SetDaliPushButtonList(string nodeMac, string newDaliPushButtonListHex)
        {
            string sensorCommand = "013D016C00DC58"; // SetDaliPushButtonList
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand + newDaliPushButtonListHex);

            if (sensorResponse.Status == HttpStatusCode.ServiceUnavailable)
                throw new BleDeviceUnreachableException(nodeMac);

            bool resp = false;
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
resp = sensorResponse.Data == "00";
                if (!resp)
                {
                    AppLog.Warn("Failed to set: " + sensorCommand + newDaliPushButtonListHex);
}

            }
            else
                AppLog.Warn("BLE failed: " + sensorResponse.Status.ToString() + "RAW: " + sensorResponse.Data);
return resp;
        }

        // Optional helpers

        /// <summary>
        /// Sends a DALI restore command and waits specifically for the 0x043F DaliRestoreDatabaseResult
        /// notification (ignoring the 0x043E start-ack). Subscribes before writing to avoid race conditions.
        /// Returns (Ok=true) on RESTORE_OK (status 0x01), (IsRestoreFailure=true) on RESTORE_FAILURE (status 0x02).
        /// </summary>
        private async Task<(bool Ok, bool IsRestoreFailure)> SendAndWaitForRestoreResultAsync(
            string nodeMac, string command, int timeoutMs = 60000)
        {
            const string RestoreResultType = "3F04"; // 0x043F little-endian
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            var subToken = _notificationService.Subscribe(nodeMac, (_, rawHex) =>
            {
                if (rawHex?.Length >= 6 &&
                    rawHex.Substring(2, 4).Equals(RestoreResultType, StringComparison.OrdinalIgnoreCase))
                {
                    tcs.TrySetResult(rawHex);
                }
            });

            try
            {
                using var writeResponse = await cassiaReadWriteService
                    .WriteBleMessage(_gatewayIpAddress, nodeMac, 19, command, "?noresponse=1")
                    .ConfigureAwait(false);

                if (writeResponse.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    AppLog.Warn($"[DALI RESTORE] BLE write failed ({writeResponse.StatusCode}) for {nodeMac}");
                    return (false, false);
                }

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed != tcs.Task)
                {
                    AppLog.Warn($"[DALI RESTORE] Timeout ({timeoutMs}ms) waiting for 0x043F result for {nodeMac}");
                    return (false, false);
                }

                var rawHex = await tcs.Task;
                if (rawHex.Length < 16)
                {
                    AppLog.Warn($"[DALI RESTORE] Response too short ({rawHex.Length} chars) for {nodeMac}");
                    return (false, false);
                }

                // Status byte is at position 14-15 in the hex string (byte 7 of the 8-byte telegram)
                var statusHex = rawHex.Substring(14, 2);
                if (!byte.TryParse(statusHex, System.Globalization.NumberStyles.HexNumber, null, out var statusByte))
                {
                    AppLog.Warn($"[DALI RESTORE] Could not parse status byte '{statusHex}' for {nodeMac}");
                    return (false, false);
                }

                AppLog.Debug($"[DALI RESTORE] 0x043F status=0x{statusByte:X2} for {nodeMac} (0x01=OK, 0x02=FAILURE)");
                return (statusByte == 0x01, statusByte == 0x02);
            }
            finally
            {
                _notificationService.Unsubscribe(nodeMac, subToken);
            }
        }

        public async Task<bool> DaliRestore102Database(string nodeMac)
        {
            const string sensorCommand = "013C0407004812"; // DaliRestore102Database (0x043C)
            var (ok, isRestoreFailure) = await SendAndWaitForRestoreResultAsync(nodeMac, sensorCommand).ConfigureAwait(false);
            if (isRestoreFailure)
                AppLog.Warn($"[DALI RESTORE] 0x043F RESTORE_FAILURE for {nodeMac} — total new commissioning needed");
            return ok;
        }

        public async Task<bool> DaliRestore103Database(string nodeMac)
        {
            const string sensorCommand = "013D040700FC64"; // DaliRestore103Database (0x043D)
            var (ok, isRestoreFailure) = await SendAndWaitForRestoreResultAsync(nodeMac, sensorCommand).ConfigureAwait(false);
            if (isRestoreFailure)
                AppLog.Warn($"[DALI RESTORE] 0x043F RESTORE_FAILURE for {nodeMac} — total new commissioning needed");
            return ok;
        }

        // 0x0400 + len 0x0008. CRC16 is 0x61AD and encoded little-endian in telegrams => AD61.
        private const string DaliCommissioningCommandPrefix = "0100040800AD61";
        private const string DaliGetDeviceDatabaseCount102Cmd = "010F0407007DA5";
        // 0x0417 + len 0x0007. CRC16 is 0x3B19 and encoded little-endian in telegrams => 193B.
        private const string DaliGetDeviceDatabaseCount103Cmd = "0117040700193B";

        public Task<(bool Success, int? Count, string Message)> DaliGetDeviceDatabaseCount102Async(
            string nodeMac,
            int maxWaitMs = 30000)
            => DaliGetDeviceDatabaseCountAsync(
                nodeMac,
                DaliGetDeviceDatabaseCount102Cmd,
                "102",
                IsDaliDatabaseCount102ReplyTelegram,
                maxWaitMs);

        public Task<(bool Success, int? Count, string Message)> DaliGetDeviceDatabaseCount103Async(
            string nodeMac,
            int maxWaitMs = 30000)
            => DaliGetDeviceDatabaseCountAsync(
                nodeMac,
                DaliGetDeviceDatabaseCount103Cmd,
                "103",
                IsDaliDatabaseCount103ReplyTelegram,
                maxWaitMs);

        private async Task<(bool Success, int? Count, string Message)> DaliGetDeviceDatabaseCountAsync(
            string nodeMac,
            string command,
            string label,
            Func<string, bool> isExpectedReplyTelegram,
            int maxWaitMs)
        {
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(5000, maxWaitMs));
            var attempt = 0;

            while (DateTime.UtcNow <= deadlineUtc)
            {
                attempt++;
                Guid token = Guid.Empty;
                var replyTcs = new TaskCompletionSource<(byte status, byte commissioningRunning, byte count)>(TaskCreationOptions.RunContinuationsAsynchronously);

                try
                {
                    token = _notificationService.Subscribe(nodeMac, (_, rawTelegram) =>
                    {
                        try
                        {
                            var normalizedRaw = NormalizeHex(rawTelegram);
                            if (normalizedRaw.Length < 14)
                                return;

                            var parsed = new GenericTelegramReply(normalizedRaw);
                            var telegramType = NormalizeHex(parsed.TelegramType);
                            if (!isExpectedReplyTelegram(telegramType))
                                return;

                            var payload = NormalizeHex(parsed.DataResult);
                            if (TryParseDaliDatabaseCountPayload(payload, out var status, out var commissioningRunning, out var count))
                            {
                                AppLog.Debug($"[DALI] DatabaseCount{label} reply type={telegramType} status=0x{status:X2} running={commissioningRunning} count={count} mac={nodeMac}");
                                replyTcs.TrySetResult((status, commissioningRunning, count));
                            }
                        }
                        catch
                        {
                            // Ignore malformed/unrelated notifications.
                        }
                    });

                    if (_notificationService is LinuxBle.LinuxBleNotificationService linuxNotify)
                    {
                        try
                        {
                            using var notifyReadyCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            await linuxNotify.EnsureNotifyingReadyAsync(nodeMac, notifyReadyCts.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Debug($"[DALI] DatabaseCount{label} notify readiness wait did not complete for {nodeMac}: {ex.Message}");
                        }
                    }

                    using var writeResponse = await cassiaReadWriteService
                        .WriteBleMessage(_gatewayIpAddress, nodeMac, 19, command, "?noresponse=1")
                        .ConfigureAwait(false);

                    if (!writeResponse.IsSuccessStatusCode)
                    {
                        var statusMsg = $"HTTP {(int)writeResponse.StatusCode} {writeResponse.StatusCode}";
                        AppLog.Warn($"[DALI] DatabaseCount{label} command write failed for {nodeMac}: {statusMsg} (attempt {attempt}).");
                        await Task.Delay(1000).ConfigureAwait(false);
                        continue;
                    }

                    var waitMs = Math.Max(1000, (int)Math.Min(8000, (deadlineUtc - DateTime.UtcNow).TotalMilliseconds));
                    if (!await TryWaitForTaskAsync(replyTcs.Task, waitMs).ConfigureAwait(false))
                    {
                        AppLog.Warn($"[DALI] DatabaseCount{label} reply timeout for {nodeMac} (attempt {attempt}).");
                        await Task.Delay(1000).ConfigureAwait(false);
                        continue;
                    }

                    var (status, commissioningRunning, count) = await replyTcs.Task.ConfigureAwait(false);
                    if (status != 0x00)
                        return (false, null, $"NACK status 0x{status:X2}");

                    if (commissioningRunning == 0x01)
                    {
                        AppLog.Info($"[DALI] DatabaseCount{label} indicates commissioning still running for {nodeMac} (attempt {attempt}). Waiting...");
                        await Task.Delay(1500).ConfigureAwait(false);
                        continue;
                    }

                    return (true, count, $"DatabaseCount{label}={count}");
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"[DALI] DatabaseCount{label} exception for {nodeMac}: {ex.Message} (attempt {attempt}).");
                    await Task.Delay(1000).ConfigureAwait(false);
                }
                finally
                {
                    if (token != Guid.Empty)
                        _notificationService.Unsubscribe(nodeMac, token);
                }
            }

            return (false, null, $"DatabaseCount{label} timed out after {Math.Max(5000, maxWaitMs)} ms.");
        }

        public async Task<(bool Success, string Message, int? DevicesFound, byte? ResultCode)> DaliRunTotalNewCommissioningScanAsync(
            string nodeMac,
            byte searchType,
            int maxWaitMs = 180000)
        {
            if (searchType != 0x00 && searchType != 0x01 && searchType != 0x03)
                return (false, $"Unsupported DALI commissioning search type: 0x{searchType:X2}", null, null);

            var command = DaliCommissioningCommandPrefix + searchType.ToString("X2", CultureInfo.InvariantCulture);
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(5000, maxWaitMs));
            var label = searchType switch
            {
                0x00 => "address-all-zone1",
                0x01 => "102 total-new",
                0x03 => "103 total-new",
                _ => $"type-0x{searchType:X2}"
            };
            var attempt = 0;

            while (DateTime.UtcNow <= deadlineUtc)
            {
                attempt++;
                Guid token = Guid.Empty;
                var ackTcs = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
                var resultTcs = new TaskCompletionSource<(byte resultCode, byte dataByte)>(TaskCreationOptions.RunContinuationsAsynchronously);

                try
                {
                    token = _notificationService.Subscribe(nodeMac, (_, rawTelegram) =>
                    {
                        try
                        {
                            var normalizedRaw = NormalizeHex(rawTelegram);
                            if (normalizedRaw.Length < 14)
                                return;

                            var parsed = new GenericTelegramReply(normalizedRaw);
                            var telegramType = NormalizeHex(parsed.TelegramType);
                            var payload = NormalizeHex(parsed.DataResult);

                            if (IsDaliCommissioningReplyTelegram(telegramType))
                            {
                                AppLog.Debug($"[DALI] Commissioning {label} notify reply type={telegramType} payload={payload} mac={nodeMac}");
                                if (TryParseCommissioningPayloadBytes(payload, out var b0, out var _unused))
                                    ackTcs.TrySetResult(b0);
                                return;
                            }

                            if (IsDaliCommissioningStatusResultTelegram(telegramType))
                            {
                                AppLog.Debug($"[DALI] Commissioning {label} notify status type={telegramType} payload={payload} mac={nodeMac}");
                                if (TryParseCommissioningPayloadBytes(payload, out var resultCode, out var dataByte))
                                    resultTcs.TrySetResult((resultCode, dataByte));
                            }
                        }
                        catch
                        {
                            // Ignore malformed/unrelated notifications.
                        }
                    });

                    // Linux-native BLE path may race StartNotify vs write. Ensure notifications are ready
                    // before sending commissioning command so fast 0x0422 replies are not lost.
                    if (_notificationService is LinuxBle.LinuxBleNotificationService linuxNotify)
                    {
                        try
                        {
                            using var notifyReadyCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            await linuxNotify.EnsureNotifyingReadyAsync(nodeMac, notifyReadyCts.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Debug($"[DALI] Commissioning {label} notify readiness wait did not complete for {nodeMac}: {ex.Message}");
                        }
                    }

                    using var writeResponse = await cassiaReadWriteService
                        .WriteBleMessage(_gatewayIpAddress, nodeMac, 19, command, "?noresponse=1")
                        .ConfigureAwait(false);

                    if (!writeResponse.IsSuccessStatusCode)
                    {
                        var statusMsg = $"HTTP {(int)writeResponse.StatusCode} {writeResponse.StatusCode}";
                        AppLog.Warn($"[DALI] Commissioning {label} command write/read failed for {nodeMac}: {statusMsg} (attempt {attempt}).");
                        await Task.Delay(2000).ConfigureAwait(false);
                        continue;
                    }

                    var ackWaitMs = Math.Max(1000, (int)Math.Min(20000, (deadlineUtc - DateTime.UtcNow).TotalMilliseconds));
                    var firstReply = await Task.WhenAny(
                        ackTcs.Task,
                        resultTcs.Task,
                        Task.Delay(Math.Max(1, ackWaitMs))).ConfigureAwait(false);

                    bool statusArrivedBeforeAck = firstReply == resultTcs.Task;
                    if (statusArrivedBeforeAck)
                    {
                        AppLog.Warn($"[DALI] Commissioning {label} received 0x0421 status before 0x0422 reply for {nodeMac} (attempt {attempt}).");
                    }

                    if (firstReply != ackTcs.Task && !statusArrivedBeforeAck)
                    {
                        AppLog.Warn($"[DALI] Commissioning {label} did not return 0x0422 reply for {nodeMac} (attempt {attempt}).");
                        await Task.Delay(2000).ConfigureAwait(false);
                        continue;
                    }

                    if (firstReply == ackTcs.Task)
                    {
                        var ackStatus = await ackTcs.Task.ConfigureAwait(false);
                        var ackStatusName = DescribeDaliCommissioningReplyStatus(ackStatus);
                        if (ackStatus == 0x03)
                        {
                            AppLog.Info($"[DALI] Commissioning {label} waiting for previous scan to finish for {nodeMac} (attempt {attempt}, status={ackStatusName}).");
                            await Task.Delay(5000).ConfigureAwait(false);
                            continue;
                        }

                        if (ackStatus != 0x00)
                        {
                            return (false, $"Commissioning {label} rejected: {ackStatusName} (0x{ackStatus:X2})", null, null);
                        }
                    }

                    async Task<(bool HasValue, byte ResultCode, byte DataByte)> TryGetNextStatusAsync(int waitMs, bool statusAlreadyAvailable)
                    {
                        TaskCompletionSource<(byte resultCode, byte dataByte)> completedStatusTcs;
                        if (statusAlreadyAvailable)
                        {
                            completedStatusTcs = resultTcs;
                        }
                        else
                        {
                            if (!await TryWaitForTaskAsync(resultTcs.Task, waitMs).ConfigureAwait(false))
                                return (false, 0x00, 0x00);
                            completedStatusTcs = resultTcs;
                        }

                        // Re-arm before awaiting so we never drop quick back-to-back status telegrams.
                        resultTcs = new TaskCompletionSource<(byte resultCode, byte dataByte)>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var (statusCode, statusData) = await completedStatusTcs.Task.ConfigureAwait(false);
                        return (true, statusCode, statusData);
                    }

                    bool IsCommissioningProgressResult(byte code)
                        => code == 0x0E || code == 0x0F || code == 0x10;

                    var resultDeadlineUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, (int)Math.Min(120000, (deadlineUtc - DateTime.UtcNow).TotalMilliseconds)));
                    var haveTerminalResult = false;
                    byte terminalResultCode = 0x00;
                    byte terminalDataByte = 0x00;
                    DateTime terminalResultSeenUtc = DateTime.MinValue;
                    bool statusAlreadyAvailable = statusArrivedBeforeAck;
                    const int commissioningDoneCountGraceMs = 6000;

                    while (DateTime.UtcNow <= resultDeadlineUtc)
                    {
                        var remainingMs = Math.Max(1, (int)(resultDeadlineUtc - DateTime.UtcNow).TotalMilliseconds);
                        var waitMs = Math.Min(5000, remainingMs);
                        var nextStatus = await TryGetNextStatusAsync(waitMs, statusAlreadyAvailable).ConfigureAwait(false);
                        statusAlreadyAvailable = false;

                        if (!nextStatus.HasValue)
                        {
                            // Some devices first emit COMMISSIONING_DONE with 0 count and shortly after
                            // emit a final DONE with the real count. Keep listening briefly for that.
                            if (haveTerminalResult &&
                                terminalResultCode == 0x03 &&
                                terminalDataByte == 0x00 &&
                                (DateTime.UtcNow - terminalResultSeenUtc).TotalMilliseconds >= commissioningDoneCountGraceMs)
                            {
                                break;
                            }

                            continue;
                        }

                        var statusCode = nextStatus.ResultCode;
                        var statusData = nextStatus.DataByte;
                        AppLog.Debug($"[DALI] Commissioning {label} status update for {nodeMac}: code=0x{statusCode:X2} ({DescribeDaliCommissioningResult(statusCode)}), data={statusData}");

                        if (IsCommissioningProgressResult(statusCode))
                            continue;

                        haveTerminalResult = true;
                        terminalResultCode = statusCode;
                        terminalDataByte = statusData;
                        terminalResultSeenUtc = DateTime.UtcNow;

                        if (statusCode != 0x03 || statusData != 0x00)
                            break;
                    }

                    if (!haveTerminalResult)
                        return (false, $"Commissioning {label} started but timed out waiting for terminal 0x0421 status result.", null, null);

                    var foundCount = (int)terminalDataByte;
                    var resultName = DescribeDaliCommissioningResult(terminalResultCode);
                    var completedOk = IsCommissioningResultSuccess(terminalResultCode);
                    var message = $"Result={resultName} (0x{terminalResultCode:X2}), devicesFound={foundCount}";

                    if (completedOk)
                        AppLog.Info($"[DALI] Commissioning {label} completed for {nodeMac}: {message}");
                    else
                        AppLog.Warn($"[DALI] Commissioning {label} failed for {nodeMac}: {message}");

                    return (completedOk, message, foundCount, terminalResultCode);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"[DALI] Commissioning {label} exception for {nodeMac}: {ex.Message} (attempt {attempt}).");
                    await Task.Delay(2000).ConfigureAwait(false);
                }
                finally
                {
                    if (token != Guid.Empty)
                        _notificationService.Unsubscribe(nodeMac, token);
                }
            }

            return (false, $"Commissioning {label} timed out after {Math.Max(5000, maxWaitMs)} ms.", null, null);
        }

        private static async Task<bool> TryWaitForTaskAsync(Task task, int timeoutMs)
        {
            var completed = await Task.WhenAny(task, Task.Delay(Math.Max(1, timeoutMs))).ConfigureAwait(false);
            return completed == task;
        }

        private static bool IsDaliCommissioningReplyTelegram(string telegramType)
            => string.Equals(telegramType, "2204", StringComparison.OrdinalIgnoreCase)
               || string.Equals(telegramType, "0422", StringComparison.OrdinalIgnoreCase);

        private static bool IsDaliCommissioningStatusResultTelegram(string telegramType)
            => string.Equals(telegramType, "2104", StringComparison.OrdinalIgnoreCase)
               || string.Equals(telegramType, "0421", StringComparison.OrdinalIgnoreCase);

        private static bool IsDaliDatabaseCount102ReplyTelegram(string telegramType)
            => string.Equals(telegramType, "1F04", StringComparison.OrdinalIgnoreCase)
               || string.Equals(telegramType, "041F", StringComparison.OrdinalIgnoreCase);

        private static bool IsDaliDatabaseCount103ReplyTelegram(string telegramType)
            => string.Equals(telegramType, "3404", StringComparison.OrdinalIgnoreCase)
               || string.Equals(telegramType, "0434", StringComparison.OrdinalIgnoreCase);

        private static bool TryParseCommissioningPayloadBytes(string? payload, out byte b0, out byte b1)
        {
            b0 = 0xFF;
            b1 = 0x00;
            var normalized = NormalizeHex(payload);
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < 2 || (normalized.Length % 2) != 0)
                return false;

            byte[] bytes;
            try
            {
                bytes = Convert.FromHexString(normalized);
            }
            catch
            {
                return false;
            }

            if (bytes.Length == 0)
                return false;

            b0 = bytes[0];
            if (bytes.Length > 1)
                b1 = bytes[1];

            return true;
        }

        private static bool TryParseDaliDatabaseCountPayload(
            string? payload,
            out byte status,
            out byte commissioningRunning,
            out byte count)
        {
            status = 0xFF;
            commissioningRunning = 0x00;
            count = 0x00;

            var normalized = NormalizeHex(payload);
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < 6 || (normalized.Length % 2) != 0)
                return false;

            byte[] bytes;
            try
            {
                bytes = Convert.FromHexString(normalized);
            }
            catch
            {
                return false;
            }

            if (bytes.Length < 3)
                return false;

            status = bytes[0];
            commissioningRunning = bytes[1];
            count = bytes[2];
            return true;
        }

        private static string DescribeDaliCommissioningReplyStatus(byte status)
            => status switch
            {
                0x00 => "ACK",
                0x01 => "NACK_RANGE_CHECK",
                0x03 => "NACK_COMMISSIONING_RUNNING",
                0x04 => "NACK_OPENPERIOD",
                0x07 => "NOT_AVAILABLE_IN_PROFILE",
                0x0B => "NACK_USER_CONFIG_BEING_SET",
                _ => "UNKNOWN"
            };

        private static bool IsCommissioningResultSuccess(byte resultCode)
            => resultCode switch
            {
                0x04 => false, // COMMISSIONING_ERROR
                0x09 => false, // ZONE_ASSIGN_ERROR
                0x0B => false, // ERROR_DALI_8BIT_FRAME_SEND
                0x0C => false, // ERROR_DALI_16BIT_FRAME_SEND
                0x0D => false, // ERROR_DALI_24BIT_FRAME_SEND
                _ => true
            };

        private static string DescribeDaliCommissioningResult(byte resultCode)
            => resultCode switch
            {
                0x00 => "NO_COMMISSIONING_RESPONSE",
                0x01 => "ONE_CONTROL_GEAR_RESTORED",
                0x02 => "MANUAL_COMMISSIONING_NEEDED",
                0x03 => "COMMISSIONING_DONE",
                0x04 => "COMMISSIONING_ERROR",
                0x05 => "NO_NEW_CONTROL_GEAR",
                0x06 => "NEW_CONTROL_GEAR_FOUND",
                0x07 => "NO_NEW_INPUT_DEVICES",
                0x08 => "NEW_INPUT_DEVICES_FOUND",
                0x09 => "ZONE_ASSIGN_ERROR",
                0x0A => "ZONE_ASSIGN_OK",
                0x0B => "ERROR_DALI_8BIT_FRAME_SEND",
                0x0C => "ERROR_DALI_16BIT_FRAME_SEND",
                0x0D => "ERROR_DALI_24BIT_FRAME_SEND",
                0x0E => "DALI_START_CG_AUTO_SCAN_FSM1",
                0x0F => "DALI_START_CG_AUTO_SCAN_FSM2",
                0x10 => "DALI_START_CG_AUTO_SCAN_FSM3",
                0x11 => "COMMISSIONING_DB_FULL",
                _ => "UNKNOWN"
            };

        public async Task<string> GetUserConfig(string nodeMac)
        {

            // Sensor
            string sensorCommand = "010B0107007C84"; //GetUserConfig
            var sensorResponse = await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);
            if (sensorResponse.Status == System.Net.HttpStatusCode.ServiceUnavailable)
                throw new BleDeviceUnreachableException(nodeMac);
            string userconfig = "";
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
userconfig = sensorResponse.Data;
            }

            return userconfig;
        }

        public async Task<bool> SetUserConfig(string nodeMac, string newuserconfig)
        {

            // Sensor
            string sensorCommand = "010D01A7009BBE"; //SetUserConfig
            var sensorResponse = await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand + newuserconfig);

            if (sensorResponse.Status == HttpStatusCode.ServiceUnavailable)
                throw new BleDeviceUnreachableException(nodeMac);

            bool resp = false;
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
resp = sensorResponse.Data == "0000" || sensorResponse.Data == "00";
                if (!resp)
                {
                    AppLog.Warn("Failed to set: " + sensorCommand + newuserconfig);
}
            }
            else
                AppLog.Warn("BLE failed: " + sensorResponse.Status.ToString() + "RAW: " + sensorResponse.Data);
return resp;
        }

        public async Task<ServiceResponse> UpgradeSensorAsync(
            string nodeMac,
            string pincode,
            bool bActor,
            bool isBootloader,
            string DetectorType,
            string FirmwareVersion,
            string logId = null,
            bool reuseExistingConnection = false)
        {
            ServiceResponse response = new ServiceResponse();
            sensorType = DetectorType;

            if (string.IsNullOrWhiteSpace(logId))
                logId = $"{nodeMac.Replace(":", "")}_{DateTime.Now:yyyyMMddHHmmss}";

            UpgradeLogger.Log(
                logId,
                nodeMac,
                isBootloader ? "Process Start Bootloader Upgrade" : "Process Start Sensor Upgrade",
                "Success",
                DetectorType
            );

            // ----------------------------
            // Local helpers (no new deps)
            // ----------------------------

            int connectMaxAttempts = Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS);
            const int loginMaxAttempts = 3;
            const int bootJumpMaxAttempts = 5;
            bool linuxNativeBackend = _connectService is LinuxBle.LinuxBleConnectionService;

            int? BootJumpDiscoverGattOverride(bool forceCassiaBootRefresh = false)
            {
                int v = RuntimeVariables.UPGRADE_CONNECT_DISCOVER_GATT_AFTER_BOOT_JUMP;
                int? discover = v < 0 ? null : (v <= 0 ? 0 : 1);
                if (forceCassiaBootRefresh && !linuxNativeBackend)
                    return 0; // 0 = fresh GATT discovery (1 = use stale cache — wrong after a boot jump)
                return discover;
            }

            async Task<bool> ConnectWithRetryAsync(string stepName, int? discoverGattOverride = null)
            {
                var connect = await ConnectOnlyWithRetryAsync(
                    maxAttempts: connectMaxAttempts,
                    delayMs: 2000,
                    stageName: stepName,
                    macAddress: nodeMac,
                    FirmwareVersion: FirmwareVersion,
                    logId: logId,
                    logSuccess: true,
                    discoverGattOverride: discoverGattOverride).ConfigureAwait(false);

                if (!connect.ok)
                    AppLog.Warn($"{stepName}: connect failed for {nodeMac} on chip {GetChipForMac(nodeMac)} with status {connect.code}. Message: {connect.msg}");

                return connect.ok;
            }

            async Task<bool> LoginWithRetryAsync()
            {
                return await EnsureLoginOnConnectedSessionUnlessBootModeAsync(
                    nodeMac,
                    pincode,
                    logId,
                    FirmwareVersion,
                    stageName: "LoggedIn",
                    maxAttempts: loginMaxAttempts,
                    preferBootOnAmbiguous: true).ConfigureAwait(false);
            }

            async Task<bool> EnsureBootModeAsync()
            {
                async Task<(bool ok, bool bootAchieved)> RecoverSessionForNextJumpAttemptAsync(int currentAttempt)
                {
                    int nextAttempt = Math.Min(bootJumpMaxAttempts, currentAttempt + 1);
                    string reconnectStage = $"Connected (jump retry recovery {nextAttempt}/{bootJumpMaxAttempts})";

                    // Cassia: disconnect the stale session first so Cassia properly re-discovers
                    // GATT on reconnect. Without this, the characteristics endpoint keeps
                    // returning cached app-mode data even though the device is in boot mode.
                    if (!linuxNativeBackend)
                    {
                        try { await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0, chip: GetChipForMac(nodeMac)).ConfigureAwait(false); }
                        catch { }
                        // Let the device fully settle before Cassia reconnects.
                        await Task.Delay(Math.Max(0, RuntimeVariables.UPGRADE_SENSOR_BOOT_PRE_RECONNECT_SETTLE_MS)).ConfigureAwait(false);
                    }

                    if (!await ConnectWithRetryAsync(reconnectStage, BootJumpDiscoverGattOverride(forceCassiaBootRefresh: true)).ConfigureAwait(false))
                    {
                        AppLog.Warn($"EnsureBootMode: recovery connect failed for {nodeMac} before jump attempt {nextAttempt}/{bootJumpMaxAttempts}.");
                        return (false, false);
                    }

                    // Cassia: wait for the fresh GATT discovery (triggered by discovergatt=1 in
                    // the connect body) to complete. Do NOT use &discovergatt=1 on the
                    // characteristics URL — that restarts the BLE scan and prevents completion.
                    if (!linuxNativeBackend)
                        await Task.Delay(Math.Max(0, RuntimeVariables.UPGRADE_SENSOR_BOOT_GATT_SETTLE_MS)).ConfigureAwait(false);

                    // Recovery connect may reveal that the device did enter boot mode after all.
                    if (CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac, preferBootOnAmbiguous: false))
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Achieved during recovery reconnect");
                        return (true, true);
                    }

                    bool loginOk = await LoginWithRetryAsync().ConfigureAwait(false);
                    if (!loginOk)
                    {
                        bool bootDetectedAfterLoginFailure = false;
                        try
                        {
                            bootDetectedAfterLoginFailure = CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac, preferBootOnAmbiguous: false);
                        }
                        catch { }

                        if (bootDetectedAfterLoginFailure)
                        {
                            UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Achieved during recovery reconnect (after login failure)");
                            return (true, true);
                        }

                        AppLog.Warn($"EnsureBootMode: recovery login failed for {nodeMac} before jump attempt {nextAttempt}/{bootJumpMaxAttempts}; continuing with next jump attempt.");
                        return (true, false);
                    }

                    return (true, false);
                }

                // Quick check first
                if (CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac))
                {
                    UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Detected");
                    return true;
                }

                // Try to jump multiple times, each time reconnect and verify
                for (int attempt = 1; attempt <= bootJumpMaxAttempts; attempt++)
                {
                    bool jumpOk = false;
                    try
                    {
                        jumpOk = await SendJumpToBootloader(_gatewayIpAddress, nodeMac, bActor);
                    }
                    catch (Exception ex)
                    {
                        UpgradeLogger.Log(logId, nodeMac, "JumpToBootloader", $"Exception (attempt {attempt}/{bootJumpMaxAttempts}): {ex.Message}");
                    }

                    if (!jumpOk)
                    {
                        UpgradeLogger.Log(logId, nodeMac, "JumpToBootloader", $"Failed (attempt {attempt}/{bootJumpMaxAttempts})");
                        if (attempt < bootJumpMaxAttempts)
                        {
                            var recovery = await RecoverSessionForNextJumpAttemptAsync(attempt).ConfigureAwait(false);
                            if (recovery.bootAchieved)
                                return true;
                            if (!recovery.ok)
                            {
                                UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", $"Recovery connect/login failed after jump failure (attempt {attempt}/{bootJumpMaxAttempts}); continuing retries");
                            }
                        }
                        await Task.Delay(3000);
                        continue;
                    }

                    UpgradeLogger.Log(logId, nodeMac, "JumpToBootloader", $"Sent (attempt {attempt}/{bootJumpMaxAttempts})");

                    // Give device time to switch to bootloader
                    int jumpDelay = 10000 + Math.Max(0, RuntimeVariables.UPGRADE_DELAY_AFTER_BOOT_JUMP_MS);
                    await Task.Delay(jumpDelay);

                    // Cassia: disconnect the stale app-mode session before reconnecting in boot
                    // mode so Cassia performs a clean GATT re-discovery rather than serving
                    // cached app-mode characteristics on the next characteristics query.
                    if (!linuxNativeBackend)
                    {
                        try { await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0, chip: GetChipForMac(nodeMac)).ConfigureAwait(false); }
                        catch (Exception ex) { AppLog.Warn($"EnsureBootMode: pre-reconnect Cassia disconnect failed for {nodeMac}: {ex.Message}"); }
                        // Give the device time to fully restart in bootloader mode before
                        // Cassia attempts a new BLE connection.
                        await Task.Delay(Math.Max(0, RuntimeVariables.UPGRADE_SENSOR_BOOT_PRE_RECONNECT_SETTLE_MS)).ConfigureAwait(false);
                    }

                    // Reconnect after jump (robust). discovergatt=1 in the CONNECT body tells
                    // Cassia to perform a fresh over-the-air GATT discovery (not cached).
                    if (!await ConnectWithRetryAsync("Connect After JumpToBoot", BootJumpDiscoverGattOverride(forceCassiaBootRefresh: true)))
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Connect After JumpToBoot", $"Failed (attempt {attempt}/{bootJumpMaxAttempts})");
                        await Task.Delay(3000);
                        continue;
                    }

                    // Cassia: wait for the GATT discovery (triggered by discovergatt=1 in the
                    // connect body) to complete before querying characteristics.
                    // DO NOT call discovergatt=1 via the characteristics URL during this wait —
                    // doing so restarts the BLE scan and prevents it from ever completing.
                    if (!linuxNativeBackend)
                        await Task.Delay(Math.Max(0, RuntimeVariables.UPGRADE_SENSOR_BOOT_GATT_SETTLE_MS)).ConfigureAwait(false);

                    // Verify boot mode with a hard bounded budget so this step does not stall.
                    int verifyBudgetMs = Math.Clamp(
                        RuntimeVariables.UPGRADE_SENSOR_BOOTMODE_VERIFY_BUDGET_MS,
                        1000,
                        linuxNativeBackend ? 10000 : 30000);
                    int verifyPollMs = Math.Max(100, RuntimeVariables.UPGRADE_SENSOR_BOOTMODE_VERIFY_POLL_MS);
                    var verifyDeadlineUtc = DateTime.UtcNow.AddMilliseconds(verifyBudgetMs);
                    int verify = 0;
                    while (true)
                    {
                        verify++;
                        bool isBoot = false;
                        try
                        {
                            // preferBootOnAmbiguous: false — do NOT trigger the secondary
                            // &discovergatt=1 URL probe.  That probe restarts Cassia's GATT
                            // scan mid-flight, causing it to never complete.  The connect
                            // body already requested a fresh discovery; just read the cache.
                            isBoot = CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac, preferBootOnAmbiguous: false);
                        }
                        catch (Exception ex)
                        {
                            UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", $"Check exception (verify {verify}): {ex.Message}");
                        }

                        if (isBoot)
                        {
                            UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Achieved");
                            AppLog.Info($"Device entered boot mode after {attempt} jump attempts.");
                            return true;
                        }

                        int remainingMs = (int)Math.Max(0, (verifyDeadlineUtc - DateTime.UtcNow).TotalMilliseconds);
                        if (remainingMs <= 0)
                            break;

                        UpgradeLogger.Log(
                            logId,
                            nodeMac,
                            "Sensor BootMode",
                            $"NotYet (verify {verify}, attempt {attempt}/{bootJumpMaxAttempts}, remaining {Math.Max(1, (int)Math.Ceiling(remainingMs / 1000.0))}s)");

                        await Task.Delay(Math.Min(verifyPollMs, remainingMs)).ConfigureAwait(false);
                    }
                    UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", $"NotYet (attempt {attempt}/{bootJumpMaxAttempts}) after {verifyBudgetMs / 1000}s verify budget");

                    // Device reconnected in Application mode — re-login before the next
                    // JumpToBootloader attempt.  The device resets its authenticated session
                    // on every disconnect, so sending JumpToBootloader on an unauthenticated
                    // connection is either rejected or causes unexpected device behaviour
                    // (crash / prolonged unresponsiveness) that makes all subsequent connect
                    // attempts fail.
                    if (attempt < bootJumpMaxAttempts)
                    {
                        // Before retrying login, do one extra boot-mode check to avoid
                        // wasting time on login attempts that cannot succeed in bootloader mode.
                        bool lateBootDetected = false;
                        try
                        {
                            lateBootDetected = CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac, preferBootOnAmbiguous: true);
                        }
                        catch { }
                        if (lateBootDetected)
                        {
                            UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Achieved (late verify before re-login)");
                            return true;
                        }

                        if (linuxNativeBackend)
                        {
                            // Linux-native: after a jump, a connected session can disappear while
                            // BlueZ still reports the previous state for a short period.
                            // Do recovery reconnect immediately instead of trying a same-session login.
                            AppLog.Info($"EnsureBootMode: linux-native recovery reconnect for {nodeMac} after verify budget expiration (attempt {attempt}/{bootJumpMaxAttempts}).");
                            var recovery = await RecoverSessionForNextJumpAttemptAsync(attempt).ConfigureAwait(false);
                            if (recovery.bootAchieved)
                                return true;
                            if (!recovery.ok)
                            {
                                UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", $"Linux-native recovery failed (attempt {attempt}/{bootJumpMaxAttempts}); continuing retries");
                            }
                        }
                        else
                        {
                        // Keep this re-login light; if it fails, recovery reconnect below
                        // performs another boot-mode check before deciding next action.
                        var reloginOk = await EnsureLoginOnConnectedSessionUnlessBootModeAsync(
                            nodeMac,
                            pincode,
                            logId,
                            FirmwareVersion,
                            stageName: "LoggedIn",
                            maxAttempts: 1,
                            preferBootOnAmbiguous: true).ConfigureAwait(false);
                        if (!reloginOk)
                        {
                            AppLog.Warn($"EnsureBootMode: re-login failed for {nodeMac} on attempt {attempt}/{bootJumpMaxAttempts}. Running recovery reconnect+login before next jump.");
                            var recovery = await RecoverSessionForNextJumpAttemptAsync(attempt).ConfigureAwait(false);
                            if (recovery.bootAchieved)
                                return true;
                            if (!recovery.ok)
                            {
                                UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", $"Recovery connect/login failed after re-login failure (attempt {attempt}/{bootJumpMaxAttempts}); continuing retries");
                            }
                        }
                        }
                    }

                    // Try again
                    await Task.Delay(3000);
                }

                try
                {
                    if (CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac, preferBootOnAmbiguous: true))
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Detected (final fallback)");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Debug($"EnsureBootMode: final fallback boot-mode check failed for {nodeMac}: {ex.Message}");
                }

                UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Failed");
                return false;
            }

            // ----------------------------
            // Step 1: Connect (robust)
            // ----------------------------
            if (!reuseExistingConnection)
            {
                if (!await ConnectWithRetryAsync("Connected"))
                {
                    response.Success = false;
                    response.StatusCode = 500;
                    response.Message = "Failed to connect to device.";
                    return response;
                }
            }
            else
            {
                UpgradeLogger.Log(logId, nodeMac, "Connected", "Skipped (reusing existing session)");
                AppLog.Info($"Skipping initial connect for {nodeMac} (reusing existing session).");
            }

            AppLog.Info($"Connected to device...{nodeMac}");
// If already in boot mode, skip login/jump and go directly to processing
            if (CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac))
            {
                AppLog.Info($"Device is already in boot mode. -> {nodeMac}");
UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Detected");
                if (linuxNativeBackend)
                {
                    await Task.Delay(3000).ConfigureAwait(false);

                    // Linux-native: keep the live bootloader session to avoid reconnect failures.
                    return await ProcessingSensorUpgrade(
                        nodeMac,
                        bActor,
                        isBootloader,
                        DetectorType,
                        FirmwareVersion,
                        logId,
                        pincode,
                        skipInitialConnect: true,
                        assumeBootMode: true);
                }

                // Cassia: force a fresh bootloader connect before programming.
                try
                {
                    AppLog.Info("Cassia boot-mode path: reconnecting before sensor programming.");
                    await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0, chip: GetChipForMac(nodeMac)).ConfigureAwait(false);
                    UpgradeLogger.Log(logId, nodeMac, "Disconnected", "Success (boot-mode reconnect preparation)", FirmwareVersion);
                }
                catch (Exception ex)
                {
                    UpgradeLogger.Log(logId, nodeMac, "Disconnected", $"Exception (boot-mode reconnect preparation): {ex.Message}", FirmwareVersion);
                }
                await Task.Delay(1000).ConfigureAwait(false);

                return await ProcessingSensorUpgrade(
                    nodeMac,
                    bActor,
                    isBootloader,
                    DetectorType,
                    FirmwareVersion,
                    logId,
                    pincode,
                    skipInitialConnect: false,
                    assumeBootMode: true);
            }

            // ----------------------------
            // Step 2: Login (robust)
            // ----------------------------
            if (!await LoginWithRetryAsync())
            {
                if (reuseExistingConnection)
                {
                    UpgradeLogger.Log(logId, nodeMac, "Login", "Failed on reused session; reconnecting");
                    if (!await ConnectWithRetryAsync("Connected (fallback after reused-session login fail)"))
                    {
                        response.Success = false;
                        response.StatusCode = 500;
                        response.Message = "Failed to connect to device.";
                        return response;
                    }
                    if (!await LoginWithRetryAsync())
                    {
                        response.Success = false;
                        response.StatusCode = 401;
                        response.Message = "Failed to login to the device.";
                        UpgradeLogger.Log(logId, nodeMac, "Login", "Failed");
                        return response;
                    }
                }
                else
                {
                response.Success = false;
                response.StatusCode = 401;
                response.Message = "Failed to login to the device.";
                UpgradeLogger.Log(logId, nodeMac, "Login", "Failed");
                return response;
                }
            }

            AppLog.Info($"Logged into device...{nodeMac}");
// ----------------------------
            // Step 3: Jump to bootloader + verify (robust)
            // ----------------------------
            if (!await EnsureBootModeAsync())
            {
                // Cassia-only: the device may have entered boot mode during the JumpToBootloader
                // attempts, but the Cassia gateway needs a fresh reconnect to re-discover GATT
                // and expose the boot characteristics. Restart the update task (reconnect) and
                // perform one final boot-mode check before giving up.
                if (!linuxNativeBackend)
                {
                    bool lateBootDetected = false;
                    try
                    {
                        AppLog.Info($"EnsureBootMode failed for {nodeMac}; disconnecting then reconnecting to detect late boot mode.");
                        // Disconnect first: Cassia only exposes the boot GATT profile after a
                        // clean disconnect → reconnect cycle.  Reconnecting without disconnecting
                        // first leaves Cassia serving stale app-mode characteristics.
                        try { await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0, chip: GetChipForMac(nodeMac)).ConfigureAwait(false); }
                        catch (Exception ex) { AppLog.Warn($"Post-EnsureBootMode disconnect exception for {nodeMac}: {ex.Message}"); }
                        // Let the device fully settle in bootloader mode before reconnecting.
                        await Task.Delay(Math.Max(0, RuntimeVariables.UPGRADE_SENSOR_BOOT_PRE_RECONNECT_SETTLE_MS)).ConfigureAwait(false);

                        if (await ConnectWithRetryAsync("Connected (post-EnsureBoot restart)", BootJumpDiscoverGattOverride(forceCassiaBootRefresh: true)).ConfigureAwait(false))
                        {
                            // Let Cassia complete the fresh GATT discovery requested by
                            // discovergatt=1 in the connect body.  Do NOT use &discovergatt=1
                            // on the characteristics URL — it restarts the BLE scan mid-flight.
                            await Task.Delay(Math.Max(0, RuntimeVariables.UPGRADE_SENSOR_BOOT_GATT_SETTLE_MS)).ConfigureAwait(false);
                            lateBootDetected = CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac, preferBootOnAmbiguous: false);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"Post-EnsureBootMode restart check exception for {nodeMac}: {ex.Message}");
                    }

                    if (lateBootDetected)
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Detected (post-EnsureBoot restart)");
                        try
                        {
                            await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0, chip: GetChipForMac(nodeMac)).ConfigureAwait(false);
                            UpgradeLogger.Log(logId, nodeMac, "Disconnected", "Success (boot-mode reconnect preparation)", FirmwareVersion);
                        }
                        catch (Exception ex)
                        {
                            UpgradeLogger.Log(logId, nodeMac, "Disconnected", $"Exception (boot-mode reconnect preparation): {ex.Message}", FirmwareVersion);
                        }
                        await Task.Delay(1000).ConfigureAwait(false);

                        return await ProcessingSensorUpgrade(
                            nodeMac,
                            bActor,
                            isBootloader,
                            DetectorType,
                            FirmwareVersion,
                            logId,
                            pincode,
                            skipInitialConnect: false,
                            assumeBootMode: true);
                    }
                }

                response.Success = false;
                response.StatusCode = 417;
                response.Message = "Failed to enter boot mode.";
                return response;
            }

            if (linuxNativeBackend)
            {
                // Linux-native: login is no longer possible/required in boot mode.
                // Keep the live session and continue straight into programming.
                UpgradeLogger.Log(logId, nodeMac, "Connected", "Session kept for sensor programming after boot mode", FirmwareVersion);
                return await ProcessingSensorUpgrade(
                    nodeMac,
                    bActor,
                    isBootloader,
                    DetectorType,
                    FirmwareVersion,
                    logId,
                    pincode,
                    skipInitialConnect: true,
                    assumeBootMode: true);
            }

            // Cassia: reconnect after boot jump so GATT is rediscovered before bootloader upload.
            try
            {
                AppLog.Info("Cassia post-jump path: disconnecting before sensor programming reconnect.");
                await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0, chip: GetChipForMac(nodeMac)).ConfigureAwait(false);
                UpgradeLogger.Log(logId, nodeMac, "Disconnected", "Success", FirmwareVersion);
            }
            catch (Exception ex)
            {
                UpgradeLogger.Log(logId, nodeMac, "Disconnected", $"Exception: {ex.Message}", FirmwareVersion);
            }

            int bootJumpDelayMs = Math.Max(0, RuntimeVariables.UPGRADE_DELAY_AFTER_BOOT_JUMP_MS);
            int postDisconnectDelay = 3000 + bootJumpDelayMs;
            await Task.Delay(postDisconnectDelay).ConfigureAwait(false);

            return await ProcessingSensorUpgrade(
                nodeMac,
                bActor,
                isBootloader,
                DetectorType,
                FirmwareVersion,
                logId,
                pincode,
                skipInitialConnect: false,
                assumeBootMode: true);
        }
        public async Task<ServiceResponse> UpgradeActorAsync(
            string nodeMac,
            string pincode,
            bool bActor,
            string DetectorType,
            string FirmwareVersion,
            string logId)
        {
            UpgradeLogger.Log(logId, nodeMac, "Process Start Actor Upgrade", "Success");
            ServiceResponse response = new();

            int connectMaxAttempts = Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS);
            const int loginMaxAttempts = 3;
            const int bootJumpMaxAttempts = 3;

            async Task<bool> ConnectWithRetryAsync(string stepName)
            {
                var connect = await ConnectOnlyWithRetryAsync(
                    maxAttempts: connectMaxAttempts,
                    delayMs: 2000,
                    stageName: stepName,
                    macAddress: nodeMac,
                    FirmwareVersion: FirmwareVersion,
                    logId: logId,
                    logSuccess: true).ConfigureAwait(false);

                if (!connect.ok)
                    AppLog.Warn($"{stepName}: connect failed for {nodeMac} on chip {GetChipForMac(nodeMac)} with status {connect.code}. Message: {connect.msg}");

                return connect.ok;
            }

            async Task<bool> LoginWithRetryAsync()
            {
                return await EnsureLoginOnConnectedSessionUnlessBootModeAsync(
                    nodeMac,
                    pincode,
                    logId,
                    FirmwareVersion,
                    stageName: "LoggedIn",
                    maxAttempts: loginMaxAttempts).ConfigureAwait(false);
            }

            async Task<bool> JumpActorToBootModeAsync()
            {
                for (int attempt = 1; attempt <= bootJumpMaxAttempts; attempt++)
                {
                    bool jumpOk = false;
                    try
                    {
                        jumpOk = await SendJumpToBootloader(_gatewayIpAddress, nodeMac, bActor);
                    }
                    catch (Exception ex)
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", $"Jump exception (attempt {attempt}/{bootJumpMaxAttempts}): {ex.Message}");
                    }

                    if (jumpOk)
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", $"JumpSent (attempt {attempt}/{bootJumpMaxAttempts})");
                        return true;
                    }

                    UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", $"JumpFailed (attempt {attempt}/{bootJumpMaxAttempts})");
                    await Task.Delay(5000);
                }

                return false;
            }

            async Task<bool> WaitForApplicationModeAsync()
            {
                int waitAttempts = Math.Max(1, RuntimeVariables.UPGRADE_ACTOR_APP_MODE_WAIT_ATTEMPTS);
                int waitDelayMs = Math.Max(0, RuntimeVariables.UPGRADE_ACTOR_APP_MODE_WAIT_DELAY_MS);

                for (int attempt = 1; attempt <= waitAttempts; attempt++)
                {
                    if (!CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac))
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", $"Cleared (attempt {attempt}/{waitAttempts})");
                        return true;
                    }

                    UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", $"Still detected; waiting {waitDelayMs}ms (attempt {attempt}/{waitAttempts})");
                    if (waitDelayMs > 0)
                        await Task.Delay(waitDelayMs).ConfigureAwait(false);
                }

                return false;
            }

            // ----------------------------
            // Step 1: Connect (robust)
            // ----------------------------
            if (!await ConnectWithRetryAsync("Connected"))
            {
                response.Success = false;
                response.StatusCode = 500;
                response.Message = "Failed to connect to device.";
                return response;
            }

            AppLog.Info($"Connected to device...{nodeMac}");
// If sensor is already in boot mode -> actor upgrade cannot proceed
            if (CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac))
            {
                UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Detected");

                bool appModeReady = await WaitForApplicationModeAsync().ConfigureAwait(false);
                if (!appModeReady)
                {
                    response.Success = false;
                    response.StatusCode = 409;
                    response.Message = "Sensor is already in boot mode. It needs to be in Application mode.";

                    UpgradeLogger.Log(logId, nodeMac, "Disconnected as sensor is in bootmode", "Info");

                    try
                    {
						await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0, chip: GetChipForMac(nodeMac));
                    }
                    catch { /* ignore */ }

                    await Task.Delay(5000);
                    return response;
                }

                if (!await ConnectWithRetryAsync("Connected (post-bootmode wait)").ConfigureAwait(false))
                {
                    response.Success = false;
                    response.StatusCode = 500;
                    response.Message = "Failed to connect to device.";
                    return response;
                }
            }

            // ----------------------------
            // Step 2: Login (robust)
            // ----------------------------
            if (!await LoginWithRetryAsync())
            {
                UpgradeLogger.Log(logId, nodeMac, "LoggedIn", "Failed on connected session; fallback Connect+Login", FirmwareVersion);
                AppLog.Warn($"Actor pre-upgrade login failed on connected session for {nodeMac}. Trying Connect+Login fallback.");

                var fallback = await ConnectAndLoginWithRetryAsync(
                    _gatewayIpAddress,
                    _gatewayPort,
                    nodeMac,
                    pincode,
                    logId,
                    FirmwareVersion,
                    maxAttempts: Math.Min(3, Math.Max(1, RuntimeVariables.UPGRADE_CONNECT_MAX_ATTEMPTS)),
                    delayBetweenAttemptsMs: 4000,
                    bootModeIsRetryable: false).ConfigureAwait(false);

                if (!fallback.Success)
                {
                    response.Success = false;
                    response.StatusCode = fallback.StatusCode == 0 ? 401 : fallback.StatusCode;
                    response.Message = string.IsNullOrWhiteSpace(fallback.Message)
                        ? "Failed to login to the device."
                        : fallback.Message;
                    return response;
                }
            }

            AppLog.Info($"Logged into device...{nodeMac}");
// ----------------------------
            // Step 3: Jump actor to bootloader (robust)
            // ----------------------------
            if (!await JumpActorToBootModeAsync())
            {
                UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", "Failed");
                response.Success = false;
                response.StatusCode = 417;
                response.Message = "Failed to enter boot mode.";
                return response;
            }

            UpgradeLogger.Log(logId, nodeMac, "Actor BootMode", "Achieved");

            // Proceed to programming step (existing flow)
            if (RuntimeVariables.UPGRADE_DELAY_AFTER_BOOT_JUMP_MS > 0)
                await Task.Delay(RuntimeVariables.UPGRADE_DELAY_AFTER_BOOT_JUMP_MS);

            return await ProcessingActorUpgrade(
                nodeMac,
                bActor,
                DetectorType,
                FirmwareVersion,
                logId,
                skipBootModeValidation: RuntimeVariables.UPGRADE_OPTIMIZE_RECONNECT_FLOW);
        }

        

        

        // Split out: ConnectLoginResult + ConnectAndLoginWithRetryAsync -> CassiaFirmwareUpgradeService.ConnectLogin.cs
        public async Task<ServiceResponse> UpgradeDeviceAsync(
            UpgradeProgress dev,
            string macAddress,
            string pincode,
            string DetectorType,
            string FirmwareVersion,
            bool upgradeActor,
            bool upgradeBootloader,
            bool upgradeSensor,
            string logId = null)
        {
            var ctx = new UpgradePipeline.DeviceUpgradeContext(
                svc: this,
                dev: dev,
                macAddress: macAddress,
                pincode: pincode,
                detectorType: DetectorType,
                firmwareVersion: FirmwareVersion,
                upgradeActor: upgradeActor,
                upgradeBootloader: upgradeBootloader,
                upgradeSensor: upgradeSensor,
                logId: logId);

            try
            {
                // preserve previous behavior by running the same stages in the same order
                var pipeline = new UpgradePipeline.DeviceUpgradePipeline();
                await pipeline.ExecuteAsync(ctx).ConfigureAwait(false);
                return ctx.Response;
            }
            catch (Exception ex)
            {
                AppLog.Error("Error during sensor and actor upgrade", ex);

                ctx.Response.Success = false;
                ctx.Response.StatusCode = 500;
                ctx.Response.Message = "An unexpected error occurred during the upgrade process.";
                UpgradeLogger.Log(logId, macAddress, $"Device Upgrade Failed: {ex.Message}", "Failed", FirmwareVersion);
                return ctx.Response;
            }
            finally
            {
                // Post-upgrade FW read is handled after all firmware work is complete (outside this attempt).

                // Skip the disconnect when PostActorStep signalled that the connection should
                // be kept open for the post-upgrade FW verification read. The outer scope
                // (ProcessSingleDeviceUpgradeAsync) will perform the final disconnect after
                // the FW read completes.
                if (!ctx.KeepConnectionOpen)
                {
                    await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, macAddress, 0, chip: ctx.ChipId).ConfigureAwait(false);
                    UpgradeLogger.Log(logId, macAddress, "Disconnected at the end of upgrade process", "Info", FirmwareVersion);
                    if (RuntimeVariables.UPGRADE_DELAY_AFTER_END_DISCONNECT_MS > 0)
                        await Task.Delay(RuntimeVariables.UPGRADE_DELAY_AFTER_END_DISCONNECT_MS).ConfigureAwait(false);
                }
            }
        }


        



        

        

        public int UpgradeDevicesInProgress = 0;


        // NOTE: command/helper methods moved to CassiaFirmwareUpgradeService.Commands.cs
        // NOTE: DeviceUpgradeSummary moved to CassiaFirmwareUpgradeService.Models.cs
    }

    /// <summary>
    /// Thrown when a BLE Get command returns result code 0x07 (NOT_AVAILABLE_IN_PROFILE),
    /// indicating the feature is permanently absent from this sensor's firmware profile.
    /// ReadOptionalAsync catches this to skip retries immediately.
    /// </summary>
    public sealed class BleFeatureNotSupportedBySensorException : Exception
    {
        public BleFeatureNotSupportedBySensorException(string feature)
            : base($"Feature '{feature}' is not available in sensor profile (NACK 0x07).") { }
    }

    /// <summary>
    /// Thrown when a BLE write returns GattCommunicationStatus.Unreachable (HTTP 503),
    /// meaning the sensor has dropped off BLE mid-session and all further reads will also fail.
    /// ReadRequiredAsync and ReadOptionalAsync re-throw this immediately without retrying.
    /// </summary>
    public sealed class BleDeviceUnreachableException : Exception
    {
        public BleDeviceUnreachableException(string nodeMac)
            : base($"BLE device '{nodeMac}' is unreachable (connection dropped mid-session).") { }
    }
}
