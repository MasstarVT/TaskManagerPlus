using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #618: persists measured cooldown events (seconds to fall 20°C from peak after load drops from
/// &gt;80% to &lt;10%) to %AppData%\TaskManagerPlus\cooldown-history.json - same
/// age/count-capped shape as ThrottleHistoryService. Charted as a monthly average trend; cooldown
/// slope is largely workload-independent, so a slowing trend is a cleaner "thermal paste is
/// drying out" signal than absolute temperatures, which #611 already has to bucket by load/power
/// to make comparable.
/// </summary>
public static class CooldownHistoryService
{
    private const int MaxAgeDays = 365;
    private const int MaxEntries = 1000;

    private static string SettingsPath => AppPaths.GetPath("cooldown-history.json");

    public static List<CooldownEvent> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<List<CooldownEvent>>(json);
                if (list is not null) return list.OrderBy(e => e.RecordedAt).ToList();
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to "no history".
        }
        return new List<CooldownEvent>();
    }

    /// <summary>Appends one measured cooldown event and re-saves, pruning by age and count the
    /// same way ThrottleHistoryService.Append does. Best-effort.</summary>
    public static void Append(CooldownEvent ev)
    {
        try
        {
            var list = Load();
            list.Add(ev);

            var cutoff = DateTime.Now.AddDays(-MaxAgeDays);
            list = list.Where(e => e.RecordedAt >= cutoff).OrderBy(e => e.RecordedAt).ToList();
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
