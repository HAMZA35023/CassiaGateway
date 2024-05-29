using AccessAPP.Models;

namespace AccessAPP.Services.HelperClasses
{
    public class CheckPincodeTelegram
    {
        private readonly TelegramHelper telegramHelper = new TelegramHelper();

        public byte ProtocolVersion { get; private set; }
        public ushort TelegramType { get; private set; }
        public ushort TotalLength { get; private set; }
        public string Payload { get; private set; }

        private readonly TelegramModel telegramModel = new();

        public CheckPincodeTelegram(string payload)
        {
            telegramModel.ProtocolVersion = telegramHelper.CreateProtocolVersion(0x01);
            telegramModel.TelegramType = telegramHelper.CreateTelegramType(0x3101);
            telegramModel.TotalLength = telegramHelper.CreateTotalLength(0x0900);
            Payload = payload;
        }

        public string Create()
        {
            // Convert the payload string to an integer
            int payloadInt = int.Parse(Payload);

            // Convert the integer to a hexadecimal string, padded to 4 characters
            string hexPayload = payloadInt.ToString("X4");

            // Convert the reversed hexadecimal string to a byte array
            telegramModel.Payload = StringToByteArray(hexPayload);

            // Create the telegram string
            return ReplaceSubstring(telegramHelper.CreateTelegram(telegramModel), "CC21", "E331");
        }


        private byte[] StringToByteArray(string hex)
        {
            int length = hex.Length / 2;
            byte[] bytes = new byte[length];
            for (int i = 0; i < length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
        static string ReplaceSubstring(string original, string toReplace, string replacement)
        {
            return original.Replace(toReplace, replacement);
        }
    }
}
