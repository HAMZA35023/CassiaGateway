using AccessAPP.Models;
using AccessAPP.Services.Helper_Classes;
using System.Text;
using System.Text.RegularExpressions;

namespace AccessAPP.Services.HelperClasses
{
    public class CheckPincodeTelegram
    {
        private TelegramHelper telegramHelper = new TelegramHelper();

        public byte ProtocolVersion { get; private set; }
        public ushort TelegramType { get; private set; }
        public ushort TotalLength { get; private set; }
        public string Payload { get; private set; }

        TelegramModel telegramModel = new();

        public CheckPincodeTelegram(string payload)
        {
            telegramModel.ProtocolVersion = telegramHelper.CreateProtocolVersion(0x01);
            telegramModel.TelegramType = telegramHelper.CreateTelegramType(0x0131);
            telegramModel.TotalLength = telegramHelper.CreateTotalLength(0x0009);
            Payload = payload;
        }

        public string Create()
        {
            string hexPayload = string.Concat(Encoding.ASCII.GetBytes(Payload)
               .Select(b => b.ToString("X2")));

            // Pad with leading zeros and reverse byte pairs
            hexPayload = hexPayload.PadLeft(4, '0');
            hexPayload = string.Concat(hexPayload.Reverse().Select((c, i) => i % 2 == 0 ? hexPayload.Substring(i, 2) : ""));
            telegramModel.Payload = StringToByteArray(hexPayload);
            return telegramHelper.CreateTelegramFromHexString(telegramModel);
        }

        private string ReverseHex(string hex)
        {
            char[] hexArray = hex.ToCharArray();
            Array.Reverse(hexArray);
            return new string(hexArray);
        }

        private byte[] StringToByteArray(string hex)
        {
            hex = Regex.Replace(hex, @"\s+", "");
            hex = ReverseHex(hex);
            int length = hex.Length / 2;
            byte[] bytes = new byte[length];
            for (int i = 0; i < length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
    }

}
