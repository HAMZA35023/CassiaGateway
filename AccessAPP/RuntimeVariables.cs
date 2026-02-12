namespace AccessAPP
{
    public class RuntimeVariables
    {

        //Bootloader sleeptimer.
        public static int WRITE_SLEEP_MS = 0;

        // If true, the app will use both BLE chips on Cassia X2000.
        // When 2 parallel upgrades are running, they will be distributed across chip 0 and chip 1.
        public static bool USE_BOTH_CASSIA_CHIPS = true;

        // Default chip to use when dual-chip is disabled (or when only one upgrade is running).
        public static int DEFAULT_CASSIA_CHIP = 1;

        // Max concurrent REST requests per chip (keep at 1 for strict ordering; 2 can be faster if stable).
        public static int CASSIA_MAX_INFLIGHT_PER_CHIP = 1;

        // Scan for BLE devices while programming more than 1 device
        public static bool BLE_SCAN_UNDER_PROGRAMMING = true;

        // Updateflow settings
        public static bool RebootDetectorAfterUpgrade = true;
        public static bool Restore102DBAfterUpgrade = true;
        public static bool RestoreSettingsAfterUpgrade = true;
        public static bool AutoSetSysFailLevelUnderUpdate = true;

        // Upgrade delay tuning (runtime-only)
        public static int UPGRADE_DELAY_AFTER_END_DISCONNECT_MS = 0;
        public static int UPGRADE_DELAY_AFTER_BOOT_JUMP_MS = 0;
        public static int UPGRADE_DELAY_AFTER_FAILED_CONNECT_MS = 1000;
        public static int UPGRADE_DELAY_AFTER_LOGIN_BEFORE_FW_READ_MS = 3000;
        public static int UPGRADE_CONNECT_MAX_ATTEMPTS = 10;
        public static bool UPGRADE_OPTIMIZE_RECONNECT_FLOW = true;

        // LED range visualization command defaults
        public static int LED_RANGE_MIN_RSSI = -75;
        public static int LED_RANGE_GREEN_THRESHOLD = -55;
        public static int LED_RANGE_BLUE_THRESHOLD = -65;
        public static int LED_RANGE_PARALLEL_PER_CHIP = 2;
        public static int LED_RANGE_MAX_CONNECT_ATTEMPTS = 5;

        // LED range auto-retry tuning
        public static int LED_RANGE_AUTORETRY_ROUNDS = 2;
        public static int LED_RANGE_AUTORETRY_DELAY_MS = 3000;
        public static int LED_RANGE_CONNECT_RETRY_DELAY_MS = 1000;
        public static int LED_RANGE_EXPECTATIONFAILED_EXTRA_DELAY_MS = 4000;
        public static int LED_RANGE_INTERNAL_ERROR_EXTRA_DELAY_MS = 4000;

        // Boot mode check retry tuning
        public static int BOOTMODE_RETRY_COUNT = 3;
        public static int BOOTMODE_RETRY_DELAY_MS = 3000;

        
    }
}
