using AccessAPP.Models;
using System.Globalization;
using System.Text;

namespace AccessAPP.Services.HelperClasses
{
    public static class ScanDataParser
    {
        public static string HexToAscii(string hex)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < hex.Length; i += 2)
            {
                string part = hex.Substring(i, 2);
                if (int.TryParse(part, NumberStyles.HexNumber, null, out int val) && val != 0)
                {
                    sb.Append((char)val);
                }
            }
            return sb.ToString();
        }

        public static string ExtractProductNumber(string scanData)
        {
            if (string.IsNullOrEmpty(scanData) || scanData.Length < 56)
                return "Unknown";

            string rawProductHex = scanData.Substring(20, 30); // Bytes 10 to 25
            string ascii = HexToAscii(rawProductHex);

            if (ascii.Contains("353-"))
                return ascii.Split('\0')[0];
            else
            {
                string byteHex = scanData.Substring(20, 2); // fallback: index-based
                if (byte.TryParse(byteHex, NumberStyles.HexNumber, null, out byte index))
                {
                    if (DetectorMetaData.NumberToMetadata.TryGetValue(index, out var meta))
                        return meta.Name;
                }
            }

            return "Unknown";
        }

        public static string GetName(string rawProductHex)
        {
            string asciiProductNumber = HexToAscii(rawProductHex);

            if (asciiProductNumber.Contains("353-"))
            {
                return asciiProductNumber.Split('\0')[0];
            }
            else
            {
                return asciiProductNumber.Length > 1
                    ? asciiProductNumber.Substring(1).Split('\0')[0]
                    : string.Empty;
            }
        }

        public static DetectorMeta GetDetectorMeta(string scanData)
        {
            string productNumber = ExtractProductNumber(scanData);
            return DetectorMetaData.ProductNumberToMetadata.ContainsKey(productNumber)
                ? DetectorMetaData.ProductNumberToMetadata[productNumber]
                : new DetectorMeta { Name = productNumber, DetectorType = "Unknown" };
        }

        public static string GetLockedInfo(string adData)
        {
            if (string.IsNullOrWhiteSpace(adData) || adData.Length < 54)
                return null;

            return adData.Substring(52, 2); // byte at index 52
        }

        public static bool? IsLocked(string scanData)
        {
            var lockedHex = GetLockedInfo(scanData);
            return lockedHex != null && lockedHex.ToUpper() == "01";
        }

        public static string ParseSoftwareVersionFromResponse(string hexResponse)
        {
            if (string.IsNullOrWhiteSpace(hexResponse) || hexResponse.Length < 10)
                return "Invalid or empty response";

            try
            {
                hexResponse = hexResponse.Replace(" ", "").Replace(":", "");

                byte[] bytes = Enumerable.Range(0, hexResponse.Length / 2)
                    .Select(i => Convert.ToByte(hexResponse.Substring(i * 2, 2), 16))
                    .ToArray();

                int startIndex = Array.FindIndex(bytes, b => b >= 32 && b < 127);
                if (startIndex == -1)
                    return "No printable ASCII in response";

                string version = Encoding.ASCII.GetString(bytes, startIndex, bytes.Length - startIndex);
                return version.TrimEnd('\0');
            }
            catch (Exception ex)
            {
                return $"Parse error: {ex.Message}";
            }
        }
    }


    public class DetectorMeta
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string DetectorFamily { get; set; }
        public string DetectorType { get; set; }
        public string DetectorOutputInfo { get; set; }
        public string DetectorDescription { get; set; }
        public string DetectorShortDescription { get; set; }
        public int Range { get; set; }
        public string DetectorMountDescription { get; set; }
    }
}
