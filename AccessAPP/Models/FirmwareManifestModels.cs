namespace AccessAPP.Models
{
    public class AddFirmwareVersionRequest
    {
        public string FirmwareVersion { get; set; } = string.Empty;
        public List<string> SupportedDetectorTypes { get; set; } = new List<string>();
    }

    public class AddFirmwareVersionResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string FirmwareVersion { get; set; } = string.Empty;
        public List<string> UpdatedDetectorTypes { get; set; } = new List<string>();
    }

    public class FirmwareManifestResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public Dictionary<string, List<string>> FirmwareManifest { get; set; } = new Dictionary<string, List<string>>();
    }
}