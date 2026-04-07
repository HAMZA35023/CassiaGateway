using AccessAppMqttWpf.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<ConfigCheckDeviceRow> ConfigCheckRows { get; } = new();

    [ObservableProperty] private ConfigCheckDeviceRow? selectedConfigCheckRow;
    [ObservableProperty] private string configCheckStatus = "Ready";
    [ObservableProperty] private bool configCheckRunning;

    private CancellationTokenSource? _configCheckCts;

    [RelayCommand]
    private async Task StartConfigCheck()
    {
        if (ConfigCheckRunning) return;

        _configCheckCts?.Cancel();
        _configCheckCts = new CancellationTokenSource();
        ConfigCheckRunning = true;

        try
        {
            await RunConfigCheckAsync(_configCheckCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Application.Current.Dispatcher.Invoke(
                () => ConfigCheckStatus = "Stopped.",
                DispatcherPriority.Background);
        }
        finally
        {
            Application.Current.Dispatcher.Invoke(
                () => ConfigCheckRunning = false,
                DispatcherPriority.Background);
        }
    }

    [RelayCommand]
    private void StopConfigCheck()
    {
        _configCheckCts?.Cancel();
    }

    [RelayCommand]
    private void RefreshConfigCheckDevices()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            ConfigCheckRows.Clear();

            foreach (var device in _devices)
            {
                var model = (device.SensorModel ?? "").Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(model)) continue;

                var profilePath = GetDetectorSettingsProfileForModel(model);
                var profileName = string.IsNullOrWhiteSpace(profilePath)
                    ? "(no profile)"
                    : Path.GetFileNameWithoutExtension(profilePath);

                var cassia = ResolveCheckCassia(device);

                ConfigCheckRows.Add(new ConfigCheckDeviceRow
                {
                    Mac = device.Mac ?? "",
                    Model = model,
                    ProfileName = profileName,
                    Cassia = cassia,
                    StatusText = "Pending",
                });
            }

            ConfigCheckStatus = $"Ready — {ConfigCheckRows.Count} device(s) loaded.";
        }, DispatcherPriority.Background);
    }

    private async Task RunConfigCheckAsync(CancellationToken ct)
    {
        var rows = await Application.Current.Dispatcher.InvokeAsync(
            () => ConfigCheckRows.Where(r => r.IsSelected).ToList(),
            DispatcherPriority.Background);

        int done = 0;
        int total = rows.Count;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            Application.Current.Dispatcher.Invoke(() =>
            {
                row.IsRunning = true;
                row.IsDone = false;
                row.HasError = false;
                row.IsSkipped = false;
                row.StatusText = "Checking…";
                row.FieldResults.Clear();
                row.MismatchCount = 0;
                ConfigCheckStatus = $"Checking {row.Mac} ({done + 1}/{total})…";
            }, DispatcherPriority.Background);

            try
            {
                await CheckDeviceAsync(row, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    row.IsRunning = false;
                    row.StatusText = "Stopped";
                }, DispatcherPriority.Background);
                throw;
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    row.IsRunning = false;
                    row.HasError = true;
                    row.IsDone = true;
                    row.StatusText = $"Error: {ex.Message}";
                }, DispatcherPriority.Background);
            }

            done++;
        }

        var mismatched = rows.Count(r => r.MismatchCount > 0);
        var errors = rows.Count(r => r.HasError);
        Application.Current.Dispatcher.Invoke(() =>
        {
            ConfigCheckStatus = $"Done — {done} checked, {mismatched} with mismatches, {errors} errors.";
        }, DispatcherPriority.Background);
    }

    private async Task CheckDeviceAsync(ConfigCheckDeviceRow row, CancellationToken ct)
    {
        var profilePath = GetDetectorSettingsProfileForModel(row.Model);

        if (string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath))
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                row.IsRunning = false;
                row.IsSkipped = true;
                row.IsDone = true;
                row.StatusText = "Skipped — no profile";
            }, DispatcherPriority.Background);
            return;
        }

        var json = await File.ReadAllTextAsync(profilePath, ct).ConfigureAwait(false);
        var profile = DetectorSettingsProfileModel.Parse(json);
        if (profile?.FieldOverrides == null || profile.FieldOverrides.Count == 0)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                row.IsRunning = false;
                row.IsSkipped = true;
                row.IsDone = true;
                row.StatusText = "Skipped — profile has no FieldOverrides";
            }, DispatcherPriority.Background);
            return;
        }

        var cassiaName = row.Cassia;
        if (string.IsNullOrWhiteSpace(cassiaName))
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                row.IsRunning = false;
                row.HasError = true;
                row.IsDone = true;
                row.StatusText = "Error — no Cassia assigned";
            }, DispatcherPriority.Background);
            return;
        }

        var device = _devices.FirstOrDefault(d =>
            string.Equals(d.Mac, row.Mac, StringComparison.OrdinalIgnoreCase));

        var fw = (device?.CurrentFw ?? profile.FirmwareVersion ?? "").Trim();
        var pincode = "";

        var result = await RequestDetectorSettingsAsync(
            cassiaName,
            row.Mac,
            row.Model,
            pincode,
            fw,
            timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        if (result == null || !result.Success)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                row.IsRunning = false;
                row.HasError = true;
                row.IsDone = true;
                row.StatusText = $"Error — {result?.Message ?? "no response"}";
            }, DispatcherPriority.Background);
            return;
        }

        var actualPatch = DetectorSettingsPatchModel.FromJsonNode(result.Settings);
        var fieldResults = CompareProfileAgainstDevice(profile.FieldOverrides, actualPatch);
        var mismatches = fieldResults.Count(r => !r.IsMatch && !r.NotInCatalog && !r.NotReadable);

        Application.Current.Dispatcher.Invoke(() =>
        {
            row.FieldResults.Clear();
            foreach (var fr in fieldResults)
                row.FieldResults.Add(fr);

            row.MismatchCount = mismatches;
            row.IsRunning = false;
            row.IsDone = true;
            row.HasError = false;
            row.StatusText = mismatches == 0
                ? $"OK — all {fieldResults.Count} fields match"
                : $"{mismatches} mismatch(es) of {fieldResults.Count} fields";
        }, DispatcherPriority.Background);
    }

    private static IReadOnlyList<ConfigCheckFieldResult> CompareProfileAgainstDevice(
        IReadOnlyList<DetectorSettingsFieldOverrideModel> overrides,
        DetectorSettingsPatchModel actualPatch)
    {
        // Build rows for every section and load actual device values into them.
        var userRows   = DetectorSettingsFieldCatalog.UserConfigFields
            .Select(d => new DetectorFieldRowViewModel(d)).ToList();
        var wiredRows  = DetectorSettingsFieldCatalog.WiredPushButtonsFields
            .Select(d => new DetectorFieldRowViewModel(d)).ToList();
        var bleRows    = DetectorSettingsFieldCatalog.BlePushButtonsFields
            .Select(d => new DetectorFieldRowViewModel(d)).ToList();
        var daliRows   = DetectorSettingsFieldCatalog.DaliPushButtonsFields
            .Select(d => new DetectorFieldRowViewModel(d)).ToList();
        var commonRows = DetectorSettingsFieldCatalog.DaliDeviceCommonParamFields
            .Select(d => new DetectorFieldRowViewModel(d)).ToList();

        static byte[] ParseHex(string? hex, int length)
        {
            var result = new byte[length];
            if (string.IsNullOrWhiteSpace(hex)) return result;
            var clean = hex.Trim();
            var bytes = Convert.FromHexString(clean.Length % 2 == 1 ? "0" + clean : clean);
            Buffer.BlockCopy(bytes, 0, result, 0, Math.Min(bytes.Length, result.Length));
            return result;
        }

        LoadRows(userRows,   ParseHex(actualPatch.UserConfigHex,            DetectorSettingsFieldCatalog.UserConfigLength));
        LoadRows(wiredRows,  ParseHex(actualPatch.PushButtonsHex,           DetectorSettingsFieldCatalog.WiredPushButtonsLength));
        LoadRows(bleRows,    ParseHex(actualPatch.BlePushButtonsHex,        DetectorSettingsFieldCatalog.BlePushButtonsLength));
        LoadRows(daliRows,   ParseHex(actualPatch.DaliPushButtonsHex,       DetectorSettingsFieldCatalog.DaliPushButtonsLength));
        LoadRows(commonRows, ParseHex(actualPatch.DaliDeviceCommonParamHex, DetectorSettingsFieldCatalog.DaliDeviceCommonParamLength));

        var rowByKey = userRows.Concat(wiredRows).Concat(bleRows).Concat(daliRows).Concat(commonRows)
            .ToDictionary(r => r.Key, r => r, StringComparer.OrdinalIgnoreCase);

        var results = new List<ConfigCheckFieldResult>();

        // Track which sections are actually available from the device response.
        bool hasUser   = !string.IsNullOrWhiteSpace(actualPatch.UserConfigHex);
        bool hasWired  = !string.IsNullOrWhiteSpace(actualPatch.PushButtonsHex);
        bool hasBle    = !string.IsNullOrWhiteSpace(actualPatch.BlePushButtonsHex);
        bool hasDali   = !string.IsNullOrWhiteSpace(actualPatch.DaliPushButtonsHex);
        bool hasCommon = !string.IsNullOrWhiteSpace(actualPatch.DaliDeviceCommonParamHex);

        var sectionAvailable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["user"]  = hasUser,
            ["wired"] = hasWired,
            ["ble"]   = hasBle,
            ["dali"]  = hasDali,
            ["dali.common"] = hasCommon,
        };

        bool SectionAvailable(string key)
        {
            if (key.StartsWith("user.", StringComparison.OrdinalIgnoreCase))  return hasUser;
            if (key.StartsWith("wired.", StringComparison.OrdinalIgnoreCase)) return hasWired;
            if (key.StartsWith("ble.", StringComparison.OrdinalIgnoreCase))   return hasBle;
            if (key.StartsWith("dali.common.", StringComparison.OrdinalIgnoreCase)) return hasCommon;
            if (key.StartsWith("dali.", StringComparison.OrdinalIgnoreCase))  return hasDali;
            return true;
        }

        foreach (var ov in overrides)
        {
            if (string.IsNullOrWhiteSpace(ov.Key)) continue;

            if (!rowByKey.TryGetValue(ov.Key, out var row))
            {
                results.Add(new ConfigCheckFieldResult
                {
                    Key = ov.Key,
                    Expected = ov.Value,
                    Actual = "",
                    IsMatch = false,
                    NotInCatalog = true,
                });
                continue;
            }

            if (!SectionAvailable(ov.Key))
            {
                results.Add(new ConfigCheckFieldResult
                {
                    Key = ov.Key,
                    Label = row.Label,
                    Expected = ov.Value,
                    Actual = "",
                    IsMatch = false,
                    NotReadable = true,
                });
                continue;
            }

            var actualValue = row.ExportProfileValue();
            // Normalise bool representations ("true"/"false" vs "1"/"0").
            var expectedNorm = NormaliseBool(ov.Value);
            var actualNorm   = NormaliseBool(actualValue);
            var isMatch = string.Equals(expectedNorm, actualNorm, StringComparison.OrdinalIgnoreCase);

            results.Add(new ConfigCheckFieldResult
            {
                Key      = ov.Key,
                Label    = row.Label,
                Expected = ov.Value,
                Actual   = actualValue,
                IsMatch  = isMatch,
            });
        }

        return results;

        static void LoadRows(IEnumerable<DetectorFieldRowViewModel> rows, byte[] bytes)
        {
            foreach (var r in rows)
                r.LoadFromBytes(bytes);
        }

        static string NormaliseBool(string v)
        {
            return v.Trim().ToLowerInvariant() switch
            {
                "1" or "true"  => "true",
                "0" or "false" => "false",
                var x          => x
            };
        }
    }

    private static string ResolveCheckCassia(DiscoveredDevice device)
    {
        if (!string.IsNullOrWhiteSpace(device.AssignedCassia))
            return device.AssignedCassia.Trim();
        if (!string.IsNullOrWhiteSpace(device.BestCassia))
            return device.BestCassia.Trim();
        return "";
    }
}
