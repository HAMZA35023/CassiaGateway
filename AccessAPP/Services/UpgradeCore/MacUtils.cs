using System;

namespace AccessAPP.Services.UpgradeCore
{
    internal static class MacUtils
    {
        public static string NormalizeMac(string? mac)
            => (mac ?? string.Empty).Trim().ToUpperInvariant();
    }
}
