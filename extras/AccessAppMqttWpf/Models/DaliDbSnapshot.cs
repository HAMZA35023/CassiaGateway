using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AccessAppMqttWpf.Models;

// ─────────────────────────────────────────────────────────────────────────────
// Observable WPF mirror of the backend DaliDbModel.cs structures.
// Used as the DataContext for the DALI DB editor window.
// ─────────────────────────────────────────────────────────────────────────────

public partial class DaliDbSnapshotVm : ObservableObject
{
    // ── 102 Application ───────────────────────────────────────────────────────
    [ObservableProperty] private Dali102AppHeaderVm   db102AppHeader  = new();
    [ObservableProperty] private Dali102AppCommonVm   db102AppCommon  = new();
    public ObservableCollection<Dali102AppDeviceTypesVm>  Db102AppDeviceTypes  { get; } = new();
    public ObservableCollection<Dali102AppDeviceGroupsVm> Db102AppDeviceGroups { get; } = new();
    public ObservableCollection<Dali102AppDeviceScenesVm> Db102AppDeviceScenes { get; } = new();

    // ── 103 Application ───────────────────────────────────────────────────────
    [ObservableProperty] private Dali103AppHeaderVm db103AppHeader = new();
    public ObservableCollection<Dali103AppInstanceTypesVm> Db103AppInstanceTypes { get; } = new();

    // ── 102 Device ────────────────────────────────────────────────────────────
    [ObservableProperty] private Dali102DeviceHeaderVm  db102DeviceHeader  = new();
    [ObservableProperty] private Dali102DeviceGeneralVm db102DeviceGeneral = new();
    [ObservableProperty] private Dali102DeviceScenesVm  db102DeviceScenes  = new();
    [ObservableProperty] private Dali102DeviceDt208Vm   db102DeviceDt208   = new();

    // ── 103 Device ────────────────────────────────────────────────────────────
    [ObservableProperty] private Dali103DeviceHeaderVm  db103DeviceHeader  = new();
    [ObservableProperty] private Dali103DeviceGeneralVm db103DeviceGeneral = new();

    [ObservableProperty] private string? instanceDataFamily; // "StandardComfort" | "BmsSlave" | null
    public ObservableCollection<Dali103InstanceDataParsedVm> InstanceDataSections { get; } = new();
}

// ─── 102 Application ─────────────────────────────────────────────────────────

public partial class Dali102AppHeaderVm : ObservableObject
{
    [ObservableProperty] private byte version;
    [ObservableProperty] private byte daliDbLen;
}

public partial class Dali102AppCommonVm : ObservableObject
{
    [ObservableProperty] private byte maxLevel;
    [ObservableProperty] private byte minLevel;
    [ObservableProperty] private byte powerOnLevel;
    [ObservableProperty] private byte systemFailureLevel;
    [ObservableProperty] private byte fadeRate;
    [ObservableProperty] private byte fadeTime;
    [ObservableProperty] private byte extendedFadeTime;
    [ObservableProperty] private byte relayCutOffSA;
    [ObservableProperty] private byte relayHvacSA;
}

public partial class Dali102AppDeviceTypesVm : ObservableObject
{
    [ObservableProperty] private byte shortAddress;
    [ObservableProperty] private byte deviceType0;
    [ObservableProperty] private byte deviceType1;
}

public partial class Dali102AppDeviceGroupsVm : ObservableObject
{
    [ObservableProperty] private byte shortAddress;
    [ObservableProperty] private byte daliGroup0;
    [ObservableProperty] private byte daliGroup1;

    // Convenience: group membership as a human-readable bitmask string
    public string Groups0to7  => Convert.ToString(DaliGroup0, 2).PadLeft(8, '0');
    public string Groups8to15 => Convert.ToString(DaliGroup1, 2).PadLeft(8, '0');
}

public partial class Dali102AppDeviceScenesVm : ObservableObject
{
    [ObservableProperty] private byte shortAddress;
    // 16 scene levels exposed as individual properties for DataGrid column binding
    [ObservableProperty] private byte scene0;  [ObservableProperty] private byte scene1;
    [ObservableProperty] private byte scene2;  [ObservableProperty] private byte scene3;
    [ObservableProperty] private byte scene4;  [ObservableProperty] private byte scene5;
    [ObservableProperty] private byte scene6;  [ObservableProperty] private byte scene7;
    [ObservableProperty] private byte scene8;  [ObservableProperty] private byte scene9;
    [ObservableProperty] private byte scene10; [ObservableProperty] private byte scene11;
    [ObservableProperty] private byte scene12; [ObservableProperty] private byte scene13;
    [ObservableProperty] private byte scene14; [ObservableProperty] private byte scene15;

    public byte[] ToArray() => new[] {
        Scene0,  Scene1,  Scene2,  Scene3,  Scene4,  Scene5,  Scene6,  Scene7,
        Scene8,  Scene9,  Scene10, Scene11, Scene12, Scene13, Scene14, Scene15
    };

    public static Dali102AppDeviceScenesVm FromArray(byte sa, byte[] arr)
    {
        var v = new Dali102AppDeviceScenesVm { ShortAddress = sa };
        if (arr.Length >= 16)
        {
            v.Scene0=arr[0]; v.Scene1=arr[1];  v.Scene2=arr[2];  v.Scene3=arr[3];
            v.Scene4=arr[4]; v.Scene5=arr[5];  v.Scene6=arr[6];  v.Scene7=arr[7];
            v.Scene8=arr[8]; v.Scene9=arr[9];  v.Scene10=arr[10];v.Scene11=arr[11];
            v.Scene12=arr[12];v.Scene13=arr[13];v.Scene14=arr[14];v.Scene15=arr[15];
        }
        return v;
    }
}

