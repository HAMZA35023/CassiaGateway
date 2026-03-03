namespace AccessAPP.Models
{
    public sealed class DeviceSettingsSnapshot
    {
        public string MacAddress { get; set; } = "";
        public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;

        public string DetectorType { get; set; } = "";
        public string FirmwareVersionTarget { get; set; } = "";
        public string? UserConfigHex { get; set; }
        public string? PushButtonsHex { get; set; }
        public string? DaliPushButtonsHex { get; set; }
        public string? DaliDeviceCommonParamHex { get; set; }
        public string? BlePushButtonsHex { get; set; }
        public string? TunableWhiteListHex { get; set; }
        public string? TunableWhitePresetHex { get; set; }
        public string? TunableWhiteDefaultKelvinHex { get; set; }
    }
}
