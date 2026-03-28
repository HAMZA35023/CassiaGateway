// Plain serialisable mirror of AccessAPP.Models.DaliDbModel — kept in the
// same namespace so that DaliDbMapper.cs and DaliDbTelePayload can compile
// without a project-reference to the AccessAPP web project.

namespace AccessAPP.Models;

public sealed class DaliDbSnapshot
{
    public Dali102AppHeader?             Db102AppHeader      { get; set; }
    public Dali102AppDeviceCommon?       Db102AppCommon      { get; set; }
    public List<Dali102AppDeviceTypes>   Db102AppDeviceTypes   { get; set; } = new();
    public List<Dali102AppDeviceGroups>  Db102AppDeviceGroups  { get; set; } = new();
    public List<Dali102AppDeviceScenes>  Db102AppDeviceScenes  { get; set; } = new();

    public Dali103AppHeader?             Db103AppHeader      { get; set; }
    public List<Dali103AppInstanceTypes> Db103AppInstanceTypes { get; set; } = new();

    public Dali102DeviceHeader?          Db102DeviceHeader   { get; set; }
    public Dali102DeviceGeneral?         Db102DeviceGeneral  { get; set; }
    public Dali102DeviceScenes?          Db102DeviceScenes   { get; set; }
    public Dali102DeviceDt208?           Db102DeviceDt208    { get; set; }

    public Dali103DeviceHeader?          Db103DeviceHeader   { get; set; }
    public Dali103DeviceGeneral?         Db103DeviceGeneral  { get; set; }

    public string?              InstanceDataFamily  { get; set; }
    public Dali103InstanceData? InstanceData0       { get; set; }
    public Dali103InstanceData? InstanceData1       { get; set; }
    public Dali103InstanceData? InstanceData2       { get; set; }
    public Dali103InstanceData? InstanceData3       { get; set; }
    public Dali103InstanceData? InstanceData4       { get; set; }
    public Dali103InstanceData? InstanceData5       { get; set; }
    public Dali103InstanceData? InstanceData6       { get; set; }
}

public sealed class Dali102AppHeader
{
    public byte Version   { get; set; }
    public byte DaliDbLen { get; set; }
}

public sealed class Dali102AppDeviceCommon
{
    public byte MaxLevel           { get; set; }
    public byte MinLevel           { get; set; }
    public byte PowerOnLevel       { get; set; }
    public byte SystemFailureLevel { get; set; }
    public byte FadeRate           { get; set; }
    public byte FadeTime           { get; set; }
    public byte ExtendedFadeTime   { get; set; }
    public byte RelayCutOffSA      { get; set; }
    public byte RelayHvacSA        { get; set; }
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
    public byte DaliGroup0   { get; set; }
    public byte DaliGroup1   { get; set; }
}

public sealed class Dali102AppDeviceScenes
{
    public byte   ShortAddress { get; set; }
    public byte[] SceneLevel   { get; set; } = new byte[16];
}

public sealed class Dali103AppHeader
{
    public byte Version   { get; set; }
    public byte DaliDbLen { get; set; }
}

public sealed class Dali103AppInstanceTypes
{
    public byte   ShortAddress  { get; set; }
    public byte[] InstanceTypes { get; set; } = new byte[8];
}

public sealed class Dali102DeviceHeader  { public byte Version { get; set; } }
public sealed class Dali103DeviceHeader  { public byte Version { get; set; } }

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

public sealed class Dali102DeviceScenes  { public byte[] SceneLevel { get; set; } = new byte[16]; }

public sealed class Dali102DeviceDt208
{
    public byte UpSwitchOnThreshold    { get; set; }
    public byte UpSwitchOffThreshold   { get; set; }
    public byte DownSwitchOnThreshold  { get; set; }
    public byte DownSwitchOffThreshold { get; set; }
    public byte ErrorHoldOffTime       { get; set; }
    public byte SwitchStatus           { get; set; }
}

public sealed class Dali103DeviceGeneral
{
    public byte   ShortAddress           { get; set; }
    public byte[] DeviceGroups           { get; set; } = new byte[4];
    public byte[] RandomAddress          { get; set; } = new byte[4];
    public byte   OperationMode          { get; set; }
    public byte   ApplicationActive      { get; set; }
    public byte   PowerCycleNotification { get; set; }
    public byte   LuxRange               { get; set; }
}

public sealed class Dali103InstanceData
{
    public byte   DbType       { get; set; }
    public byte[] Data         { get; set; } = Array.Empty<byte>();
    public bool   NotSupported { get; set; }
}
