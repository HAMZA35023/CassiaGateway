using AccessAppMqttWpf.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;

namespace AccessAppMqttWpf.ViewModels;

public partial class DetectorSettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DiscoveredDevice? _device;
    private static readonly Regex DetectorModelRegex = new(@"^[PM]\d{2}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

    [ObservableProperty] private string macAddress = "";
    [ObservableProperty] private string cassiaName = "";
    [ObservableProperty] private string detectorType = "";
    [ObservableProperty] private string firmwareVersion = "";
    [ObservableProperty] private string pincode = "";
    [ObservableProperty] private bool writeOnlyChanged = true;
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

    public bool CanQueueUpdateWithProfile => _device != null;

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

        if (!patch.HasAnyValue)
        {
            StatusText = "Select at least one field to include in post-update settings profile.";
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
            RunDali102TotalNewScanAfterUpdate = profile.RunDali102TotalNewScanAfterUpdate;
            RunDali103TotalNewScanAfterUpdate = profile.RunDali103TotalNewScanAfterUpdate;

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
            }
            else
            {
                // Backward compatibility with legacy section-level profiles.
                if (profile.Settings != null && profile.Settings.HasAnyValue)
                    LoadRowsFromPatch(profile.Settings);

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

        ClearAllSelections();
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
}
