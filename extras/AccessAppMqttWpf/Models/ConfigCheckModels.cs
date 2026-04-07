using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AccessAppMqttWpf.Models;

public partial class ConfigCheckDeviceRow : ObservableObject
{
    [ObservableProperty] private bool isSelected = true;
    [ObservableProperty] private string mac = "";
    [ObservableProperty] private string model = "";
    [ObservableProperty] private string profileName = "";
    [ObservableProperty] private string cassia = "";
    [ObservableProperty] private string statusText = "Pending";
    [ObservableProperty] private int mismatchCount;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool isDone;
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private bool isSkipped;

    public ObservableCollection<ConfigCheckFieldResult> FieldResults { get; } = new();
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

    public string MatchGlyph =>
        NotInCatalog || NotReadable ? "-" : (IsMatch ? "✓" : "✗");
}
