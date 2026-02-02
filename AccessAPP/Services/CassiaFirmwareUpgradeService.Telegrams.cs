using System;
using System.Linq;
using AccessAPP.Logging;
using AccessAPP.Services.HelperClasses;

namespace AccessAPP.Services
{
    public partial class CassiaFirmwareUpgradeService
    {
        public static UInt64 MacToInt64(string macAddress)
                {
                    string hex = macAddress.Replace(":", "");
                    return Convert.ToUInt64(hex, 16);
                }

        public static string MacToString(UInt64 macAddress)
                {
                    return string.Join(":",
                                        BitConverter.GetBytes(macAddress).Reverse()
                                        .Select(b => b.ToString("X2"))).Substring(6);
                }

        Bootloader_Utils.CyBtldr_ProgressUpdate Upd = new Bootloader_Utils.CyBtldr_ProgressUpdate(CassiaFirmwareUpgradeService.ProgressUpdate);
    }
}
