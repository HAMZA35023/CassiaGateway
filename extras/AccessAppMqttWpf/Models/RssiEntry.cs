using CommunityToolkit.Mvvm.ComponentModel;

namespace AccessAppMqttWpf.Models;

public partial class RssiEntry : ObservableObject
{
    [ObservableProperty] private string cassia = "";
    [ObservableProperty] private int rssi;
    [ObservableProperty] private bool isQueued;

    public string Text => $"{Cassia}:{Rssi}";

    partial void OnCassiaChanged(string value) => OnPropertyChanged(nameof(Text));
    partial void OnRssiChanged(int value) => OnPropertyChanged(nameof(Text));
}
