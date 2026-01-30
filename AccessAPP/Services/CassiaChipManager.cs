using System;
using System.Collections.Concurrent;

namespace AccessAPP.Services
{
    public static class CassiaChipManager
    {
        private static readonly ConcurrentDictionary<string, int> _chipByMac = new(StringComparer.OrdinalIgnoreCase);

        public static void SetChip(string mac, int chip)
        {
            if (string.IsNullOrWhiteSpace(mac)) return;
            _chipByMac[mac] = chip;
        }

        public static bool TryGetChip(string mac, out int chip)
        {
            chip = -1;
            if (string.IsNullOrWhiteSpace(mac)) return false;
            return _chipByMac.TryGetValue(mac, out chip);
        }

        public static void ReleaseChip(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return;
            _chipByMac.TryRemove(mac, out _);
        }
    }
}
