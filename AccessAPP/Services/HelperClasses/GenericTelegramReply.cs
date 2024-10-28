namespace AccessAPP.Services.HelperClasses
{
    public class GenericTelegramReply
    {
        public string ProtocolVersion { get; }
        public string TelegramType { get; }
        public string TotalLength { get; }
        public string Crc16 { get; }
        public string DataResult { get; }

        public GenericTelegramReply(string value)
        {
            if (value.Length < 14)
            {
                throw new ArgumentException("Invalid telegram response length.");
            }

            ProtocolVersion = value.Substring(0, 2);
            TelegramType = value.Substring(2, 4);
            TotalLength = value.Substring(6, 4);
            Crc16 = value.Substring(10, 4);
            DataResult = value.Length > 14 ? value.Substring(14) : string.Empty;
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

        public T GetDataResult<T>(Func<string, T> parseFunction)
        {
            return parseFunction(DataResult);
        }

        public override string ToString()
        {
            return $"ProtocolVersion: {ProtocolVersion}, TelegramType: {TelegramType}, TotalLength: {TotalLength}, Crc16: {Crc16}, DataResult: {DataResult}";
        }
    }

    public class DataInformationView
    {
        public string BleSpecMsg { get; set; }
        public string Msg { get; set; }
        public bool Ack { get; set; }
    }
}
