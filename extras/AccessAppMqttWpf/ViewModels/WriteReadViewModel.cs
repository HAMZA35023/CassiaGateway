using AccessAppMqttWpf.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;

namespace AccessAppMqttWpf.ViewModels;

public partial class WriteReadViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DiscoveredDevice _device;
    private readonly string _mac;

    public string Title => $"Write-Read • {_mac} • {_device.BestCassia}";

    public ObservableCollection<string> Lines { get; } = new();

    [ObservableProperty] private string? selectedLine;

    [ObservableProperty] private string hex = "";
    [ObservableProperty] private int handle = 19;
    [ObservableProperty] private bool noResponse = true;
    [ObservableProperty] private bool expectReply;
    [ObservableProperty] private int timeoutSeconds = 15;

    public WriteReadViewModel(MainViewModel main, DiscoveredDevice device)
    {
        _main = main;
        _device = device;
        _mac = (device.Mac ?? "").Trim().ToUpperInvariant();

        _main.PlainReplyReceived += OnPlainReply;
    }

    public void Dispose()
    {
        _main.PlainReplyReceived -= OnPlainReply;
    }


    private void SetClipboardTextSafe(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        void doSet()
        {
            // Clipboard can be temporarily locked by other processes (Remote Desktop, password managers, etc.)
            // Retry a few times to avoid random failures.
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return;
                }
                catch (ExternalException)
                {
                    System.Threading.Thread.Sleep(50);
                }
            }
        }

        if (Application.Current?.Dispatcher is null)
        {
            doSet();
            return;
        }

        if (Application.Current.Dispatcher.CheckAccess())
            doSet();
        else
            Application.Current.Dispatcher.InvokeAsync(doSet, System.Windows.Threading.DispatcherPriority.Background);
    }


    private void OnPlainReply(string mac, string msg)
    {
        if (!mac.Equals(_mac, StringComparison.OrdinalIgnoreCase))
            return;

        // Show exactly what backend sends (including MAC) so it's easy to copy/compare.
        Lines.Insert(0, $"[{DateTimeOffset.Now:HH:mm:ss}] {mac}: {msg}");
    }

    [RelayCommand]
    private void CopyLine()
    {
        if (string.IsNullOrWhiteSpace(SelectedLine)) return;
        SetClipboardTextSafe(SelectedLine);
    }

    [RelayCommand]
    private void CopyHex()
    {
        var src = SelectedLine;
        if (string.IsNullOrWhiteSpace(src))
            src = Lines.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(src)) return;

        var hex = ExtractHex(src);
        if (string.IsNullOrWhiteSpace(hex)) return;

        SetClipboardTextSafe(hex);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task Connect()
    {
        await _main.ConnectDeviceAsync(_device);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task Disconnect()
    {
        await _main.DisconnectDeviceAsync(_device);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task Send()
    {
        var useTimeout = ExpectReply ? TimeoutSeconds : (int?)null;
        await _main.SendWriteReadAsync(_device, Hex, Handle, NoResponse, ExpectReply, useTimeout);
        Lines.Insert(0, $"[{DateTimeOffset.Now:HH:mm:ss}] TX handle={Handle} hex={NormalizeHex(Hex)} expectReply={ExpectReply}");
    }

    private static string NormalizeHex(string s)
    {
        s = (s ?? "").Trim();
        if (s.StartsWith("0X", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        // Allow common formats: "01 10 ...", "01-10-...", etc.
        s = System.Text.RegularExpressions.Regex.Replace(s, "[^0-9A-Fa-f]", "");
        return s.ToUpperInvariant();
    }

    private static string ExtractHex(string s)
    {
        // Find the *longest* hex-ish chunk, allowing separators like '-' or spaces.
        // Examples we want to support:
        //  - notif=01-10-00-09-00-FB-95-1D-01
        //  - 0110000900FB951D01
        //  - 0x01 10 00 ...
        if (string.IsNullOrWhiteSpace(s)) return "";

        // Look for either a long continuous hex run OR repeated byte pairs with separators.
        var candidates = Regex.Matches(s, @"(?i)(?:0x)?(?:[0-9a-f]{2}(?:[^0-9a-f]+[0-9a-f]{2}){3,}|[0-9a-f]{8,})")
            .Cast<Match>()
            .Select(m => NormalizeHex(m.Value))
            .Where(x => x.Length >= 8)
            .OrderByDescending(x => x.Length)
            .ToList();

        return candidates.FirstOrDefault() ?? "";
    }
}
