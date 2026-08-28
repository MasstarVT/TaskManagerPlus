using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Item 66: loads/saves HangHistorySettings to %AppData%\TaskManagerPlus\hang-history.json (via
/// AppPaths, so portable mode redirects it too) - same shape as PollIntervalSettingsService/
/// CrashAnalysisSettingsService. ProcessesViewModel calls RecordHang once per hang episode (a
/// process transitioning from "Not responding" back to responding, or exiting while still hung -
/// see ProcessesViewModel.MergeInto), and StabilityViewModel's "Application hangs" card reads the
/// persisted list back via Load() on every refresh.
///
/// RecordHang does a synchronous Load-modify-Save round trip on every call rather than keeping an
/// in-memory cache - a hang episode ending is a rare event (at most a handful of pids per poll
/// tick, usually zero), nothing like the once-a-second cadence every other per-tick read in this
/// app has to stay cheap for, so the small extra file I/O here is a non-issue and keeps this
/// service stateless like every other settings service in the app.
/// </summary>
public static class HangHistoryService
{
    private static string SettingsPath => AppPaths.GetPath("hang-history.json");

    // Capped so a churny machine (many different short-lived executables each hanging once over a
    // long session) doesn't grow this file forever - the least-recently-hung entries are dropped
    // first, same "oldest falls off" shape ProcessesViewModel.RecentlyExited already uses.
    private const int MaxEntries = 200;

    public static HangHistorySettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<HangHistorySettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt/unreadable settings file - fall back to defaults.
        }
        return HangHistorySettings.Defaults;
    }

    public static void Save(HangHistorySettings settings)
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
            // Best-effort - if we can't persist, this session's hang tracking just doesn't survive
            // a restart; nothing else in the app depends on this succeeding.
        }
    }

    /// <summary>Records one completed hang episode for the given executable name - updates
    /// PeakDurationSeconds (a max, never decreases) and increments HangCount, then saves
    /// immediately. durationSeconds &lt;= 0 or a blank executable name are silently ignored (not a
    /// real completed episode).</summary>
    public static void RecordHang(string? executableName, int durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(executableName) || durationSeconds <= 0) return;

        var settings = Load();
        var existing = settings.Entries.FirstOrDefault(e =>
            string.Equals(e.ExecutableName, executableName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = new HangHistoryEntry { ExecutableName = executableName.Trim() };
            settings.Entries.Add(existing);
        }

        existing.HangCount++;
        existing.PeakDurationSeconds = Math.Max(existing.PeakDurationSeconds, durationSeconds);
        existing.LastHangTime = DateTime.Now;

        if (settings.Entries.Count > MaxEntries)
            settings.Entries = settings.Entries.OrderByDescending(e => e.LastHangTime).Take(MaxEntries).ToList();

        Save(settings);
    }
}
