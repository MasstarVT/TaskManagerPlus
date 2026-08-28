using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>#948: loads/saves the Timeline panel's lane-visibility/date-range/correlation-window
/// settings - AppPaths.SettingsDirectory\timeline-view.json, same shape as every other settings
/// file in this app (see AlertThresholdsService).</summary>
public static class TimelineViewSettingsService
{
    private static string SettingsPath => AppPaths.GetPath("timeline-view.json");

    public static TimelineViewSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<TimelineViewSettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults.
        }
        return TimelineViewSettings.Defaults;
    }

    public static void Save(TimelineViewSettings settings)
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
