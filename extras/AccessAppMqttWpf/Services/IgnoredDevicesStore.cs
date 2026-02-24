using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AccessAppMqttWpf.Services;

public sealed class IgnoredDevicesStore
{
    private const string RegistryPath = @"Software\Cassia\AccessAppMqttWpf";
    private const string ValueName = "IgnoredDevices";

    public IReadOnlyCollection<string> Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            if (key == null) return Array.Empty<string>();

            var value = key.GetValue(ValueName);
            if (value is string[] list)
                return list.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

            if (value is string single && !string.IsNullOrWhiteSpace(single))
                return new[] { single };
        }
        catch
        {
            // best-effort
        }

        return Array.Empty<string>();
    }

    public void Save(IEnumerable<string> macs)
    {
        try
        {
            var list = (macs ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            if (key == null) return;

            if (list.Length == 0)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            key.SetValue(ValueName, list, RegistryValueKind.MultiString);
        }
        catch
        {
            // best-effort
        }
    }
}
