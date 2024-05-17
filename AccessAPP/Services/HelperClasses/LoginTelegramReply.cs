using AccessAPP.Models;

namespace AccessAPP.Services.HelperClasses
{
    public class LoginTelegramReply
    {
        public string ProtocolVersion { get; }
        public string TelegramType { get; }
        public string TotalLength { get; }
        public string Crc16 { get; }
        public string LoginResult { get; }

        private static readonly Dictionary<int, LoginInformationView> LoginResultMap = new Dictionary<int, LoginInformationView>
    {
        { 0, new LoginInformationView { BleSpecMsg = "LOGIN NACK", Msg = "Login failed", Ack = false, PincodeRequired = false } },
        { 1, new LoginInformationView { BleSpecMsg = "LOGIN ACK. No pincode required", Msg = "Login succeeded", Ack = true, PincodeRequired = false } },
        { 2, new LoginInformationView { BleSpecMsg = "LOGIN ACK. Pincode Required", Msg = "A pincode is required to be able to login to the detector", Ack = true, PincodeRequired = true } },
        { 3, new LoginInformationView { BleSpecMsg = "Login.Ack. Open Period Active. No Pincode Required.", Msg = "Open period is currently active", Ack = false, PincodeRequired = false } }
    };

        public LoginTelegramReply(string value)
        {
            ProtocolVersion = value.Substring(0, 2);
            TelegramType = value.Substring(2, 4);
            TotalLength = value.Substring(6, 4);
            Crc16 = value.Substring(10, 4);
            LoginResult = value.Substring(14, 2);
        }

        private int SwapBytes(string bytes)
        {
            return (int)((bytes[0] << 8) | bytes[1]);
        }

        public int GetProtocolVersion()
        {
            return SwapBytes(ProtocolVersion);
        }

        public int GetTelegramType()
        {
            return SwapBytes(TelegramType);
        }

        public int GetTotalLength()
        {
            return SwapBytes(TotalLength);
        }

        public int GetCrc16()
        {
            return SwapBytes(Crc16);
        }

        public LoginInformationView GetResult()
        {
            int key = Convert.ToInt32(LoginResult, 16);
            return LoginResultMap.ContainsKey(key) ? LoginResultMap[key] : null;
        }

        public bool IsAck(string value)
        {
            string telegramType = value.Substring(2, 4);
            string result = value.Substring(14, 2);

            if (telegramType == "1100")
            {
                return result == "01";
            }

            return false;
        }
    }


}
