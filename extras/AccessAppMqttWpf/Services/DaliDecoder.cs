using AccessAppMqttWpf.Models;

namespace AccessAppMqttWpf.Services;

/// <summary>
/// Stateful DALI V2 frame decoder.  One instance per logging session so that
/// sequence-gap detection and query-status tracking are per-device.
/// </summary>
public sealed class DaliDecoder
{
    // ── Session state ─────────────────────────────────────────────────────────
    private int? _lastSeq;
    private bool _lastWasQueryStatus;
    private DateTimeOffset? _lastQueryStatusTime;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes a single 5-byte DALI log entry produced by the device firmware.
    /// Returns <c>null</c> for type-0 (no data) entries.
    /// </summary>
    /// <param name="data">Exactly 5 bytes: [type, seq, log0, log1, log2].</param>
    /// <param name="timestamp">UTC time to stamp the entry with.</param>
    public DaliLogEntry? Decode(ReadOnlySpan<byte> data, DateTimeOffset timestamp)
    {
        if (data.Length < 5) return null;

        int  type = data[0];
        byte seq  = data[1];
        byte b0   = data[2];
        byte b1   = data[3];
        byte b2   = data[4];

        // ── Sequence-gap detection ────────────────────────────────────────────
        int? missingEntries = null;
        if (_lastSeq.HasValue)
        {
            int expected = (_lastSeq.Value + 1) & 0xFF;
            if (seq != expected)
                missingEntries = (seq - expected + 256) % 256;
        }
        _lastSeq = seq;

        return type switch
        {
            0 => null,                                    // no data — skip
            1 => DecodeBackward(seq, b0, timestamp, missingEntries),
            2 => DecodeForward16(seq, b0, b1, timestamp, missingEntries),
            3 => DecodeForward24(seq, b0, b1, b2, timestamp, missingEntries),
            _ => MakeEntry(seq, "", "Info", null, null, null, $"Unknown type {type}", null, null, timestamp, missingEntries)
        };
    }

    // ── Type 1: 8-bit backward frame (gear response) ─────────────────────────

    private DaliLogEntry DecodeBackward(byte seq, byte b0, DateTimeOffset ts, int? missing)
    {
        string rawHex = $"{b0:X2}";
        string desc;

        if (_lastWasQueryStatus &&
            _lastQueryStatusTime.HasValue &&
            (ts - _lastQueryStatusTime.Value).TotalMilliseconds < 1000)
        {
            desc = DecodeStatusRegister(b0);
            _lastWasQueryStatus = false;
        }
        else
        {
            desc = b0 == 0xFF ? "YES (0xFF)"
                 : b0 == 0x00 ? "NO / 0 (0x00)"
                 : $"0x{b0:X2} ({b0})";
            desc = $"Gear Response: {desc}";
        }

        return MakeEntry(seq, rawHex, "BW", null, null, null, desc, rawHex, b0, ts, missing);
    }

    // ── Type 2: 16-bit forward frame ─────────────────────────────────────────

