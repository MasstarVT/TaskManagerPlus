using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>Loads and saves LeakWatchSettings (#406) to leak-watch.json - same
/// load/save-fails-silently-to-defaults shape as PollIntervalSettingsService/ThemeService.</summary>
public static class LeakWatchSettingsService
{
    private static string SettingsPath => AppPaths.GetPath("leak-watch.json");

    public static LeakWatchSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<LeakWatchSettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults.
        }
        return LeakWatchSettings.Defaults;
    }

    public static void Save(LeakWatchSettings settings)
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
