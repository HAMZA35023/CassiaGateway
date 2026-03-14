using System;
using System.Linq;
using System.Windows;
using AccessAppMqttWpf.Services;

namespace AccessAppMqttWpf;

public partial class App : Application
{
    private static readonly SettingsStore SettingsStore = new();
    public static string CurrentTheme { get; private set; } = "Light";

    public static void ApplyTheme(string theme, bool persist = true)
    {
        var app = Current;
        if (app == null)
            return;

        theme = string.IsNullOrWhiteSpace(theme) ? "Light" : theme.Trim();
        var uri = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase)
            ? new Uri("Themes/Theme.Dark.xaml", UriKind.Relative)
            : new Uri("Themes/Theme.Light.xaml", UriKind.Relative);

        var dicts = app.Resources.MergedDictionaries;
        var existing = dicts.FirstOrDefault(d =>
            d.Source != null && d.Source.OriginalString.StartsWith("Themes/Theme.", StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            var index = dicts.IndexOf(existing);
            if (index >= 0)
                dicts[index] = new ResourceDictionary { Source = uri };
        }
        else
        {
            dicts.Add(new ResourceDictionary { Source = uri });
        }

        CurrentTheme = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";

        if (persist)
        {
            var settings = SettingsStore.Load();
            settings.accessapp ??= new AccessAppSettings();
            settings.accessapp.theme = CurrentTheme;
            SettingsStore.Save(settings);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Services.AppLog.Init();
        try
        {
            var settings = SettingsStore.Load();
            var theme = settings.accessapp?.theme;
            if (!string.IsNullOrWhiteSpace(theme))
                ApplyTheme(theme, persist: false);
        }
        catch
        {
            ApplyTheme("Light", persist: false);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (MainWindow?.DataContext is ViewModels.MainViewModel vm)
                vm.ShutdownLocalServices();
        }
        catch { }
        base.OnExit(e);
    }
}