    private DaliLogEntry DecodeForward16(byte seq, byte addrByte, byte dataByte, DateTimeOffset ts, int? missing)
    {
        string rawHex = $"{addrByte:X2}{dataByte:X2}";
        bool   sBit   = (addrByte & 0x01) == 1;

        // Determine addressing
        string? addrType;
        int?    addr;
        bool    isSpecial = false;

        if (addrByte == 0xFF || addrByte == 0xFE)
        {
            addrType = "Broadcast"; addr = null;
        }
        else if ((addrByte & 0x80) == 0x00)
        {
            addrType = "Device"; addr = (addrByte >> 1) & 0x3F;
        }
        else if ((addrByte & 0xE0) == 0x80)
        {
            addrType = "Group"; addr = (addrByte >> 1) & 0x0F;
        }
        else if (addrByte >= 0xA1 && addrByte <= 0xC9)
        {
            addrType = "Special"; addr = null; isSpecial = true;
        }
        else
        {
            addrType = null; addr = addrByte >> 1;
        }

        // Track QUERY STATUS to decode the following backward frame
        if (sBit && dataByte == 0x90) // QUERY STATUS
        {
            _lastWasQueryStatus = true;
            _lastQueryStatusTime = ts;
        }

        string desc;
        string? valHex;
        int?    valDec;

        if (isSpecial)
        {
            valHex = $"{dataByte:X2}"; valDec = dataByte;
            desc   = SpecialCommandName(addrByte);
        }
        else if (!sBit)
        {
            // Direct Arc Power Control (DAPC)
            valHex = $"{dataByte:X2}"; valDec = dataByte;
            desc = dataByte == 0x00 ? "DAPC: OFF (fade to MIN then off)"
                 : dataByte == 0xFE ? "DAPC: 100% (MAX level)"
                 : $"DAPC: {DapcToPercent(dataByte):F1}% (0x{dataByte:X2})";
        }
        else
        {
            valHex = $"{dataByte:X2}"; valDec = dataByte;
            desc   = ForwardCommandName(dataByte);
        }

        return MakeEntry(seq, rawHex, "DALI", addrType, addr, null, desc, valHex, valDec, ts, missing);
    }

    // ── Type 3: 24-bit extended frame (DALI-2 event or instance command) ─────

    private DaliLogEntry DecodeForward24(byte seq, byte b0, byte b1, byte b2, DateTimeOffset ts, int? missing)
    {
        string rawHex = $"{b0:X2}{b1:X2}{b2:X2}";

        bool devicePresent     = (b0 & 0x80) == 0;
        bool instanceTypeInB1  = (b1 & 0x80) == 0;

        string? addrType;
        int?    addr;
        int?    instanceTypeNum;

        if (!devicePresent)
        {
            // Scheme 0: no short address, instance type in b0
            instanceTypeNum = (b0 & 0x7E) >> 1;
            int instanceNo  = (b1 & 0x7C) >> 2;
            addrType = "Instance"; addr = instanceNo;
        }
        else if (instanceTypeInB1)
        {
            // Scheme 1: device short address + instance type
            instanceTypeNum = (b1 & 0x7C) >> 2;
            addrType = "Device";   addr = (b0 & 0x7E) >> 1;
        }
        else
        {
            // Scheme 2: device short address + instance number (fixed vs V1)
            instanceTypeNum = null;
            addrType = "Device";   addr = (b0 & 0x7E) >> 1;
        }

        string? instTypeName = InstanceTypeName(instanceTypeNum);

        // Classify: extended command or event?
        byte opcode = b2;
        if (IsInstanceCommand(opcode))
        {
            string desc = InstanceCommandName(opcode);
            // Infer instance type from opcode when not in frame
            if (instanceTypeNum == null && devicePresent && !instanceTypeInB1)
                instTypeName = InferInstanceType(opcode);

            return MakeEntry(seq, rawHex, "DALI-2", addrType, addr, instTypeName, desc,
                             $"{opcode:X2}", opcode, ts, missing);
        }
        else
        {
            // Event frame
            int eventInfo = ((b1 & 0x03) << 8) | b2;
            string desc   = DecodeEvent(instanceTypeNum, eventInfo);

            return MakeEntry(seq, rawHex, "DALI-2", addrType, addr, instTypeName, desc,
                             $"{eventInfo:X3}", eventInfo, ts, missing);
        }
    }

    // ── Entry factory ─────────────────────────────────────────────────────────

