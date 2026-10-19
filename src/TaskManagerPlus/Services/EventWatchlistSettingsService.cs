using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>#136: loads and saves EventWatchlistSettings to
/// %AppData%\TaskManagerPlus\event-watchlist.json (routed through AppPaths, so portable mode
/// redirects it too) - same shape/contract as EventFilterSettingsService. A missing or corrupt file
/// falls back to an empty watchlist rather than throwing, same "degrade, never fabricate" rule every
/// other settings file in this app follows.</summary>
public static class EventWatchlistSettingsService
{
    private static string SettingsPath => AppPaths.GetPath("event-watchlist.json");

    public static EventWatchlistSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<EventWatchlistSettings>(json);
                if (settings?.Entries is not null)
                {
                    // EventsViewModel adds every entry to its watchlist while it is being built;
                    // null entries/text from a hand edit are dropped or blanked here instead.
                    var entries = settings.Entries.Where(e => e is not null).ToList();
                    if (entries.Count != settings.Entries.Count) AppPaths.MarkUnreadable(SettingsPath);
                    foreach (var e in entries)
                    {
                        e.Channel ??= string.Empty;
                        e.Provider ??= string.Empty;
                    }
                    settings.Entries = entries;
                    return settings;
                }
                AppPaths.MarkUnreadable(SettingsPath);
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to an empty watchlist, keeping a copy
            // of the user's file for the next Save.
            AppPaths.MarkUnreadable(SettingsPath);
        }
        return new EventWatchlistSettings();
    }

    public static void Save(EventWatchlistSettings settings)
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
            // Best-effort - if we can't persist, the watchlist just doesn't survive this session.
        }
    }
}
