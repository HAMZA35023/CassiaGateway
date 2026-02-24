using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace AccessAppMqttWpf.Models;

public partial class RuntimeVariableGroup : ObservableObject
{
    public RuntimeVariableGroup(string key, string title)
    {
        Key = key ?? "";
        Title = title ?? "";
        Variables = new ObservableCollection<RuntimeVariableItem>();
        Variables.CollectionChanged += OnVariablesCollectionChanged;
    }

    public string Key { get; }
    public string Title { get; }
    public ObservableCollection<RuntimeVariableItem> Variables { get; }
    public string Header => $"{Title} ({Variables.Count})";

    private void OnVariablesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Header));
    }
}
