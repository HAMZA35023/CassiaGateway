namespace AccessAPP.Services.HelperClasses
{
    public static class FirmwareResolver
    {
        public static string ResolveFirmwareFile(string detectorType, string firmwareVersion, bool isActor, bool isBootloader)
        {
            if (isBootloader)
                return "FirmwareVersions/353BL10604.cyacd";

            string firmwareCode = GetFirmwareCode(detectorType, isActor);

            if (string.IsNullOrEmpty(firmwareCode))
                throw new ArgumentException("Unsupported combination of detectorType and role");

            string versionFragment = ParseVersionToCode(firmwareVersion);

            return $"FirmwareVersions/353{firmwareCode}{versionFragment}.cyacd";
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
                ("230V", false) => "AP1",  // Sec/1CH/2CH
                ("PROD", false) => "AP0",
                ("PROD", true) => "AP7",
                _ => null
            };
        }

        private static string ParseVersionToCode(string version)
        {
            // Expects format like "v02.27" or "V02.18"
            if (string.IsNullOrWhiteSpace(version) || !version.ToLower().StartsWith("v"))
                throw new ArgumentException("Firmware version must start with 'v'");

            var digits = version.Substring(1).Replace(".", "");
            if (digits.Length != 4)
                throw new ArgumentException("Firmware version must be in format vXX.XX");

            return digits;
        }
    }

}
