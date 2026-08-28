using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #657: overnight/standby battery-drain calculator, persisted to standby-drain.json. Each
/// completed sleep/wake pair (from WakeHistoryService's Power-Troubleshooter-derived Sleep
/// Time/Wake Time) is matched against the nearest battery-percent sample either side of it in
/// PowerHistoryLogService's existing once-a-minute persisted trail (extended - see
/// PowerTempSample.BatteryPercent). This is necessarily reconstructed after the fact, not measured
/// live: the app itself is asleep for the interval being measured, so there is no tick to sample
/// "at the moment of sleep." A session this app never logged a battery-percent sample near on
/// either side is simply skipped, never estimated - the same degrade-never-fabricate stance as
/// every other persisted-metric service in this app. Same load/reconcile/cap-and-save shape as
/// ThrottleHistoryService/PowerHistoryLogService - fails silently to an empty list on a missing or
/// corrupt file.
/// </summary>
public static class StandbyDrainService
{
    private const int MaxSessions = 60;
    private static readonly TimeSpan SampleTolerance = TimeSpan.FromMinutes(12);
    private const double MinHoursAsleep = 0.1; // ~6 minutes - filters out noise from a very short nap/lid-close

    private static string SettingsPath => AppPaths.GetPath("standby-drain.json");

    public static List<StandbyDrainSession> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<List<StandbyDrainSession>>(json);
                if (list is not null) return list.OrderByDescending(s => s.SleepTime).ToList();
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to "no history".
        }
        return new List<StandbyDrainSession>();
    }

    private static void Save(List<StandbyDrainSession> sessions)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var capped = sessions.OrderByDescending(s => s.SleepTime).Take(MaxSessions).ToList();
            var json = JsonSerializer.Serialize(capped, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the trend just has less history to work with.
        }
    }

    /// <summary>Computes any newly-observable sessions from the given wake-history entries against
    /// the persisted power-history-log battery samples, merges them into standby-drain.json
    /// (deduped by SleepTime), and returns the full up-to-date list, newest first.</summary>
    public static List<StandbyDrainSession> ReconcileAndSave(IEnumerable<WakeHistoryEntry> wakeHistory, List<PowerTempSample> powerHistorySamples)
    {
        var existing = Load();
        var knownSleepTimes = new HashSet<DateTime>(existing.Select(s => s.SleepTime));

        foreach (var entry in wakeHistory.Where(e => e.SleepTime is not null))
        {
            var sleepTime = entry.SleepTime!.Value;
            if (knownSleepTimes.Contains(sleepTime)) continue;

            double hoursAsleep = (entry.WakeTime - sleepTime).TotalHours;
            if (hoursAsleep < MinHoursAsleep) continue;

            var beforeSample = PowerHistoryLogService.FindNearest(powerHistorySamples, sleepTime, SampleTolerance);
            var afterSample = PowerHistoryLogService.FindNearest(powerHistorySamples, entry.WakeTime, SampleTolerance);
            if (beforeSample?.BatteryPercent is not { } beforePct || afterSample?.BatteryPercent is not { } afterPct) continue;
            if (afterPct > beforePct) continue; // net gain (e.g. charged on AC through an S3 sleep) - not a drain session

            existing.Add(new StandbyDrainSession
            {
                SleepTime = sleepTime,
                WakeTime = entry.WakeTime,
                SleepBatteryPercent = beforePct,
                WakeBatteryPercent = afterPct,
            });
            knownSleepTimes.Add(sleepTime);
        }

        Save(existing);
        return Load();
    }
}
