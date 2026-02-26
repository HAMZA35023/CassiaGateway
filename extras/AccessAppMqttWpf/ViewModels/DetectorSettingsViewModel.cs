using AccessAppMqttWpf.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace AccessAppMqttWpf.ViewModels;

public partial class DetectorSettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DiscoveredDevice? _device;
    private bool _suppressTunableWhiteAutoApply;
    private static readonly Regex DetectorModelRegex = new(@"^[PM]\d{2}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const int TunableWhiteMinKelvin = 1800;
    private const int TunableWhiteMaxKelvin = 6500;
    private const int TunableWhiteNoChangeKelvin = 65535;
    private const int TunableWhiteKelvinStep = 100;
    private const int TunableWhiteMinLux = 20;
    private const int TunableWhiteMaxLux = 2000;
    private const int TunableWhiteNoChangeLux = 65535;
    private const int TunableWhiteLuxStep = 10;
    private const int TunableWhiteListSetPayloadLength = 99;
    private const int TunableWhitePresetSetPayloadLength = 18;
    private const int TunableWhiteDefaultKelvinSetPayloadLength = 4;

    public DetectorSettingsViewModel(MainViewModel main, DiscoveredDevice? device)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _device = device;

        if (_device != null)
        {
            MacAddress = (_device.Mac ?? "").Trim().ToUpperInvariant();
            CassiaName = ResolveCassiaName(_device);
            DetectorType = ResolveDetectorType(_device);
            FirmwareVersion = (_device.ProcessFirmware ?? "").Trim();
            StatusText = "Read current settings to load baseline and edit specific fields.";
        }
        else
        {
            StatusText = "New profile mode. Select fields and values, then save profile.";
        }

        InitializeFieldRows();
    }

    public string WindowTitle
        => string.IsNullOrWhiteSpace(MacAddress)
            ? "Detector Settings - New Profile"
            : $"Detector Settings - {MacAddress}";
    public event Action? RequestClose;

    public ObservableCollection<DetectorFieldRowViewModel> UserConfigRows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> WiredPushButtonsRows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> BlePushButtonsRows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> DaliPushButtonsRows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> DaliDeviceCommonRows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> UserGeneralRows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> UserZone1Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> UserZone2Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> UserZone3Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> UserZone4Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> UserZone5Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> WiredGeneralRows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> WiredPb1Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> WiredPb2Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> WiredPb3Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> WiredPb4Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> BleGeneralRows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> BlePb1Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> BlePb2Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> BlePb3Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> BlePb4Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> DaliGeneralRows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> DaliPb1Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> DaliPb2Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> DaliPb3Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> DaliPb4Rows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> DaliCommonLevelRows { get; } = new();
    public ObservableCollection<DetectorFieldRowViewModel> DaliCommonFadeRows { get; } = new();
    public ObservableCollection<TunableWhiteHourPointViewModel> TunableWhiteHourPoints { get; } = new();

    [ObservableProperty] private string macAddress = "";
    [ObservableProperty] private string cassiaName = "";
    [ObservableProperty] private string detectorType = "";
    [ObservableProperty] private string firmwareVersion = "";
    [ObservableProperty] private string pincode = "";
    [ObservableProperty] private bool writeOnlyChanged = true;
    [ObservableProperty] private bool runDaliAddressAllToZone1AfterUpdate;
    [ObservableProperty] private bool runDali102TotalNewScanAfterUpdate;
    [ObservableProperty] private bool runDali103TotalNewScanAfterUpdate;
    [ObservableProperty] private string profilePath = "";
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private bool isBusy;

    [ObservableProperty] private int selectedUserFieldCount;
    [ObservableProperty] private int selectedWiredFieldCount;
    [ObservableProperty] private int selectedBleFieldCount;
    [ObservableProperty] private int selectedDaliFieldCount;
    [ObservableProperty] private int selectedDaliCommonFieldCount;
    [ObservableProperty] private int selectedTunableWhiteSectionCount;

    [ObservableProperty] private bool applyTunableWhiteList;
    [ObservableProperty] private bool applyTunableWhitePreset;
    [ObservableProperty] private bool applyTunableWhiteDefaultKelvin;
    [ObservableProperty] private int tunableWhiteListVersion = 2;
    [ObservableProperty] private bool tunableWhiteListEnabled = true;
    [ObservableProperty] private int tunableWhiteScheduleAllKelvin = 4000;
    [ObservableProperty] private int tunableWhiteScheduleAllLux = 500;
    [ObservableProperty] private int tunableWhitePresetVersion = 2;
    [ObservableProperty] private int tunableWhitePreset1Kelvin = 3000;
    [ObservableProperty] private int tunableWhitePreset1Lux = 500;
    [ObservableProperty] private int tunableWhitePreset2Kelvin = 3500;
    [ObservableProperty] private int tunableWhitePreset2Lux = 500;
    [ObservableProperty] private int tunableWhitePreset3Kelvin = 4500;
    [ObservableProperty] private int tunableWhitePreset3Lux = 500;
    [ObservableProperty] private int tunableWhitePreset4Kelvin = 5500;
    [ObservableProperty] private int tunableWhitePreset4Lux = 500;
    [ObservableProperty] private int tunableWhiteDefaultKelvinVersion = 1;
    [ObservableProperty] private int tunableWhiteDefaultKelvin = 4000;

    public bool CanQueueUpdateWithProfile => _device != null;
    public Brush TunableWhitePreset1Brush => CreateKelvinBrush(TunableWhitePreset1Kelvin);
    public Brush TunableWhitePreset1LuxBrush => CreateLuxBrush(TunableWhitePreset1Lux);
    public Brush TunableWhitePreset2Brush => CreateKelvinBrush(TunableWhitePreset2Kelvin);
    public Brush TunableWhitePreset2LuxBrush => CreateLuxBrush(TunableWhitePreset2Lux);
    public Brush TunableWhitePreset3Brush => CreateKelvinBrush(TunableWhitePreset3Kelvin);
    public Brush TunableWhitePreset3LuxBrush => CreateLuxBrush(TunableWhitePreset3Lux);
    public Brush TunableWhitePreset4Brush => CreateKelvinBrush(TunableWhitePreset4Kelvin);
    public Brush TunableWhitePreset4LuxBrush => CreateLuxBrush(TunableWhitePreset4Lux);
    public Brush TunableWhiteDefaultKelvinBrush => CreateKelvinBrush(TunableWhiteDefaultKelvin);

    partial void OnApplyTunableWhiteListChanged(bool value) => UpdateSelectionCounters();
    partial void OnApplyTunableWhitePresetChanged(bool value) => UpdateSelectionCounters();
    partial void OnApplyTunableWhiteDefaultKelvinChanged(bool value) => UpdateSelectionCounters();

    partial void OnTunableWhiteScheduleAllKelvinChanged(int value)
    {
        TunableWhiteScheduleAllKelvin = ClampKelvin(value);
        MarkTunableWhiteListEdited();
    }

    partial void OnTunableWhiteScheduleAllLuxChanged(int value)
    {
        TunableWhiteScheduleAllLux = ClampLux(value);
        MarkTunableWhiteListEdited();
    }

    partial void OnTunableWhiteListVersionChanged(int value) => MarkTunableWhiteListEdited();
    partial void OnTunableWhiteListEnabledChanged(bool value) => MarkTunableWhiteListEdited();
    partial void OnTunableWhitePresetVersionChanged(int value) => MarkTunableWhitePresetEdited();

    partial void OnTunableWhitePreset1KelvinChanged(int value)
    {
        TunableWhitePreset1Kelvin = ClampKelvin(value);
        OnPropertyChanged(nameof(TunableWhitePreset1Brush));
        MarkTunableWhitePresetEdited();
    }

    partial void OnTunableWhitePreset1LuxChanged(int value)
    {
        TunableWhitePreset1Lux = ClampLux(value);
        OnPropertyChanged(nameof(TunableWhitePreset1LuxBrush));
        MarkTunableWhitePresetEdited();
    }

    partial void OnTunableWhitePreset2KelvinChanged(int value)
    {
        TunableWhitePreset2Kelvin = ClampKelvin(value);
        OnPropertyChanged(nameof(TunableWhitePreset2Brush));
        MarkTunableWhitePresetEdited();
    }

    partial void OnTunableWhitePreset2LuxChanged(int value)
    {
        TunableWhitePreset2Lux = ClampLux(value);
        OnPropertyChanged(nameof(TunableWhitePreset2LuxBrush));
        MarkTunableWhitePresetEdited();
    }

    partial void OnTunableWhitePreset3KelvinChanged(int value)
    {
        TunableWhitePreset3Kelvin = ClampKelvin(value);
        OnPropertyChanged(nameof(TunableWhitePreset3Brush));
        MarkTunableWhitePresetEdited();
    }

    partial void OnTunableWhitePreset3LuxChanged(int value)
    {
        TunableWhitePreset3Lux = ClampLux(value);
        OnPropertyChanged(nameof(TunableWhitePreset3LuxBrush));
        MarkTunableWhitePresetEdited();
    }

    partial void OnTunableWhitePreset4KelvinChanged(int value)
    {
        TunableWhitePreset4Kelvin = ClampKelvin(value);
        OnPropertyChanged(nameof(TunableWhitePreset4Brush));
        MarkTunableWhitePresetEdited();
    }

    partial void OnTunableWhitePreset4LuxChanged(int value)
    {
        TunableWhitePreset4Lux = ClampLux(value);
        OnPropertyChanged(nameof(TunableWhitePreset4LuxBrush));
        MarkTunableWhitePresetEdited();
    }

    partial void OnTunableWhiteDefaultKelvinChanged(int value)
    {
        TunableWhiteDefaultKelvin = ClampKelvin(value);
        OnPropertyChanged(nameof(TunableWhiteDefaultKelvinBrush));
    }

    [RelayCommand]
    private void ApplyTunableWhiteScheduleToAllHours()
    {
        ApplyTunableWhiteScheduleKelvinToAllHours();
        ApplyTunableWhiteScheduleLuxToAllHours();
    }

    [RelayCommand]
    private void ApplyTunableWhiteScheduleKelvinToAllHours()
    {
        MarkTunableWhiteListEdited();
        var kelvin = ClampKelvin(TunableWhiteScheduleAllKelvin);
        foreach (var point in TunableWhiteHourPoints)
            point.Kelvin = kelvin;
    }

    [RelayCommand]
    private void ApplyTunableWhiteScheduleLuxToAllHours()
    {
        MarkTunableWhiteListEdited();
        var lux = ClampLux(TunableWhiteScheduleAllLux);
        foreach (var point in TunableWhiteHourPoints)
            point.Lux = lux;
    }

    [RelayCommand]
    private async Task ReadCurrent()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(CassiaName) || string.IsNullOrWhiteSpace(MacAddress))
        {
            StatusText = "Cassia or MAC address is missing.";
            return;
        }

        IsBusy = true;
        StatusText = "Reading detector settings...";

        try
        {
            var result = await _main.RequestDetectorSettingsAsync(
                CassiaName,
                MacAddress,
                DetectorType,
                Pincode,
                FirmwareVersion);

            if (result == null)
            {
                StatusText = "No response from detector.";
                return;
            }

            LoadRowsFromPatch(DetectorSettingsPatchModel.FromJsonNode(result.Settings));

            if (!string.IsNullOrWhiteSpace(result.DetectorType))
                DetectorType = result.DetectorType;
            if (!string.IsNullOrWhiteSpace(result.FirmwareVersion))
                FirmwareVersion = result.FirmwareVersion;

            StatusText = result.Message;
        }
        catch (Exception ex)
        {
            StatusText = "Read failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyNow()
    {
        if (IsBusy) return;

        DetectorSettingsPatchModel patch;
        try
        {
            patch = BuildPatchFromSelectedFields();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return;
        }

        if (!patch.HasAnyValue)
        {
            StatusText = "Select at least one field to overwrite.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CassiaName) || string.IsNullOrWhiteSpace(MacAddress))
        {
            StatusText = "Cassia name or MAC address is missing.";
            return;
        }

        IsBusy = true;
        StatusText = "Applying detector settings...";

        try
        {
            var result = await _main.ApplyDetectorSettingsAsync(
                CassiaName,
                MacAddress,
                DetectorType,
                Pincode,
                FirmwareVersion,
                patch,
                WriteOnlyChanged);

            if (result == null)
            {
                StatusText = "No response from detector.";
                return;
            }

            if (result.Settings != null)
                LoadRowsFromPatch(DetectorSettingsPatchModel.FromJsonNode(result.Settings));

            StatusText = result.Message;
        }
        catch (Exception ex)
        {
            StatusText = "Apply failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task QueueUpdateWithProfile()
    {
        if (IsBusy) return;

        DetectorSettingsPatchModel patch;
        try
        {
            patch = BuildPatchFromSelectedFields();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return;
        }

        var hasAnyPostUpdateRun =
            RunDaliAddressAllToZone1AfterUpdate
            || RunDali102TotalNewScanAfterUpdate
            || RunDali103TotalNewScanAfterUpdate;

        if (!patch.HasAnyValue && !hasAnyPostUpdateRun)
        {
            StatusText = "Select at least one settings field or post-update profile run option.";
            return;
        }

        if (_device == null)
        {
            StatusText = "Queue update requires a selected detector. Open settings from a device row for that flow.";
            return;
        }

        IsBusy = true;
        StatusText = "Queueing update with post-update settings profile...";
        try
        {
            await _main.QueueDeviceAndRequestWithDetectorSettingsAsync(
                _device,
                patch,
                RunDaliAddressAllToZone1AfterUpdate,
                RunDali102TotalNewScanAfterUpdate,
                RunDali103TotalNewScanAfterUpdate);
            StatusText = "Update queued with detector settings profile.";
        }
        catch (Exception ex)
        {
            StatusText = "Queue update failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void LoadProfile()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Detector settings profile (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dlg.ShowDialog() != true)
                return;

            var json = File.ReadAllText(dlg.FileName);
            var profile = DetectorSettingsProfileModel.Parse(json);
            if (profile == null)
            {
                StatusText = "Profile file could not be parsed.";
                return;
            }

            ProfilePath = dlg.FileName;
            if (!string.IsNullOrWhiteSpace(profile.DetectorType))
                DetectorType = profile.DetectorType!.Trim();
            if (!string.IsNullOrWhiteSpace(profile.FirmwareVersion))
                FirmwareVersion = profile.FirmwareVersion!.Trim();
            WriteOnlyChanged = profile.WriteOnlyChanged;
            RunDaliAddressAllToZone1AfterUpdate = profile.RunDaliAddressAllToZone1AfterUpdate;
            RunDali102TotalNewScanAfterUpdate = profile.RunDali102TotalNewScanAfterUpdate;
            RunDali103TotalNewScanAfterUpdate = profile.RunDali103TotalNewScanAfterUpdate;
            var profileSettings = (profile.Settings ?? new DetectorSettingsPatchModel()).CloneNormalized();

            ClearAllSelections();

            var overrides = profile.FieldOverrides ?? new List<DetectorSettingsFieldOverrideModel>();
            if (overrides.Count > 0)
            {
                var rowByKey = GetAllRows().ToDictionary(r => r.Key, r => r, StringComparer.OrdinalIgnoreCase);
                foreach (var ov in overrides)
                {
                    if (ov == null || string.IsNullOrWhiteSpace(ov.Key))
                        continue;
                    if (!rowByKey.TryGetValue(ov.Key, out var row))
                        continue;
                    if (row.TryImportProfileValue(ov.Value))
                        row.IsSelected = true;
                }

                LoadTunableWhiteFromPatch(profileSettings);
            }
            else
            {
                // Backward compatibility with legacy section-level profiles.
                if (profileSettings.HasAnyValue)
                    LoadRowsFromPatch(profileSettings);

                if (profile.ApplyUserConfig)
                    SelectAll(UserConfigRows);
                if (profile.ApplyPushButtons)
                    SelectAll(WiredPushButtonsRows);
                if (profile.ApplyBlePushButtons)
                    SelectAll(BlePushButtonsRows);
                if (profile.ApplyDaliPushButtons)
                    SelectAll(DaliPushButtonsRows);
                if (profile.ApplyDaliDeviceCommonParam)
                    SelectAll(DaliDeviceCommonRows);
            }

            ApplyTunableWhiteList = profile.ApplyTunableWhiteList || !string.IsNullOrWhiteSpace(profileSettings.TunableWhiteListHex);
            ApplyTunableWhitePreset = profile.ApplyTunableWhitePreset || !string.IsNullOrWhiteSpace(profileSettings.TunableWhitePresetHex);
            ApplyTunableWhiteDefaultKelvin = profile.ApplyTunableWhiteDefaultKelvin || !string.IsNullOrWhiteSpace(profileSettings.TunableWhiteDefaultKelvinHex);

            UpdateSelectionCounters();
            StatusText = "Profile loaded.";
        }
        catch (Exception ex)
        {
            StatusText = "Load profile failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void SaveProfile()
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Detector settings profile (*.json)|*.json|All files (*.*)|*.*",
                FileName = string.IsNullOrWhiteSpace(ProfilePath)
                    ? $"detector-settings-{DateTime.Now:yyyyMMdd_HHmmss}.json"
                    : Path.GetFileName(ProfilePath)
            };

            if (dlg.ShowDialog() != true)
                return;

            var fieldOverrides = GetAllRows()
                .Where(r => r.IsSelected)
                .Select(r => new DetectorSettingsFieldOverrideModel
                {
                    Key = r.Key,
                    Value = r.ExportProfileValue()
                })
                .ToList();

            var patch = BuildPatchFromSelectedFields(throwOnValidationErrors: false);

            var profile = new DetectorSettingsProfileModel
            {
                Version = "2",
                Name = Path.GetFileNameWithoutExtension(dlg.FileName),
                DetectorType = DetectorType?.Trim(),
                FirmwareVersion = FirmwareVersion?.Trim(),
                WriteOnlyChanged = WriteOnlyChanged,
                FieldOverrides = fieldOverrides,
                Settings = patch,
                ApplyUserConfig = !string.IsNullOrWhiteSpace(patch.UserConfigHex),
                ApplyPushButtons = !string.IsNullOrWhiteSpace(patch.PushButtonsHex),
                ApplyDaliPushButtons = !string.IsNullOrWhiteSpace(patch.DaliPushButtonsHex),
                ApplyDaliDeviceCommonParam = !string.IsNullOrWhiteSpace(patch.DaliDeviceCommonParamHex),
                ApplyBlePushButtons = !string.IsNullOrWhiteSpace(patch.BlePushButtonsHex),
                ApplyTunableWhiteList = ApplyTunableWhiteList && !string.IsNullOrWhiteSpace(patch.TunableWhiteListHex),
                ApplyTunableWhitePreset = ApplyTunableWhitePreset && !string.IsNullOrWhiteSpace(patch.TunableWhitePresetHex),
                ApplyTunableWhiteDefaultKelvin = ApplyTunableWhiteDefaultKelvin && !string.IsNullOrWhiteSpace(patch.TunableWhiteDefaultKelvinHex),
                RunDaliAddressAllToZone1AfterUpdate = RunDaliAddressAllToZone1AfterUpdate,
                RunDali102TotalNewScanAfterUpdate = RunDali102TotalNewScanAfterUpdate,
                RunDali103TotalNewScanAfterUpdate = RunDali103TotalNewScanAfterUpdate
            };

            File.WriteAllText(dlg.FileName, profile.ToJson());
            ProfilePath = dlg.FileName;
            StatusText = "Profile saved.";
        }
        catch (Exception ex)
        {
            StatusText = "Save profile failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    private void InitializeFieldRows()
    {
        AddRows(UserConfigRows, DetectorSettingsFieldCatalog.UserConfigFields);
        AddRows(WiredPushButtonsRows, DetectorSettingsFieldCatalog.WiredPushButtonsFields);
        AddRows(BlePushButtonsRows, DetectorSettingsFieldCatalog.BlePushButtonsFields);
        AddRows(DaliPushButtonsRows, DetectorSettingsFieldCatalog.DaliPushButtonsFields);
        AddRows(DaliDeviceCommonRows, DetectorSettingsFieldCatalog.DaliDeviceCommonParamFields);
        InitializeTunableWhiteHours();
        BuildTabCollections();
        UpdateSelectionCounters();
    }

    private void BuildTabCollections()
    {
        FillFiltered(UserGeneralRows, UserConfigRows, row => row.Group.Equals("General", StringComparison.OrdinalIgnoreCase));
        FillFiltered(UserZone1Rows, UserConfigRows, row => row.Group.Equals("Zone 1", StringComparison.OrdinalIgnoreCase));
        FillFiltered(UserZone2Rows, UserConfigRows, row => row.Group.Equals("Zone 2", StringComparison.OrdinalIgnoreCase));
        FillFiltered(UserZone3Rows, UserConfigRows, row => row.Group.Equals("Zone 3", StringComparison.OrdinalIgnoreCase));
        FillFiltered(UserZone4Rows, UserConfigRows, row => row.Group.Equals("Zone 4", StringComparison.OrdinalIgnoreCase));
        FillFiltered(UserZone5Rows, UserConfigRows, row => row.Group.Equals("Zone 5", StringComparison.OrdinalIgnoreCase));

        FillFiltered(WiredGeneralRows, WiredPushButtonsRows, row => row.Group.Equals("General", StringComparison.OrdinalIgnoreCase));
        FillFiltered(WiredPb1Rows, WiredPushButtonsRows, row => row.Group.Equals("Wired PB 1", StringComparison.OrdinalIgnoreCase));
        FillFiltered(WiredPb2Rows, WiredPushButtonsRows, row => row.Group.Equals("Wired PB 2", StringComparison.OrdinalIgnoreCase));
        FillFiltered(WiredPb3Rows, WiredPushButtonsRows, row => row.Group.Equals("Wired PB 3", StringComparison.OrdinalIgnoreCase));
        FillFiltered(WiredPb4Rows, WiredPushButtonsRows, row => row.Group.Equals("Wired PB 4", StringComparison.OrdinalIgnoreCase));

        FillFiltered(BleGeneralRows, BlePushButtonsRows, row => row.Group.Equals("General", StringComparison.OrdinalIgnoreCase));
        FillFiltered(BlePb1Rows, BlePushButtonsRows, row => row.Group.StartsWith("BLE PB 1", StringComparison.OrdinalIgnoreCase));
        FillFiltered(BlePb2Rows, BlePushButtonsRows, row => row.Group.StartsWith("BLE PB 2", StringComparison.OrdinalIgnoreCase));
        FillFiltered(BlePb3Rows, BlePushButtonsRows, row => row.Group.StartsWith("BLE PB 3", StringComparison.OrdinalIgnoreCase));
        FillFiltered(BlePb4Rows, BlePushButtonsRows, row => row.Group.StartsWith("BLE PB 4", StringComparison.OrdinalIgnoreCase));

        FillFiltered(DaliGeneralRows, DaliPushButtonsRows, row => row.Group.Equals("General", StringComparison.OrdinalIgnoreCase));
        FillFiltered(DaliPb1Rows, DaliPushButtonsRows, row => row.Group.StartsWith("DALI PB 1", StringComparison.OrdinalIgnoreCase));
        FillFiltered(DaliPb2Rows, DaliPushButtonsRows, row => row.Group.StartsWith("DALI PB 2", StringComparison.OrdinalIgnoreCase));
        FillFiltered(DaliPb3Rows, DaliPushButtonsRows, row => row.Group.StartsWith("DALI PB 3", StringComparison.OrdinalIgnoreCase));
        FillFiltered(DaliPb4Rows, DaliPushButtonsRows, row => row.Group.StartsWith("DALI PB 4", StringComparison.OrdinalIgnoreCase));

        FillFiltered(DaliCommonLevelRows, DaliDeviceCommonRows, row => row.Group.Equals("Levels", StringComparison.OrdinalIgnoreCase));
        FillFiltered(DaliCommonFadeRows, DaliDeviceCommonRows, row => row.Group.Equals("Fade", StringComparison.OrdinalIgnoreCase));
    }

    private static void FillFiltered(
        ObservableCollection<DetectorFieldRowViewModel> target,
        IEnumerable<DetectorFieldRowViewModel> source,
        Func<DetectorFieldRowViewModel, bool> filter)
    {
        target.Clear();
        foreach (var row in source.Where(filter))
            target.Add(row);
    }

    private void AddRows(
        ObservableCollection<DetectorFieldRowViewModel> target,
        IReadOnlyList<DetectorFieldDefinition> definitions)
    {
        foreach (var def in definitions)
        {
            var row = new DetectorFieldRowViewModel(def);
            row.PropertyChanged += OnFieldRowPropertyChanged;
            target.Add(row);
        }
    }

    private void InitializeTunableWhiteHours()
    {
        TunableWhiteHourPoints.Clear();
        for (var hour = 0; hour < 24; hour++)
        {
            var point = new TunableWhiteHourPointViewModel(hour, 4000, 500);
            point.PropertyChanged += OnTunableWhiteHourPointPropertyChanged;
            TunableWhiteHourPoints.Add(point);
        }
    }

    private void OnTunableWhiteHourPointPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(TunableWhiteHourPointViewModel.Kelvin), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(TunableWhiteHourPointViewModel.Lux), StringComparison.Ordinal))
            MarkTunableWhiteListEdited();
    }

    private void OnFieldRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(DetectorFieldRowViewModel.IsSelected), StringComparison.Ordinal))
            return;
        UpdateSelectionCounters();
    }

    private void UpdateSelectionCounters()
    {
        SelectedUserFieldCount = UserConfigRows.Count(r => r.IsSelected);
        SelectedWiredFieldCount = WiredPushButtonsRows.Count(r => r.IsSelected);
        SelectedBleFieldCount = BlePushButtonsRows.Count(r => r.IsSelected);
        SelectedDaliFieldCount = DaliPushButtonsRows.Count(r => r.IsSelected);
        SelectedDaliCommonFieldCount = DaliDeviceCommonRows.Count(r => r.IsSelected);
        SelectedTunableWhiteSectionCount =
            (ApplyTunableWhiteList ? 1 : 0)
            + (ApplyTunableWhitePreset ? 1 : 0)
            + (ApplyTunableWhiteDefaultKelvin ? 1 : 0);
    }

    private void MarkTunableWhiteListEdited()
    {
        if (_suppressTunableWhiteAutoApply)
            return;

        if (!ApplyTunableWhiteList)
            ApplyTunableWhiteList = true;
        else
            UpdateSelectionCounters();
    }

    private void MarkTunableWhitePresetEdited()
    {
        if (_suppressTunableWhiteAutoApply)
            return;

        if (!ApplyTunableWhitePreset)
            ApplyTunableWhitePreset = true;
        else
            UpdateSelectionCounters();
    }

    private IEnumerable<DetectorFieldRowViewModel> GetAllRows()
    {
        foreach (var row in UserConfigRows) yield return row;
        foreach (var row in WiredPushButtonsRows) yield return row;
        foreach (var row in BlePushButtonsRows) yield return row;
        foreach (var row in DaliPushButtonsRows) yield return row;
        foreach (var row in DaliDeviceCommonRows) yield return row;
    }

    private void LoadRowsFromPatch(DetectorSettingsPatchModel patch)
    {
        patch ??= new DetectorSettingsPatchModel();

        LoadSectionRows(UserConfigRows, patch.UserConfigHex, DetectorSettingsFieldCatalog.UserConfigLength);
        LoadSectionRows(WiredPushButtonsRows, patch.PushButtonsHex, DetectorSettingsFieldCatalog.WiredPushButtonsLength);
        LoadSectionRows(BlePushButtonsRows, patch.BlePushButtonsHex, DetectorSettingsFieldCatalog.BlePushButtonsLength);
        LoadSectionRows(DaliPushButtonsRows, patch.DaliPushButtonsHex, DetectorSettingsFieldCatalog.DaliPushButtonsLength);
        LoadSectionRows(DaliDeviceCommonRows, patch.DaliDeviceCommonParamHex, DetectorSettingsFieldCatalog.DaliDeviceCommonParamLength);
        LoadTunableWhiteFromPatch(patch);

        ClearAllSelections();
        ApplyTunableWhiteList = false;
        ApplyTunableWhitePreset = false;
        ApplyTunableWhiteDefaultKelvin = false;
        UpdateSelectionCounters();
    }

    private static void LoadSectionRows(
        IEnumerable<DetectorFieldRowViewModel> rows,
        string? sectionHex,
        int sectionLength)
    {
        var bytes = ParseHexBytes(sectionHex, sectionLength);
        foreach (var row in rows)
            row.LoadFromBytes(bytes);
    }

    private DetectorSettingsPatchModel BuildPatchFromSelectedFields(bool throwOnValidationErrors = true)
    {
        var patch = new DetectorSettingsPatchModel();
        var errors = new List<string>();

        BuildSectionPatch(UserConfigRows, DetectorSettingsFieldCatalog.UserConfigLength,
            out var userHex, out var userMask, errors);
        if (!string.IsNullOrWhiteSpace(userHex))
        {
            patch.UserConfigHex = userHex;
            patch.UserConfigMaskHex = userMask;
        }

        BuildSectionPatch(WiredPushButtonsRows, DetectorSettingsFieldCatalog.WiredPushButtonsLength,
            out var wiredHex, out var wiredMask, errors);
        if (!string.IsNullOrWhiteSpace(wiredHex))
        {
            patch.PushButtonsHex = wiredHex;
            patch.PushButtonsMaskHex = wiredMask;
        }

        BuildSectionPatch(BlePushButtonsRows, DetectorSettingsFieldCatalog.BlePushButtonsLength,
            out var bleHex, out var bleMask, errors);
        if (!string.IsNullOrWhiteSpace(bleHex))
        {
            patch.BlePushButtonsHex = bleHex;
            patch.BlePushButtonsMaskHex = bleMask;
        }

        BuildSectionPatch(DaliPushButtonsRows, DetectorSettingsFieldCatalog.DaliPushButtonsLength,
            out var daliHex, out var daliMask, errors);
        if (!string.IsNullOrWhiteSpace(daliHex))
        {
            patch.DaliPushButtonsHex = daliHex;
            patch.DaliPushButtonsMaskHex = daliMask;
        }

        BuildSectionPatch(DaliDeviceCommonRows, DetectorSettingsFieldCatalog.DaliDeviceCommonParamLength,
            out var daliCommonHex, out var daliCommonMask, errors);
        if (!string.IsNullOrWhiteSpace(daliCommonHex))
        {
            patch.DaliDeviceCommonParamHex = daliCommonHex;
            patch.DaliDeviceCommonParamMaskHex = daliCommonMask;
        }

        if (ApplyTunableWhiteList)
        {
            if (TryBuildTunableWhiteListHex(out var twListHex, out var twListError))
                patch.TunableWhiteListHex = twListHex;
            else if (!string.IsNullOrWhiteSpace(twListError))
                errors.Add(twListError);
        }

        if (ApplyTunableWhitePreset)
        {
            if (TryBuildTunableWhitePresetHex(out var twPresetHex, out var twPresetError))
                patch.TunableWhitePresetHex = twPresetHex;
            else if (!string.IsNullOrWhiteSpace(twPresetError))
                errors.Add(twPresetError);
        }

        if (ApplyTunableWhiteDefaultKelvin)
        {
            if (TryBuildTunableWhiteDefaultKelvinHex(out var twKelvinHex, out var twKelvinError))
                patch.TunableWhiteDefaultKelvinHex = twKelvinHex;
            else if (!string.IsNullOrWhiteSpace(twKelvinError))
                errors.Add(twKelvinError);
        }

        if (errors.Count > 0 && throwOnValidationErrors)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        return patch.CloneNormalized();
    }

    private static void BuildSectionPatch(
        IEnumerable<DetectorFieldRowViewModel> rows,
        int sectionLength,
        out string sectionValueHex,
        out string sectionMaskHex,
        ICollection<string> errors)
    {
        var selected = rows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            sectionValueHex = string.Empty;
            sectionMaskHex = string.Empty;
            return;
        }

        var valueBytes = new byte[sectionLength];
        var maskBytes = new byte[sectionLength];

        foreach (var row in selected)
        {
            if (!row.TryApplyTo(valueBytes, maskBytes, out var err) && !string.IsNullOrWhiteSpace(err))
                errors.Add(err);
        }

        if (maskBytes.All(b => b == 0))
        {
            sectionValueHex = string.Empty;
            sectionMaskHex = string.Empty;
            return;
        }

        sectionValueHex = Convert.ToHexString(valueBytes);
        sectionMaskHex = Convert.ToHexString(maskBytes);
    }

    private void LoadTunableWhiteFromPatch(DetectorSettingsPatchModel patch)
    {
        _suppressTunableWhiteAutoApply = true;
        try
        {
            if (TryParseTunableWhiteListHex(patch.TunableWhiteListHex, out var twListVersion, out var twListEnabled, out var hours))
            {
                TunableWhiteListVersion = twListVersion;
                TunableWhiteListEnabled = twListEnabled;
                for (var i = 0; i < Math.Min(24, TunableWhiteHourPoints.Count); i++)
                {
                    TunableWhiteHourPoints[i].Kelvin = ClampKelvinOrNoChange(hours[i].Kelvin);
                    TunableWhiteHourPoints[i].Lux = ClampLuxOrNoChange(hours[i].Lux);
                }

                if (TunableWhiteHourPoints.Count > 0)
                {
                    TunableWhiteScheduleAllKelvin = TunableWhiteHourPoints[0].Kelvin;
                    TunableWhiteScheduleAllLux = TunableWhiteHourPoints[0].Lux;
                }
            }

            if (TryParseTunableWhitePresetHex(patch.TunableWhitePresetHex, out var twPresetVersion, out var presets))
            {
                TunableWhitePresetVersion = twPresetVersion;
                TunableWhitePreset1Kelvin = ClampKelvinOrNoChange(presets[0].Kelvin);
                TunableWhitePreset1Lux = ClampLuxOrNoChange(presets[0].Lux);
                TunableWhitePreset2Kelvin = ClampKelvinOrNoChange(presets[1].Kelvin);
                TunableWhitePreset2Lux = ClampLuxOrNoChange(presets[1].Lux);
                TunableWhitePreset3Kelvin = ClampKelvinOrNoChange(presets[2].Kelvin);
                TunableWhitePreset3Lux = ClampLuxOrNoChange(presets[2].Lux);
                TunableWhitePreset4Kelvin = ClampKelvinOrNoChange(presets[3].Kelvin);
                TunableWhitePreset4Lux = ClampLuxOrNoChange(presets[3].Lux);
            }

            if (TryParseTunableWhiteDefaultKelvinHex(patch.TunableWhiteDefaultKelvinHex, out var twDefaultVersion, out var kelvin))
            {
                TunableWhiteDefaultKelvinVersion = twDefaultVersion;
                TunableWhiteDefaultKelvin = ClampKelvinOrNoChange(kelvin);
            }
        }
        finally
        {
            _suppressTunableWhiteAutoApply = false;
        }
    }

    private bool TryBuildTunableWhiteListHex(out string hex, out string error)
    {
        hex = string.Empty;
        error = string.Empty;

        if (TunableWhiteHourPoints.Count < 24)
        {
            error = "Tunable White list is missing hour points.";
            return false;
        }

        if (TunableWhiteListVersion < 0 || TunableWhiteListVersion > 255)
        {
            error = "Tunable White list version must be 0..255.";
            return false;
        }

        var payload = new byte[TunableWhiteListSetPayloadLength];
        payload[0] = 0x01; // Set
        payload[1] = (byte)TunableWhiteListVersion;
        payload[2] = TunableWhiteListEnabled ? (byte)1 : (byte)0;

        for (var hour = 0; hour < 24; hour++)
        {
            var kelvin = TunableWhiteHourPoints[hour].Kelvin;
            var lux = TunableWhiteHourPoints[hour].Lux;

            if (!IsValidKelvinValue(kelvin))
            {
                error = $"Hour {hour:00} Kelvin {kelvin}K is outside {TunableWhiteMinKelvin}..{TunableWhiteMaxKelvin}K (or {TunableWhiteNoChangeKelvin}=No Change).";
                return false;
            }

            if (!IsValidLuxValue(lux))
            {
                error = $"Hour {hour:00} Lux {lux} is outside {TunableWhiteMinLux}..{TunableWhiteMaxLux} (or {TunableWhiteNoChangeLux}=No Change).";
                return false;
            }

            var hourOffset = 3 + (hour * 4);
            WriteUInt16Le(payload, hourOffset, kelvin);
            WriteUInt16Le(payload, hourOffset + 2, lux);
        }

        hex = Convert.ToHexString(payload);
        return true;
    }

    private bool TryBuildTunableWhitePresetHex(out string hex, out string error)
    {
        hex = string.Empty;
        error = string.Empty;

        if (TunableWhitePresetVersion < 0 || TunableWhitePresetVersion > 255)
        {
            error = "Tunable White preset version must be 0..255.";
            return false;
        }

        var values = new[]
        {
            new TunableWhiteSetting(TunableWhitePreset1Kelvin, TunableWhitePreset1Lux),
            new TunableWhiteSetting(TunableWhitePreset2Kelvin, TunableWhitePreset2Lux),
            new TunableWhiteSetting(TunableWhitePreset3Kelvin, TunableWhitePreset3Lux),
            new TunableWhiteSetting(TunableWhitePreset4Kelvin, TunableWhitePreset4Lux)
        };

        for (var i = 0; i < values.Length; i++)
        {
            if (!IsValidKelvinValue(values[i].Kelvin))
            {
                error = $"Preset {i + 1} Kelvin {values[i].Kelvin}K is outside {TunableWhiteMinKelvin}..{TunableWhiteMaxKelvin}K (or {TunableWhiteNoChangeKelvin}=No Change).";
                return false;
            }

            if (!IsValidLuxValue(values[i].Lux))
            {
                error = $"Preset {i + 1} Lux {values[i].Lux} is outside {TunableWhiteMinLux}..{TunableWhiteMaxLux} (or {TunableWhiteNoChangeLux}=No Change).";
                return false;
            }
        }

        var payload = new byte[TunableWhitePresetSetPayloadLength];
        payload[0] = 0x01; // Set
        payload[1] = (byte)TunableWhitePresetVersion;
        for (var i = 0; i < values.Length; i++)
        {
            var offset = 2 + (i * 4);
            WriteUInt16Le(payload, offset, values[i].Kelvin);
            WriteUInt16Le(payload, offset + 2, values[i].Lux);
        }

        hex = Convert.ToHexString(payload);
        return true;
    }

    private bool TryBuildTunableWhiteDefaultKelvinHex(out string hex, out string error)
    {
        hex = string.Empty;
        error = string.Empty;

        if (TunableWhiteDefaultKelvinVersion < 0 || TunableWhiteDefaultKelvinVersion > 255)
        {
            error = "Tunable White default Kelvin version must be 0..255.";
            return false;
        }

        if (TunableWhiteDefaultKelvin < TunableWhiteMinKelvin || TunableWhiteDefaultKelvin > TunableWhiteMaxKelvin)
        {
            error = $"Default Kelvin {TunableWhiteDefaultKelvin}K is outside {TunableWhiteMinKelvin}..{TunableWhiteMaxKelvin}K.";
            return false;
        }

        var payload = new byte[TunableWhiteDefaultKelvinSetPayloadLength];
        payload[0] = 0x01; // Set
        payload[1] = (byte)TunableWhiteDefaultKelvinVersion;
        payload[2] = (byte)(TunableWhiteDefaultKelvin & 0xFF);
        payload[3] = (byte)((TunableWhiteDefaultKelvin >> 8) & 0xFF);
        hex = Convert.ToHexString(payload);
        return true;
    }

    private static bool TryParseTunableWhiteListHex(string? inputHex, out int version, out bool enabled, out TunableWhiteSetting[] hours)
    {
        version = 2;
        enabled = true;
        hours = Enumerable.Repeat(new TunableWhiteSetting(4000, 500), 24).ToArray();

        var bytes = ParseFlexibleHexBytes(inputHex);
        if (bytes.Length == 0)
            return false;

        int offset;
        if (bytes.Length >= 100 && bytes[0] <= 0x01)
        {
            version = bytes[2];
            enabled = bytes[3] != 0;
            offset = 4;
        }
        else if (bytes.Length >= 99 && bytes[0] <= 0x01)
        {
            version = bytes[1];
            enabled = bytes[2] != 0;
            offset = 3;
        }
        else if (bytes.Length >= 98)
        {
            version = bytes[0];
            enabled = bytes[1] != 0;
            offset = 2;
        }
        else
        {
            return false;
        }

        if (bytes.Length < offset + 96)
            return false;

        hours = new TunableWhiteSetting[24];
        for (var i = 0; i < 24; i++)
        {
            var hourOffset = offset + (i * 4);
            var kelvin = ReadUInt16Le(bytes, hourOffset);
            var lux = ReadUInt16Le(bytes, hourOffset + 2);
            hours[i] = new TunableWhiteSetting(kelvin, lux);
        }

        return true;
    }

    private static bool TryParseTunableWhitePresetHex(string? inputHex, out int version, out TunableWhiteSetting[] presets)
    {
        version = 2;
        presets = new[]
        {
            new TunableWhiteSetting(3000, 500),
            new TunableWhiteSetting(3500, 500),
            new TunableWhiteSetting(4500, 500),
            new TunableWhiteSetting(5500, 500)
        };

        var bytes = ParseFlexibleHexBytes(inputHex);
        if (bytes.Length == 0)
            return false;

        int offset;
        if (bytes.Length >= 19 && bytes[0] <= 0x01)
        {
            var candA = (version: bytes[1], result: bytes[2], score: ScoreTwPresetReplyCandidate(bytes[1], bytes[2]));
            var candB = (version: bytes[2], result: bytes[1], score: ScoreTwPresetReplyCandidate(bytes[2], bytes[1]));
            var chosen = candA.score >= candB.score ? candA : candB;

            if (chosen.score >= 0)
            {
                if (chosen.result != 0x00)
                    return false;
                version = chosen.version;
            }
            else
            {
                version = bytes[1];
            }

            offset = 3;
        }
        else if (bytes.Length >= 18 && bytes[0] <= 0x01)
        {
            version = bytes[1];
            offset = 2;
        }
        else if (bytes.Length >= 17)
        {
            version = bytes[0];
            offset = 1;
        }
        else
        {
            return false;
        }

        if (bytes.Length < offset + 16)
            return false;

        presets = new TunableWhiteSetting[4];
        for (var i = 0; i < 4; i++)
        {
            var presetOffset = offset + (i * 4);
            presets[i] = new TunableWhiteSetting(
                ReadUInt16Le(bytes, presetOffset),
                ReadUInt16Le(bytes, presetOffset + 2));
        }

        return true;
    }

    private static int ScoreTwPresetReplyCandidate(byte version, byte result)
    {
        if (!IsKnownTwResultCode(result))
            return -100;

        var score = 0;
        score += 2; // known result code
        if (version is 0x01 or 0x02)
            score += 2;
        if (result == 0x00)
            score += 1;
        if (version != 0x00)
            score += 1;
        return score;
    }

    private static bool IsKnownTwResultCode(byte result)
        => result is 0x00 or 0x01 or 0x02 or 0x03 or 0x04 or 0x07;

    private static bool TryParseTunableWhiteDefaultKelvinHex(string? inputHex, out int version, out int kelvin)
    {
        version = 1;
        kelvin = 4000;

        var bytes = ParseFlexibleHexBytes(inputHex);
        if (bytes.Length == 0)
            return false;

        if (bytes.Length >= 5 && bytes[0] <= 0x01)
        {
            version = bytes[2];
            kelvin = bytes[3] | (bytes[4] << 8);
            return true;
        }

        if (bytes.Length >= 4 && bytes[0] <= 0x01)
        {
            version = bytes[1];
            kelvin = bytes[2] | (bytes[3] << 8);
            return true;
        }

        if (bytes.Length >= 3)
        {
            version = bytes[0];
            kelvin = bytes[1] | (bytes[2] << 8);
            return true;
        }

        return false;
    }

    private static void WriteUInt16Le(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value & 0xFF);
        target[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static int ReadUInt16Le(IReadOnlyList<byte> source, int offset)
        => source[offset] | (source[offset + 1] << 8);

    private static bool IsValidKelvinValue(int kelvin)
        => kelvin == TunableWhiteNoChangeKelvin
           || (kelvin >= TunableWhiteMinKelvin && kelvin <= TunableWhiteMaxKelvin);

    private static bool IsValidLuxValue(int lux)
        => lux == TunableWhiteNoChangeLux
           || (lux >= TunableWhiteMinLux && lux <= TunableWhiteMaxLux);

    private static byte[] ParseFlexibleHexBytes(string? input)
    {
        var clean = new string((input ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if ((clean.Length & 1) != 0)
            clean = "0" + clean;
        if (clean.Length == 0)
            return Array.Empty<byte>();

        try
        {
            return Convert.FromHexString(clean);
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private static int ClampKelvin(int kelvin)
        => SnapToStep(kelvin, TunableWhiteMinKelvin, TunableWhiteMaxKelvin, TunableWhiteKelvinStep);

    private static int ClampLux(int lux)
        => SnapToStep(lux, TunableWhiteMinLux, TunableWhiteMaxLux, TunableWhiteLuxStep);

    private static int ClampKelvinOrNoChange(int kelvin)
        => kelvin == TunableWhiteNoChangeKelvin ? TunableWhiteMaxKelvin : ClampKelvin(kelvin);

    private static int ClampLuxOrNoChange(int lux)
        => lux == TunableWhiteNoChangeLux ? TunableWhiteMaxLux : ClampLux(lux);

    private static int SnapToStep(int value, int min, int max, int step)
    {
        var clamped = Math.Max(min, Math.Min(max, value));
        var snapped = min + ((int)Math.Round((clamped - min) / (double)step, MidpointRounding.AwayFromZero) * step);
        return Math.Max(min, Math.Min(max, snapped));
    }

    private static Brush CreateKelvinBrush(int kelvin)
    {
        var k = ClampKelvin(kelvin);
        var t = (k - TunableWhiteMinKelvin) / (double)(TunableWhiteMaxKelvin - TunableWhiteMinKelvin);

        var r = (byte)Math.Round(255 - (60 * t));
        var g = (byte)Math.Round(170 + (55 * t));
        var b = (byte)Math.Round(110 + (145 * t));
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private static Brush CreateLuxBrush(int lux)
    {
        var l = ClampLux(lux);
        var normalized = (l - TunableWhiteMinLux) / (double)(TunableWhiteMaxLux - TunableWhiteMinLux);
        var dimmed = Math.Min(1.0, Math.Pow(normalized, 0.35));

        var r = (byte)Math.Round(80 + (175 * dimmed));
        var g = (byte)Math.Round(90 + (160 * dimmed));
        var b = (byte)Math.Round(110 + (110 * dimmed));
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private static byte[] ParseHexBytes(string? input, int expectedLength)
    {
        var clean = new string((input ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if ((clean.Length & 1) != 0)
            clean = "0" + clean;
        if (clean.Length == 0)
            return new byte[expectedLength];

        var bytes = Convert.FromHexString(clean);
        if (bytes.Length == expectedLength)
            return bytes;

        var resized = new byte[expectedLength];
        Array.Copy(bytes, 0, resized, 0, Math.Min(bytes.Length, expectedLength));
        return resized;
    }

    private void ClearAllSelections()
    {
        foreach (var row in GetAllRows())
            row.IsSelected = false;
    }

    private static void SelectAll(IEnumerable<DetectorFieldRowViewModel> rows)
    {
        foreach (var row in rows)
            row.IsSelected = true;
    }

    private string ResolveCassiaName(DiscoveredDevice device)
    {
        var cassia = (device.AssignedCassia ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(cassia))
            return cassia;

        cassia = (device.BestCassia ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(cassia))
            return cassia;

        var online = _main.CassiaGateways.FirstOrDefault(g => string.Equals(g.State, "online", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(online?.Name))
            return online.Name.Trim();

        return (_main.CassiaGateways.FirstOrDefault()?.Name ?? "").Trim();
    }

    private static string ResolveDetectorType(DiscoveredDevice device)
    {
        var candidates = new[]
        {
            (device.SensorModel ?? "").Trim(),
            (device.DetectorType ?? "").Trim()
        };

        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c))
                continue;

            var upper = c.ToUpperInvariant();
            var m = DetectorModelRegex.Match(upper);
            if (m.Success)
                return m.Value.Replace("M", "P", StringComparison.Ordinal);
        }

        return "";
    }

    private readonly record struct TunableWhiteSetting(int Kelvin, int Lux);
}

public partial class TunableWhiteHourPointViewModel : ObservableObject
{
    private const int MinKelvin = 1800;
    private const int MaxKelvin = 6500;
    private const int KelvinStep = 100;
    private const int MinLux = 20;
    private const int MaxLux = 2000;
    private const int LuxStep = 10;

    public TunableWhiteHourPointViewModel(int hour, int kelvin, int lux)
    {
        Hour = Math.Clamp(hour, 0, 23);
        this.kelvin = SnapToStep(kelvin, MinKelvin, MaxKelvin, KelvinStep);
        this.lux = SnapToStep(lux, MinLux, MaxLux, LuxStep);
    }

    public int Hour { get; }
    public string HourLabel => Hour.ToString("00", CultureInfo.InvariantCulture);
    public string KelvinLabel => $"{Kelvin} K";
    public string LuxLabel => $"{Lux} lux";
    public Brush KelvinBrush => CreateKelvinBrush(Kelvin);
    public Brush LuxBrush => CreateLuxBrush(Lux);
    public double KelvinChartHeight => 14d + ((Kelvin - MinKelvin) * 72d / (MaxKelvin - MinKelvin));
    public double LuxChartHeight => 14d + ((Lux - MinLux) * 72d / (MaxLux - MinLux));

    [ObservableProperty] private int kelvin;
    [ObservableProperty] private int lux;

    partial void OnKelvinChanged(int value)
    {
        var snapped = SnapToStep(value, MinKelvin, MaxKelvin, KelvinStep);
        if (value != snapped)
        {
            Kelvin = snapped;
            return;
        }

        OnPropertyChanged(nameof(KelvinLabel));
        OnPropertyChanged(nameof(KelvinBrush));
        OnPropertyChanged(nameof(KelvinChartHeight));
    }

    partial void OnLuxChanged(int value)
    {
        var snapped = SnapToStep(value, MinLux, MaxLux, LuxStep);
        if (value != snapped)
        {
            Lux = snapped;
            return;
        }

        OnPropertyChanged(nameof(LuxLabel));
        OnPropertyChanged(nameof(LuxBrush));
        OnPropertyChanged(nameof(LuxChartHeight));
    }

    private static Brush CreateKelvinBrush(int kelvin)
    {
        var k = Math.Clamp(kelvin, MinKelvin, MaxKelvin);
        var t = (k - MinKelvin) / (double)(MaxKelvin - MinKelvin);

        var r = (byte)Math.Round(255 - (60 * t));
        var g = (byte)Math.Round(170 + (55 * t));
        var b = (byte)Math.Round(110 + (145 * t));
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private static Brush CreateLuxBrush(int lux)
    {
        var l = Math.Clamp(lux, MinLux, MaxLux);
        var normalized = (l - MinLux) / (double)(MaxLux - MinLux);
        var dimmed = Math.Min(1.0, Math.Pow(normalized, 0.35));

        var r = (byte)Math.Round(80 + (175 * dimmed));
        var g = (byte)Math.Round(90 + (160 * dimmed));
        var b = (byte)Math.Round(110 + (110 * dimmed));
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private static int SnapToStep(int value, int min, int max, int step)
    {
        var clamped = Math.Max(min, Math.Min(max, value));
        var snapped = min + ((int)Math.Round((clamped - min) / (double)step, MidpointRounding.AwayFromZero) * step);
        return Math.Max(min, Math.Min(max, snapped));
    }
}
