using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #604: persists closed throttle episodes (EnergyThermalsViewModel) to
/// %AppData%\TaskManagerPlus\throttle-history.json (routed through AppPaths, same shape as
/// PollIntervalSettingsService/ThemeService) - turns the Energy &amp; Thermals throttle list from
/// "the 10 most recent in-memory entries" into a rolling, cross-session "has this been getting
/// worse" record. Capped by both age and count so the file can't grow unbounded on a machine that
/// throttles constantly. Fails silently to an empty list on a missing/corrupt file, same as every
/// other settings file in this app.
/// </summary>
public static class ThrottleHistoryService
{
    private const int MaxAgeDays = 180;
    private const int MaxEntries = 500;

    private static string SettingsPath => AppPaths.GetPath("throttle-history.json");

    public static List<ThrottleEpisode> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<List<ThrottleEpisode>>(json);
                if (list is not null) return list.OrderBy(e => e.Start).ToList();
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to "no history".
        }
        return new List<ThrottleEpisode>();
    }

    /// <summary>Appends one closed episode and re-saves, pruning entries older than
    /// <see cref="MaxAgeDays"/> and capping the total count at <see cref="MaxEntries"/> (oldest
    /// dropped first). Best-effort - if this can't persist, the session still has the episode in
    /// EnergyThermalsViewModel's in-memory RecentThrottleEpisodes list.</summary>
    public static void Append(ThrottleEpisode episode)
    {
        try
        {
            var list = Load();
            list.Add(episode);

            var cutoff = DateTime.Now.AddDays(-MaxAgeDays);
            list = list.Where(e => e.End >= cutoff).OrderBy(e => e.Start).ToList();
            if (list.Count > MaxEntries) list = list.Skip(list.Count - MaxEntries).ToList();

            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }
}
