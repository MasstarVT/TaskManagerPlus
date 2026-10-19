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
                if (settings?.Filters is not null) return Sanitize(settings);
                AppPaths.MarkUnreadable(SettingsPath);
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to the built-in presets, keeping a
            // copy of the user's file for the next Save.
            AppPaths.MarkUnreadable(SettingsPath);
        }
        return EventFilterSettings.Defaults;
    }

    /// <summary>EventsViewModel enumerates Filters while it is being constructed and reads
    /// Channels.Count/XPath when one is applied - explicit nulls in a hand-edited file would throw
    /// there, so null filters are dropped and null lists/text replaced.</summary>
    private static EventFilterSettings Sanitize(EventFilterSettings settings)
    {
        var filters = settings.Filters.Where(f => f is not null).ToList();
        if (filters.Count != settings.Filters.Count) AppPaths.MarkUnreadable(SettingsPath);
        foreach (var f in filters)
        {
            f.Name ??= string.Empty;
            f.XPath = string.IsNullOrWhiteSpace(f.XPath) ? "*" : f.XPath;
            f.Channels = f.Channels?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList() ?? new List<string>();
            f.Columns = f.Columns?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList() ?? new List<string>();
        }
        settings.Filters = filters;
        return settings;
    }

    public static void Save(EventFilterSettings settings)
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
            // Best-effort - if we can't persist, saved filters just don't survive this session.
        }
    }
}
