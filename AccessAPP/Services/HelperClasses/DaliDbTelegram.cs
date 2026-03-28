namespace AccessAPP.Services.HelperClasses;

/// <summary>
/// Builds DALI database BLE telegrams (§6.1, opcodes 0x0500–0x0507) in little-endian
/// wire format, matching the existing hand-coded telegram constants in the codebase.
///
/// Frame layout (all multi-byte fields little-endian):
///   Byte 0      : Protocol version (0x01)
///   Bytes 1-2   : Telegram type LE
///   Bytes 3-4   : Total length LE  (= 7 + payload.Length)
///   Bytes 5-6   : CRC-16 LE
///   Bytes 7+    : Payload
/// </summary>
public static class DaliDbTelegram
{
    // ─── Telegram type constants ──────────────────────────────────────────────
    public const ushort TypeEnable           = 0x0500;
    public const ushort TypeEnableResponse   = 0x0501;
    public const ushort TypeDisable          = 0x0502;
    public const ushort TypeDisableResponse  = 0x0503;
    public const ushort TypeGetParams        = 0x0504;
    public const ushort TypeGetParamsResponse= 0x0505;
    public const ushort TypeSetParams        = 0x0506;
    public const ushort TypeSetParamsResponse= 0x0507;

    // ─── Status codes in responses (byte 7 of raw hex) ───────────────────────
    public const byte StatusAck           = 0x00;
    public const byte StatusNackRange     = 0x01;
    public const byte StatusNackNotAllowed= 0x08;

    // ─── DbType enum values ───────────────────────────────────────────────────
    public const byte DbType102AppHeader       = 0;
    public const byte DbType102AppDeviceTypes  = 1;
    public const byte DbType102AppDeviceGroups = 2;
    public const byte DbType102AppDeviceScenes = 3;
    public const byte DbType102AppDeviceCommon = 4;
    public const byte DbType103AppHeader       = 5;
    public const byte DbType103AppInstanceTypes= 6;
    public const byte DbType102DeviceHeader    = 7;
    public const byte DbType102DeviceGeneral   = 8;
    public const byte DbType102DeviceScenes    = 9;
    public const byte DbType102DeviceDt208     = 10;
    public const byte DbType103DeviceHeader    = 11;
    public const byte DbType103DeviceGeneral   = 12;
    public const byte DbType103InstanceData0   = 13;
    public const byte DbType103InstanceData1   = 14;
    public const byte DbType103InstanceData2   = 15;
    public const byte DbType103InstanceData3   = 16;
    public const byte DbType103InstanceData4   = 17;
    public const byte DbType103InstanceData5   = 18;
    public const byte DbType103InstanceData6   = 19;

    /// <summary>Expected DataLen (in request payload) for each dbType that has a fixed response size.</summary>
    private static readonly Dictionary<byte, byte> KnownReadDataLen = new()
    {
        [DbType102AppHeader]        = 2,
        [DbType102AppDeviceTypes]   = 3,   // includes ShortAddress byte
        [DbType102AppDeviceGroups]  = 3,
        [DbType102AppDeviceScenes]  = 17,
        [DbType102AppDeviceCommon]  = 9,
        [DbType103AppHeader]        = 2,
        [DbType103AppInstanceTypes] = 9,   // includes ShortAddress byte
        [DbType102DeviceHeader]     = 1,
        [DbType102DeviceGeneral]    = 16,
        [DbType102DeviceScenes]     = 16,
        [DbType102DeviceDt208]      = 6,
        [DbType103DeviceHeader]     = 1,
        [DbType103DeviceGeneral]    = 13,
        // 13-19 differ per family; use max (18) so the device returns whatever it has
        [DbType103InstanceData0]    = 18,
        [DbType103InstanceData1]    = 18,
        [DbType103InstanceData2]    = 18,
        [DbType103InstanceData3]    = 18,
        [DbType103InstanceData4]    = 18,
        [DbType103InstanceData5]    = 18,
        [DbType103InstanceData6]    = 18,
    };

    // ─── Per-address dbTypes (ShortAddress appended to payload) ──────────────
    private static readonly HashSet<byte> PerAddressTypes = new()
    {
        DbType102AppDeviceTypes,
        DbType102AppDeviceGroups,
        DbType102AppDeviceScenes,
        DbType103AppInstanceTypes,
    };

    public static bool IsPerAddressType(byte dbType) => PerAddressTypes.Contains(dbType);

    // ─── Telegram builders ────────────────────────────────────────────────────

    /// <summary>0x0500 DaliRestoreDatabaseEnable (no payload)</summary>
    public static string BuildEnable()  => Build(TypeEnable);

