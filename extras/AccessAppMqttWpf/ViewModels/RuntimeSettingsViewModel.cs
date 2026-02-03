using AccessAppMqttWpf.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace AccessAppMqttWpf.ViewModels;

public partial class RuntimeSettingsViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;

    public RuntimeSettingsViewModel(MainViewModel main, string cassiaName)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        CassiaName = (cassiaName ?? "").Trim();
        Variables = new ObservableCollection<RuntimeVariableItem>();
        _main.RuntimeVariablesReceived += OnRuntimeVariablesReceived;
        _ = RefreshAsync();
    }

    public string CassiaName { get; }

    public ObservableCollection<RuntimeVariableItem> Variables { get; }

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string statusText = "";

    public event Action? RequestClose;

    public void Dispose()
    {
        _main.RuntimeVariablesReceived -= OnRuntimeVariablesReceived;
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (string.IsNullOrWhiteSpace(CassiaName))
        {
            RequestClose?.Invoke();
            return;
        }

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var v in Variables)
        {
            if (v.TryGetValue(out var value, out var err))
                payload[v.Name] = value;
            else
                errors.Add($"{v.Name}: {err}");
        }

        if (errors.Count > 0)
        {
            StatusText = "Fix invalid values: " + string.Join("  ", errors.Take(3));
            return;
        }

        await _main.SetRuntimeForCassiaAsync(CassiaName, payload).ConfigureAwait(false);
        StatusText = "Sent update. Waiting for refresh...";
        await RefreshAsync().ConfigureAwait(false);

        Application.Current.Dispatcher.Invoke(() => RequestClose?.Invoke());
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    private async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(CassiaName))
            return;

        IsLoading = true;
        StatusText = "Loading runtime variables...";

        var snapshot = await _main.RequestRuntimeVariablesAsync(CassiaName).ConfigureAwait(false);

        Application.Current.Dispatcher.Invoke(() =>
        {
            Variables.Clear();
            if (snapshot != null)
            {
                foreach (var item in snapshot.Values.OrderBy(v => v.Name).Select(RuntimeVariableItem.FromValue))
                    Variables.Add(item);
                StatusText = $"Loaded {Variables.Count} variables.";
            }
            else
            {
                StatusText = "No runtime variables returned.";
            }

            IsLoading = false;
        });
    }

    private void OnRuntimeVariablesReceived(string cassia, IReadOnlyDictionary<string, RuntimeVariableValue> vars)
    {
        if (!string.Equals(cassia, CassiaName, StringComparison.OrdinalIgnoreCase)) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            Variables.Clear();
            foreach (var item in vars.Values.OrderBy(v => v.Name).Select(RuntimeVariableItem.FromValue))
                Variables.Add(item);
            StatusText = $"Updated {Variables.Count} variables.";
        });
    }
}
