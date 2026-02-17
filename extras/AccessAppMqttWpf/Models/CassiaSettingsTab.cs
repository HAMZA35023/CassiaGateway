using System.Collections.ObjectModel;

namespace AccessAppMqttWpf.Models;

public sealed class CassiaSettingsTab
{
    public CassiaSettingsTab(string key, string title)
    {
        Key = string.IsNullOrWhiteSpace(key) ? "tab" : key.Trim();
        Title = string.IsNullOrWhiteSpace(title) ? Key : title.Trim();
    }

    public string Key { get; }
    public string Title { get; }
    public ObservableCollection<CassiaSettingsSection> Sections { get; } = new();
}
