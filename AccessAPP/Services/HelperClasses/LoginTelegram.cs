using AccessAPP.Models;

namespace AccessAPP.Services.HelperClasses
{
    public class LoginTelegram
    {
        private readonly TelegramHelper telegramHelper = new TelegramHelper();

        public byte ProtocolVersion { get; private set; }
        public ushort TelegramType { get; private set; }
        public byte TotalLength { get; private set; }
        public byte[] Payload { get; private set; }
        TelegramModel telegramModel = new();

        public LoginTelegram()
        {
            telegramModel.ProtocolVersion = telegramHelper.CreateProtocolVersion(0x01);
            telegramModel.TelegramType = telegramHelper.CreateTelegramType(0x1000);
            telegramModel.TotalLength = telegramHelper.CreateTotalLength(0x0900);
            telegramModel.Payload = new byte[] { 0x01, 0x1D};
        }

    public string Create()
        {
            return telegramHelper.CreateTelegram(telegramModel);
        }
    }

}
