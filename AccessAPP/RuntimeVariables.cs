namespace AccessAPP
{
    public class RuntimeVariables
    {

        // Bootloader write pacing (ms). 0 = no extra delay between writes.
        public static int WRITE_SLEEP_MS = 0;

        // Actor BLE chunking/pacing.
        // Chunk size for actor boot packets (bytes). 0/negative = default (80).
        public static int ACTOR_CHUNK_SIZE = 80;
        // Delay between actor chunks (ms). 0 = no extra delay.
        public static int ACTOR_INTER_CHUNK_SLEEP_MS = 10;

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

        // After a boot-jump, cached GATT can be stale. Use 0 to force rediscovery on next connect.
        // Set to -1 to fall back to CASSIA_CONNECT_DISCOVER_GATT.
        public static int UPGRADE_CONNECT_DISCOVER_GATT_AFTER_BOOT_JUMP = 0;

        // Scan for BLE devices while programming more than 1 device.
        public static bool BLE_SCAN_UNDER_PROGRAMMING = true;

        // Update flow settings
        // Reboot detector after upgrade (typically after actor update).
        public static bool RebootDetectorAfterUpgrade = true;
        // Restore DALI 102 database after upgrade (DALI master devices).
        public static bool Restore102DBAfterUpgrade = true;
        // Backup + restore settings across upgrade when supported.
        public static bool RestoreSettingsAfterUpgrade = true;
        // Temporarily set DALI SysFail level during update (usually to 0xFF).
        public static bool AutoSetSysFailLevelUnderUpdate = true;

        // Upgrade delay tuning (runtime-only; all values in milliseconds)
        // Delay after final disconnect at the end of an upgrade attempt.
        public static int UPGRADE_DELAY_AFTER_END_DISCONNECT_MS = 0;
        // Extra delay after JumpToBootloader before next step.
        public static int UPGRADE_DELAY_AFTER_BOOT_JUMP_MS = 0;
        // Extra delay added after a failed connect (in addition to backoff).
        public static int UPGRADE_DELAY_AFTER_FAILED_CONNECT_MS = 500;
        // Timeout per connect attempt.
        public static int UPGRADE_CONNECT_ATTEMPT_TIMEOUT_MS = 12000;
        // Delay after connect before login (session stabilization).
        public static int UPGRADE_CONNECT_STABILIZATION_DELAY_MS = 500;
        // Number of /gap/nodes state checks after a non-OK connect.
        public static int UPGRADE_CONNECT_GATEWAY_STATE_CHECK_ATTEMPTS = 2;
        // Delay between gateway state checks.
        public static int UPGRADE_CONNECT_GATEWAY_STATE_CHECK_DELAY_MS = 250;
        // Number of state checks after HTTP 500 connect (final check).
        public static int UPGRADE_CONNECT_GATEWAY_STATE_CHECK_ATTEMPTS_ON_500 = 1;
        // Delay between state checks after HTTP 500 connect (final check).
        public static int UPGRADE_CONNECT_GATEWAY_STATE_CHECK_DELAY_MS_ON_500 = 250;
        // Number of state checks after HTTP 500 before in-attempt retry.
        public static int UPGRADE_CONNECT_GATEWAY_STATE_CHECK_ATTEMPTS_ON_500_PRE_RETRY = 1;
        // Delay between state checks after HTTP 500 before in-attempt retry.
        public static int UPGRADE_CONNECT_GATEWAY_STATE_CHECK_DELAY_MS_ON_500_PRE_RETRY = 100;
        // Initial delay before first state check after HTTP 500 (final check).
        public static int UPGRADE_CONNECT_GATEWAY_STATE_CHECK_INITIAL_DELAY_MS_ON_500 = 500;
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
        public static int UPGRADE_CONNECT_RETRY_BACKOFF_MULTIPLIER_X100 = 170;
        // Max backoff delay between connect attempts.
        public static int UPGRADE_CONNECT_RETRY_BACKOFF_MAX_MS = 15000;
        // Random jitter applied to retry delay (percent, 0-90).
        public static int UPGRADE_CONNECT_RETRY_JITTER_PCT = 30;
        // Timeout per login attempt.
        public static int UPGRADE_LOGIN_ATTEMPT_TIMEOUT_MS = 8000;
        // Login retries on the same connected session before reconnecting.
        public static int UPGRADE_LOGIN_RETRIES_PER_CONNECTED_SESSION = 2;
        // Delay between login retries.
        public static int UPGRADE_LOGIN_RETRY_DELAY_MS = 400;
        // Small delay from "connected" to sending the login telegram.
        public static int UPGRADE_LOGIN_DELAY_AFTER_CONNECT_MS = 300;
        // Delay after login before reading firmware version.
        public static int UPGRADE_DELAY_AFTER_LOGIN_BEFORE_FW_READ_MS = 3000;
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
        // Max connect attempts per step.
        public static int UPGRADE_CONNECT_MAX_ATTEMPTS = 10;
        // Enable optimized flow that reuses sessions and reduces reconnects.
        public static bool UPGRADE_OPTIMIZE_RECONNECT_FLOW = true;
        // Trust /gap/nodes connected state to recover after connect errors.
        public static bool UPGRADE_CONNECT_TRUST_GATEWAY_CONNECTED_STATE = true;

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

        
    }
}