    private static DaliLogEntry MakeEntry(
        byte seq, string rawHex, string signalType,
        string? addrType, int? addr, string? instType,
        string desc, string? valHex, int? valDec,
        DateTimeOffset ts, int? missing)
    {
        var parts = new List<string>(6);
        if (!string.IsNullOrEmpty(rawHex))   parts.Add($"Raw: {rawHex}");
        if (!string.IsNullOrEmpty(addrType)) parts.Add($"Addr: {addrType}{(addr.HasValue ? $" {addr}" : "")}");
        if (!string.IsNullOrEmpty(instType)) parts.Add($"Type: {instType}");
        if (!string.IsNullOrEmpty(desc))     parts.Add(desc);
        if (!string.IsNullOrEmpty(valHex))   parts.Add($"Data: 0x{valHex}");

        return new DaliLogEntry
        {
            Seq             = seq,
            Timestamp       = ts,
            RawHex          = rawHex,
            SignalType      = signalType,
            AddressType     = addrType,
            Address         = addr,
            InstanceType    = instType,
            Description     = desc,
            ValueHex        = valHex,
            ValueDec        = valDec,
            FullTranslation = string.Join(" | ", parts),
            MissingEntries  = missing,
        };
    }

    // ── DAPC percentage ───────────────────────────────────────────────────────

    private static double DapcToPercent(int n)
        => Math.Round(Math.Pow(10, (n - 1) / (253.0 / 3.0) - 1), 1);

    // ── Status register decode ────────────────────────────────────────────────

    private static string DecodeStatusRegister(byte s)
    {
        var bits = new List<string>(8);
        if ((s & 0x01) != 0) bits.Add("ControlGearFailure");
        if ((s & 0x02) != 0) bits.Add("LampFailure");
        if ((s & 0x04) != 0) bits.Add("LampOn");
        if ((s & 0x08) != 0) bits.Add("LimitError");
        if ((s & 0x10) != 0) bits.Add("FadeRunning");
        if ((s & 0x20) != 0) bits.Add("ResetState");
        if ((s & 0x40) != 0) bits.Add("ShortAddrIsMask");
        if ((s & 0x80) != 0) bits.Add("PowerCycleSeen");
        return bits.Count == 0
            ? $"Status: 0x{s:X2} (all clear)"
            : $"Status: {string.Join(", ", bits)}";
    }

    // ── Special command table ─────────────────────────────────────────────────

    private static string SpecialCommandName(byte addrByte) => addrByte switch
    {
        0xA1 => "TERMINATE",
        0xA3 => "DTR0",
        0xA5 => "INITIALISE",
        0xA7 => "RANDOMISE",
        0xA9 => "COMPARE",
        0xAB => "WITHDRAW",
        0xAD => "PING",
        0xB1 => "SET SEARCHADDR H",
        0xB3 => "SET SEARCHADDR M",
        0xB5 => "SET SEARCHADDR L",
        0xB7 => "PROGRAM SHORT ADDRESS",
        0xB9 => "VERIFY SHORT ADDRESS",
        0xBB => "QUERY SHORT ADDRESS",
        0xBD => "PHYSICAL SELECTION",
        0xC1 => "ENABLE DEVICE TYPE",
        0xC3 => "DTR1",
        0xC5 => "DTR2",
        0xC7 => "WRITE MEMORY LOCATION",
        0xC9 => "WRITE MEMORY LOCATION (no reply)",
        _    => $"Special 0x{addrByte:X2}",
    };

    // ── Forward command table (data byte when S-bit=1) ────────────────────────

