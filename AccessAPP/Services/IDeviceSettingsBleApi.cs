using System.Threading.Tasks;

namespace AccessAPP.Services
{
    /// <summary>
    /// Abstraction over the BLE settings read/write commands used for backup/restore.
    /// Implemented by CassiaFirmwareUpgradeService (for now).
    /// </summary>
    public interface IDeviceSettingsBleApi
    {
        Task<string> GetUserConfig(string nodeMac);
        Task<bool> SetUserConfig(string nodeMac, string newUserConfigHex);

        Task<string> GetWiredPushButtonList(string nodeMac);
        Task<bool> SetWiredPushButtonList(string nodeMac, string newWiredPushButtonListHex);

        Task<string> GetDaliPushButtonList(string nodeMac);
        Task<bool> SetDaliPushButtonList(string nodeMac, string newDaliPushButtonListHex);
        Task<string> GetDaliDeviceCommonParam(string nodeMac);
        Task<bool> SetDaliDeviceCommonParam(string nodeMac, string newDaliDeviceCommonParamHex, string? currentDaliDeviceCommonParamHex = null);

        Task<string> GetBLEPushButtonList(string nodeMac);
        Task<bool> SetBLEPushButtonList(string nodeMac, string newBlePushButtonListHex);

        Task<string> GetTunableWhiteList(string nodeMac);
        Task<bool> SetTunableWhiteList(string nodeMac, string tunableWhiteListHex);
        Task<string> GetTunableWhitePreset(string nodeMac);
        Task<bool> SetTunableWhitePreset(string nodeMac, string tunableWhitePresetHex);
        Task<string> GetTunableWhiteDefaultKelvin(string nodeMac);
        Task<bool> SetTunableWhiteDefaultKelvin(string nodeMac, string tunableWhiteDefaultKelvinHex);

        // Optional, you can keep these if you want them in backup service later
        Task<bool> DaliRestore102Database(string nodeMac);
        Task<bool> DaliRestore103Database(string nodeMac);
        Task<bool> DaliSetDeviceSysFailLevelAsync(string nodeMac, byte sysFailLevel);
    }
}
