using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #625/#638: persists a coarse (about once a minute - see EnergyThermalsViewModel's append
/// cadence) trail of CPU package temperature/power and GPU power to
/// %AppData%\TaskManagerPlus\power-history-log.json, so a Kernel-Power 41 unexpected-reboot event
/// or a WHEA hardware-error event (both read from the event log well after the fact, possibly a
/// different app session than the one that recorded the conditions) can still be correlated
/// against "what was this machine drawing/running at right around that time." Same
/// load/append/cap-and-prune shape as ThrottleHistoryService - fails silently to an empty list on
/// a missing/corrupt file.
/// </summary>
public static class PowerHistoryLogService
{
    // ~3 days at one sample/minute - enough to catch a Kernel-Power 41 event correlated against
    // this session's own recent history without growing into a real telemetry log.
    private const int MaxEntries = 4320;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    private static string SettingsPath => AppPaths.GetPath("power-history-log.json");

    public static List<PowerTempSample> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<List<PowerTempSample>>(json);
                if (list is not null) return list.OrderBy(s => s.Timestamp).ToList();
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to "no history".
        }
        return new List<PowerTempSample>();
    }

    /// <summary>Appends one sample and re-saves, pruning entries older than <see cref="MaxAge"/>
    /// and capping the total count at <see cref="MaxEntries"/>. Best-effort, same as every other
    /// persisted-JSON write in this app.</summary>
    public static void Append(PowerTempSample sample)
    {
        try
        {
            var list = Load();
            list.Add(sample);

            var cutoff = DateTime.Now - MaxAge;
            list = list.Where(s => s.Timestamp >= cutoff).OrderBy(s => s.Timestamp).ToList();
            if (list.Count > MaxEntries) list = list.Skip(list.Count - MaxEntries).ToList();

            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, correlation just has less history to work with.
        }
    }

    /// <summary>Nearest sample to <paramref name="when"/> within <paramref name="tolerance"/> -
    /// null when the log has nothing recorded that close (a fresh install, or a gap while the app
    /// wasn't running over that stretch).</summary>
    public static PowerTempSample? FindNearest(List<PowerTempSample> samples, DateTime when, TimeSpan tolerance)
    {
        PowerTempSample? nearest = null;
        double bestDeltaSeconds = double.MaxValue;
        foreach (var s in samples)
        {
            double delta = Math.Abs((s.Timestamp - when).TotalSeconds);
            if (delta < bestDeltaSeconds) { bestDeltaSeconds = delta; nearest = s; }
        }
        return nearest is not null && bestDeltaSeconds <= tolerance.TotalSeconds ? nearest : null;
    }
}
