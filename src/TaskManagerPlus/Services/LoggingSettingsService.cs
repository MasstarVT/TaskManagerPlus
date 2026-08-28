using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>Loads and saves logging preferences to %AppData%\TaskManagerPlus\logging-settings.json
/// - same shape as ThemeService/AlertThresholdsService's persistence.</summary>
public static class LoggingSettingsService
{
    // Round 12, #87: routed through AppPaths so portable mode redirects this next to the exe.
    private static string SettingsPath => AppPaths.GetPath("logging-settings.json");

    public static LoggingSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<LoggingSettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt/unreadable settings file - fall back to defaults.
        }
        return LoggingSettings.Defaults;
    }

    public static void Save(LoggingSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort - if we can't persist, the setting just won't survive a restart.
        }
    }
}