// ─── 103 Application ─────────────────────────────────────────────────────────

public partial class Dali103AppHeaderVm : ObservableObject
{
    [ObservableProperty] private byte version;
    [ObservableProperty] private byte daliDbLen;
}

public partial class Dali103AppInstanceTypesVm : ObservableObject
{
    [ObservableProperty] private byte shortAddress;
    [ObservableProperty] private byte instanceType0; [ObservableProperty] private byte instanceType1;
    [ObservableProperty] private byte instanceType2; [ObservableProperty] private byte instanceType3;
    [ObservableProperty] private byte instanceType4; [ObservableProperty] private byte instanceType5;
    [ObservableProperty] private byte instanceType6; [ObservableProperty] private byte instanceType7;

    public byte[] ToArray() => new[] {
        InstanceType0, InstanceType1, InstanceType2, InstanceType3,
        InstanceType4, InstanceType5, InstanceType6, InstanceType7
    };
}

// ─── 102 Device ──────────────────────────────────────────────────────────────

public partial class Dali102DeviceHeaderVm : ObservableObject
{
    [ObservableProperty] private byte version;
}

public partial class Dali102DeviceGeneralVm : ObservableObject
{
    [ObservableProperty] private byte lastLightLevel;
    [ObservableProperty] private byte powerOnLevel;
    [ObservableProperty] private byte systemFailureLevel;
    [ObservableProperty] private byte minLevel;
    [ObservableProperty] private byte maxLevel;
    [ObservableProperty] private byte fadeRate;
    [ObservableProperty] private byte fadeTime;
    [ObservableProperty] private byte extendedFadeTimeBase;
    [ObservableProperty] private byte extendedFadeTimeMultiplier;
    [ObservableProperty] private byte shortAddress;
    // RandomAddress — 4 individual bytes
    [ObservableProperty] private byte randomAddress0;
    [ObservableProperty] private byte randomAddress1;
    [ObservableProperty] private byte randomAddress2;
    [ObservableProperty] private byte randomAddress3;
    // GearGroups — 2 individual bytes (bitmask: bit N = member of DALI group N)
    [ObservableProperty] private byte gearGroup0;  // groups 0-7
    [ObservableProperty] private byte gearGroup1;  // groups 8-15
}

public partial class Dali102DeviceScenesVm : ObservableObject
{
    [ObservableProperty] private byte scene0;  [ObservableProperty] private byte scene1;
    [ObservableProperty] private byte scene2;  [ObservableProperty] private byte scene3;
    [ObservableProperty] private byte scene4;  [ObservableProperty] private byte scene5;
    [ObservableProperty] private byte scene6;  [ObservableProperty] private byte scene7;
    [ObservableProperty] private byte scene8;  [ObservableProperty] private byte scene9;
    [ObservableProperty] private byte scene10; [ObservableProperty] private byte scene11;
    [ObservableProperty] private byte scene12; [ObservableProperty] private byte scene13;
    [ObservableProperty] private byte scene14; [ObservableProperty] private byte scene15;
}

public partial class Dali102DeviceDt208Vm : ObservableObject
{
    [ObservableProperty] private byte upSwitchOnThreshold;
    [ObservableProperty] private byte upSwitchOffThreshold;
    [ObservableProperty] private byte downSwitchOnThreshold;
    [ObservableProperty] private byte downSwitchOffThreshold;
    [ObservableProperty] private byte errorHoldOffTime;
    [ObservableProperty] private byte switchStatus;
}

// ─── 103 Device ──────────────────────────────────────────────────────────────

public partial class Dali103DeviceHeaderVm : ObservableObject
{
    [ObservableProperty] private byte version;
}

public partial class Dali103DeviceGeneralVm : ObservableObject
{
    [ObservableProperty] private byte shortAddress;
    // DeviceGroups — 4 individual bytes (bitmask: bit N = member of device group N)
    [ObservableProperty] private byte deviceGroup0;  // groups 0-7
    [ObservableProperty] private byte deviceGroup1;  // groups 8-15
    [ObservableProperty] private byte deviceGroup2;  // groups 16-23
    [ObservableProperty] private byte deviceGroup3;  // groups 24-31
    // RandomAddress — 4 individual bytes
    [ObservableProperty] private byte randomAddress0;
    [ObservableProperty] private byte randomAddress1;
    [ObservableProperty] private byte randomAddress2;
    [ObservableProperty] private byte randomAddress3;
    [ObservableProperty] private byte operationMode;
    [ObservableProperty] private byte applicationActive;
    [ObservableProperty] private byte powerCycleNotification;
    [ObservableProperty] private byte luxRange;
}

// ─── 103 Instance Data (parsed) ──────────────────────────────────────────────

/// <summary>One named byte field within an instance-data section.</summary>
public partial class InstanceFieldVm : ObservableObject
{
    public string Name { get; init; } = "";
    [ObservableProperty] private byte value;
}

/// <summary>
/// Parsed view of one instance-data slot (dbType 13-19).
/// Fields are populated from raw bytes using the §6.1.6 spec field names.
/// </summary>
public partial class Dali103InstanceDataParsedVm : ObservableObject
{
    public byte   DbType       { get; set; }
    public string Label        { get; set; } = "";
    public bool   NotSupported { get; set; }
    public bool   IsAvailable  => !NotSupported;

    public ObservableCollection<InstanceFieldVm> Fields { get; } = new();

    /// <summary>Returns raw bytes from the current field values (for serialisation).</summary>
    public byte[] ToRawBytes() => Fields.Select(f => f.Value).ToArray();
}
