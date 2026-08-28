using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>#108: loads and saves EventFilterSettings to
/// %AppData%\TaskManagerPlus\event-filters.json (routed through AppPaths, so portable mode
/// redirects it too) - same shape as PollIntervalSettingsService/ThemeService. A missing or corrupt
/// file falls back to EventFilterSettings.Defaults (the built-in "Crash triage" / "Storage errors" /
/// "Service failures" / "Boot problems" presets) rather than an empty list, so the Events tab always
/// has at least the built-ins to start from.</summary>
public static class EventFilterSettingsService
{
    private static string SettingsPath => AppPaths.GetPath("event-filters.json");

    public static EventFilterSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<EventFilterSettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to the built-in presets.
        }
        return EventFilterSettings.Defaults;
    }

    public static void Save(EventFilterSettings settings)
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
            // Best-effort - if we can't persist, saved filters just don't survive this session.
        }
    }
}
