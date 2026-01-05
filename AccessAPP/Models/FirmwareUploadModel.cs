namespace AccessAPP.Models
{
    public class FirmwareUploadRequest
    {
        public IFormFile ZipFile { get; set; }
    }

    public class FirmwareUploadResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string ExtractedVersion { get; set; }
        public string TargetDirectory { get; set; }
        public List<string> ExtractedFiles { get; set; } = new List<string>();
    }

    public class FirmwareValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        public string ExtractedVersion { get; set; }
        public bool IsPKType { get; set; }
    }
}