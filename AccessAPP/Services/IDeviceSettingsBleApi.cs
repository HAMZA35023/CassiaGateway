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

        Task<string> GetBLEPushButtonList(string nodeMac);
        Task<bool> SetBLEPushButtonList(string nodeMac, string newBlePushButtonListHex);

        // Optional, you can keep these if you want them in backup service later
        Task<bool> DaliRestore102Database(string nodeMac);
        Task<bool> DaliRestore103Database(string nodeMac);
    }
}
