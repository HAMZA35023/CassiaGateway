using AccessAPP.Services.Helper_Classes;
using System;
using System.Linq;

namespace AccessAPP.Services.HelperClasses
{
    public class Telegram
    {
        private TelegramHeader _header;
        private byte[] _payload;
        private byte[] _bytes;

        public Telegram(TelegramHeader header, byte[] payload)
        {
            _header = header;
            _payload = payload;
            _bytes = _header.Bytes.Concat(payload.Reverse()).ToArray();
        }

        public static string decimalToHex(int d)
        {
            string h = d.ToString("X");
            return h.Length % 2 == 0 ? h : "0" + h;
        }

        public string getBytes()
        {
            string msg = "";
            foreach (byte b in _bytes)
            {
                msg += decimalToHex(b);
            }
            return msg;
        }
    }
}
