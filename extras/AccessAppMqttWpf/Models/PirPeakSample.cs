using System;

namespace AccessAppMqttWpf.Models;

/// <summary>
/// One PIR peak status snapshot received from the AccessApp tele/pir-peak message.
/// </summary>
public sealed class PirPeakSample
{
    public DateTimeOffset TimeUtc { get; init; }
    public string Mac { get; init; } = "";

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
}
