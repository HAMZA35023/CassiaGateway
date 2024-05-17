namespace AccessAPP.Models
{
    public class TelegramModel
    {
        public byte ProtocolVersion { get; set; }
        public ushort TelegramType { get; set; }
        public ushort TotalLength { get; set; }
        public byte[] Payload { get; set; }
        // public ushort Crc16 { get; set; }
    }

}
