using System.Text.RegularExpressions;

namespace AccessAPP.Services.HelperClasses
{
    public static class FirmwareResolver
    {
        public static string ResolveFirmwareFile(string detectorType, string firmwareVersion, bool isActor, bool isBootloader)
        {
            string versionCode = ParseVersionToCode(firmwareVersion); // e.g., "0236"
            string basePath = $"FirmwareVersions/{versionCode}";

            if (isBootloader)
            {
                // Dynamically pick the bootloader file in the version folder
                string folderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, basePath);
                var bootloaderFile = Directory
                    .EnumerateFiles(folderPath, "353BL*.cyacd", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();

                if (bootloaderFile == null)
                    throw new FileNotFoundException($"Bootloader file not found in {folderPath}");

                return $"{basePath}/{Path.GetFileName(bootloaderFile)}";

            }

            string firmwareCode = GetFirmwareCode(detectorType, isActor);
            if (string.IsNullOrEmpty(firmwareCode))
                throw new ArgumentException("Unsupported combination of detectorType and role");

            string firmwareFile = $"353{firmwareCode}{versionCode}.cyacd";
            return $"{basePath}/{firmwareFile}";
        }

        private static string GetFirmwareCode(string detectorType, bool isActor)
        {
            detectorType = detectorType?.ToUpperInvariant();

            return (detectorType, isActor) switch
            {
                ("P48", true) => "AP2",
                ("P48", false) => "AP4",
                ("P47", true) => "AP5",
                ("P47", false) => "AP6",
                ("P46", false) or ("P49", false) => "AP3",
                ("P41", false) => "AP1",
                ("P42", false) => "AP1",
                ("PROD", false) => "AP0",
                ("PROD", true) => "AP7",
                _ => null
            };
        }

        private static string ParseVersionToCode(string version)
        {
            if (string.IsNullOrWhiteSpace(version) || !version.ToLower().StartsWith("v"))
                throw new ArgumentException("Firmware version must start with 'v'");

            var digits = version.Substring(1).Replace(".", "");
            if (digits.Length != 4)
                throw new ArgumentException("Firmware version must be in format vXX.XX");

            return digits;
        }

        public static bool ShouldUpgradeBootloader(string detectorType, string firmwareVersion, string currentFirmwareVersion)
        {
            try
            {
                // If no current firmware info, always upgrade bootloader
                if (string.IsNullOrWhiteSpace(currentFirmwareVersion))
                    return true;

                // 1. Resolve the bootloader file path for the given version
                string bootloaderPath = ResolveFirmwareFile(detectorType, firmwareVersion, isActor: false, isBootloader: true);

                // 2. Extract bootloader version from current device string and from filename
                string currentBootVer = ExtractBootVersionFromString(currentFirmwareVersion);
                string expectedBootVer = ExtractBootVersionFromFilename(bootloaderPath);

                // 3. If either is missing, default to upgrade
                if (string.IsNullOrEmpty(currentBootVer) || string.IsNullOrEmpty(expectedBootVer))
                    return true;

                // 4. Compare versions
                return currentBootVer != expectedBootVer;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FirmwareResolver fallback: {ex.Message}");
                return true; // fallback to upgrade if anything fails
            }
        }


        private static string ExtractBootVersionFromFilename(string filename)
        {
            var match = Regex.Match(filename ?? "", @"BL10(\d)(\d{2})");
            if (match.Success)
            {
                return $"{match.Groups[1].Value}.{match.Groups[2].Value}"; // e.g., "6.04"
            }
            return null;
        }


        private static string ExtractBootVersionFromString(string fwString)
        {
            var match = Regex.Match(fwString ?? "", @"Boot:\s*([0-9]+)\.([0-9]+)");
            return match.Success ? $"{match.Groups[1].Value}.{match.Groups[2].Value}" : null;
        }

    }


}
