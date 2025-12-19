using AccessAPP.Models;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace AccessAPP.Services
{
    public class FirmwareUploadService
    {
        private readonly ILogger<FirmwareUploadService> _logger;
        private readonly string _firmwareBasePath;

        public FirmwareUploadService(ILogger<FirmwareUploadService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _firmwareBasePath = Path.Combine(Directory.GetCurrentDirectory(), "FirmwareVersions");
            
            // Ensure FirmwareVersions directory exists
            if (!Directory.Exists(_firmwareBasePath))
            {
                Directory.CreateDirectory(_firmwareBasePath);
            }
        }

        public async Task<FirmwareUploadResponse> ProcessFirmwareUpload(IFormFile zipFile)
        {
            var response = new FirmwareUploadResponse();

            try
            {
                // Step 1: Validate the uploaded file
                var validation = ValidateUploadedFile(zipFile);
                if (!validation.IsValid)
                {
                    response.Success = false;
                    response.Message = validation.ErrorMessage;
                    return response;
                }

                // Step 2: Validate filename pattern and extract version
                var filenameValidation = ValidateFilenamePattern(zipFile.FileName);
                if (!filenameValidation.IsValid)
                {
                    response.Success = false;
                    response.Message = filenameValidation.ErrorMessage;
                    return response;
                }

                response.ExtractedVersion = filenameValidation.ExtractedVersion;

                // Step 3: Create target directory
                var targetDirectory = Path.Combine(_firmwareBasePath, response.ExtractedVersion);
                if (Directory.Exists(targetDirectory))
                {
                    // Optionally, you can choose to overwrite or return an error
                    Directory.Delete(targetDirectory, true);
                }
                Directory.CreateDirectory(targetDirectory);
                response.TargetDirectory = targetDirectory;

                // Step 4: Extract and validate ZIP contents
                using (var stream = new MemoryStream())
                {
                    await zipFile.CopyToAsync(stream);
                    stream.Position = 0;

                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                    {
                        var extractionResult = await ExtractAndValidateArchive(archive, targetDirectory);
                        if (!extractionResult.IsValid)
                        {
                            // Cleanup on failure
                            if (Directory.Exists(targetDirectory))
                            {
                                Directory.Delete(targetDirectory, true);
                            }
                            response.Success = false;
                            response.Message = extractionResult.ErrorMessage;
                            return response;
                        }

                        response.ExtractedFiles = extractionResult.ExtractedFiles;
                    }
                }

                response.Success = true;
                response.Message = $"Firmware version {response.ExtractedVersion} successfully uploaded and extracted.";
                
                _logger.LogInformation($"Successfully processed firmware upload: {zipFile.FileName} -> {targetDirectory}");
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing firmware upload: {zipFile.FileName}");
                response.Success = false;
                response.Message = $"Internal error: {ex.Message}";
                return response;
            }
        }

        private FirmwareValidationResult ValidateUploadedFile(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return new FirmwareValidationResult 
                { 
                    IsValid = false, 
                    ErrorMessage = "No file uploaded or file is empty." 
                };
            }

            if (file.Length > 50 * 1024 * 1024) // 50MB limit
            {
                return new FirmwareValidationResult 
                { 
                    IsValid = false, 
                    ErrorMessage = "File size exceeds maximum limit of 50MB." 
                };
            }

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension != ".zip")
            {
                return new FirmwareValidationResult 
                { 
                    IsValid = false, 
                    ErrorMessage = "Only ZIP files are supported." 
                };
            }

            return new FirmwareValidationResult { IsValid = true };
        }

        private FirmwareValidationResult ValidateFilenamePattern(string filename)
        {
            try
            {
                // Remove .zip extension for pattern matching
                var filenameWithoutExt = Path.GetFileNameWithoutExtension(filename);
                
                // Pattern: 353PK2A238A238A2380604A238A238A238
                // We need to:
                // 1. Check if it contains "PK" (not "PD")
                // 2. Extract the repeating version pattern
                
                if (!filenameWithoutExt.Contains("PK"))
                {
                    return new FirmwareValidationResult 
                    { 
                        IsValid = false, 
                        ErrorMessage = "Invalid firmware type. Only PK firmware packages are supported (found PD or other type)." 
                    };
                }

                if (filenameWithoutExt.Contains("PD"))
                {
                    return new FirmwareValidationResult 
                    { 
                        IsValid = false, 
                        ErrorMessage = "PD firmware type is not supported. Please upload PK firmware packages only." 
                    };
                }

                // Extract version using regex pattern
                // Looking for pattern like: 353PK2{version}{version}{version}0604{version}{version}{version}
                var match = Regex.Match(filenameWithoutExt, @"353PK\d([A-Z]\d{3})(\1)(\1)\d{4}(\1)(\1)(\1)");
                
                if (!match.Success)
                {
                    // Try alternative pattern matching for version extraction
                    var versionMatches = Regex.Matches(filenameWithoutExt, @"[A-Z]\d{3}");
                    if (versionMatches.Count >= 3)
                    {
                        var firstVersion = versionMatches[0].Value;
                        // Check if the first version appears multiple times
                        var occurrences = versionMatches.Cast<Match>().Count(m => m.Value == firstVersion);
                        
                        if (occurrences >= 3)
                        {
                            return new FirmwareValidationResult 
                            { 
                                IsValid = true, 
                                ExtractedVersion = firstVersion,
                                IsPKType = true
                            };
                        }
                    }
                    
                    return new FirmwareValidationResult 
                    { 
                        IsValid = false, 
                        ErrorMessage = $"Invalid firmware filename pattern. Expected pattern like '353PK2A238A238A2380604A238A238A238.zip' but got '{filename}'." 
                    };
                }

                var extractedVersion = match.Groups[1].Value;
                
                return new FirmwareValidationResult 
                { 
                    IsValid = true, 
                    ExtractedVersion = extractedVersion,
                    IsPKType = true
                };
            }
            catch (Exception ex)
            {
                return new FirmwareValidationResult 
                { 
                    IsValid = false, 
                    ErrorMessage = $"Error validating filename pattern: {ex.Message}" 
                };
            }
        }

        private async Task<ExtractResult> ExtractAndValidateArchive(ZipArchive archive, string targetDirectory)
        {
            var result = new ExtractResult { ExtractedFiles = new List<string>() };
            
            try
            {
                foreach (var entry in archive.Entries)
                {
                    // Skip directories
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;

                    // Validate file extension
                    var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (extension != ".cyacd")
                    {
                        result.IsValid = false;
                        result.ErrorMessage = $"Invalid file type found: {entry.Name}. Only .cyacd files are supported.";
                        return result;
                    }

                    // Extract file
                    var destinationPath = Path.Combine(targetDirectory, entry.Name);
                    
                    // Security check: prevent directory traversal
                    if (!destinationPath.StartsWith(targetDirectory))
                    {
                        result.IsValid = false;
                        result.ErrorMessage = $"Security violation: Invalid path detected for {entry.Name}";
                        return result;
                    }

                    using (var entryStream = entry.Open())
                    using (var fileStream = new FileStream(destinationPath, FileMode.Create))
                    {
                        await entryStream.CopyToAsync(fileStream);
                    }

                    result.ExtractedFiles.Add(entry.Name);
                }

                if (result.ExtractedFiles.Count == 0)
                {
                    result.IsValid = false;
                    result.ErrorMessage = "No .cyacd files found in the uploaded ZIP archive.";
                    return result;
                }

                result.IsValid = true;
                return result;
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.ErrorMessage = $"Error extracting archive: {ex.Message}";
                return result;
            }
        }

        public List<string> GetAvailableFirmwareVersions()
        {
            try
            {
                if (!Directory.Exists(_firmwareBasePath))
                    return new List<string>();

                return Directory.GetDirectories(_firmwareBasePath)
                    .Select(Path.GetFileName)
                    .OrderBy(v => v)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving firmware versions");
                return new List<string>();
            }
        }

        public async Task<bool> DeleteFirmwareVersion(string version)
        {
            try
            {
                var versionPath = Path.Combine(_firmwareBasePath, version);
                if (Directory.Exists(versionPath))
                {
                    Directory.Delete(versionPath, true);
                    _logger.LogInformation($"Deleted firmware version: {version}");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting firmware version: {version}");
                return false;
            }
        }

        private class ExtractResult
        {
            public bool IsValid { get; set; }
            public string ErrorMessage { get; set; }
            public List<string> ExtractedFiles { get; set; }
        }
    }
}