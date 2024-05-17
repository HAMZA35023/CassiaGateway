using System;
using System.Collections.Generic;

namespace AccessAPP.Services.HelperClasses
{
    public class CheckPincodeReply
    {
        public string ProtocolVersion { get; }
        public string TelegramType { get; }
        public string TotalLength { get; }
        public string Crc16 { get; }
        public string Result { get; }

        private static readonly Dictionary<int, CheckPincodeResult> PincodeResultMap = new Dictionary<int, CheckPincodeResult>
        {
            { 0, new CheckPincodeResult { BleSpecMsg = "OK", Msg = "Pincode is accepted", Ack = true } },
            { 1, new CheckPincodeResult { BleSpecMsg = "Check pincode failed", Msg = "Pincode is not accepted", Ack = false } }
        };

        public CheckPincodeReply(string value)
        {
            ProtocolVersion = value.Substring(0, 2);
            TelegramType = value.Substring(2, 4);
            TotalLength = value.Substring(6, 4);
            Crc16 = value.Substring(10, 4);
            Result = value.Substring(14, 2);
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

        public CheckPincodeResult GetResult()
        {
            int key = Convert.ToInt32(Result, 16);
            return PincodeResultMap.ContainsKey(key) ? PincodeResultMap[key] : null;
        }
    }

    public class CheckPincodeResult
    {
        public string BleSpecMsg { get; set; }
        public string Msg { get; set; }
        public bool Ack { get; set; }
    }
}
