using AccessAppMqttWpf.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AccessAppMqttWpf.ViewModels;

/// <summary>
/// Converts between the serialisable backend model (AccessAPP.Models.DaliDbSnapshot)
/// and the observable WPF view-model (DaliDbSnapshotVm).
/// </summary>
internal static class DaliDbMapper
{
    // ── Backend → WPF VM ──────────────────────────────────────────────────────

    public static DaliDbSnapshotVm ToVm(AccessAPP.Models.DaliDbSnapshot src)
    {
        var vm = new DaliDbSnapshotVm();

        // 102 App Header
        if (src.Db102AppHeader is { } h102)
        {
            vm.Db102AppHeader.Version   = h102.Version;
            vm.Db102AppHeader.DaliDbLen = h102.DaliDbLen;
        }

        // 102 App Common
        if (src.Db102AppCommon is { } c102)
        {
            vm.Db102AppCommon.MaxLevel           = c102.MaxLevel;
            vm.Db102AppCommon.MinLevel           = c102.MinLevel;
            vm.Db102AppCommon.PowerOnLevel       = c102.PowerOnLevel;
            vm.Db102AppCommon.SystemFailureLevel = c102.SystemFailureLevel;
            vm.Db102AppCommon.FadeRate           = c102.FadeRate;
            vm.Db102AppCommon.FadeTime           = c102.FadeTime;
            vm.Db102AppCommon.ExtendedFadeTime   = c102.ExtendedFadeTime;
            vm.Db102AppCommon.RelayCutOffSA      = c102.RelayCutOffSA;
            vm.Db102AppCommon.RelayHvacSA        = c102.RelayHvacSA;
        }

        foreach (var t in src.Db102AppDeviceTypes)
            vm.Db102AppDeviceTypes.Add(new Dali102AppDeviceTypesVm {
                ShortAddress = t.ShortAddress, DeviceType0 = t.DeviceType0, DeviceType1 = t.DeviceType1 });

        foreach (var g in src.Db102AppDeviceGroups)
            vm.Db102AppDeviceGroups.Add(new Dali102AppDeviceGroupsVm {
                ShortAddress = g.ShortAddress, DaliGroup0 = g.DaliGroup0, DaliGroup1 = g.DaliGroup1 });

        foreach (var s in src.Db102AppDeviceScenes)
            vm.Db102AppDeviceScenes.Add(Dali102AppDeviceScenesVm.FromArray(s.ShortAddress, s.SceneLevel));

        // 103 App
        if (src.Db103AppHeader is { } h103)
        {
            vm.Db103AppHeader.Version   = h103.Version;
            vm.Db103AppHeader.DaliDbLen = h103.DaliDbLen;
        }

        foreach (var it in src.Db103AppInstanceTypes)
        {
            var row = new Dali103AppInstanceTypesVm { ShortAddress = it.ShortAddress };
            if (it.InstanceTypes.Length >= 8)
            {
                row.InstanceType0 = it.InstanceTypes[0]; row.InstanceType1 = it.InstanceTypes[1];
                row.InstanceType2 = it.InstanceTypes[2]; row.InstanceType3 = it.InstanceTypes[3];
                row.InstanceType4 = it.InstanceTypes[4]; row.InstanceType5 = it.InstanceTypes[5];
                row.InstanceType6 = it.InstanceTypes[6]; row.InstanceType7 = it.InstanceTypes[7];
            }
            vm.Db103AppInstanceTypes.Add(row);
        }

        // 102 Device
        if (src.Db102DeviceHeader is { } dh102)
            vm.Db102DeviceHeader.Version = dh102.Version;

        if (src.Db102DeviceGeneral is { } dg102)
        {
            vm.Db102DeviceGeneral.LastLightLevel             = dg102.LastLightLevel;
            vm.Db102DeviceGeneral.PowerOnLevel               = dg102.PowerOnLevel;
            vm.Db102DeviceGeneral.SystemFailureLevel         = dg102.SystemFailureLevel;
            vm.Db102DeviceGeneral.MinLevel                   = dg102.MinLevel;
            vm.Db102DeviceGeneral.MaxLevel                   = dg102.MaxLevel;
            vm.Db102DeviceGeneral.FadeRate                   = dg102.FadeRate;
            vm.Db102DeviceGeneral.FadeTime                   = dg102.FadeTime;
            vm.Db102DeviceGeneral.ExtendedFadeTimeBase       = dg102.ExtendedFadeTimeBase;
            vm.Db102DeviceGeneral.ExtendedFadeTimeMultiplier = dg102.ExtendedFadeTimeMultiplier;
            vm.Db102DeviceGeneral.ShortAddress               = dg102.ShortAddress;
            if (dg102.RandomAddress.Length >= 4)
            {
                vm.Db102DeviceGeneral.RandomAddress0 = dg102.RandomAddress[0];
                vm.Db102DeviceGeneral.RandomAddress1 = dg102.RandomAddress[1];
                vm.Db102DeviceGeneral.RandomAddress2 = dg102.RandomAddress[2];
                vm.Db102DeviceGeneral.RandomAddress3 = dg102.RandomAddress[3];
            }
            if (dg102.GearGroups.Length >= 2)
            {
                vm.Db102DeviceGeneral.GearGroup0 = dg102.GearGroups[0];
                vm.Db102DeviceGeneral.GearGroup1 = dg102.GearGroups[1];
            }
        }

        if (src.Db102DeviceScenes is { } ds102)
        {
            var sc = vm.Db102DeviceScenes;
            sc.Scene0=ds102.SceneLevel[0]; sc.Scene1=ds102.SceneLevel[1];
            sc.Scene2=ds102.SceneLevel[2]; sc.Scene3=ds102.SceneLevel[3];
            sc.Scene4=ds102.SceneLevel[4]; sc.Scene5=ds102.SceneLevel[5];
            sc.Scene6=ds102.SceneLevel[6]; sc.Scene7=ds102.SceneLevel[7];
            sc.Scene8=ds102.SceneLevel[8]; sc.Scene9=ds102.SceneLevel[9];
            sc.Scene10=ds102.SceneLevel[10]; sc.Scene11=ds102.SceneLevel[11];
            sc.Scene12=ds102.SceneLevel[12]; sc.Scene13=ds102.SceneLevel[13];
            sc.Scene14=ds102.SceneLevel[14]; sc.Scene15=ds102.SceneLevel[15];
        }

        if (src.Db102DeviceDt208 is { } dt208)
        {
            vm.Db102DeviceDt208.UpSwitchOnThreshold    = dt208.UpSwitchOnThreshold;
            vm.Db102DeviceDt208.UpSwitchOffThreshold   = dt208.UpSwitchOffThreshold;
            vm.Db102DeviceDt208.DownSwitchOnThreshold  = dt208.DownSwitchOnThreshold;
            vm.Db102DeviceDt208.DownSwitchOffThreshold = dt208.DownSwitchOffThreshold;
            vm.Db102DeviceDt208.ErrorHoldOffTime       = dt208.ErrorHoldOffTime;
            vm.Db102DeviceDt208.SwitchStatus           = dt208.SwitchStatus;
        }

        // 103 Device
        if (src.Db103DeviceHeader is { } dh103)
            vm.Db103DeviceHeader.Version = dh103.Version;

        if (src.Db103DeviceGeneral is { } dg103)
        {
            vm.Db103DeviceGeneral.ShortAddress           = dg103.ShortAddress;
            vm.Db103DeviceGeneral.OperationMode          = dg103.OperationMode;
            vm.Db103DeviceGeneral.ApplicationActive      = dg103.ApplicationActive;
            vm.Db103DeviceGeneral.PowerCycleNotification = dg103.PowerCycleNotification;
            vm.Db103DeviceGeneral.LuxRange               = dg103.LuxRange;
            if (dg103.DeviceGroups.Length >= 4)
            {
                vm.Db103DeviceGeneral.DeviceGroup0 = dg103.DeviceGroups[0];
                vm.Db103DeviceGeneral.DeviceGroup1 = dg103.DeviceGroups[1];
                vm.Db103DeviceGeneral.DeviceGroup2 = dg103.DeviceGroups[2];
                vm.Db103DeviceGeneral.DeviceGroup3 = dg103.DeviceGroups[3];
            }
            if (dg103.RandomAddress.Length >= 4)
            {
                vm.Db103DeviceGeneral.RandomAddress0 = dg103.RandomAddress[0];
                vm.Db103DeviceGeneral.RandomAddress1 = dg103.RandomAddress[1];
                vm.Db103DeviceGeneral.RandomAddress2 = dg103.RandomAddress[2];
                vm.Db103DeviceGeneral.RandomAddress3 = dg103.RandomAddress[3];
            }
        }

        // Instance data — parsed into named fields per §6.1.6 spec
        vm.InstanceDataFamily = src.InstanceDataFamily;
        foreach (var (id, idx) in new[] {
            (src.InstanceData0,0),(src.InstanceData1,1),(src.InstanceData2,2),
            (src.InstanceData3,3),(src.InstanceData4,4),(src.InstanceData5,5),(src.InstanceData6,6) })
        {
            if (id is null) continue;
            var section = new Dali103InstanceDataParsedVm {
                DbType       = id.DbType,
                Label        = $"Instance Data {idx}  (dbType {id.DbType})",
                NotSupported = id.NotSupported,
            };
            if (!id.NotSupported && id.Data.Length > 0)
            {
                var names = GetInstanceDataFieldNames(id.DbType, src.InstanceDataFamily);
                for (int i = 0; i < Math.Min(names.Length, id.Data.Length); i++)
                    section.Fields.Add(new InstanceFieldVm { Name = names[i], Value = id.Data[i] });
            }
            vm.InstanceDataSections.Add(section);
        }

        return vm;
    }

