using System.Buffers.Binary;

namespace AccessAPP.Services.HelperClasses;

/// <summary>
/// Parsed PIR peak status from BLE telegram reply 0x0247.
/// Layout (76 bytes, little-endian):
///   0..3   : uint32 TickCount
///   4..27  : 6 × float32 (AMin, AMax, BMin, BMax, CMin, CMax)
///   28..75 : 12 × uint32 (A: Low,T,High,Dur; B: Low,T,High,Dur; C: Low,T,High,Dur)
/// </summary>
public sealed record PirPeakStatusData
{
    public uint TickCount { get; init; }

    public float AMin { get; init; }
    public float AMax { get; init; }
    public float BMin { get; init; }
    public float BMax { get; init; }
    public float CMin { get; init; }
    public float CMax { get; init; }

    public float ADelta => AMax - AMin;
    public float BDelta => BMax - BMin;
    public float CDelta => CMax - CMin;

    public uint ALow { get; init; }
    public uint AT { get; init; }
    public uint AHigh { get; init; }
    public uint ADuration { get; init; }

    public uint BLow { get; init; }
    public uint BT { get; init; }
    public uint BHigh { get; init; }
    public uint BDuration { get; init; }

    public uint CLow { get; init; }
    public uint CT { get; init; }
    public uint CHigh { get; init; }
    public uint CDuration { get; init; }

    // Trigger levels from opcode 0x0240 — set by the session after a separate read
    public ushort? TrigA { get; init; }
    public ushort? TrigB { get; init; }
    public ushort? TrigC { get; init; }

    // Walktest state read from user config at session start
    public bool? WalktestActive { get; init; }

    public static PirPeakStatusData? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 76) return null;

        int o = 0;
        uint tick = BinaryPrimitives.ReadUInt32LittleEndian(data[o..(o += 4)]);

        float aMin = BitConverter.ToSingle(data[o..(o += 4)]);
        float aMax = BitConverter.ToSingle(data[o..(o += 4)]);
        float bMin = BitConverter.ToSingle(data[o..(o += 4)]);
        float bMax = BitConverter.ToSingle(data[o..(o += 4)]);
        float cMin = BitConverter.ToSingle(data[o..(o += 4)]);
        float cMax = BitConverter.ToSingle(data[o..(o += 4)]);

        uint aLow  = BinaryPrimitives.ReadUInt32LittleEndian(data[o..(o += 4)]);
        uint aT    = BinaryPrimitives.ReadUInt32LittleEndian(data[o..(o += 4)]);
        uint aHigh = BinaryPrimitives.ReadUInt32LittleEndian(data[o..(o += 4)]);
        uint aDur  = BinaryPrimitives.ReadUInt32LittleEndian(data[o..(o += 4)]);

        uint bLow  = BinaryPrimitives.ReadUInt32LittleEndian(data[o..(o += 4)]);
        uint bT    = BinaryPrimitives.ReadUInt32LittleEndian(data[o..(o += 4)]);
        uint bHigh = BinaryPrimitives.ReadUInt32LittleEndian(data[o..(o += 4)]);
        uint bDur  = BinaryPrimitives.ReadUInt32LittleEndian(data[o..(o += 4)]);

        uint cLow  = BinaryPrimitives.ReadUInt32LittleEndian(data[o..(o += 4)]);
        uint cT    = BinaryPrimitives.ReadUInt32LittleEndian(data[o..(o += 4)]);
        uint cHigh = BinaryPrimitives.ReadUInt32LittleEndian(data[o..(o += 4)]);
        uint cDur  = BinaryPrimitives.ReadUInt32LittleEndian(data[o..(o += 4)]);

        return new PirPeakStatusData
        {
            TickCount = tick,
            AMin = aMin, AMax = aMax,
            BMin = bMin, BMax = bMax,
            CMin = cMin, CMax = cMax,
            ALow = aLow, AT = aT, AHigh = aHigh, ADuration = aDur,
            BLow = bLow, BT = bT, BHigh = bHigh, BDuration = bDur,
            CLow = cLow, CT = cT, CHigh = cHigh, CDuration = cDur
        };
    }
}
