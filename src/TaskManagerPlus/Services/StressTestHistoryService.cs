using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #700: persisted log of stress-test run summaries (stress-test-history.json) - one small row per
/// run, so "same test, 12°C hotter and 400 MHz lower than three months ago" can be computed without
/// keeping every past run's full trace around. Same load/append/cap-and-save JSON shape as
/// GpuHangHistoryService/PciLinkHistoryService, fails silently to "no history" on a missing or
/// corrupt file.
/// </summary>
public static class StressTestHistoryService
{
    private const int MaxEntries = 200;

    private static string SettingsPath => AppPaths.GetPath("stress-test-history.json");

    public static List<StressTestHistoryEntry> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<List<StressTestHistoryEntry>>(json);
                if (list is not null) return list.OrderByDescending(e => e.Timestamp).ToList();
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to "no history".
        }
        return new List<StressTestHistoryEntry>();
    }

    public static List<StressTestHistoryEntry> Append(StressTestHistoryEntry newEntry)
    {
        var entries = Load();
        entries.Insert(0, newEntry);
        entries = entries.Take(MaxEntries).ToList();

        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, this one run just won't show up in future
            // comparisons.
        }

        return entries;
    }

    /// <summary>The most recent PRIOR entry of the same test type - what a freshly finished run
    /// should be compared against. Null when this is the first recorded run of that type.</summary>
    public static StressTestHistoryEntry? FindMostRecentOfSameType(IEnumerable<StressTestHistoryEntry> entries, StressTestType type, DateTime excludeAtOrAfter)
        => entries.Where(e => e.TestType == type && e.Timestamp < excludeAtOrAfter)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefault();
}
