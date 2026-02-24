using System.Collections.ObjectModel;

namespace AccessAppMqttWpf.Models;

public sealed class CassiaSettingsSection
{
    public CassiaSettingsSection(string title)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "Section" : title.Trim();
    }

    public string Title { get; }
    public ObservableCollection<CassiaEditableSettingItem> Items { get; } = new();
}
