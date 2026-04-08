using AccessAppMqttWpf.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;

namespace AccessAppMqttWpf.ViewModels;

public partial class MainViewModel
{
    // ──────────────────────────────────────────────────
    // How many devices one Cassia can handle in parallel
    // ──────────────────────────────────────────────────
    private const int ConfigCheckWorkersPerCassia = 2;

    // RSSI threshold above which a newly discovered device is auto-checked
    private const int ConfigCheckAutoCheckRssiThreshold = -75;

    public ObservableCollection<ConfigCheckDeviceRow> ConfigCheckRows { get; } = new();
    public ICollectionView ConfigCheckView { get; private set; } = null!;
    public ObservableCollection<ConfigCheckModelFilter> ConfigCheckModelFilters { get; } = new();

    [ObservableProperty] private ConfigCheckDeviceRow? selectedConfigCheckRow;
    [ObservableProperty] private string configCheckStatus = "Ready";
    [ObservableProperty] private bool configCheckRunning;

    // When true: auto-check newly discovered devices that have a profile and good RSSI
    [ObservableProperty] private bool configCheckAutoActive;
    [ObservableProperty] private bool hideNoProfile;
    [ObservableProperty] private bool hideOk;
    [ObservableProperty] private bool hideOutOfRange;
    [ObservableProperty] private int minRssiToCheck = -90;

    // MACs deliberately cleared by the user — not re-added by MergeConfigCheckDevices
    private readonly HashSet<string> _configCheckClearedMacs = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _configCheckCts;
    private DispatcherTimer? _configCheckRefreshTimer;

    // ─── Initialize view with filtering ──────────────────────────────────────────