    private static string ForwardCommandName(byte d) => d switch
    {
        0x00 => "OFF",
        0x01 => "UP",
        0x02 => "DOWN",
        0x03 => "STEP UP",
        0x04 => "STEP DOWN",
        0x05 => "RECALL MAX LEVEL",
        0x06 => "RECALL MIN LEVEL",
        0x07 => "STEP DOWN AND OFF",
        0x08 => "ON AND STEP UP",
        0x09 => "ENABLE DAPC SEQUENCE",
        0x0A => "GO TO LAST ACTIVE LEVEL",
        0x20 => "RESET",
        0x21 => "STORE ACTUAL LEVEL IN DTR0",
        0x22 => "SAVE PERSISTENT VARIABLES",
        0x23 => "SET OPERATING MODE (DTR0)",
        0x24 => "RESET MEMORY BANK (DTR0)",
        0x25 => "IDENTIFY DEVICE",
        0x2A => "SET MAX LEVEL (DTR0)",
        0x2B => "SET MIN LEVEL (DTR0)",
        0x2C => "SET SYSTEM FAILURE LEVEL (DTR0)",
        0x2D => "SET POWER ON LEVEL (DTR0)",
        0x2E => "SET FADE TIME (DTR0)",
        0x2F => "SET FADE RATE (DTR0)",
        0x30 => "SET EXTENDED FADE TIME (DTR0)",
        0x80 => "SET SHORT ADDRESS (DTR0)",
        0x81 => "ENABLE WRITE MEMORY",
        0x90 => "QUERY STATUS",
        0x91 => "QUERY CONTROL GEAR PRESENT",
        0x92 => "QUERY LAMP FAILURE",
        0x93 => "QUERY LAMP POWER ON",
        0x94 => "QUERY LIMIT ERROR",
        0x95 => "QUERY RESET STATE",
        0x96 => "QUERY MISSING SHORT ADDRESS",
        0x97 => "QUERY VERSION NUMBER",
        0x98 => "QUERY CONTENT DTR0",
        0x99 => "QUERY DEVICE TYPE",
        0x9A => "QUERY PHYSICAL MINIMUM",
        0x9B => "QUERY POWER FAILURE",
        0x9C => "QUERY CONTENT DTR1",
        0x9D => "QUERY CONTENT DTR2",
        0x9E => "QUERY OPERATING MODE",
        0x9F => "QUERY LIGHT SOURCE TYPE",
        0xA0 => "QUERY ACTUAL LEVEL",
        0xA1 => "QUERY MAX LEVEL",
        0xA2 => "QUERY MIN LEVEL",
        0xA3 => "QUERY POWER ON LEVEL",
        0xA4 => "QUERY SYSTEM FAILURE LEVEL",
        0xA5 => "QUERY FADE TIME/RATE",
        0xA6 => "QUERY MANUFACTURER SPECIFIC MODE",
        0xA7 => "QUERY NEXT DEVICE TYPE",
        0xA8 => "QUERY EXTENDED FADE TIME",
        0xAA => "QUERY GEAR FAILURE",
        0xC0 => "QUERY GROUPS 0-7",
        0xC1 => "QUERY GROUPS 8-15",
        0xC2 => "QUERY RANDOM ADDRESS H",
        0xC3 => "QUERY RANDOM ADDRESS M",
        0xC4 => "QUERY RANDOM ADDRESS L",
        0xC5 => "READ MEMORY LOCATION",
        0xFF => "QUERY EXTENDED VERSION NUMBER",
        _    => d >= 0x10 && d <= 0x1F ? $"GO TO SCENE {d - 0x10}"
               : d >= 0x40 && d <= 0x4F ? $"SET SCENE {d - 0x40} (DTR0)"
               : d >= 0x50 && d <= 0x5F ? $"REMOVE FROM SCENE {d - 0x50}"
               : d >= 0x60 && d <= 0x6F ? $"ADD TO GROUP {d - 0x60}"
               : d >= 0x70 && d <= 0x7F ? $"REMOVE FROM GROUP {d - 0x70}"
               : d >= 0xB0 && d <= 0xBF ? $"QUERY SCENE LEVEL {d - 0xB0}"
               : $"Command 0x{d:X2}",
    };

    // ── DALI-2 instance command classification ────────────────────────────────

    private static bool IsInstanceCommand(byte op) =>
        op is 0x21 or 0x22 or 0x23 or 0x26 or 0x2B
           or >= 0x30 and <= 0x33
           or >= 0x3C and <= 0x3F
           or >= 0x65 and <= 0x68
           or >= 0x80 and <= 0x84
           or 0x86
           or >= 0x88 and <= 0x92;

    // ── DALI-2 instance command table ─────────────────────────────────────────

