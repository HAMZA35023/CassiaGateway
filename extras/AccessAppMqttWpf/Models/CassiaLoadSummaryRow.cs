using CommunityToolkit.Mvvm.ComponentModel;

namespace AccessAppMqttWpf.Models;

public partial class CassiaLoadSummaryRow : ObservableObject
{
    [ObservableProperty] private string cassia = "";
    [ObservableProperty] private int beforeLoad;
    [ObservableProperty] private int afterLoad;
    [ObservableProperty] private int delta;

    // Optional: show how much of the load is queue vs programming, if available.
    [ObservableProperty] private int beforeQueue;
    [ObservableProperty] private int beforeProgramming;
}
