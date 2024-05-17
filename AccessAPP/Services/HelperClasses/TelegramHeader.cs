namespace AccessAPP.Services.Helper_Classes
{
    public class TelegramHeader
    {
        private byte _protocolVersion;
        private ushort _telegramType;
        private ushort _totalLength;
        private ushort _CRC16;
        public byte[] Bytes { get; private set; }

        public TelegramHeader(byte protocolVersion, ushort telegramType, ushort totalLength)
        {
            _protocolVersion = protocolVersion;
            _telegramType = telegramType;
            _totalLength = totalLength;

            // Convert ushort to little-endian byte array
            byte[] telegramTypeBytes = BitConverter.GetBytes(_telegramType);
            byte[] totalLengthBytes = BitConverter.GetBytes(_totalLength);

            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(telegramTypeBytes);
                Array.Reverse(totalLengthBytes);
            }

            // Calculate CRC16
            byte[] message = new byte[] { _protocolVersion };
            byte[] messageWithTelegramTypeAndLength = new byte[message.Length + telegramTypeBytes.Length + totalLengthBytes.Length];
            Buffer.BlockCopy(message, 0, messageWithTelegramTypeAndLength, 0, message.Length);
            Buffer.BlockCopy(telegramTypeBytes, 0, messageWithTelegramTypeAndLength, message.Length, telegramTypeBytes.Length);
            Buffer.BlockCopy(totalLengthBytes, 0, messageWithTelegramTypeAndLength, message.Length + telegramTypeBytes.Length, totalLengthBytes.Length);
            byte[] crc16Bytes = BitConverter.GetBytes(CalculateCrc(messageWithTelegramTypeAndLength));

            // Create header byte array
            Bytes = new byte[7];
            Bytes[0] = _protocolVersion;
            Buffer.BlockCopy(telegramTypeBytes, 0, Bytes, 1, 2);
            Buffer.BlockCopy(totalLengthBytes, 0, Bytes, 3, 2);
            Buffer.BlockCopy(crc16Bytes, 0, Bytes, 5, 2);
        }

        private ushort CalculateCrc(byte[] message, ushort crc = 0x8005, ushort poly = 0x1021)
        {
            foreach (byte b in message)
            {
                ushort s = (ushort)(b << 8);
                crc ^= s;

                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x8000) != 0)
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
    }
}
