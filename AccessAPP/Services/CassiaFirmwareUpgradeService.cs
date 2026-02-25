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
	        int? discoverGattOverride = null)
	        => ConnectOnlyWithRetryAsync(maxAttempts, delayMs, stageName, macAddress, firmwareVersion, logId ?? "", logSuccess, discoverGattOverride);

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
            int maxAttempts = 3)
        {
            if (CheckIfDeviceInBootMode(_gatewayIpAddress, macAddress))
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

                    if (statusOk && pinOk)
                    {
                        AppLog.Debug($"{stageName}: login success for {macAddress} on attempt {attempt}/{attempts}.");
                        UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Success (attempt {attempt}/{attempts})", firmwareVersion ?? "");
                        return true;
                    }

                    AppLog.Debug($"{stageName}: login failed for {macAddress} on attempt {attempt}/{attempts}. Status={statusText}, pinRequired={pinReq}, pinAccepted={loginResult.ResponseBody.PinCodeAccepted}.");
                    UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Failed (attempt {attempt}/{attempts}) - Status={statusText}", firmwareVersion ?? "");
                }
                catch (OperationCanceledException)
                {
                    AppLog.Debug($"{stageName}: login timeout for {macAddress} on attempt {attempt}/{attempts}.");
                    UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Timeout (attempt {attempt}/{attempts}, {loginTimeoutMs / 1000}s)", firmwareVersion ?? "");
                }
                catch (Exception ex)
                {
                    AppLog.Debug($"{stageName}: login exception for {macAddress} on attempt {attempt}/{attempts}: {ex.Message}");
                    UpgradeLogger.Log(logId ?? "", macAddress, stageName, $"Exception (attempt {attempt}/{attempts}): {ex.Message}", firmwareVersion ?? "");
                }

                if (attempt < attempts)
                    await Task.Delay(retryDelayMs).ConfigureAwait(false);
            }

            // Final safety: boot mode can flip after a jump and the first check may be stale.
            // If we are actually in boot mode, treat login as "not required".
            try
            {
                if (CheckIfDeviceInBootMode(_gatewayIpAddress, macAddress))
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
        private const string DaliSetDeviceFadeTimePrefix = "01150409004FC6";
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
                    AppLog.Warn($"[DALI] CommonParam set failed: MAC={nodeMac}, Label={label}, Status={sensorResponse.Status}, RAW={sensorResponse.Data}, Cmd={cmd}");
                    return false;
                }

                var reply = NormalizeHex(sensorResponse.Data);
                var ok = reply == "00" || reply == "0000";
                if (!ok)
                    AppLog.Warn($"[DALI] CommonParam set rejected: MAC={nodeMac}, Label={label}, Reply={reply}, Cmd={cmd}");

                return ok;
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
                configured = 5000;

            return Math.Min(5000, configured);
        }

        private async Task<DataResponseModel?> GetDataWithSysFailTimeoutAsync(
            string nodeMac,
            string cmd,
            string label)
        {
            int timeoutMs = GetSysFailTimeoutMs();
            var task = _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, nodeMac, cmd);
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

        public async Task<string> GetWiredPushButtonList(string nodeMac)
        {
            string sensorCommand = "0113010700181A"; // GetWiredPushButtonList
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

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
        public async Task<bool> DaliRestore102Database(string nodeMac)
        {
            string sensorCommand = "013C0407004812"; // DaliRestore102Database
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

            bool resp = false;
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
resp = true;
            }

            return resp;
        }

        public async Task<bool> DaliRestore103Database(string nodeMac)
        {
            string sensorCommand = "013D040700FC64"; // DaliRestore103Database
            var sensorResponse = await _connectService.GetDataFromBleDevice(
                _gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);

            bool resp = false;
            if (sensorResponse.Status.ToString() == "OK" && !string.IsNullOrEmpty(sensorResponse.Data))
            {
                AppLog.Debug(sensorResponse.Data);
resp = true;
            }

            return resp;
        }

        public async Task<string> GetUserConfig(string nodeMac)
        {

            // Sensor
            string sensorCommand = "010B0107007C84"; //GetUserConfig 
            var sensorResponse = await _connectService.GetDataFromBleDevice(_gatewayIpAddress, _gatewayPort, nodeMac, sensorCommand);
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

            int? BootJumpDiscoverGattOverride()
            {
                int v = RuntimeVariables.UPGRADE_CONNECT_DISCOVER_GATT_AFTER_BOOT_JUMP;
                if (v < 0) return null;
                return v <= 0 ? 0 : 1;
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
                    maxAttempts: loginMaxAttempts).ConfigureAwait(false);
            }

            async Task<bool> EnsureBootModeAsync()
            {
                async Task<(bool ok, bool bootAchieved)> RecoverSessionForNextJumpAttemptAsync(int currentAttempt)
                {
                    int nextAttempt = Math.Min(bootJumpMaxAttempts, currentAttempt + 1);
                    string reconnectStage = $"Connected (jump retry recovery {nextAttempt}/{bootJumpMaxAttempts})";

                    if (!await ConnectWithRetryAsync(reconnectStage, BootJumpDiscoverGattOverride()).ConfigureAwait(false))
                    {
                        AppLog.Warn($"EnsureBootMode: recovery connect failed for {nodeMac} before jump attempt {nextAttempt}/{bootJumpMaxAttempts}.");
                        return (false, false);
                    }

                    // Recovery connect may reveal that the device did enter boot mode after all.
                    if (CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac))
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Achieved during recovery reconnect");
                        return (true, true);
                    }

                    bool loginOk = await LoginWithRetryAsync().ConfigureAwait(false);
                    if (!loginOk)
                    {
                        AppLog.Warn($"EnsureBootMode: recovery login failed for {nodeMac} before jump attempt {nextAttempt}/{bootJumpMaxAttempts}.");
                        return (false, false);
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
                                UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", $"Abort retries after jump failure (attempt {attempt}/{bootJumpMaxAttempts}); recovery connect/login failed");
                                return false;
                            }
                        }
                        await Task.Delay(3000);
                        continue;
                    }

                    UpgradeLogger.Log(logId, nodeMac, "JumpToBootloader", $"Sent (attempt {attempt}/{bootJumpMaxAttempts})");

                    // Give device time to switch to bootloader
                    int jumpDelay = 10000 + Math.Max(0, RuntimeVariables.UPGRADE_DELAY_AFTER_BOOT_JUMP_MS);
                    await Task.Delay(jumpDelay);

                    // Reconnect after jump (robust)
                    if (!await ConnectWithRetryAsync("Connect After JumpToBoot", BootJumpDiscoverGattOverride()))
                    {
                        UpgradeLogger.Log(logId, nodeMac, "Connect After JumpToBoot", $"Failed (attempt {attempt}/{bootJumpMaxAttempts})");
                        await Task.Delay(3000);
                        continue;
                    }

                    // Verify boot mode (sometimes needs multiple checks)
                    for (int verify = 1; verify <= 5; verify++)
                    {
                        bool isBoot = false;
                        try
                        {
                            isBoot = CheckIfDeviceInBootMode(_gatewayIpAddress, nodeMac);
                        }
                        catch (Exception ex)
                        {
                            UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", $"Check exception (verify {verify}/5): {ex.Message}");
                        }

                        if (isBoot)
                        {
                            UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", "Achieved");
                            AppLog.Info($"Device entered boot mode after {attempt} jump attempts.");
return true;
                        }

                        UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", $"NotYet (verify {verify}/5, attempt {attempt}/{bootJumpMaxAttempts})");
                        await Task.Delay(1500);
                    }

                    // Device reconnected in Application mode — re-login before the next
                    // JumpToBootloader attempt.  The device resets its authenticated session
                    // on every disconnect, so sending JumpToBootloader on an unauthenticated
                    // connection is either rejected or causes unexpected device behaviour
                    // (crash / prolonged unresponsiveness) that makes all subsequent connect
                    // attempts fail.
                    if (attempt < bootJumpMaxAttempts)
                    {
                        var reloginOk = await LoginWithRetryAsync().ConfigureAwait(false);
                        if (!reloginOk)
                        {
                            AppLog.Warn($"EnsureBootMode: re-login failed for {nodeMac} on attempt {attempt}/{bootJumpMaxAttempts}. Running recovery reconnect+login before next jump.");
                            var recovery = await RecoverSessionForNextJumpAttemptAsync(attempt).ConfigureAwait(false);
                            if (recovery.bootAchieved)
                                return true;
                            if (!recovery.ok)
                            {
                                UpgradeLogger.Log(logId, nodeMac, "Sensor BootMode", $"Abort retries after re-login failure (attempt {attempt}/{bootJumpMaxAttempts}); recovery connect/login failed");
                                return false;
                            }
                        }
                    }

                    // Try again
                    await Task.Delay(3000);
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
                await Task.Delay(3000);

                // Device is already connected by the caller above — skip the redundant reconnect
                // inside ProcessingSensorUpgrade. Reconnecting would disconnect the working
                // BlueZ link and then fail to re-establish it (bootloader device not immediately
                // ready for a new connection attempt).
                return await ProcessingSensorUpgrade(nodeMac, bActor, isBootloader, DetectorType, FirmwareVersion, logId, pincode, skipInitialConnect: true);
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
                response.Success = false;
                response.StatusCode = 417;
                response.Message = "Failed to enter boot mode.";
                return response;
            }

            // ----------------------------
            // Disconnect and prepare for upgrade process
            // ----------------------------
            try
            {
                AppLog.Info("device disconnected and will reconnect after 3s");
await _connectService.DisconnectFromBleDevice(_gatewayIpAddress, nodeMac, 0, chip: GetChipForMac(nodeMac));
                UpgradeLogger.Log(logId, nodeMac, "Disconnected", "Success");
            }
            catch (Exception ex)
            {
                UpgradeLogger.Log(logId, nodeMac, "Disconnected", $"Exception: {ex.Message}");
                // continue anyway (device might already have dropped)
            }

            int bootJumpDelayMs = RuntimeVariables.UPGRADE_DELAY_AFTER_BOOT_JUMP_MS;
            int postDisconnectDelay = 3000 + Math.Max(0, bootJumpDelayMs);
            await Task.Delay(postDisconnectDelay);

            // Now do the actual programming flow
            return await ProcessingSensorUpgrade(nodeMac, bActor, isBootloader, DetectorType, FirmwareVersion, logId, pincode);
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
                response.Success = false;
                response.StatusCode = 401;
                response.Message = "Failed to login to the device.";
                return response;
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
}
