using CommunityToolkit.Mvvm.ComponentModel;

namespace AccessAppMqttWpf.Models;

public partial class LedRangeDeviceRow : ObservableObject
{
    [ObservableProperty] private string mac = "";
    [ObservableProperty] private string model = "";
    [ObservableProperty] private int rssi;
    [ObservableProperty] private int chip;
    [ObservableProperty] private string color = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string error = "";
}
