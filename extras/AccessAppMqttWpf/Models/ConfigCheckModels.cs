using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AccessAppMqttWpf.Models;

public partial class ConfigCheckDeviceRow : ObservableObject
{
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private string mac = "";
    [ObservableProperty] private string model = "";
    [ObservableProperty] private string profileName = "";
    [ObservableProperty] private string cassia = "";
    [ObservableProperty] private int rssi = int.MinValue;
    [ObservableProperty] private string fwCurrent = "";
    [ObservableProperty] private string statusText = "Pending";
    [ObservableProperty] private int mismatchCount;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool isDone;
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private bool isSkipped;

    // Timestamp of last real RSSI update from a live scan — used to detect out-of-range (stale > 60 s)
    public DateTimeOffset LastRssiUpdatedUtc { get; set; } = DateTimeOffset.MinValue;

    public string RssiDisplay => Rssi == int.MinValue ? "" : Rssi.ToString();

    partial void OnRssiChanged(int value)       => OnPropertyChanged(nameof(RssiDisplay));

    // True only when there are mismatches to fix and we're not busy
    public bool CanApply => IsDone && !HasError && !IsSkipped && !IsRunning && MismatchCount > 0;

    partial void OnIsDoneChanged(bool value)       => OnPropertyChanged(nameof(CanApply));
    partial void OnHasErrorChanged(bool value)     => OnPropertyChanged(nameof(CanApply));
    partial void OnIsSkippedChanged(bool value)    => OnPropertyChanged(nameof(CanApply));
    partial void OnIsRunningChanged(bool value)    => OnPropertyChanged(nameof(CanApply));
    partial void OnMismatchCountChanged(int value) => OnPropertyChanged(nameof(CanApply));

    public ObservableCollection<ConfigCheckFieldResult> FieldResults { get; } = new();
}

public sealed class ConfigCheckModelFilter : ObservableObject
{
    public string Model { get; init; } = "";
    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}

public sealed class ConfigCheckFieldResult
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Expected { get; init; } = "";
    public string Actual { get; init; } = "";
    public bool IsMatch { get; init; }
    public bool NotInCatalog { get; init; }
    public bool NotReadable { get; init; }
    // True for informational rows (firmware) that should not trigger row coloring
    public bool IsInfoRow { get; init; }

    public string MatchGlyph =>
        NotInCatalog || NotReadable ? "-" : (IsMatch ? "✓" : "✗");
}
