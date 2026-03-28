using AccessAppMqttWpf.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace AccessAppMqttWpf.ViewModels;

public partial class DaliDbEditorViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel   _main;
    private readonly DiscoveredDevice _device;

    public DaliDbSnapshotVm Snapshot { get; }

    public string Title => $"DALI DB Editor  •  {(_device.Mac ?? "").Trim().ToUpperInvariant()}";

    [ObservableProperty] private string statusText = "Loaded from device.";
    [ObservableProperty] private bool   isBusy;

    public event Action? RequestClose;

    public DaliDbEditorViewModel(MainViewModel main, DiscoveredDevice device, DaliDbSnapshotVm snapshot)
    {
        _main    = main    ?? throw new ArgumentNullException(nameof(main));
        _device  = device  ?? throw new ArgumentNullException(nameof(device));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task SaveToDevice()
    {
        IsBusy     = true;
        StatusText = "Writing DALI DB to device…";
        try
        {
            await _main.SendDaliDbWriteAsync(_device, Snapshot);
            StatusText = _main.ConnectionStatus;
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsNotBusy => !IsBusy;

    [RelayCommand]
    private void ExportJson()
    {
        var dlg = new SaveFileDialog
        {
            Title            = "Export DALI DB as JSON",
            Filter           = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt       = ".json",
            FileName         = $"dali-db-{(_device.Mac ?? "device").Trim()}.json"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var snap = DaliDbMapper.FromVm(Snapshot);
            var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            StatusText = $"Exported to {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed:\n{ex.Message}", "Export Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ImportJson()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Import DALI DB from JSON",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var snap = JsonSerializer.Deserialize<AccessAPP.Models.DaliDbSnapshot>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (snap is null) { StatusText = "Import failed: empty or invalid JSON."; return; }

            var imported = DaliDbMapper.ToVm(snap);
            ApplyImport(imported);
            StatusText = $"Imported from {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed:\n{ex.Message}", "Import Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    // ── Import helper — copy all observable fields from imported VM ───────────

    private void ApplyImport(DaliDbSnapshotVm src)
    {
        // 102 App Header
        Snapshot.Db102AppHeader.Version   = src.Db102AppHeader.Version;
        Snapshot.Db102AppHeader.DaliDbLen = src.Db102AppHeader.DaliDbLen;

        // 102 App Common
        var d = src.Db102AppCommon;
        var s = Snapshot.Db102AppCommon;
        s.MaxLevel = d.MaxLevel; s.MinLevel = d.MinLevel;
        s.PowerOnLevel = d.PowerOnLevel; s.SystemFailureLevel = d.SystemFailureLevel;
        s.FadeRate = d.FadeRate; s.FadeTime = d.FadeTime;
        s.ExtendedFadeTime = d.ExtendedFadeTime;
        s.RelayCutOffSA = d.RelayCutOffSA; s.RelayHvacSA = d.RelayHvacSA;

        // 102 App per-device collections — replace entirely
        Snapshot.Db102AppDeviceTypes.Clear();
        foreach (var t in src.Db102AppDeviceTypes)
            Snapshot.Db102AppDeviceTypes.Add(t);

        Snapshot.Db102AppDeviceGroups.Clear();
        foreach (var g in src.Db102AppDeviceGroups)
            Snapshot.Db102AppDeviceGroups.Add(g);

        Snapshot.Db102AppDeviceScenes.Clear();
        foreach (var sc in src.Db102AppDeviceScenes)
            Snapshot.Db102AppDeviceScenes.Add(sc);

        // 103 App Header
        Snapshot.Db103AppHeader.Version   = src.Db103AppHeader.Version;
        Snapshot.Db103AppHeader.DaliDbLen = src.Db103AppHeader.DaliDbLen;

        Snapshot.Db103AppInstanceTypes.Clear();
        foreach (var it in src.Db103AppInstanceTypes)
            Snapshot.Db103AppInstanceTypes.Add(it);

        // 102 Device Header
        Snapshot.Db102DeviceHeader.Version = src.Db102DeviceHeader.Version;

        // 102 Device General
        var dg = src.Db102DeviceGeneral;
        var sg = Snapshot.Db102DeviceGeneral;
        sg.LastLightLevel = dg.LastLightLevel; sg.PowerOnLevel = dg.PowerOnLevel;
        sg.SystemFailureLevel = dg.SystemFailureLevel; sg.MinLevel = dg.MinLevel;
        sg.MaxLevel = dg.MaxLevel; sg.FadeRate = dg.FadeRate; sg.FadeTime = dg.FadeTime;
        sg.ExtendedFadeTimeBase = dg.ExtendedFadeTimeBase;
        sg.ExtendedFadeTimeMultiplier = dg.ExtendedFadeTimeMultiplier;
        sg.ShortAddress = dg.ShortAddress;
        sg.RandomAddress = dg.RandomAddress; sg.GearGroups = dg.GearGroups;

        // 102 Device Scenes
        var ds = src.Db102DeviceScenes;
        var ss = Snapshot.Db102DeviceScenes;
        ss.Scene0=ds.Scene0; ss.Scene1=ds.Scene1; ss.Scene2=ds.Scene2; ss.Scene3=ds.Scene3;
        ss.Scene4=ds.Scene4; ss.Scene5=ds.Scene5; ss.Scene6=ds.Scene6; ss.Scene7=ds.Scene7;
        ss.Scene8=ds.Scene8; ss.Scene9=ds.Scene9; ss.Scene10=ds.Scene10; ss.Scene11=ds.Scene11;
        ss.Scene12=ds.Scene12; ss.Scene13=ds.Scene13; ss.Scene14=ds.Scene14; ss.Scene15=ds.Scene15;

        // 102 Device DT208
        var dt = src.Db102DeviceDt208;
        var st = Snapshot.Db102DeviceDt208;
        st.UpSwitchOnThreshold=dt.UpSwitchOnThreshold; st.UpSwitchOffThreshold=dt.UpSwitchOffThreshold;
        st.DownSwitchOnThreshold=dt.DownSwitchOnThreshold; st.DownSwitchOffThreshold=dt.DownSwitchOffThreshold;
        st.ErrorHoldOffTime=dt.ErrorHoldOffTime; st.SwitchStatus=dt.SwitchStatus;

        // 103 Device Header
        Snapshot.Db103DeviceHeader.Version = src.Db103DeviceHeader.Version;

        // 103 Device General
        var d3 = src.Db103DeviceGeneral;
        var s3 = Snapshot.Db103DeviceGeneral;
        s3.ShortAddress=d3.ShortAddress; s3.DeviceGroups=d3.DeviceGroups;
        s3.RandomAddress=d3.RandomAddress; s3.OperationMode=d3.OperationMode;
        s3.ApplicationActive=d3.ApplicationActive; s3.PowerCycleNotification=d3.PowerCycleNotification;
        s3.LuxRange=d3.LuxRange;

        // Instance data
        Snapshot.InstanceDataFamily = src.InstanceDataFamily;
        Snapshot.InstanceDataSections.Clear();
        foreach (var id in src.InstanceDataSections)
            Snapshot.InstanceDataSections.Add(id);
    }

    public void Dispose() { }
}
