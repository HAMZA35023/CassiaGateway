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
        public static int UPGRADE_DELAY_AFTER_FAILED_CONNECT_MS = 0;

        
    }
}