    internal void InitConfigCheckView()
    {
        ConfigCheckView = CollectionViewSource.GetDefaultView(ConfigCheckRows);
        ConfigCheckView.Filter = obj =>
        {
            if (obj is not ConfigCheckDeviceRow row) return false;
            return RowMatchesFilters(row);
        };

        // Re-apply filter whenever a row's relevant properties change
        ConfigCheckRows.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ConfigCheckDeviceRow row in e.NewItems)
                    row.PropertyChanged += OnConfigCheckRowPropertyChanged;
            if (e.OldItems != null)
                foreach (ConfigCheckDeviceRow row in e.OldItems)
                    row.PropertyChanged -= OnConfigCheckRowPropertyChanged;
        };

        // Re-apply filter when any model checkbox changes
        ConfigCheckModelFilters.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ConfigCheckModelFilter f in e.NewItems)
                    f.PropertyChanged += (_, _) => RefreshConfigCheckView();
        };
    }

    private void OnConfigCheckRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Refresh filter when properties that affect visibility change
        // Rssi is included because -127 (out of range) can hide rows when HideOutOfRange is set
        if (e.PropertyName is nameof(ConfigCheckDeviceRow.StatusText)
                           or nameof(ConfigCheckDeviceRow.ProfileName)
                           or nameof(ConfigCheckDeviceRow.Rssi)
                           or nameof(ConfigCheckDeviceRow.Model))
        {
            RefreshConfigCheckView();
        }
    }

    // ─── Timer-based auto-refresh ──────────────────────────────────────────────

    internal void StartConfigCheckAutoRefresh()
    {
        if (_configCheckRefreshTimer != null) return;
        _configCheckRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _configCheckRefreshTimer.Tick += (_, _) => OnConfigCheckTimerTick();
        _configCheckRefreshTimer.Start();
    }

    internal void StopConfigCheckAutoRefresh()
    {
        _configCheckRefreshTimer?.Stop();
        _configCheckRefreshTimer = null;
    }

    private void OnConfigCheckTimerTick()
    {
        MergeConfigCheckDevices();

        // When auto-active: kick off a check if there are pending selected rows that pass check filters
        if (ConfigCheckAutoActive && !ConfigCheckRunning)
        {
            var hasPending = ConfigCheckRows.Any(r => r.IsSelected && !r.IsRunning && RowPassesCheckFilters(r));
            if (hasPending)
                _ = StartConfigCheckAsync();
        }
    }

    // ─── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleConfigCheckAuto()
    {
        ConfigCheckAutoActive = !ConfigCheckAutoActive;
    }

    [RelayCommand]
    private async Task StartConfigCheck()
    {
        await StartConfigCheckAsync().ConfigureAwait(false);
    }

    private async Task StartConfigCheckAsync()
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
        MergeConfigCheckDevices();
    }

    [RelayCommand]
    private void ClearCheckedConfigDevices()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var toRemove = ConfigCheckRows
                .Where(r => r.IsDone || r.IsSkipped)
                .ToList();
            foreach (var r in toRemove)
            {
                _configCheckClearedMacs.Add(r.Mac);
                ConfigCheckRows.Remove(r);
            }

            ConfigCheckStatus = $"{ConfigCheckRows.Count} device(s) remaining.";
            UpdateConfigCheckModelsList();
        }, DispatcherPriority.Background);
    }

    [RelayCommand]
    private async Task ApplyConfigToRow(ConfigCheckDeviceRow row)
    {
        if (row == null || row.IsRunning) return;

        var profilePath = GetDetectorSettingsProfileForModel(row.Model);
        if (string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath)) return;

        var json    = await File.ReadAllTextAsync(profilePath).ConfigureAwait(false);
        var profile = DetectorSettingsProfileModel.Parse(json);
        if (profile?.FieldOverrides == null || profile.FieldOverrides.Count == 0) return;

        var patch  = DetectorSettingsFieldCatalog.BuildPatchFromFieldOverrides(profile.FieldOverrides);
        var device = _devices.FirstOrDefault(d => string.Equals(d.Mac, row.Mac, StringComparison.OrdinalIgnoreCase));
        var fw     = (device?.CurrentFw ?? profile.FirmwareVersion ?? "").Trim();

        Application.Current.Dispatcher.Invoke(() =>
        {
            row.IsRunning    = true;
            row.IsDone       = false;
            row.HasError     = false;
            row.IsSkipped    = false;
            row.StatusText   = "Applying…";
            row.FieldResults.Clear();
            row.MismatchCount = 0;
        }, DispatcherPriority.Background);

        try
        {
            var result = await ApplyDetectorSettingsAsync(
                row.Cassia,
                row.Mac,
                row.Model,
                pincode: "",
                firmwareVersion: fw,
                patch: patch,
                writeOnlyChanged: false,
                timeout: TimeSpan.FromSeconds(90)).ConfigureAwait(false);

            if (result == null || !result.Success)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    row.IsRunning = false;
                    row.HasError  = true;
                    row.IsDone    = true;
                    row.StatusText = $"Apply error — {result?.Message ?? "no response"}";
                }, DispatcherPriority.Background);
                return;
            }

            // Re-check to verify the write took effect
            Application.Current.Dispatcher.Invoke(
                () => row.StatusText = "Verifying…",
                DispatcherPriority.Background);

            await CheckDeviceAsync(row, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                row.IsRunning  = false;
                row.HasError   = true;
                row.IsDone     = true;
                row.StatusText = $"Error: {ex.Message}";
            }, DispatcherPriority.Background);
        }
    }

    // ─── Device list merge (adds new, updates RSSI/Cassia, auto-checks) ────────

    internal void MergeConfigCheckDevices()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var existingByMac = ConfigCheckRows
                .ToDictionary(r => r.Mac, r => r, StringComparer.OrdinalIgnoreCase);

            // Build a set of MACs currently visible in _devices for out-of-range detection
            var activeMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var device in _devices)
            {
                var model = (device.SensorModel ?? "").Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(model)) continue;

                var mac = device.Mac ?? "";
                activeMacs.Add(mac);

                // Skip devices the user has deliberately cleared
                if (_configCheckClearedMacs.Contains(mac)) continue;

                var profilePath = GetDetectorSettingsProfileForModel(model);
                var profileName = string.IsNullOrWhiteSpace(profilePath)
                    ? "(no profile)"
                    : Path.GetFileNameWithoutExtension(profilePath);

                var cassia = ResolveCheckCassia(device);
                var rssi = device.BestRssi;

                if (existingByMac.TryGetValue(device.Mac ?? "", out var existing))
                {
                    // Update live fields — FwCurrent is intentionally NOT updated here;
                    // it is only populated from the get-detector-settings BLE response.
                    existing.Cassia      = cassia;
                    existing.Rssi        = rssi;
                    existing.ProfileName = profileName;

                    // Auto-check if RSSI crossed threshold and not yet done
                    if (rssi >= ConfigCheckAutoCheckRssiThreshold
                        && !existing.IsDone
                        && !existing.IsRunning
                        && !existing.IsSelected)
                    {
                        existing.IsSelected = true;
                    }
                }
                else
                {
                    var autoCheck = rssi >= ConfigCheckAutoCheckRssiThreshold;
                    ConfigCheckRows.Add(new ConfigCheckDeviceRow
                    {
                        Mac         = device.Mac ?? "",
                        Model       = model,
                        ProfileName = profileName,
                        Cassia      = cassia,
                        Rssi        = rssi,
                        IsSelected  = autoCheck,
                        StatusText  = "Pending",
                    });
                }
            }

            // Rows that are no longer in _devices show RSSI -127 (out of range)
            foreach (var row in existingByMac.Values)
            {
                if (!activeMacs.Contains(row.Mac))
                    row.Rssi = -127;
            }

            ConfigCheckStatus = $"{ConfigCheckRows.Count} device(s) — {ConfigCheckRows.Count(r => r.IsSelected && !r.IsDone && !r.IsRunning)} pending.";
            UpdateConfigCheckModelsList();
        }, DispatcherPriority.Background);
    }

    // Returns true if any model filter checkbox is checked
    private bool AnyModelFilterActive() => ConfigCheckModelFilters.Any(f => f.IsChecked);

    private bool ModelPassesFilter(string model)
    {
        if (!AnyModelFilterActive()) return true;
        return ConfigCheckModelFilters.Any(f => f.IsChecked && f.Model.Equals(model, StringComparison.OrdinalIgnoreCase));
    }

    // Check if a row is visible in the view.
    // Min RSSI is intentionally NOT applied here — once a row is added to the list it stays visible.
    // RSSI only gates auto-selection and which rows get validated.
    private bool RowMatchesFilters(ConfigCheckDeviceRow row)
    {
        if (HideNoProfile && row.ProfileName == "(no profile)")
            return false;
        if (HideOk && row.StatusText.StartsWith("OK"))
            return false;
        if (HideOutOfRange && row.Rssi == -127)
            return false;
        if (!ModelPassesFilter(row.Model))
            return false;
        return true;
    }

    // Check if a row should be included in a validation run (RSSI gated here).
    private bool RowPassesCheckFilters(ConfigCheckDeviceRow row)
    {
        if (HideNoProfile && row.ProfileName == "(no profile)")
            return false;
        if (row.Rssi != int.MinValue && row.Rssi < MinRssiToCheck)
            return false;
        if (!ModelPassesFilter(row.Model))
            return false;
        return true;
    }

    // Refresh the filter when settings change
    internal void RefreshConfigCheckView()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (ConfigCheckView is ICollectionView view)
                view.Refresh();
        }, DispatcherPriority.Background);
    }

    // Sync model filter checkboxes — adds new models, preserves existing checked state, removes gone models
    private void UpdateConfigCheckModelsList()
    {
        var models = ConfigCheckRows
            .Select(r => r.Model)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Remove filters for models that no longer exist
        foreach (var f in ConfigCheckModelFilters.ToList())
            if (!models.Contains(f.Model))
                ConfigCheckModelFilters.Remove(f);

        // Add new models
        var existing = ConfigCheckModelFilters.Select(f => f.Model).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models.OrderBy(m => m))
            if (!existing.Contains(model))
                ConfigCheckModelFilters.Add(new ConfigCheckModelFilter { Model = model });
    }

    partial void OnHideNoProfileChanged(bool value) => RefreshConfigCheckView();
    partial void OnHideOkChanged(bool value) => RefreshConfigCheckView();
    partial void OnHideOutOfRangeChanged(bool value) => RefreshConfigCheckView();
    partial void OnMinRssiToCheckChanged(int value) => RefreshConfigCheckView();

    // ─── Check runner ──────────────────────────────────────────────────────────

    private async Task RunConfigCheckAsync(CancellationToken ct)
    {
        var rows = await Application.Current.Dispatcher.InvokeAsync(
            () => ConfigCheckRows.Where(r => r.IsSelected && !r.IsRunning && RowPassesCheckFilters(r)).ToList(),
            DispatcherPriority.Background);

        if (rows.Count == 0)
        {
            Application.Current.Dispatcher.Invoke(
                () => ConfigCheckStatus = "Nothing to check — select devices first.",
                DispatcherPriority.Background);
            return;
        }

        // Group by Cassia so each Cassia runs up to ConfigCheckWorkersPerCassia in parallel
        var byCassia = rows.GroupBy(r => r.Cassia, StringComparer.OrdinalIgnoreCase).ToList();

        int total = rows.Count;
        int done = 0;

        Application.Current.Dispatcher.Invoke(
            () => ConfigCheckStatus = $"Checking 0/{total}…",
            DispatcherPriority.Background);

        // Semaphore per Cassia: keyed by name (case-insensitive)
        var semaphores = byCassia.ToDictionary(
            g => g.Key,
            _ => new SemaphoreSlim(ConfigCheckWorkersPerCassia, ConfigCheckWorkersPerCassia),
            StringComparer.OrdinalIgnoreCase);

        var tasks = rows.Select(async row =>
        {
            ct.ThrowIfCancellationRequested();

            var sem = semaphores.TryGetValue(row.Cassia, out var s) ? s : null;
            if (sem != null)
                await sem.WaitAsync(ct).ConfigureAwait(false);

            Application.Current.Dispatcher.Invoke(() =>
            {
                row.IsRunning = true;
                row.IsDone = false;
                row.HasError = false;
                row.IsSkipped = false;
                row.StatusText = "Checking…";
                row.FieldResults.Clear();
                row.MismatchCount = 0;
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
            finally
            {
                sem?.Release();
                var d = Interlocked.Increment(ref done);
                Application.Current.Dispatcher.Invoke(
                    () => ConfigCheckStatus = $"Checking {d}/{total}…",
                    DispatcherPriority.Background);
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* individual errors already captured */ }

        var mismatched = rows.Count(r => r.MismatchCount > 0);
        var errors = rows.Count(r => r.HasError);
        Application.Current.Dispatcher.Invoke(
            () => ConfigCheckStatus = $"Done — {done} checked, {mismatched} mismatch(es), {errors} error(s).",
            DispatcherPriority.Background);
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

        if (string.IsNullOrWhiteSpace(row.Cassia))
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

        var result = await RequestDetectorSettingsAsync(
            row.Cassia,
            row.Mac,
            row.Model,
            pincode: "",
            firmwareVersion: fw,
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
        var fieldResults = CompareProfileAgainstDevice(profile.FieldOverrides, actualPatch).ToList();

        // firmwareVersion in the result is the actual version read from the device on the same
        // BLE session. Normalise to "Sensor App" short form (e.g. "v02.28") like the rest of the app.
        var rawDeviceFw  = result.FirmwareVersion.Trim();
        var deviceFw     = NormaliseFwString(rawDeviceFw);

        if (!string.IsNullOrWhiteSpace(deviceFw))
        {
            Application.Current.Dispatcher.Invoke(
                () => row.FwCurrent = deviceFw,
                DispatcherPriority.Background);
        }

        // Always prepend a firmware row when the profile specifies an expected version.
        // IsInfoRow = true so the row never triggers background coloring.
        if (!string.IsNullOrWhiteSpace(profile.FirmwareVersion))
        {
            var profileFw = NormaliseFwString(profile.FirmwareVersion.Trim());
            var fwKnown   = !string.IsNullOrWhiteSpace(deviceFw);
            // Strip leading 'v'/'V' before comparing — profile may store "v02.38" while
            // the device response returns "02.38".
            var fwMatch   = fwKnown && string.Equals(
                deviceFw.TrimStart('v', 'V'),
                profileFw.TrimStart('v', 'V'),
                StringComparison.OrdinalIgnoreCase);

            fieldResults.Insert(0, new ConfigCheckFieldResult
            {
                Key       = "_firmware",
                Label     = "Firmware version",
                Expected  = profileFw,
                Actual    = fwKnown ? deviceFw : "(not read)",
                IsMatch   = fwMatch,
                IsInfoRow = true,
            });
        }

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
            row.IsSelected = false;
            row.StatusText = mismatches == 0
                ? $"OK — all {fieldResults.Count} fields match"
                : $"{mismatches} mismatch(es) of {fieldResults.Count} fields";
        }, DispatcherPriority.Background);
    }

    // ─── Comparison ───────────────────────────────────────────────────────────

    private static IReadOnlyList<ConfigCheckFieldResult> CompareProfileAgainstDevice(
        IReadOnlyList<DetectorSettingsFieldOverrideModel> overrides,
        DetectorSettingsPatchModel actualPatch)
    {
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
            var buf = new byte[length];
            if (string.IsNullOrWhiteSpace(hex)) return buf;
            var clean = hex.Trim();
            var src = Convert.FromHexString(clean.Length % 2 == 1 ? "0" + clean : clean);
            Buffer.BlockCopy(src, 0, buf, 0, Math.Min(src.Length, buf.Length));
            return buf;
        }

        static void LoadRows(IEnumerable<DetectorFieldRowViewModel> rows, byte[] bytes)
        {
            foreach (var r in rows) r.LoadFromBytes(bytes);
        }

        LoadRows(userRows,   ParseHex(actualPatch.UserConfigHex,            DetectorSettingsFieldCatalog.UserConfigLength));
        LoadRows(wiredRows,  ParseHex(actualPatch.PushButtonsHex,           DetectorSettingsFieldCatalog.WiredPushButtonsLength));
        LoadRows(bleRows,    ParseHex(actualPatch.BlePushButtonsHex,        DetectorSettingsFieldCatalog.BlePushButtonsLength));
        LoadRows(daliRows,   ParseHex(actualPatch.DaliPushButtonsHex,       DetectorSettingsFieldCatalog.DaliPushButtonsLength));
        LoadRows(commonRows, ParseHex(actualPatch.DaliDeviceCommonParamHex, DetectorSettingsFieldCatalog.DaliDeviceCommonParamLength));

        var rowByKey = userRows.Concat(wiredRows).Concat(bleRows).Concat(daliRows).Concat(commonRows)
            .ToDictionary(r => r.Key, r => r, StringComparer.OrdinalIgnoreCase);

        bool hasUser   = !string.IsNullOrWhiteSpace(actualPatch.UserConfigHex);
        bool hasWired  = !string.IsNullOrWhiteSpace(actualPatch.PushButtonsHex);
        bool hasBle    = !string.IsNullOrWhiteSpace(actualPatch.BlePushButtonsHex);
        bool hasDali   = !string.IsNullOrWhiteSpace(actualPatch.DaliPushButtonsHex);
        bool hasCommon = !string.IsNullOrWhiteSpace(actualPatch.DaliDeviceCommonParamHex);

        bool SectionAvailable(string key)
        {
            if (key.StartsWith("user.",        StringComparison.OrdinalIgnoreCase)) return hasUser;
            if (key.StartsWith("wired.",       StringComparison.OrdinalIgnoreCase)) return hasWired;
            if (key.StartsWith("ble.",         StringComparison.OrdinalIgnoreCase)) return hasBle;
            if (key.StartsWith("dali.common.", StringComparison.OrdinalIgnoreCase)) return hasCommon;
            if (key.StartsWith("dali.",        StringComparison.OrdinalIgnoreCase)) return hasDali;
            return true;
        }

        static string NormaliseBool(string v) => v.Trim().ToLowerInvariant() switch
        {
            "1" or "true"  => "true",
            "0" or "false" => "false",
            var x          => x
        };

        var results = new List<ConfigCheckFieldResult>();

        foreach (var ov in overrides)
        {
            if (string.IsNullOrWhiteSpace(ov.Key)) continue;

            if (!rowByKey.TryGetValue(ov.Key, out var row))
            {
                results.Add(new ConfigCheckFieldResult
                {
                    Key = ov.Key, Expected = ov.Value, Actual = "",
                    IsMatch = false, NotInCatalog = true,
                });
                continue;
            }

            if (!SectionAvailable(ov.Key))
            {
                results.Add(new ConfigCheckFieldResult
                {
                    Key = ov.Key, Label = row.Label, Expected = ov.Value, Actual = "",
                    IsMatch = false, NotReadable = true,
                });
                continue;
            }

            var actualValue = row.ExportProfileValue();
            var isMatch = string.Equals(NormaliseBool(ov.Value), NormaliseBool(actualValue),
                StringComparison.OrdinalIgnoreCase);

            results.Add(new ConfigCheckFieldResult
            {
                Key = ov.Key, Label = row.Label,
                Expected = ov.Value, Actual = actualValue,
                IsMatch = isMatch,
            });
        }

        return results;
    }

    private static string ResolveCheckCassia(DiscoveredDevice device)
    {
        if (!string.IsNullOrWhiteSpace(device.AssignedCassia)) return device.AssignedCassia.Trim();
        if (!string.IsNullOrWhiteSpace(device.BestCassia))     return device.BestCassia.Trim();
        return "";
    }

    // Normalise a raw FW string (e.g. "Sensor: App: 02.28 Boot: 1.05 | Actor: ...")
    // to the short sensor-app form used everywhere else (e.g. "02.28").
    // Falls back to the raw string if parsing fails.
    private static string NormaliseFwString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var m = SensorAppFromStatusRx.Match(raw);
        if (m.Success) return m.Groups["app"].Value.Trim();
        return raw;
    }
}
