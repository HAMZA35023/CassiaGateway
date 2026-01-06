using System;
using System.IO;
using System.Text.Json;

public static class FirmwareRulesLoader
{
    public static SettingsVersionPatcher.RulesRoot LoadFromRootFolder()
    {
        try
        {
            // Root folder = AppContext.BaseDirectory (typically where exe runs)
            var root = AppContext.BaseDirectory;
            var path = Path.Combine(root, "firmware-rules.json");

            Console.WriteLine($"[Rules] Loading rules from: {path}");

            if (!File.Exists(path))
            {
                Console.WriteLine($"[Rules][WARN] Rules file not found. No patching will be applied.");
                return new SettingsVersionPatcher.RulesRoot();
            }

            var json = File.ReadAllText(path);

            var rr = JsonSerializer.Deserialize<SettingsVersionPatcher.RulesRoot>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (rr?.Rules == null)
            {
                Console.WriteLine($"[Rules][WARN] Rules parsed but empty/null.");
                return new SettingsVersionPatcher.RulesRoot();
            }

            Console.WriteLine($"[Rules] Loaded {rr.Rules.Count} rule(s).");
            return rr;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Rules][ERROR] Failed to load rules: {ex}");
            return new SettingsVersionPatcher.RulesRoot();
        }
    }
}
