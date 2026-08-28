using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #609: persists one median-idle-CPU-temperature entry per calendar day
/// (EnergyThermalsViewModel's idle-window tracker) to
/// %AppData%\TaskManagerPlus\thermal-baseline.json - a rising idle floor at unchanged ambient is a
/// cleaner dust/pump-failure signal than load temps (which are confounded by how hard the machine
/// happened to be working that day). Kept as its own file rather than folded into
/// throttle-history.json (#604) - the two are genuinely different shapes (one entry/day vs. one
/// entry/episode) and different producers (idle-window tracker vs. throttle-episode tracker), so
/// merging them would just mean every reader has to filter by record type instead of reading the
/// file it actually wants.
/// </summary>
public static class ThermalBaselineService
{
    private const int MaxEntries = 730; // ~2 years, one per day

    private static string SettingsPath => AppPaths.GetPath("thermal-baseline.json");

    public static List<ThermalBaselineEntry> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<List<ThermalBaselineEntry>>(json);
                if (list is not null) return list.OrderBy(e => e.Date).ToList();
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to "no history".
        }
        return new List<ThermalBaselineEntry>();
    }

    /// <summary>Adds or overwrites today's entry (one per calendar day) with the median idle
    /// temperature just measured. Best-effort, same as every other settings write in this app.</summary>
    public static void RecordToday(double medianIdleTempC)
    {
        try
        {
            var list = Load();
            var today = DateTime.Now.Date;
            list.RemoveAll(e => e.Date == today);
            list.Add(new ThermalBaselineEntry { Date = today, MedianIdleTempC = medianIdleTempC });
            list = list.OrderBy(e => e.Date).ToList();
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
