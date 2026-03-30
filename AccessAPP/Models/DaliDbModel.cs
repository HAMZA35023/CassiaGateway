namespace AccessAPP.Models;

// ─────────────────────────────────────────────────────────────────────────────
// DALI Database snapshot – plain serialisable classes (no WPF dependency).
// Mirrors the BLE telegram spec §6.1.5 / §6.1.6 / §6.1.7.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Full DALI database snapshot read from / written to a device.</summary>
public sealed class DaliDbSnapshot
{
    // ── 102 Application (commissioning) database ─────────────────────────────
    public Dali102AppHeader?          Db102AppHeader      { get; set; }
    public Dali102AppDeviceCommon?    Db102AppCommon      { get; set; }
    public List<Dali102AppDeviceTypes>   Db102AppDeviceTypes   { get; set; } = new();
    public List<Dali102AppDeviceGroups>  Db102AppDeviceGroups  { get; set; } = new();
    public List<Dali102AppDeviceScenes>  Db102AppDeviceScenes  { get; set; } = new();

    // ── 103 Application (commissioning) database ─────────────────────────────
    public Dali103AppHeader?          Db103AppHeader      { get; set; }
    public List<Dali103AppInstanceTypes> Db103AppInstanceTypes { get; set; } = new();

    // ── 102 Device database ───────────────────────────────────────────────────
    public Dali102DeviceHeader?       Db102DeviceHeader   { get; set; }
    public Dali102DeviceGeneral?      Db102DeviceGeneral  { get; set; }
    public Dali102DeviceScenes?       Db102DeviceScenes   { get; set; }
    public Dali102DeviceDt208?        Db102DeviceDt208    { get; set; }

    // ── 103 Device database ───────────────────────────────────────────────────
    public Dali103DeviceHeader?       Db103DeviceHeader   { get; set; }
    public Dali103DeviceGeneral?      Db103DeviceGeneral  { get; set; }

    // Instance data 0-6 – layout differs between Standard/Comfort (P47/P48) and BMS/Slave (P46).
    // Raw bytes are stored so the caller can display/edit them; family is indicated by InstanceDataFamily.
    public string?                    InstanceDataFamily  { get; set; } // "StandardComfort" | "BmsSlave" | null
    public Dali103InstanceData?       InstanceData0       { get; set; }
    public Dali103InstanceData?       InstanceData1       { get; set; }
    public Dali103InstanceData?       InstanceData2       { get; set; }
    public Dali103InstanceData?       InstanceData3       { get; set; }
    public Dali103InstanceData?       InstanceData4       { get; set; }
    public Dali103InstanceData?       InstanceData5       { get; set; }
    public Dali103InstanceData?       InstanceData6       { get; set; }
}

// ─── 102 Application ─────────────────────────────────────────────────────────

public sealed class Dali102AppHeader
{
    public byte Version    { get; set; }
    public byte DaliDbLen  { get; set; } // number of devices in 102 commissioning DB (0-64)
}

public sealed class Dali102AppDeviceCommon
{
    public byte MaxLevel             { get; set; }
    public byte MinLevel             { get; set; }
    public byte PowerOnLevel         { get; set; }
    public byte SystemFailureLevel   { get; set; }
    public byte FadeRate             { get; set; }
    public byte FadeTime             { get; set; }
    public byte ExtendedFadeTime     { get; set; }
    public byte RelayCutOffSA        { get; set; }
    public byte RelayHvacSA          { get; set; }
}

public sealed class Dali102AppDeviceTypes
{
    public byte ShortAddress { get; set; }
    public byte DeviceType0  { get; set; }
    public byte DeviceType1  { get; set; }
}

public sealed class Dali102AppDeviceGroups
{
    public byte ShortAddress { get; set; }
    public byte DaliGroup0   { get; set; } // bits 0-7  → groups 0-7
    public byte DaliGroup1   { get; set; } // bits 0-7  → groups 8-15
}

public sealed class Dali102AppDeviceScenes
{
    public byte   ShortAddress { get; set; }
    public byte[] SceneLevel   { get; set; } = new byte[16]; // 16 scenes, 0xFF = unset
}

// ─── 103 Application ─────────────────────────────────────────────────────────

public sealed class Dali103AppHeader
{
    public byte Version   { get; set; }
    public byte DaliDbLen { get; set; }
}

public sealed class Dali103AppInstanceTypes
{
    public byte   ShortAddress    { get; set; }
    public byte[] InstanceTypes   { get; set; } = new byte[8];
    // Each byte: high nibble = InstanceNumber, low nibble = InstanceType
}

// ─── 102 Device ──────────────────────────────────────────────────────────────

public sealed class Dali102DeviceHeader
{
    public byte Version { get; set; }
}

public sealed class Dali102DeviceGeneral
{
    public byte   LastLightLevel             { get; set; }
    public byte   PowerOnLevel               { get; set; }
    public byte   SystemFailureLevel         { get; set; }
    public byte   MinLevel                   { get; set; }
    public byte   MaxLevel                   { get; set; }
    public byte   FadeRate                   { get; set; }
    public byte   FadeTime                   { get; set; }
    public byte   ExtendedFadeTimeBase       { get; set; }
    public byte   ExtendedFadeTimeMultiplier { get; set; }
    public byte   ShortAddress               { get; set; }
    public byte[] RandomAddress              { get; set; } = new byte[4];
    public byte[] GearGroups                 { get; set; } = new byte[2];
}

public sealed class Dali102DeviceScenes
{
    public byte[] SceneLevel { get; set; } = new byte[16];
}

public sealed class Dali102DeviceDt208
{
    public byte UpSwitchOnThreshold    { get; set; }
    public byte UpSwitchOffThreshold   { get; set; }
    public byte DownSwitchOnThreshold  { get; set; }
    public byte DownSwitchOffThreshold { get; set; }
    public byte ErrorHoldOffTime       { get; set; }
    public byte SwitchStatus           { get; set; }
}

// ─── 103 Device ──────────────────────────────────────────────────────────────

public sealed class Dali103DeviceHeader
{
    public byte Version { get; set; }
}

public sealed class Dali103DeviceGeneral
{
    public byte   ShortAddress             { get; set; }
    public byte[] DeviceGroups             { get; set; } = new byte[4];
    public byte[] RandomAddress            { get; set; } = new byte[4];
    public byte   OperationMode            { get; set; }
    public byte   ApplicationActive        { get; set; }
    public byte   PowerCycleNotification   { get; set; }
    public byte   LuxRange                 { get; set; }
}

/// <summary>
/// Raw bytes for one DALI 103 device instance-data block (dbType 13-19).
/// The interpretation differs between Standard/Comfort and BMS/Slave families –
/// see DaliDbSnapshot.InstanceDataFamily.
/// </summary>
public sealed class Dali103InstanceData
{
    public byte DbType { get; set; }     // 13-19
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public bool NotSupported { get; set; } // true when device returned NACK_NOT_ALLOWED
}
