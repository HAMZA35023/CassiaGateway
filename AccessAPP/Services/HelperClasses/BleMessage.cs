namespace AccessAPP.Services.HelperClasses
{
    public class BleMessage
    {
        public enum BleMsgId
        {
            Unknown = 0,
            ActorBootPacket = 1,
        }

        public BleMsgId _BleMessageType { get; set; }
        public byte[] _BleMessageDataBuffer { get; set; }
        public byte[] _BleMessageBuffer { get; private set; }

        const ushort TelegramType = 0x0014;
        private const int HEADER_SIZE = 5;
        private const int CRC_SIZE = 2;

        public bool EncodeGetBleTelegram()
        {
            if (_BleMessageType == BleMsgId.Unknown)
                return false;

            int payloadSize = _BleMessageDataBuffer?.Length ?? 0;
            int totalSize = HEADER_SIZE + CRC_SIZE + payloadSize;
            _BleMessageBuffer = new byte[totalSize];

            // Header
            _BleMessageBuffer[0] = 0x01; // Protocol Version
            _BleMessageBuffer[1] = (byte)TelegramType; // Message Type (LSB)
            _BleMessageBuffer[2] = (byte)((ushort)TelegramType >> 8); // Message Type (MSB)
            _BleMessageBuffer[3] = (byte)totalSize; // Length (LSB)
            _BleMessageBuffer[4] = (byte)(totalSize >> 8); // Length (MSB)

            // CRC for header
            ushort crc = CalcCrc16(_BleMessageBuffer, HEADER_SIZE);
            _BleMessageBuffer[5] = (byte)crc; // CRC (LSB)
            _BleMessageBuffer[6] = (byte)(crc >> 8); // CRC (MSB)

            // Payload
            if (payloadSize > 0)
                Array.Copy(_BleMessageDataBuffer, 0, _BleMessageBuffer, HEADER_SIZE + CRC_SIZE, payloadSize);

            return true;
        }

        private ushort CalcCrc16(byte[] data, int length)
        {
            ushort crc = 0x8005;
            for (int i = 0; i < length; i++)
            {
                crc ^= (ushort)(data[i] << 8);
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x8000) != 0)
                        crc = (ushort)((crc << 1) ^ 0x1021);
                    else
                        crc <<= 1;
                }
            }
            return crc;
        }
    }

}