    /// <summary>0x0502 DaliRestoreDatabaseDisable (no payload)</summary>
    public static string BuildDisable() => Build(TypeDisable);

    /// <summary>
    /// 0x0504 DaliGetDatabaseParameters.
    /// <paramref name="shortAddress"/> is only included for per-address types (1,2,3,6).
    /// </summary>
    public static string BuildGet(byte dbType, byte shortAddress = 0)
    {
        var dataLen = KnownReadDataLen.TryGetValue(dbType, out var dl) ? dl : (byte)18;
        return IsPerAddressType(dbType)
            ? Build(TypeGetParams, new byte[] { dbType, dataLen, shortAddress })
            : Build(TypeGetParams, new byte[] { dbType, dataLen });
    }

    /// <summary>
    /// 0x0506 DaliSetDatabaseParameters.
    /// <paramref name="data"/> is everything AFTER dbType+DataLen (the actual payload bytes as per spec).
    /// </summary>
    public static string BuildSet(byte dbType, byte[] data)
        => Build(TypeSetParams, BuildSetPayload(dbType, data));

    private static byte[] BuildSetPayload(byte dbType, byte[] data)
    {
        var payload = new byte[2 + data.Length];
        payload[0] = dbType;
        payload[1] = (byte)data.Length;
        data.CopyTo(payload, 2);
        return payload;
    }

    // ─── Response parsers ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the telegram type from a raw hex notification string.
    /// Type is at hex chars 2-5 (bytes 1-2), stored little-endian.
    /// </summary>
    public static ushort ParseType(string rawHex)
    {
        if (rawHex.Length < 6) return 0;
        return (ushort)(HexByte(rawHex, 2) | (HexByte(rawHex, 3) << 8));
    }

    /// <summary>
    /// Parses the 0x0505 GetParametersResponse.
    /// Returns (status, dataLen, data bytes) or (0xFF, 0, []) on parse error.
    /// </summary>
    public static (byte Status, byte DataLen, byte[] Data) ParseGetResponse(string rawHex)
    {
        // Min length: 7 bytes header + 1 status + 1 DataLen = 9 bytes = 18 hex chars
        if (rawHex.Length < 18) return (0xFF, 0, Array.Empty<byte>());

        byte status  = HexByte(rawHex, 7);
        byte dataLen = HexByte(rawHex, 8);

        int dataStart = 18; // byte 9 → hex char 18
        int expectedHexLen = dataStart + dataLen * 2;
        if (rawHex.Length < expectedHexLen)
            return (0xFF, 0, Array.Empty<byte>());

        var data = new byte[dataLen];
        for (int i = 0; i < dataLen; i++)
            data[i] = HexByte(rawHex, 9 + i);

        return (status, dataLen, data);
    }

    /// <summary>
    /// Parses the 0x0501/0x0503/0x0507 simple-status response.
    /// Returns the status byte (0x00 = ACK) or 0xFF on parse error.
    /// </summary>
    public static byte ParseStatusResponse(string rawHex)
    {
        if (rawHex.Length < 16) return 0xFF;
        return HexByte(rawHex, 7);
    }

    // ─── Core builder ────────────────────────────────────────────────────────

    /// <summary>Builds a complete little-endian telegram hex string.</summary>
    private static string Build(ushort type, byte[]? payload = null)
    {
        payload ??= Array.Empty<byte>();
        int totalLen = 7 + payload.Length;

        Span<byte> header5 = stackalloc byte[5];
        header5[0] = 0x01;
        header5[1] = (byte)(type & 0xFF);
        header5[2] = (byte)(type >> 8);
        header5[3] = (byte)(totalLen & 0xFF);
        header5[4] = (byte)(totalLen >> 8);

        ushort crc = CalcCrc16(header5);

        var frame = new byte[7 + payload.Length];
        header5.CopyTo(frame);
        frame[5] = (byte)(crc & 0xFF);
        frame[6] = (byte)(crc >> 8);
        payload.CopyTo(frame, 7);

        return Convert.ToHexString(frame);
    }

    /// <summary>
    /// CRC-16 matching existing constants (init=0x8005, poly=0x1021).
    /// </summary>
    private static ushort CalcCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0x8005;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0
                    ? (ushort)(((crc << 1) ^ 0x1021) & 0xFFFF)
                    : (ushort)(crc << 1);
        }
        return crc;
    }

    private static byte HexByte(string s, int byteIndex)
        => Convert.ToByte(s.Substring(byteIndex * 2, 2), 16);
}
