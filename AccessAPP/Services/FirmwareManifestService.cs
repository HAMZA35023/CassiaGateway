using AccessAPP.Models;
using Newtonsoft.Json;

namespace AccessAPP.Services
{
    public class FirmwareManifestService
    {
        private readonly ILogger<FirmwareManifestService> _logger;
        private readonly string _manifestFilePath;
        private readonly object _lockObject = new object();

        public FirmwareManifestService(ILogger<FirmwareManifestService> logger)
        {
            _logger = logger;
            _manifestFilePath = Path.Combine(Directory.GetCurrentDirectory(), "FirmwareVersions", "FirmwareManifest.json");

            // Ensure directory exists
            var directory = Path.GetDirectoryName(_manifestFilePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Create manifest file if it doesn't exist
            InitializeManifestIfNotExists();
        }

        public FirmwareManifestResponse GetFirmwareManifest()
        {
            try
            {
                lock (_lockObject)
                {
                    if (!File.Exists(_manifestFilePath))
                    {
                        return new FirmwareManifestResponse
                        {
                            Success = false,
                            Message = "Firmware manifest file not found."
                        };
                    }

                    var jsonContent = File.ReadAllText(_manifestFilePath);
                    var manifest = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(jsonContent);

                    if (manifest == null)
                    {
                        return new FirmwareManifestResponse
                        {
                            Success = false,
                            Message = "Invalid firmware manifest format."
                        };
                    }

                    return new FirmwareManifestResponse
                    {
                        Success = true,
                        Message = "Firmware manifest retrieved successfully.",
                        FirmwareManifest = manifest
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading firmware manifest");
                return new FirmwareManifestResponse
                {
                    Success = false,
                    Message = $"Error reading firmware manifest: {ex.Message}"
                };
            }
        }

        public AddFirmwareVersionResponse AddFirmwareVersion(AddFirmwareVersionRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.FirmwareVersion))
                {
                    return new AddFirmwareVersionResponse
                    {
                        Success = false,
                        Message = "Firmware version is required."
                    };
                }

                if (request.SupportedDetectorTypes == null || request.SupportedDetectorTypes.Count == 0)
                {
                    return new AddFirmwareVersionResponse
                    {
                        Success = false,
                        Message = "At least one supported detector type is required."
                    };
                }

                // Format the firmware version properly
                var formattedVersion = FormatFirmwareVersion(request.FirmwareVersion);
                
                _logger.LogInformation($"Original version: '{request.FirmwareVersion}' -> Formatted version: '{formattedVersion}'");

                lock (_lockObject)
                {
                    var manifestResponse = GetFirmwareManifest();
                    if (!manifestResponse.Success)
                    {
                        return new AddFirmwareVersionResponse
                        {
                            Success = false,
                            Message = manifestResponse.Message
                        };
                    }

                    var manifest = manifestResponse.FirmwareManifest;
                    var response = new AddFirmwareVersionResponse
                    {
                        FirmwareVersion = formattedVersion,
                        UpdatedDetectorTypes = new List<string>()
                    };

                    // Process each detector type
                    foreach (var detectorType in request.SupportedDetectorTypes)
                    {
                        if (string.IsNullOrWhiteSpace(detectorType))
                            continue;

                        // Check if detector type exists, create if not
                        if (!manifest.ContainsKey(detectorType))
                        {
                            manifest[detectorType] = new List<string>();
                        }

                        // Check if version already exists for this detector
                        if (!manifest[detectorType].Contains(formattedVersion))
                        {
                            // Add the firmware version
                            manifest[detectorType].Add(formattedVersion);

                            // Sort versions to maintain order
                            manifest[detectorType].Sort(CompareVersions);

                            response.UpdatedDetectorTypes.Add(detectorType);
                        }
                    }

                    // Save the manifest
                    var jsonContent = JsonConvert.SerializeObject(manifest, Formatting.Indented);
                    File.WriteAllText(_manifestFilePath, jsonContent);

                    response.Success = true;
                    response.Message = response.UpdatedDetectorTypes.Count > 0
                        ? $"Successfully added firmware version {formattedVersion} to {response.UpdatedDetectorTypes.Count} detector type(s)."
                        : $"Firmware version {formattedVersion} already exists in all specified detector types.";

                    _logger.LogInformation($"Added firmware version {formattedVersion} to detectors: {string.Join(", ", response.UpdatedDetectorTypes)}");

                    return response;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error adding firmware version {request.FirmwareVersion}");
                return new AddFirmwareVersionResponse
                {
                    Success = false,
                    Message = $"Error adding firmware version: {ex.Message}",
                    FirmwareVersion = request.FirmwareVersion
                };
            }
        }

        /// <summary>
        /// Formats firmware version to the standard format (v02.XX)
        /// </summary>
        /// <param name="version">Raw version string (e.g., "G237", "A238", "0237", "v02.37", "2.37")</param>
        /// <returns>Formatted version string (e.g., "v02.37", "vA2.38")</returns>
        private string FormatFirmwareVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return version;

            // Remove any existing 'v' prefix and clean up
            var cleanVersion = version.Trim().TrimStart('v', 'V');

            // Handle different input formats
            if (cleanVersion.StartsWith("G"))
            {
                // Format: G237 -> v02.37
                var numberPart = cleanVersion.Substring(1); // Remove 'G'
                if (numberPart.Length >= 3)
                {
                    // Extract major and minor version
                    var major = numberPart.Substring(0, 1);
                    var minor = numberPart.Substring(1);
                    return $"v0{major}.{minor}";
                }
            }
            else if (cleanVersion.StartsWith("A"))
            {
                // Format: A238 -> vA2.38
                var numberPart = cleanVersion.Substring(1); // Remove 'A'
                if (numberPart.Length >= 3)
                {
                    // Extract major and minor version
                    var major = numberPart.Substring(0, 1);
                    var minor = numberPart.Substring(1);
                    return $"vA{major}.{minor}";
                }
            }
            else if (cleanVersion.Length == 4 && cleanVersion.All(char.IsDigit))
            {
                // Format: 0237 -> v02.37
                var major = cleanVersion.Substring(1, 1);
                var minor = cleanVersion.Substring(2);
                return $"v0{major}.{minor}";
            }
            else if (cleanVersion.Contains("."))
            {
                // Format: 2.37 -> v02.37
                var parts = cleanVersion.Split('.');
                if (parts.Length == 2 && parts.All(p => p.All(char.IsDigit)))
                {
                    var major = parts[0].PadLeft(2, '0');
                    var minor = parts[1];
                    return $"v{major}.{minor}";
                }
                else if (parts.Length == 2)
                {
                    // Handle cases like A2.38 -> vA2.38 (already has dot)
                    return $"v{cleanVersion}";
                }
            }
            else if (cleanVersion.Length >= 3 && char.IsLetter(cleanVersion[0]) && cleanVersion.Substring(1).All(char.IsDigit))
            {
                // Handle other letter prefixes like A238, B345, etc.
                var letterPrefix = cleanVersion.Substring(0, 1);
                var numberPart = cleanVersion.Substring(1);
                if (numberPart.Length >= 3)
                {
                    var major = numberPart.Substring(0, 1);
                    var minor = numberPart.Substring(1);
                    return $"v{letterPrefix}{major}.{minor}";
                }
            }

            // If already in correct format or unknown format, add 'v' prefix if missing
            return cleanVersion.StartsWith("0") ? $"v{cleanVersion}" : $"v{cleanVersion}";
        }

