using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>Loads and saves SummarySettings to %AppData%\TaskManagerPlus\summary-settings.json -
/// same shape as ThemeService/AlertThresholdsService.</summary>
public static class SummarySettingsService
{
    // Round 12, #87: routed through AppPaths so portable mode redirects this next to the exe.
    private static string SettingsPath => AppPaths.GetPath("summary-settings.json");

    public static SummarySettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<SummarySettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults.
        }
        return SummarySettings.Defaults;
    }

    public static void Save(SummarySettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }
}