    // ── WPF VM → Backend model ────────────────────────────────────────────────

    public static AccessAPP.Models.DaliDbSnapshot FromVm(DaliDbSnapshotVm vm)
    {
        var snap = new AccessAPP.Models.DaliDbSnapshot();

        snap.Db102AppHeader = new AccessAPP.Models.Dali102AppHeader {
            Version   = vm.Db102AppHeader.Version,
            DaliDbLen = vm.Db102AppHeader.DaliDbLen
        };

        snap.Db102AppCommon = new AccessAPP.Models.Dali102AppDeviceCommon {
            MaxLevel           = vm.Db102AppCommon.MaxLevel,
            MinLevel           = vm.Db102AppCommon.MinLevel,
            PowerOnLevel       = vm.Db102AppCommon.PowerOnLevel,
            SystemFailureLevel = vm.Db102AppCommon.SystemFailureLevel,
            FadeRate           = vm.Db102AppCommon.FadeRate,
            FadeTime           = vm.Db102AppCommon.FadeTime,
            ExtendedFadeTime   = vm.Db102AppCommon.ExtendedFadeTime,
            RelayCutOffSA      = vm.Db102AppCommon.RelayCutOffSA,
            RelayHvacSA        = vm.Db102AppCommon.RelayHvacSA
        };

        foreach (var t in vm.Db102AppDeviceTypes)
            snap.Db102AppDeviceTypes.Add(new AccessAPP.Models.Dali102AppDeviceTypes {
                ShortAddress = t.ShortAddress, DeviceType0 = t.DeviceType0, DeviceType1 = t.DeviceType1 });

        foreach (var g in vm.Db102AppDeviceGroups)
            snap.Db102AppDeviceGroups.Add(new AccessAPP.Models.Dali102AppDeviceGroups {
                ShortAddress = g.ShortAddress, DaliGroup0 = g.DaliGroup0, DaliGroup1 = g.DaliGroup1 });

        foreach (var s in vm.Db102AppDeviceScenes)
        {
            var entry = new AccessAPP.Models.Dali102AppDeviceScenes { ShortAddress = s.ShortAddress };
            s.ToArray().CopyTo(entry.SceneLevel, 0);
            snap.Db102AppDeviceScenes.Add(entry);
        }

        snap.Db103AppHeader = new AccessAPP.Models.Dali103AppHeader {
            Version   = vm.Db103AppHeader.Version,
            DaliDbLen = vm.Db103AppHeader.DaliDbLen
        };

        foreach (var it in vm.Db103AppInstanceTypes)
        {
            var entry = new AccessAPP.Models.Dali103AppInstanceTypes { ShortAddress = it.ShortAddress };
            it.ToArray().CopyTo(entry.InstanceTypes, 0);
            snap.Db103AppInstanceTypes.Add(entry);
        }

        snap.Db102DeviceHeader  = new AccessAPP.Models.Dali102DeviceHeader  { Version = vm.Db102DeviceHeader.Version };
        snap.Db103DeviceHeader  = new AccessAPP.Models.Dali103DeviceHeader  { Version = vm.Db103DeviceHeader.Version };

        var dg = vm.Db102DeviceGeneral;
        snap.Db102DeviceGeneral = new AccessAPP.Models.Dali102DeviceGeneral {
            LastLightLevel             = dg.LastLightLevel,
            PowerOnLevel               = dg.PowerOnLevel,
            SystemFailureLevel         = dg.SystemFailureLevel,
            MinLevel                   = dg.MinLevel,
            MaxLevel                   = dg.MaxLevel,
            FadeRate                   = dg.FadeRate,
            FadeTime                   = dg.FadeTime,
            ExtendedFadeTimeBase       = dg.ExtendedFadeTimeBase,
            ExtendedFadeTimeMultiplier = dg.ExtendedFadeTimeMultiplier,
            ShortAddress               = dg.ShortAddress,
            RandomAddress              = new[] { dg.RandomAddress0, dg.RandomAddress1, dg.RandomAddress2, dg.RandomAddress3 },
            GearGroups                 = new[] { dg.GearGroup0, dg.GearGroup1 }
        };

        var sc = vm.Db102DeviceScenes;
        snap.Db102DeviceScenes = new AccessAPP.Models.Dali102DeviceScenes {
            SceneLevel = new[] { sc.Scene0,sc.Scene1,sc.Scene2,sc.Scene3,sc.Scene4,sc.Scene5,sc.Scene6,sc.Scene7,
                                 sc.Scene8,sc.Scene9,sc.Scene10,sc.Scene11,sc.Scene12,sc.Scene13,sc.Scene14,sc.Scene15 }
        };

        var dt = vm.Db102DeviceDt208;
        snap.Db102DeviceDt208 = new AccessAPP.Models.Dali102DeviceDt208 {
            UpSwitchOnThreshold    = dt.UpSwitchOnThreshold,
            UpSwitchOffThreshold   = dt.UpSwitchOffThreshold,
            DownSwitchOnThreshold  = dt.DownSwitchOnThreshold,
            DownSwitchOffThreshold = dt.DownSwitchOffThreshold,
            ErrorHoldOffTime       = dt.ErrorHoldOffTime,
            SwitchStatus           = dt.SwitchStatus
        };

        var g3 = vm.Db103DeviceGeneral;
        snap.Db103DeviceGeneral = new AccessAPP.Models.Dali103DeviceGeneral {
            ShortAddress           = g3.ShortAddress,
            DeviceGroups           = new[] { g3.DeviceGroup0, g3.DeviceGroup1, g3.DeviceGroup2, g3.DeviceGroup3 },
            RandomAddress          = new[] { g3.RandomAddress0, g3.RandomAddress1, g3.RandomAddress2, g3.RandomAddress3 },
            OperationMode          = g3.OperationMode,
            ApplicationActive      = g3.ApplicationActive,
            PowerCycleNotification = g3.PowerCycleNotification,
            LuxRange               = g3.LuxRange
        };

        snap.InstanceDataFamily = vm.InstanceDataFamily;
        foreach (var id in vm.InstanceDataSections)
        {
            var entry = new AccessAPP.Models.Dali103InstanceData {
                DbType       = id.DbType,
                NotSupported = id.NotSupported,
                Data         = id.NotSupported ? Array.Empty<byte>() : id.ToRawBytes()
            };
            byte idx = (byte)(id.DbType - 13);
            switch (idx)
            {
                case 0: snap.InstanceData0 = entry; break;
                case 1: snap.InstanceData1 = entry; break;
                case 2: snap.InstanceData2 = entry; break;
                case 3: snap.InstanceData3 = entry; break;
                case 4: snap.InstanceData4 = entry; break;
                case 5: snap.InstanceData5 = entry; break;
                case 6: snap.InstanceData6 = entry; break;
            }
        }

        return snap;
    }

