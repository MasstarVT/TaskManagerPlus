using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #237: loads and saves the rolling hang-log (every window that went hung per #235 and later
/// recovered) to hang-log.json under AppPaths.SettingsDirectory - same shape/fail-silently-to-
/// defaults-on-corrupt-file convention as LoggingSettingsService/DashboardLayoutService. Capped at
/// MaxEntries most-recent on every save so the file doesn't grow forever on a machine that hangs
/// often.
/// </summary>
public static class HangLogService
{
    private const int MaxEntries = 200;

    // Round 12, #87: routed through AppPaths so portable mode redirects this next to the exe.
    private static string SettingsPath => AppPaths.GetPath("hang-log.json");

    public static List<HangLogEntry> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var entries = JsonSerializer.Deserialize<List<HangLogEntry>>(json);
                if (entries is not null) return entries;
            }
        }
        catch
        {
            // Corrupt/unreadable settings file - fall back to an empty log, same as every other
            // settings file in this app.
        }
        return new List<HangLogEntry>();
    }

    /// <summary>Caps to the most recent <see cref="MaxEntries"/> entries (by StartTime) before
    /// writing, regardless of how large the in-memory list handed in has grown.</summary>
    public static void Save(List<HangLogEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var capped = entries
                .OrderByDescending(e => e.StartTime)
                .Take(MaxEntries)
                .OrderBy(e => e.StartTime)
                .ToList();
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(capped, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort - if we can't persist, the log just won't survive a restart.
        }
    }
}
