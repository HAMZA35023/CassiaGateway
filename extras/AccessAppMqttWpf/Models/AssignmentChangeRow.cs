using CommunityToolkit.Mvvm.ComponentModel;

namespace AccessAppMqttWpf.Models;

/// <summary>
/// A single suggested (or unchanged) assignment row shown in AssignmentPlanWindow.
/// </summary>
public partial class AssignmentChangeRow : ObservableObject
{
    [ObservableProperty] private string mac = "";
    [ObservableProperty] private string closestCassia = "";
    [ObservableProperty] private int closestRssi;

    [ObservableProperty] private string currentAssigned = "";
    [ObservableProperty] private string suggestedAssigned = "";
    [ObservableProperty] private int suggestedRssi;

    [ObservableProperty] private string reason = "";

    public bool IsChange =>
        !string.IsNullOrWhiteSpace(SuggestedAssigned) &&
        !string.IsNullOrWhiteSpace(CurrentAssigned) &&
        !SuggestedAssigned.Equals(CurrentAssigned, System.StringComparison.OrdinalIgnoreCase);
}
