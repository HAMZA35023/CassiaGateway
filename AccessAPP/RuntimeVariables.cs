namespace AccessAPP
{
    public class RuntimeVariables
    {
        // Logging: minimum level for Serilog + fallback console. Default = Warning.
        // Accepted values (case-insensitive): verbose, debug, information|info, warning|warn, error, fatal.
        public static string LOG_MIN_LEVEL = "Warning";

        // Bootloader write pacing (ms). 0 = no extra delay between writes.
        public static int WRITE_SLEEP_MS = 1;

        // Actor BLE chunking/pacing.
        // Chunk size for actor boot packets (bytes). 0/negative = default (80).
        public static int ACTOR_CHUNK_SIZE = 80;
        // Delay between actor chunks (ms). 0 = no extra delay.
        public static int ACTOR_INTER_CHUNK_SLEEP_MS = 1;

        // If true, the app will use both BLE chips on Cassia X2000.
        // When multiple parallel upgrades are running, they will be distributed across chip 0 and chip 1.
        public static bool USE_BOTH_CASSIA_CHIPS = true;

        // Default chip to use when dual-chip is disabled (or when only one upgrade is running).
        // Valid values: 0 or 1.
        public static int DEFAULT_CASSIA_CHIP = 1;

        // Max concurrent REST/GATT requests per chip.
        // Keep at 1 for strict ordering; 2 can be faster if the gateway is stable.
        public static int CASSIA_MAX_INFLIGHT_PER_CHIP = 1;

        // Cassia connect behavior: 1 = use cached GATT when available (faster), 0 = no cache.
        public static int CASSIA_CONNECT_DISCOVER_GATT = 0;

        // Cassia discovergatt value for the connect after a boot-jump.
        // Cassia API: 0 = fresh BLE GATT discovery (correct after jump — device has new profile),
        //             1 = use cached GATT database (stale: still shows app-mode characteristics).
        // Set to -1 to fall back to CASSIA_CONNECT_DISCOVER_GATT.
        public static int UPGRADE_CONNECT_DISCOVER_GATT_AFTER_BOOT_JUMP = 0;

        // Scan for BLE devices while programming more than 1 device.
        public static bool BLE_SCAN_UNDER_PROGRAMMING = true;
        // BLE scan chip mode for /gap/nodes SSE:
        // -1 = gateway default (no chip query), 0 = chip 0, 1 = chip 1, 2 = both chips.
        public static int BLE_SCAN_CHIP_MODE = 0;
        // After scan resumes (when scanning was paused under upgrade), delay stale-device (-127) publishes.
        public static int BLE_STALE_DELAY_AFTER_SCAN_RESUME_MS = 30000;

        // Update flow settings
        // Reboot detector after upgrade (typically after actor update).
        public static bool RebootDetectorAfterUpgrade = true;
        // Restore DALI 102 database after upgrade (DALI master devices).
        public static bool Restore102DBAfterUpgrade = true;
        // Backup + restore settings across upgrade when supported.
        public static bool RestoreSettingsAfterUpgrade = true;
        // Temporarily set DALI SysFail level during update (usually to 0xFF).
        public static bool AutoSetSysFailLevelUnderUpdate = true;
        // Seconds offset applied when syncing UnixTime after a Tunable White list write.
        public static int TUNABLE_WHITE_UNIX_TIME_OFFSET_SECONDS = 0;

        // MQTT telemetry
        // Periodic status publish interval for tele/.../status (seconds).
        public static int MQTT_STATUS_HEARTBEAT_SECONDS = 10;

        // Upgrade delay tuning (runtime-only; all values in milliseconds)
        // Delay after final disconnect at the end of an upgrade attempt.
        public static int UPGRADE_DELAY_AFTER_END_DISCONNECT_MS = 0;
        // Extra delay after JumpToBootloader before next step.
        public static int UPGRADE_DELAY_AFTER_BOOT_JUMP_MS = 0;
        // Extra delay added after a failed connect (in addition to backoff).
        public static int UPGRADE_DELAY_AFTER_FAILED_CONNECT_MS = 1500;
        // Timeout per connect attempt.
        // Must exceed: pre-disconnect wait (6 s) + ConnectAsync (5 s) + Connected poll (1.5 s)
        // + LINUX_BLE_SERVICES_RESOLVED_TIMEOUT_MS (10 s) + stabilize (1.2 s) + login (8 s) = ~32 s.
        // 35 s gives a comfortable margin.
        public static int UPGRADE_CONNECT_ATTEMPT_TIMEOUT_MS = 35000;
        // Delay after connect before login (session stabilization).
        public static int UPGRADE_CONNECT_STABILIZATION_DELAY_MS = 1200;
        // Number of /gap/nodes state checks after a non-OK connect.
        public static int UPGRADE_CONNECT_GATEWAY_STATE_CHECK_ATTEMPTS = 4;
        // Delay between gateway state checks.
        public static int UPGRADE_CONNECT_GATEWAY_STATE_CHECK_DELAY_MS = 500;
        // Number of state checks after HTTP 500 connect (final check).
        public static int UPGRADE_CONNECT_GATEWAY_STATE_CHECK_ATTEMPTS_ON_500 = 2;
        // Delay between state checks after HTTP 500 connect (final check).
        public static int UPGRADE_CONNECT_GATEWAY_STATE_CHECK_DELAY_MS_ON_500 = 250;
        // Number of state checks after HTTP 500 before in-attempt retry.
        public static int UPGRADE_CONNECT_GATEWAY_STATE_CHECK_ATTEMPTS_ON_500_PRE_RETRY = 1;
        // Delay between state checks after HTTP 500 before in-attempt retry.
        public static int UPGRADE_CONNECT_GATEWAY_STATE_CHECK_DELAY_MS_ON_500_PRE_RETRY = 100;
        // Initial delay before first state check after HTTP 500 (final check).
        public static int UPGRADE_CONNECT_GATEWAY_STATE_CHECK_INITIAL_DELAY_MS_ON_500 = 1000;
        // Initial delay before first state check after HTTP 500 (pre-retry).
        public static int UPGRADE_CONNECT_GATEWAY_STATE_CHECK_INITIAL_DELAY_MS_ON_500_PRE_RETRY = 250;
        // Quick retries within a single connect attempt on transient HTTP 500.
        public static int UPGRADE_CONNECT_TRANSIENT_500_RETRIES_PER_ATTEMPT = 2;
        // Delay between transient 500 retries within a connect attempt.
        public static int UPGRADE_CONNECT_TRANSIENT_500_RETRY_DELAY_MS = 1200;
        // Skip disconnect after HTTP 500 to reduce gateway churn.
        public static bool UPGRADE_CONNECT_SKIP_DISCONNECT_ON_500 = true;
        // Serialize connect+login per chip (prevents overlap on same chip).
        public static bool UPGRADE_CONNECT_LOGIN_USE_PER_CHIP_GATE = true;
        // Backoff multiplier (x100). Example: 170 => 1.7x.
        public static int UPGRADE_CONNECT_RETRY_BACKOFF_MULTIPLIER_X100 = 200;
        // Max backoff delay between connect attempts.
        public static int UPGRADE_CONNECT_RETRY_BACKOFF_MAX_MS = 15000;
        // Random jitter applied to retry delay (percent, 0-90).
        public static int UPGRADE_CONNECT_RETRY_JITTER_PCT = 30;
        // Timeout per login attempt.
        public static int UPGRADE_LOGIN_ATTEMPT_TIMEOUT_MS = 8000;
        // Login retries on the same connected session before reconnecting.
        public static int UPGRADE_LOGIN_RETRIES_PER_CONNECTED_SESSION = 2;
        // Delay between login retries.
        public static int UPGRADE_LOGIN_RETRY_DELAY_MS = 800;
        // Small delay from "connected" to sending the login telegram.
        public static int UPGRADE_LOGIN_DELAY_AFTER_CONNECT_MS = 800;
        // Delay after login before reading firmware version.
        public static int UPGRADE_DELAY_AFTER_LOGIN_BEFORE_FW_READ_MS = 5000;
        // Precheck FW-read login timeout (connected-session path).
        public static int UPGRADE_PRECHECK_LOGIN_ATTEMPT_TIMEOUT_MS = 2000;
        // Precheck FW-read: extra settle delay before login.
        public static int UPGRADE_PRECHECK_LOGIN_SETTLE_DELAY_MS = 0;
        // Precheck FW-read: delay after login before first FW read.
        public static int UPGRADE_PRECHECK_DELAY_AFTER_LOGIN_BEFORE_FW_READ_MS = 0;
        // Delay before post-upgrade firmware verification read.
        public static int UPGRADE_POST_UPGRADE_FW_READ_DELAY_MS = 3000;
        // Firmware read attempts (per part).
        public static int UPGRADE_FW_READ_ATTEMPTS = 3;
        // Delay between firmware read attempts.
        public static int UPGRADE_FW_READ_RETRY_DELAY_MS = 1000;
        // Retries for each firmware read command.
        public static int UPGRADE_FW_COMMAND_RETRY_ATTEMPTS = 3;
        // Delay between firmware command retries.
        public static int UPGRADE_FW_COMMAND_RETRY_DELAY_MS = 800;
        // Max time to wait for DALI SysFail get/set (capped to 5s).
        public static int UPGRADE_DALI_SYSFAIL_TIMEOUT_MS = 5000;
        // Settings backup: per-field read attempts.
        public static int UPGRADE_SETTINGS_BACKUP_READ_ATTEMPTS = 3;
        // Settings backup: delay between read attempts.
        public static int UPGRADE_SETTINGS_BACKUP_READ_RETRY_DELAY_MS = 1000;
        // Settings backup: full backup retry rounds.
        public static int UPGRADE_SETTINGS_BACKUP_RETRY_ROUNDS = 2;
        // Settings backup: delay between backup rounds.
        public static int UPGRADE_SETTINGS_BACKUP_RETRY_DELAY_MS = 2000;
        // Settings restore/apply: max wait per BLE write action before the attempt is failed.
        public static int UPGRADE_SETTINGS_RESTORE_WRITE_TIMEOUT_MS = 15000;
        // Max connect attempts per step.
        public static int UPGRADE_CONNECT_MAX_ATTEMPTS = 10;
        // Precheck probe connect: tighter cap to avoid long stalls before upgrade starts.
        public static int UPGRADE_PRECHECK_PROBE_CONNECT_MAX_ATTEMPTS = 2;
        // Delay between precheck probe connect retries.
        public static int UPGRADE_PRECHECK_PROBE_CONNECT_RETRY_DELAY_MS = 500;
        // Timeout per precheck probe connect attempt.
        public static int UPGRADE_PRECHECK_PROBE_CONNECT_ATTEMPT_TIMEOUT_MS = 4500;
        // Timeout per pipeline probe connect attempt (Connected (probe) / retry).
        public static int UPGRADE_PROBE_CONNECT_ATTEMPT_TIMEOUT_MS = 10000;
        // Max Connect+Login attempts for FW-precheck reconnect fallback.
        public static int UPGRADE_PRECHECK_FW_READ_CONNECT_LOGIN_MAX_ATTEMPTS = 3;
        // Enable optimized flow that reuses sessions and reduces reconnects.
        public static bool UPGRADE_OPTIMIZE_RECONNECT_FLOW = true;
        // Trust /gap/nodes connected state to recover after connect errors.
        public static bool UPGRADE_CONNECT_TRUST_GATEWAY_CONNECTED_STATE = true;
        // After a successful upgrade, reconnect and keep the session alive while blinking blue LED (disabled by default).
        public static bool UPGRADE_POST_UPDATE_BLUE_LED_HOLD_ENABLED = false;
        // Adaptive balancer: if one worker is persistently slow, add delay to the other workers to lower gateway pressure.
        public static bool UPGRADE_WORKER_BALANCER_ENABLED = false;
        // Slow-worker threshold in % progress per minute.
        public static double UPGRADE_WORKER_BALANCER_SLOW_THRESHOLD_PCT_PER_MIN = 7.0;
        // Ignore balancer decisions until this progress level is reached.
        public static int UPGRADE_WORKER_BALANCER_MIN_PROGRESS_PCT = 3;
        // Number of consecutive low-rate samples required before a worker is marked as slow.
        public static int UPGRADE_WORKER_BALANCER_LOW_STREAK = 3;
        // Number of consecutive recovery samples required before clearing slow state.
        public static int UPGRADE_WORKER_BALANCER_RECOVERY_STREAK = 2;
        // Balancer only acts when at least this many workers are active.
        public static int UPGRADE_WORKER_BALANCER_MIN_ACTIVE_WORKERS = 2;
        // Extra delay (ms) added to non-slow workers while balancing is active.
        public static int UPGRADE_WORKER_BALANCER_RELIEF_DELAY_MS = 300;

        // End-of-upgrade reporting (best-effort, no local buffering)
        // Enable posting upgrade results to the statistics endpoint.
        public static bool UPGRADE_RESULT_DB_LOG_ENABLED = true;
        // Endpoint for upgrade result logging.
        public static string UPGRADE_RESULT_DB_LOG_URL = "https://prod.statistics.niko-test.nu/api/logentry";
        // HTTP timeout for upgrade result logging.
        public static int UPGRADE_RESULT_DB_LOG_TIMEOUT_MS = 5000;

        // LED range visualization command defaults
        // Minimum RSSI considered "in range".
        public static int LED_RANGE_MIN_RSSI = -75;
        // RSSI threshold for green indication.
        public static int LED_RANGE_GREEN_THRESHOLD = -55;
        // RSSI threshold for blue indication.
        public static int LED_RANGE_BLUE_THRESHOLD = -65;
        // Parallel LED range operations per chip.
        public static int LED_RANGE_PARALLEL_PER_CHIP = 2;
        // Max connect attempts for LED range commands.
        public static int LED_RANGE_MAX_CONNECT_ATTEMPTS = 5;

        // LED range auto-retry tuning
        // Number of auto-retry rounds for LED range.
        public static int LED_RANGE_AUTORETRY_ROUNDS = 2;
        // Delay between auto-retry rounds.
        public static int LED_RANGE_AUTORETRY_DELAY_MS = 3000;
        // Delay between connect retries in LED range.
        public static int LED_RANGE_CONNECT_RETRY_DELAY_MS = 1000;
        // Extra delay after HTTP 417 Expectation Failed.
        public static int LED_RANGE_EXPECTATIONFAILED_EXTRA_DELAY_MS = 4000;
        // Extra delay after HTTP 500 Internal Server Error.
        public static int LED_RANGE_INTERNAL_ERROR_EXTRA_DELAY_MS = 4000;

        // Boot mode check retry tuning
        // Number of retries for boot mode check.
        public static int BOOTMODE_RETRY_COUNT = 3;
        // Delay between boot mode check retries.
        public static int BOOTMODE_RETRY_DELAY_MS = 3000;
        // Linux-native boot mode check: per-attempt timeout for GATT mode detection.
        public static int LINUX_BLE_BOOTMODE_CHECK_TIMEOUT_MS = 5000;
        // Linux-native boot mode check: retries when mode is unknown or transiently unavailable.
        public static int LINUX_BLE_BOOTMODE_RETRY_COUNT = 1;
        // Linux-native boot mode check: delay between retries.
        public static int LINUX_BLE_BOOTMODE_RETRY_DELAY_MS = 250;
        // Linux-native boot mode check: fallback UUID lookup timeout when mode is Unknown.
        public static int LINUX_BLE_BOOTMODE_CHAR_LOOKUP_TIMEOUT_MS = 2500;
        // Sensor upgrade: post-jump bootmode verify total budget.
        public static int UPGRADE_SENSOR_BOOTMODE_VERIFY_BUDGET_MS = 25000;
        // Sensor upgrade: delay between post-jump bootmode verify polls.
        public static int UPGRADE_SENSOR_BOOTMODE_VERIFY_POLL_MS = 750;
        // Cassia sensor upgrade: wait after disconnect before reconnecting post-jump.
        // Gives the device time to fully restart in bootloader mode before Cassia
        // attempts a new BLE connection and GATT discovery.
        public static int UPGRADE_SENSOR_BOOT_PRE_RECONNECT_SETTLE_MS = 5000;
        // Cassia sensor upgrade: wait after connect (discovergatt=1) before querying
        // characteristics. discovergatt=1 in the connect body triggers a fresh BLE GATT
        // scan; this delay lets that scan complete without restarting it via URL probes.
        // 5 s is sufficient because the device is in boot mode within ~5 s of the jump.
        public static int UPGRADE_SENSOR_BOOT_GATT_SETTLE_MS = 5000;

        // Actor upgrade: wait for sensor to return to application mode if boot mode detected.
        public static int UPGRADE_ACTOR_APP_MODE_WAIT_ATTEMPTS = 6;
        public static int UPGRADE_ACTOR_APP_MODE_WAIT_DELAY_MS = 5000;

        // Actor boot-mode robustness
        public static int UPGRADE_ACTOR_BOOTMODE_RETRY_COUNT = 5;
        public static int UPGRADE_ACTOR_BOOTMODE_RETRY_DELAY_MS = 5000;
        public static int UPGRADE_ACTOR_BOOTMODE_CHECK_TIMEOUT_MS = 120000; // ms
        public static int UPGRADE_ACTOR_POST_BOOTMODE_DELAY_MS = 5000;
        // Actor upload retries and pacing (actor-only)
        public static int UPGRADE_ACTOR_UPLOAD_MAX_ATTEMPTS = 2;
        public static int UPGRADE_ACTOR_UPLOAD_RETRY_DELAY_MS = 3000;
        public static int UPGRADE_ACTOR_WRITE_SLEEP_MS = 1;
        // Sensor/bootloader upload retries (non-actor).
        // Transient notification/gateway hiccups are common; a local retry is cheaper than
        // restarting the entire device-upgrade attempt.
        public static int UPGRADE_SENSOR_UPLOAD_MAX_ATTEMPTS = 2;
        public static int UPGRADE_SENSOR_UPLOAD_RETRY_DELAY_MS = 2000;
        public static int UPGRADE_BOOTLOADER_UPLOAD_MAX_ATTEMPTS = 2;
        public static int UPGRADE_BOOTLOADER_UPLOAD_RETRY_DELAY_MS = 2000;
        // Read callback wait for incoming programming notification data (sensor/bootloader/actor).
        public static int UPGRADE_PROGRAMMING_NOTIFICATION_WAIT_MS = 30000;

        // BLE backend selection.
        // "auto"            = auto-detect: cassia (if GatewayConfiguration:IpAddress is set) → windows-native (Windows) → linux-native (Linux x64/ARM).
        // "cassia"          = use the Cassia gateway REST/SSE API.
        // "linux-native"    = use Linux BlueZ via D-Bus (Tmds.DBus) directly.
        // "windows-native"  = use Windows.Devices.Bluetooth WinRT APIs directly (Windows only).
        public static string BLE_BACKEND = "auto";

        // Windows native BLE: MAC address prefix filter for scanning (empty = no filter).
        public static string WINDOWS_BLE_MAC_PREFIX = "10:B9:F7";

        // Linux native BLE: HCI adapter name exposed by BlueZ (e.g. "hci0", "hci1").
        // Used as the default adapter for connections and as the fallback when LINUX_BLE_ADAPTERS is empty.
        public static string LINUX_BLE_ADAPTER = "hci0";

        // Linux native BLE: comma-separated list of HCI adapters to scan simultaneously (e.g. "hci0,hci1").
        // When non-empty this overrides LINUX_BLE_ADAPTER for scanning; LINUX_BLE_ADAPTER is still used
        // as the fallback for connection if no per-device adapter has been recorded by the scanner.
        public static string LINUX_BLE_ADAPTERS = "hci0";

        /// <summary>
        /// Returns the effective HCI adapter list for scanning.
        /// Uses LINUX_BLE_ADAPTERS (comma-separated) when set; falls back to LINUX_BLE_ADAPTER.
        /// </summary>
        public static string[] GetLinuxBleAdapterList() =>
            !string.IsNullOrWhiteSpace(LINUX_BLE_ADAPTERS)
                ? LINUX_BLE_ADAPTERS.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [LINUX_BLE_ADAPTER];

        // Linux native BLE: MAC address prefix filter for scanning (empty = no filter).
        public static string LINUX_BLE_MAC_PREFIX = "10:B9:F7";

        // Linux native BLE: how long to wait for BlueZ ServicesResolved=true after connect (ms).
        // In normal conditions GATT discovery finishes within 5-10 s.  Post-firmware-boot can
        // take longer, but the connect+login retry loop now handles that via bootModeIsRetryable.
        // 10 s is a balanced default; raise if devices in your environment are consistently slow.
        public static int LINUX_BLE_SERVICES_RESOLVED_TIMEOUT_MS = 10000;
        // Linux native BLE: additional short wait in mode detection when ServicesResolved is still false.
        // Helps avoid false "not boot mode" results right after reconnect/jump transitions.
        public static int LINUX_BLE_MODE_DETECT_SR_GRACE_MS = 1200;

        // Linux native BLE: characteristic handle used for BLE write/notify operations.
        // This is the GATT handle number (decimal) of the main control characteristic.
        public static int LINUX_BLE_CONTROL_HANDLE = 19;

        // Linux native BLE: characteristic handle for enabling sensor notifications (CCCD).
        public static int LINUX_BLE_NOTIFY_CCCD_SENSOR_HANDLE = 15;

        // Linux native BLE: characteristic handle for enabling actor notifications (CCCD).
        public static int LINUX_BLE_NOTIFY_CCCD_ACTOR_HANDLE = 16;

        // Linux native BLE: when writing, how long to wait for the characteristic handle to appear after connect (ms).
        public static int LINUX_BLE_WRITE_FIND_CHAR_TIMEOUT_MS = 1500;
        // Linux native BLE: max time to wait for command notification after a write (ms).
        public static int LINUX_BLE_DATA_NOTIFICATION_TIMEOUT_MS = 12000;
        // Linux native BLE: max time to wait for notify pipeline readiness before login write (ms).
        public static int LINUX_BLE_LOGIN_NOTIFY_READY_TIMEOUT_MS = 5000;

        // Linux native BLE: request a shorter BLE connection interval after connecting using btmgmt/hcitool.
        // Reduces per-packet write latency from ~45 ms to 15–30 ms at the cost of slightly higher radio duty cycle.
        // WARNING: some device firmware disconnects when it receives a connection parameter update request it does
        //          not expect (especially during the upgrade login phase).  Disabled by default; enable only after
        //          confirming the target firmware handles conn-update correctly.
        public static bool LINUX_BLE_ENABLE_CI_UPDATE = false;

    }
}