    // ── Instance data field names per §6.1.6 spec ─────────────────────────────

    internal static string[] GetInstanceDataFieldNames(byte dbType, string? family)
    {
        bool sc = string.Equals(family, "StandardComfort", StringComparison.OrdinalIgnoreCase);
        return (dbType - 13) switch
        {
            // InstanceData0 — instance groups
            0 when sc  => Seq("IG0", 6).Concat(Seq("IG1", 6)).Concat(Seq("IG2", 6)).ToArray(),
            0          => Seq("IG0", 2).Concat(Seq("IG1", 2)).Concat(Seq("IG2", 2)).ToArray(),
            // InstanceData1 — active / scheme / priority
            1 when sc  => Seq("Active", 6).Concat(Seq("Scheme", 6)).Concat(Seq("Priority", 6)).ToArray(),
            1          => Seq("Active", 2).Concat(Seq("Scheme", 2)).Concat(Seq("Priority", 2)).ToArray(),
            // InstanceData2 — event filter
            2 when sc  => FilterNames(0, 4),
            2          => FilterNames(0, 2),
            // InstanceData3 — event filter continued (SC only; BMS = NACK)
            3 when sc  => FilterNames(4, 2),
            // InstanceData4 — timing 303 (both families)
            4          => new[] { "tDeadTime303", "tHold303", "tReport303" },
            // InstanceData5 — timing 304 (both families)
            5          => new[] { "tReport304", "tDeadTime304", "HysteresisMin304", "Hysteresis304" },
            // InstanceData6 — push-button timing (SC only; BMS = NACK)
            6 when sc  => Seq("tShort301", 4).Concat(Seq("tDouble301", 4))
                                             .Concat(Seq("tRepeat301", 4))
                                             .Concat(Seq("tStuck301", 4)).ToArray(),
            _          => Array.Empty<string>()
        };
    }

    private static IEnumerable<string> Seq(string prefix, int count)
        => Enumerable.Range(0, count).Select(i => $"{prefix}_{i}");

    private static string[] FilterNames(int startGroup, int groupCount)
        => Enumerable.Range(startGroup, groupCount)
                     .SelectMany(g => Enumerable.Range(0, 4).Select(b => $"Filter{g}_{b}"))
                     .ToArray();
}
