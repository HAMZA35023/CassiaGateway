using System.Text.RegularExpressions;

namespace AccessAPP.Models
{
    public sealed class DetectorSettingsPatch
    {
        private static readonly Regex NonHexRegex = new("[^0-9A-Fa-f]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public string? UserConfigHex { get; set; }
        public string? UserConfigMaskHex { get; set; }
        public string? PushButtonsHex { get; set; }
        public string? PushButtonsMaskHex { get; set; }
        public string? DaliPushButtonsHex { get; set; }
        public string? DaliPushButtonsMaskHex { get; set; }
        public string? DaliDeviceCommonParamHex { get; set; }
        public string? DaliDeviceCommonParamMaskHex { get; set; }
        public string? BlePushButtonsHex { get; set; }
        public string? BlePushButtonsMaskHex { get; set; }
        public string? TunableWhiteListHex { get; set; }
        public string? TunableWhitePresetHex { get; set; }
        public string? TunableWhiteDefaultKelvinHex { get; set; }

        public bool HasAnyValue()
            => !string.IsNullOrWhiteSpace(UserConfigHex)
               || !string.IsNullOrWhiteSpace(PushButtonsHex)
               || !string.IsNullOrWhiteSpace(DaliPushButtonsHex)
               || !string.IsNullOrWhiteSpace(DaliDeviceCommonParamHex)
               || !string.IsNullOrWhiteSpace(BlePushButtonsHex)
               || !string.IsNullOrWhiteSpace(TunableWhiteListHex)
               || !string.IsNullOrWhiteSpace(TunableWhitePresetHex)
               || !string.IsNullOrWhiteSpace(TunableWhiteDefaultKelvinHex);

        public DetectorSettingsPatch CloneNormalized()
        {
            return new DetectorSettingsPatch
            {
                UserConfigHex = NormalizeHex(UserConfigHex),
                UserConfigMaskHex = NormalizeHex(UserConfigMaskHex),
                PushButtonsHex = NormalizeHex(PushButtonsHex),
                PushButtonsMaskHex = NormalizeHex(PushButtonsMaskHex),
                DaliPushButtonsHex = NormalizeHex(DaliPushButtonsHex),
                DaliPushButtonsMaskHex = NormalizeHex(DaliPushButtonsMaskHex),
                DaliDeviceCommonParamHex = NormalizeHex(DaliDeviceCommonParamHex),
                DaliDeviceCommonParamMaskHex = NormalizeHex(DaliDeviceCommonParamMaskHex),
                BlePushButtonsHex = NormalizeHex(BlePushButtonsHex),
                BlePushButtonsMaskHex = NormalizeHex(BlePushButtonsMaskHex),
                TunableWhiteListHex = NormalizeHex(TunableWhiteListHex),
                TunableWhitePresetHex = NormalizeHex(TunableWhitePresetHex),
                TunableWhiteDefaultKelvinHex = NormalizeHex(TunableWhiteDefaultKelvinHex)
            };
        }

        public DeviceSettingsSnapshot ToSnapshot(
            string? macAddress = null,
            string? detectorType = null,
            string? firmwareVersionTarget = null)
        {
            var normalized = CloneNormalized();
            return new DeviceSettingsSnapshot
            {
                MacAddress = macAddress ?? string.Empty,
                DetectorType = detectorType ?? string.Empty,
                FirmwareVersionTarget = firmwareVersionTarget ?? string.Empty,
                UserConfigHex = normalized.UserConfigHex,
                PushButtonsHex = normalized.PushButtonsHex,
                DaliPushButtonsHex = normalized.DaliPushButtonsHex,
                DaliDeviceCommonParamHex = normalized.DaliDeviceCommonParamHex,
                BlePushButtonsHex = normalized.BlePushButtonsHex,
                TunableWhiteListHex = normalized.TunableWhiteListHex,
                TunableWhitePresetHex = normalized.TunableWhitePresetHex,
                TunableWhiteDefaultKelvinHex = normalized.TunableWhiteDefaultKelvinHex
            };
        }

        public static DetectorSettingsPatch FromSnapshot(DeviceSettingsSnapshot? snapshot)
        {
            if (snapshot == null)
                return new DetectorSettingsPatch();

            return new DetectorSettingsPatch
            {
                UserConfigHex = NormalizeHex(snapshot.UserConfigHex),
                PushButtonsHex = NormalizeHex(snapshot.PushButtonsHex),
                DaliPushButtonsHex = NormalizeHex(snapshot.DaliPushButtonsHex),
                DaliDeviceCommonParamHex = NormalizeHex(snapshot.DaliDeviceCommonParamHex),
                BlePushButtonsHex = NormalizeHex(snapshot.BlePushButtonsHex),
                TunableWhiteListHex = NormalizeHex(snapshot.TunableWhiteListHex),
                TunableWhitePresetHex = NormalizeHex(snapshot.TunableWhitePresetHex),
                TunableWhiteDefaultKelvinHex = NormalizeHex(snapshot.TunableWhiteDefaultKelvinHex)
            };
        }

        private static string? NormalizeHex(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            var compact = NonHexRegex.Replace(input.Trim(), string.Empty).ToUpperInvariant();
            return compact.Length == 0 ? null : compact;
        }
    }
}