    private static string InstanceCommandName(byte op) => op switch
    {
        0x21 => "SET HOLD TIMER (DTR0)",
        0x22 => "SET REPORT TIMER (DTR0)",
        0x23 => "SET DEADTIME TIMER (DTR0)",
        0x26 => "SET SENSITIVITY (DTR0)",
        0x2B => "QUERY SENSITIVITY",
        0x30 => "SET REPORT TIMER (DTR0)",
        0x31 => "SET HYSTERESIS (DTR0)",
        0x32 => "SET DEADTIME TIMER (DTR0)",
        0x33 => "SET HYSTERESIS MIN (DTR0)",
        0x3C => "QUERY HYSTERESIS MIN",
        0x3D => "QUERY DEADTIME TIMER",
        0x3E => "QUERY REPORT TIMER",
        0x3F => "QUERY HYSTERESIS",
        0x65 => "SET INSTANCE GROUP 1 (DTR0)",
        0x66 => "SET INSTANCE GROUP 2 (DTR0)",
        0x67 => "SET EVENT SCHEME (DTR0)",
        0x68 => "SET EVENT FILTER (DTR0)",
        0x80 => "QUERY INSTANCE TYPE",
        0x81 => "QUERY RESOLUTION",
        0x82 => "QUERY INSTANCE ERROR",
        0x83 => "QUERY INSTANCE STATUS",
        0x84 => "QUERY EVENT PRIORITY",
        0x86 => "QUERY INSTANCE ENABLED",
        0x88 => "QUERY PRIMARY GROUP",
        0x89 => "QUERY INSTANCE GROUP 1",
        0x8A => "QUERY INSTANCE GROUP 2",
        0x8B => "QUERY EVENT SCHEME",
        0x8C => "QUERY INPUT VALUE",
        0x8D => "QUERY INPUT VALUE LATCH",
        0x8E => "QUERY FEATURE TYPE",
        0x8F => "QUERY NEXT FEATURE TYPE",
        0x90 => "QUERY EVENT FILTER 0-7",
        0x91 => "QUERY EVENT FILTER 8-15",
        0x92 => "QUERY EVENT FILTER 16-23",
        _    => $"Instance Command 0x{op:X2}",
    };

    // ── Instance type helpers ─────────────────────────────────────────────────

    private static string? InstanceTypeName(int? n) => n switch
    {
        1 => "Push Button",
        2 => "Analog Input",
        3 => "Occupancy Sensor",
        4 => "Light Sensor",
        _ => null,
    };

    private static string? InferInstanceType(byte op)
    {
        if (op is 0x21 or 0x22 or 0x23 or 0x26 or 0x2B)
            return "Occupancy Sensor";
        if (op is >= 0x30 and <= 0x33 or 0x3C or 0x3D or 0x3E or 0x3F)
            return "Light Sensor";
        return null;
    }

    // ── Event decode ──────────────────────────────────────────────────────────

    private static string DecodeEvent(int? instanceTypeNum, int eventInfo)
    {
        if (instanceTypeNum is 0 or > 4)
            instanceTypeNum = null;

        return instanceTypeNum switch
        {
            1 => DecodePushButtonEvent(eventInfo & 0xF),
            2 => $"Analog Value: {eventInfo}",
            3 => DecodeOccupancyEvent(eventInfo),
            4 => $"Illuminance: {eventInfo} lux",
            _ => $"Event 0x{eventInfo:X3}",
        };
    }

    private static string DecodePushButtonEvent(int code) => code switch
    {
        0x0 => "Button Released",
        0x1 => "Button Pressed",
        0x2 => "Short Press",
        0x5 => "Double Press",
        0x9 => "Long Press Start",
        0xB => "Long Press Repeat",
        0xC => "Long Press Stop",
        0xE => "Button Free (was stuck)",
        0xF => "Button Stuck",
        _   => $"Push Button Event 0x{code:X}",
    };

    private static string DecodeOccupancyEvent(int eventInfo)
    {
        bool   movement = (eventInfo & 0x1) == 1;
        string state    = ((eventInfo >> 1) & 0x3) switch
        {
            0 => "Vacant",
            1 => "Occupied",
            2 => "Still Vacant",
            3 => "Still Occupied",
            _ => "Unknown",
        };
        return $"{state}, {(movement ? "movement" : "no movement")}";
    }
}
