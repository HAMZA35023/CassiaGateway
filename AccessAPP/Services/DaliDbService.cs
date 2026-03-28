using AccessAPP.Models;
using AccessAPP.Services.BleAbstractions;
using AccessAPP.Services.HelperClasses;
using Microsoft.Extensions.Configuration;

namespace AccessAPP.Services;

/// <summary>
/// Reads and writes the full DALI database over BLE using the §6.1 restore-mode protocol
/// (0x0500 enable → 0x0504/0x0506 get/set loop → 0x0502 disable).
///
/// Precondition: the BLE device must already be connected and logged in.
/// </summary>
public sealed class DaliDbService
{
    private readonly IBleReadWriteService    _rw;
    private readonly IBleNotificationService _notif;
    private readonly string                  _gatewayIp;

    private const int BleHandle       = 19;
    private const int DefaultTimeoutMs = 10_000;
    private const int WriteDelayMs     = 100;

    public DaliDbService(
        IBleReadWriteService    readWriteService,
        IBleNotificationService notificationService,
        IConfiguration          configuration)
    {
        _rw        = readWriteService;
        _notif     = notificationService;
        _gatewayIp = configuration.GetValue<string>("GatewayConfiguration:IpAddress") ?? "127.0.0.1";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<DaliDbSnapshot> ReadAsync(string mac, CancellationToken ct = default)
    {
        AppLog.Info($"[DaliDb] READ start for {mac}");

        await EnableAsync(mac, ct).ConfigureAwait(false);
        var snap = new DaliDbSnapshot();

        try
        {
            // ── 102 App header (need DaliDbLen before reading per-SA arrays) ───
            snap.Db102AppHeader = await Read102AppHeaderAsync(mac, ct).ConfigureAwait(false);
            int count102 = snap.Db102AppHeader?.DaliDbLen ?? 0;

            snap.Db102AppCommon = await Read102AppCommonAsync(mac, ct).ConfigureAwait(false);

            for (byte sa = 0; sa < count102; sa++)
            {
                snap.Db102AppDeviceTypes.Add(
                    await Read102AppDeviceTypesAsync(mac, sa, ct).ConfigureAwait(false));
                snap.Db102AppDeviceGroups.Add(
                    await Read102AppDeviceGroupsAsync(mac, sa, ct).ConfigureAwait(false));
                snap.Db102AppDeviceScenes.Add(
                    await Read102AppDeviceScenesAsync(mac, sa, ct).ConfigureAwait(false));
            }

            // ── 103 App ───────────────────────────────────────────────────────
            snap.Db103AppHeader = await Read103AppHeaderAsync(mac, ct).ConfigureAwait(false);
            int count103 = snap.Db103AppHeader?.DaliDbLen ?? 0;

            for (byte sa = 0; sa < count103; sa++)
                snap.Db103AppInstanceTypes.Add(
                    await Read103AppInstanceTypesAsync(mac, sa, ct).ConfigureAwait(false));

            // ── 102 Device ────────────────────────────────────────────────────
            snap.Db102DeviceHeader  = await Read102DeviceHeaderAsync(mac, ct).ConfigureAwait(false);
            snap.Db102DeviceGeneral = await Read102DeviceGeneralAsync(mac, ct).ConfigureAwait(false);
            snap.Db102DeviceScenes  = await Read102DeviceScenesAsync(mac, ct).ConfigureAwait(false);
            snap.Db102DeviceDt208   = await Read102DeviceDt208Async(mac, ct).ConfigureAwait(false);

            // ── 103 Device ────────────────────────────────────────────────────
            snap.Db103DeviceHeader  = await Read103DeviceHeaderAsync(mac, ct).ConfigureAwait(false);
            snap.Db103DeviceGeneral = await Read103DeviceGeneralAsync(mac, ct).ConfigureAwait(false);

            // Instance data 0-6 (family-agnostic – try all, mark unsupported on NACK)
            for (byte idx = 0; idx <= 6; idx++)
            {
                var result = await ReadInstanceDataAsync(mac, (byte)(DaliDbTelegram.DbType103InstanceData0 + idx), ct)
                    .ConfigureAwait(false);
                SetInstanceData(snap, idx, result);
            }

            // Infer family from instance data 0 response size
            if (snap.InstanceData0 is { NotSupported: false } id0)
                snap.InstanceDataFamily = id0.Data.Length >= 18 ? "StandardComfort" : "BmsSlave";
        }
        finally
        {
            await DisableAsync(mac, default).ConfigureAwait(false);
        }

        AppLog.Info($"[DaliDb] READ complete for {mac}");
        return snap;
    }

    public async Task WriteAsync(string mac, DaliDbSnapshot snap, CancellationToken ct = default)
    {
        AppLog.Info($"[DaliDb] WRITE start for {mac}");

        await EnableAsync(mac, ct).ConfigureAwait(false);

        try
        {
            // ── 102 App ───────────────────────────────────────────────────────
            if (snap.Db102AppHeader is { } h102)
                await SetAsync(mac, DaliDbTelegram.DbType102AppHeader,
                    new[] { h102.Version, h102.DaliDbLen }, ct).ConfigureAwait(false);

            foreach (var t in snap.Db102AppDeviceTypes)
                await SetAsync(mac, DaliDbTelegram.DbType102AppDeviceTypes,
                    new[] { t.ShortAddress, t.DeviceType0, t.DeviceType1 }, ct).ConfigureAwait(false);

            foreach (var g in snap.Db102AppDeviceGroups)
                await SetAsync(mac, DaliDbTelegram.DbType102AppDeviceGroups,
                    new[] { g.ShortAddress, g.DaliGroup0, g.DaliGroup1 }, ct).ConfigureAwait(false);

            foreach (var s in snap.Db102AppDeviceScenes)
            {
                var data = new byte[17];
                data[0] = s.ShortAddress;
                s.SceneLevel.CopyTo(data, 1);
                await SetAsync(mac, DaliDbTelegram.DbType102AppDeviceScenes, data, ct).ConfigureAwait(false);
            }

            if (snap.Db102AppCommon is { } c102)
                await SetAsync(mac, DaliDbTelegram.DbType102AppDeviceCommon, new[] {
                    c102.MaxLevel, c102.MinLevel, c102.PowerOnLevel,
                    c102.SystemFailureLevel, c102.FadeRate, c102.FadeTime,
                    c102.ExtendedFadeTime, c102.RelayCutOffSA, c102.RelayHvacSA
                }, ct).ConfigureAwait(false);

            // ── 103 App ───────────────────────────────────────────────────────
            if (snap.Db103AppHeader is { } h103)
                await SetAsync(mac, DaliDbTelegram.DbType103AppHeader,
                    new[] { h103.Version, h103.DaliDbLen }, ct).ConfigureAwait(false);

            foreach (var it in snap.Db103AppInstanceTypes)
            {
                var data = new byte[9];
                data[0] = it.ShortAddress;
                it.InstanceTypes.CopyTo(data, 1);
                await SetAsync(mac, DaliDbTelegram.DbType103AppInstanceTypes, data, ct).ConfigureAwait(false);
            }

            // ── 102 Device ────────────────────────────────────────────────────
            if (snap.Db102DeviceHeader is { } dh102)
                await SetAsync(mac, DaliDbTelegram.DbType102DeviceHeader,
                    new[] { dh102.Version }, ct).ConfigureAwait(false);

            if (snap.Db102DeviceGeneral is { } dg102)
            {
                var data = new byte[16];
                data[0]  = dg102.LastLightLevel;
                data[1]  = dg102.PowerOnLevel;
                data[2]  = dg102.SystemFailureLevel;
                data[3]  = dg102.MinLevel;
                data[4]  = dg102.MaxLevel;
                data[5]  = dg102.FadeRate;
                data[6]  = dg102.FadeTime;
                data[7]  = dg102.ExtendedFadeTimeBase;
                data[8]  = dg102.ExtendedFadeTimeMultiplier;
                data[9]  = dg102.ShortAddress;
                dg102.RandomAddress.CopyTo(data, 10);
                dg102.GearGroups.CopyTo(data, 14);
                await SetAsync(mac, DaliDbTelegram.DbType102DeviceGeneral, data, ct).ConfigureAwait(false);
            }

            if (snap.Db102DeviceScenes is { } ds102)
                await SetAsync(mac, DaliDbTelegram.DbType102DeviceScenes,
                    ds102.SceneLevel, ct).ConfigureAwait(false);

            if (snap.Db102DeviceDt208 is { } dt208)
                await SetAsync(mac, DaliDbTelegram.DbType102DeviceDt208, new[] {
                    dt208.UpSwitchOnThreshold, dt208.UpSwitchOffThreshold,
                    dt208.DownSwitchOnThreshold, dt208.DownSwitchOffThreshold,
                    dt208.ErrorHoldOffTime, dt208.SwitchStatus
                }, ct).ConfigureAwait(false);

            // ── 103 Device ────────────────────────────────────────────────────
            if (snap.Db103DeviceHeader is { } dh103)
                await SetAsync(mac, DaliDbTelegram.DbType103DeviceHeader,
                    new[] { dh103.Version }, ct).ConfigureAwait(false);

            if (snap.Db103DeviceGeneral is { } dg103)
            {
                var data = new byte[11];
                dg103.DeviceGroups.CopyTo(data, 0);
                dg103.RandomAddress.CopyTo(data, 4);
                data[8]  = dg103.ApplicationActive;
                data[9]  = dg103.PowerCycleNotification;
                data[10] = dg103.LuxRange;
                await SetAsync(mac, DaliDbTelegram.DbType103DeviceGeneral, data, ct).ConfigureAwait(false);
            }

            // Instance data: write only non-null, non-unsupported sections
            foreach (var id in new[] {
                snap.InstanceData0, snap.InstanceData1, snap.InstanceData2,
                snap.InstanceData3, snap.InstanceData4, snap.InstanceData5,
                snap.InstanceData6 })
            {
                if (id is null || id.NotSupported || id.Data.Length == 0) continue;
                await SetAsync(mac, id.DbType, id.Data, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await DisableAsync(mac, default).ConfigureAwait(false);
        }

        AppLog.Info($"[DaliDb] WRITE complete for {mac}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Enable / Disable
    // ─────────────────────────────────────────────────────────────────────────

    private async Task EnableAsync(string mac, CancellationToken ct)
    {
        var hex = DaliDbTelegram.BuildEnable();
        var status = await SendAndWaitAsync(mac, hex, DaliDbTelegram.TypeEnableResponse, ct)
            .ConfigureAwait(false);
        if (status != DaliDbTelegram.StatusAck)
            AppLog.Warn($"[DaliDb] Enable NACK (0x{status:X2}) for {mac}");
    }

    private async Task DisableAsync(string mac, CancellationToken ct)
    {
        var hex = DaliDbTelegram.BuildDisable();
        var status = await SendAndWaitAsync(mac, hex, DaliDbTelegram.TypeDisableResponse, ct)
            .ConfigureAwait(false);
        if (status != DaliDbTelegram.StatusAck)
            AppLog.Warn($"[DaliDb] Disable NACK (0x{status:X2}) for {mac}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-section read helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<Dali102AppHeader?> Read102AppHeaderAsync(string mac, CancellationToken ct)
    {
        var (status, _, data) = await GetAsync(mac, DaliDbTelegram.DbType102AppHeader, ct: ct).ConfigureAwait(false);
        if (status != DaliDbTelegram.StatusAck || data.Length < 2) return null;
        return new Dali102AppHeader { Version = data[0], DaliDbLen = data[1] };
    }

    private async Task<Dali102AppDeviceCommon?> Read102AppCommonAsync(string mac, CancellationToken ct)
    {
        var (status, _, data) = await GetAsync(mac, DaliDbTelegram.DbType102AppDeviceCommon, ct: ct).ConfigureAwait(false);
        if (status != DaliDbTelegram.StatusAck || data.Length < 9) return null;
        return new Dali102AppDeviceCommon {
            MaxLevel = data[0], MinLevel = data[1], PowerOnLevel = data[2],
            SystemFailureLevel = data[3], FadeRate = data[4], FadeTime = data[5],
            ExtendedFadeTime = data[6], RelayCutOffSA = data[7], RelayHvacSA = data[8]
        };
    }

    private async Task<Dali102AppDeviceTypes> Read102AppDeviceTypesAsync(string mac, byte sa, CancellationToken ct)
    {
        var (status, _, data) = await GetAsync(mac, DaliDbTelegram.DbType102AppDeviceTypes, sa, ct).ConfigureAwait(false);
        if (status != DaliDbTelegram.StatusAck || data.Length < 3)
            return new Dali102AppDeviceTypes { ShortAddress = sa };
        return new Dali102AppDeviceTypes { ShortAddress = data[0], DeviceType0 = data[1], DeviceType1 = data[2] };
    }

    private async Task<Dali102AppDeviceGroups> Read102AppDeviceGroupsAsync(string mac, byte sa, CancellationToken ct)
    {
        var (status, _, data) = await GetAsync(mac, DaliDbTelegram.DbType102AppDeviceGroups, sa, ct).ConfigureAwait(false);
        if (status != DaliDbTelegram.StatusAck || data.Length < 3)
            return new Dali102AppDeviceGroups { ShortAddress = sa };
        return new Dali102AppDeviceGroups { ShortAddress = data[0], DaliGroup0 = data[1], DaliGroup1 = data[2] };
    }

    private async Task<Dali102AppDeviceScenes> Read102AppDeviceScenesAsync(string mac, byte sa, CancellationToken ct)
    {
        var entry = new Dali102AppDeviceScenes { ShortAddress = sa };
        var (status, _, data) = await GetAsync(mac, DaliDbTelegram.DbType102AppDeviceScenes, sa, ct).ConfigureAwait(false);
        if (status == DaliDbTelegram.StatusAck && data.Length >= 17)
        {
            entry.ShortAddress = data[0];
            Array.Copy(data, 1, entry.SceneLevel, 0, 16);
        }
        return entry;
    }

    private async Task<Dali103AppHeader?> Read103AppHeaderAsync(string mac, CancellationToken ct)
    {
        var (status, _, data) = await GetAsync(mac, DaliDbTelegram.DbType103AppHeader, ct: ct).ConfigureAwait(false);
        if (status != DaliDbTelegram.StatusAck || data.Length < 2) return null;
        return new Dali103AppHeader { Version = data[0], DaliDbLen = data[1] };
    }

    private async Task<Dali103AppInstanceTypes> Read103AppInstanceTypesAsync(string mac, byte sa, CancellationToken ct)
    {
        var entry = new Dali103AppInstanceTypes { ShortAddress = sa };
        var (status, _, data) = await GetAsync(mac, DaliDbTelegram.DbType103AppInstanceTypes, sa, ct).ConfigureAwait(false);
        if (status == DaliDbTelegram.StatusAck && data.Length >= 9)
        {
            entry.ShortAddress = data[0];
            Array.Copy(data, 1, entry.InstanceTypes, 0, 8);
        }
        return entry;
    }

    private async Task<Dali102DeviceHeader?> Read102DeviceHeaderAsync(string mac, CancellationToken ct)
    {
        var (status, _, data) = await GetAsync(mac, DaliDbTelegram.DbType102DeviceHeader, ct: ct).ConfigureAwait(false);
        if (status != DaliDbTelegram.StatusAck || data.Length < 1) return null;
        return new Dali102DeviceHeader { Version = data[0] };
    }

    private async Task<Dali102DeviceGeneral?> Read102DeviceGeneralAsync(string mac, CancellationToken ct)
    {
        var (status, _, data) = await GetAsync(mac, DaliDbTelegram.DbType102DeviceGeneral, ct: ct).ConfigureAwait(false);
        if (status != DaliDbTelegram.StatusAck || data.Length < 16) return null;
        var g = new Dali102DeviceGeneral {
            LastLightLevel             = data[0],
            PowerOnLevel               = data[1],
            SystemFailureLevel         = data[2],
            MinLevel                   = data[3],
            MaxLevel                   = data[4],
            FadeRate                   = data[5],
            FadeTime                   = data[6],
            ExtendedFadeTimeBase       = data[7],
            ExtendedFadeTimeMultiplier = data[8],
            ShortAddress               = data[9],
        };
        Array.Copy(data, 10, g.RandomAddress, 0, 4);
        Array.Copy(data, 14, g.GearGroups,    0, 2);
        return g;
    }

    private async Task<Dali102DeviceScenes?> Read102DeviceScenesAsync(string mac, CancellationToken ct)
    {
        var (status, _, data) = await GetAsync(mac, DaliDbTelegram.DbType102DeviceScenes, ct: ct).ConfigureAwait(false);
        if (status != DaliDbTelegram.StatusAck || data.Length < 16) return null;
        var s = new Dali102DeviceScenes();
        Array.Copy(data, 0, s.SceneLevel, 0, 16);
        return s;
    }

    private async Task<Dali102DeviceDt208?> Read102DeviceDt208Async(string mac, CancellationToken ct)
    {
        var (status, _, data) = await GetAsync(mac, DaliDbTelegram.DbType102DeviceDt208, ct: ct).ConfigureAwait(false);
        if (status != DaliDbTelegram.StatusAck || data.Length < 6) return null;
        return new Dali102DeviceDt208 {
            UpSwitchOnThreshold    = data[0], UpSwitchOffThreshold   = data[1],
            DownSwitchOnThreshold  = data[2], DownSwitchOffThreshold = data[3],
            ErrorHoldOffTime       = data[4], SwitchStatus           = data[5]
        };
    }

    private async Task<Dali103DeviceHeader?> Read103DeviceHeaderAsync(string mac, CancellationToken ct)
    {
        var (status, _, data) = await GetAsync(mac, DaliDbTelegram.DbType103DeviceHeader, ct: ct).ConfigureAwait(false);
        if (status != DaliDbTelegram.StatusAck || data.Length < 1) return null;
        return new Dali103DeviceHeader { Version = data[0] };
    }

    private async Task<Dali103DeviceGeneral?> Read103DeviceGeneralAsync(string mac, CancellationToken ct)
    {
        var (status, _, data) = await GetAsync(mac, DaliDbTelegram.DbType103DeviceGeneral, ct: ct).ConfigureAwait(false);
        if (status != DaliDbTelegram.StatusAck || data.Length < 13) return null;
        var g = new Dali103DeviceGeneral { ShortAddress = data[0] };
        Array.Copy(data, 0, g.DeviceGroups,  0, 4);
        Array.Copy(data, 4, g.RandomAddress, 0, 4);
        g.OperationMode          = data[8];
        g.ApplicationActive      = data[9];
        g.PowerCycleNotification = data[10];
        g.LuxRange               = data[11];
        // Note: spec has ShortAddress at data[0] for 103 device general
        g.ShortAddress = data[0];
        return g;
    }

    private async Task<Dali103InstanceData> ReadInstanceDataAsync(string mac, byte dbType, CancellationToken ct)
    {
        var (status, _, data) = await GetAsync(mac, dbType, ct: ct).ConfigureAwait(false);
        return new Dali103InstanceData
        {
            DbType       = dbType,
            Data         = data,
            NotSupported = status == DaliDbTelegram.StatusNackNotAllowed
        };
    }

    private static void SetInstanceData(DaliDbSnapshot snap, byte idx, Dali103InstanceData val)
    {
        switch (idx)
        {
            case 0: snap.InstanceData0 = val; break;
            case 1: snap.InstanceData1 = val; break;
            case 2: snap.InstanceData2 = val; break;
            case 3: snap.InstanceData3 = val; break;
            case 4: snap.InstanceData4 = val; break;
            case 5: snap.InstanceData5 = val; break;
            case 6: snap.InstanceData6 = val; break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Low-level BLE helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a 0x0504 GetParameters telegram and returns (status, dataLen, data).
    /// </summary>
    private async Task<(byte Status, byte DataLen, byte[] Data)> GetAsync(
        string mac, byte dbType, byte shortAddress = 0, CancellationToken ct = default)
    {
        var expectedTypeHex = TypeToHex(DaliDbTelegram.TypeGetParamsResponse);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = _notif.Subscribe(mac, (_, raw) =>
        {
            if (raw?.Length >= 6 && raw.Substring(2, 4).Equals(expectedTypeHex, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(raw);
        });

        try
        {
            var hex = DaliDbTelegram.BuildGet(dbType, shortAddress);
            using var resp = await _rw.WriteBleMessage(_gatewayIp, mac, BleHandle, hex, "?noresponse=1", ct: ct)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Warn($"[DaliDb] GET dbType={dbType} BLE write failed ({resp.StatusCode}) for {mac}");
                return (0xFF, 0, Array.Empty<byte>());
            }

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(DefaultTimeoutMs, ct)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                AppLog.Warn($"[DaliDb] GET dbType={dbType} timeout for {mac}");
                return (0xFF, 0, Array.Empty<byte>());
            }

            return DaliDbTelegram.ParseGetResponse(await tcs.Task);
        }
        finally
        {
            _notif.Unsubscribe(mac, token);
        }
    }

    /// <summary>
    /// Sends a 0x0506 SetParameters telegram and waits for 0x0507 ACK.
    /// </summary>
    private async Task SetAsync(string mac, byte dbType, byte[] data, CancellationToken ct)
    {
        await Task.Delay(WriteDelayMs, ct).ConfigureAwait(false); // throttle writes

        var expectedTypeHex = TypeToHex(DaliDbTelegram.TypeSetParamsResponse);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = _notif.Subscribe(mac, (_, raw) =>
        {
            if (raw?.Length >= 6 && raw.Substring(2, 4).Equals(expectedTypeHex, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(raw);
        });

        try
        {
            var hex = DaliDbTelegram.BuildSet(dbType, data);
            using var resp = await _rw.WriteBleMessage(_gatewayIp, mac, BleHandle, hex, "?noresponse=1", ct: ct)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Warn($"[DaliDb] SET dbType={dbType} BLE write failed ({resp.StatusCode}) for {mac}");
                return;
            }

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(DefaultTimeoutMs, ct)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                AppLog.Warn($"[DaliDb] SET dbType={dbType} timeout for {mac}");
                return;
            }

            var status = DaliDbTelegram.ParseStatusResponse(await tcs.Task);
            if (status != DaliDbTelegram.StatusAck)
                AppLog.Warn($"[DaliDb] SET dbType={dbType} NACK (0x{status:X2}) for {mac}");
        }
        finally
        {
            _notif.Unsubscribe(mac, token);
        }
    }

    /// <summary>
    /// Sends a telegram and waits for a response of <paramref name="expectedType"/>.
    /// Returns the status byte (byte 7) of the response.
    /// </summary>
    private async Task<byte> SendAndWaitAsync(string mac, string hexCmd, ushort expectedType, CancellationToken ct)
    {
        var expectedTypeHex = TypeToHex(expectedType);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = _notif.Subscribe(mac, (_, raw) =>
        {
            if (raw?.Length >= 6 && raw.Substring(2, 4).Equals(expectedTypeHex, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(raw);
        });

        try
        {
            using var resp = await _rw.WriteBleMessage(_gatewayIp, mac, BleHandle, hexCmd, "?noresponse=1", ct: ct)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Warn($"[DaliDb] SendAndWait BLE write failed ({resp.StatusCode}) for {mac}");
                return 0xFF;
            }

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(DefaultTimeoutMs, ct)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                AppLog.Warn($"[DaliDb] SendAndWait timeout (expected 0x{expectedType:X4}) for {mac}");
                return 0xFF;
            }

            return DaliDbTelegram.ParseStatusResponse(await tcs.Task);
        }
        finally
        {
            _notif.Unsubscribe(mac, token);
        }
    }

    /// <summary>Converts a ushort telegram type to its 4-char little-endian hex representation.</summary>
    private static string TypeToHex(ushort type)
    {
        // Stored LE in telegram: low byte first
        byte lo = (byte)(type & 0xFF);
        byte hi = (byte)(type >> 8);
        return $"{lo:X2}{hi:X2}";
    }
}
