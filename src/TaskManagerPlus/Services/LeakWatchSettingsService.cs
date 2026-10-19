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
                if (settings?.WatchedImageNames is not null)
                {
                    // LeakWatchViewModel's constructor builds one row per name - drop null/blank
                    // names from a hand edit rather than throw there.
                    var names = settings.WatchedImageNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                    if (names.Count != settings.WatchedImageNames.Count) AppPaths.MarkUnreadable(SettingsPath);
                    settings.WatchedImageNames = names;
                    return settings;
                }
                AppPaths.MarkUnreadable(SettingsPath);
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults, keeping a copy of the
            // user's file for the next Save.
            AppPaths.MarkUnreadable(SettingsPath);
        }
        return LeakWatchSettings.Defaults;
    }

    public static void Save(LeakWatchSettings settings)
    {
        try
        {
            if (!AppPaths.PreserveUnreadable(SettingsPath)) return;
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