        private void InitializeManifestIfNotExists()
        {
            try
            {
                if (!File.Exists(_manifestFilePath))
                {
                    var defaultManifest = new Dictionary<string, List<string>>
                    {
                        ["P48"] = new List<string> { "v02.12", "v02.14", "v02.15", "v02.18", "v02.21", "v02.27", "v02.30", "v02.32" },
                        ["P47"] = new List<string> { "v02.18", "v02.27" },
                        ["P46"] = new List<string> { "v02.16", "v02.20", "v02.25", "v02.28", "v02.31", "v02.33", "v02.35" },
                        ["V230"] = new List<string> { "v02.12", "v02.14", "v02.15", "v02.18", "v02.21", "v02.27", "v02.30", "v02.32" }
                    };

                    var jsonContent = JsonConvert.SerializeObject(defaultManifest, Formatting.Indented);
                    File.WriteAllText(_manifestFilePath, jsonContent);

                    _logger.LogInformation("Created default firmware manifest file");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing firmware manifest file");
            }
        }

        private int CompareVersions(string version1, string version2)
        {
            try
            {
                // Remove 'v' prefix if present and compare as version numbers
                var v1 = version1.TrimStart('v').Replace(".", "");
                var v2 = version2.TrimStart('v').Replace(".", "");

                if (int.TryParse(v1, out int num1) && int.TryParse(v2, out int num2))
                {
                    return num1.CompareTo(num2);
                }

                // Fallback to string comparison
                return string.Compare(version1, version2, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Compare(version1, version2, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}