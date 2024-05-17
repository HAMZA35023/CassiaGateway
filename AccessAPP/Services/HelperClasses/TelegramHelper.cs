using AccessAPP.Models;
using AccessAPP.Services.Helper_Classes;
using AccessAPP.Services.HelperClasses;

namespace AccessAPP.Services
{
    public class TelegramHelper
    {
        public byte CreateProtocolVersion(byte protocolVersion)
        {
            if (protocolVersion == 0) throw new ArgumentException("The protocol version cannot be null");
            if (protocolVersion > 0xFF) throw new ArgumentException("The protocol version cannot be greater than 1 byte or 255 in decimal");
            return protocolVersion;
        }

        public ushort CreateTelegramType(ushort telegramType)
        {
            if (telegramType == 0) throw new ArgumentException("The telegram type cannot be null");
            if (telegramType > 0xFFFF) throw new ArgumentException("The telegram type cannot be greater than 2 bytes or 65535 in decimal");
            return telegramType;
        }

        public ushort CreateTotalLength(ushort totalLength)
        {
            if (totalLength == 0) throw new ArgumentException("The total length cannot be null");
            if (totalLength > 0xFFFF) throw new ArgumentException("The total length cannot be greater than 2 bytes or 65535 in decimal");
            return totalLength;
        }

        public ushort CreateCrc16(ushort crc16)
        {
            if (crc16 == 0) throw new ArgumentException("The CRC16 cannot be null");
            if (crc16 > 256) throw new ArgumentException("The CRC16 cannot be greater than 1 byte or 255 in decimal");
            return crc16;
        }

        public ushort CalcCrc16(byte[] message, ushort crc = 0x8005, ushort poly = 0x1021)
        {
            for (int i = 0; i < message.Length; i++)
            {
                ushort s = (ushort)(message[i] << 8);

                crc ^= s;

                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x8000) > 0)
                    {
                        crc = (ushort)(((crc << 1) ^ poly) & 0xFFFF);
                    }
                    else
                    {
                        crc <<= 1;
                    }
                }
            }

            return crc;
        }

        public string CreateTelegram(TelegramModel t)
        {
            TelegramHeader telegramHeader = new TelegramHeader(t.ProtocolVersion, t.TelegramType, t.TotalLength);
            Telegram telegram = new Telegram(telegramHeader, t.Payload);
            return telegram.getBytes().ToUpper();
        }

        public string CreateTelegramFromHexString(TelegramModel t)
        {
            TelegramHeader telegramHeader = new TelegramHeader(t.ProtocolVersion, t.TelegramType, t.TotalLength);
            string headerHex = BitConverter.ToString(telegramHeader.Bytes).Replace("-", "");
            string result = headerHex + BitConverter.ToString(t.Payload).Replace("-", "");
            return result;
        }


        public byte[] SwapBytes(ushort bytes)
        {
            return new byte[] { (byte)(bytes >> 8), (byte)(bytes & 0xFF) };
        }
    }
}
