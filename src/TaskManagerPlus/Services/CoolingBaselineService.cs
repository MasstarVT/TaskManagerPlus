using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #611: persists one rolling-median steady-state CPU package temperature per
/// (load-decile x power-decile) bucket per calendar month to
/// %AppData%\TaskManagerPlus\cooling-baseline.json (same shape/routing as ThermalBaselineService/
/// ThrottleHistoryService). Kept as its own file rather than folded into thermal-baseline.json -
/// the two are different shapes (one entry per idle-day vs. one entry per load/power bucket per
/// month) and different producers (idle-window tracker vs. every-tick bucket sampler), so merging
/// them would just mean every reader filters by record type instead of reading the file it
/// actually wants.
/// </summary>
public static class CoolingBaselineService
{
    // 10 load deciles x 10 power deciles x ~4 years of months is comfortably under this.
    private const int MaxEntries = 6000;

    private static string SettingsPath => AppPaths.GetPath("cooling-baseline.json");

    public static List<CoolingBucketEntry> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<List<CoolingBucketEntry>>(json);
                if (list is not null) return list;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to "no history".
        }
        return new List<CoolingBucketEntry>();
    }

    /// <summary>Merges one session's freshly computed batch median into the persisted
    /// bucket/month entry - a sample-count-weighted average of medians (an approximation of a
    /// true rolling median across sessions, since raw samples from earlier sessions aren't kept
    /// on disk, only each batch's own median is). Best-effort, same as every other settings write
    /// in this app.</summary>
    public static void RecordBatch(int loadDecile, int powerDecile, DateTime when, double batchMedianTempC, int batchSampleCount)
    {
        if (batchSampleCount <= 0) return;

        try
        {
            var list = Load();
            var existing = list.FirstOrDefault(e =>
                e.LoadDecile == loadDecile && e.PowerDecile == powerDecile &&
                e.Year == when.Year && e.Month == when.Month);

            if (existing is null)
            {
                list.Add(new CoolingBucketEntry
                {
                    LoadDecile = loadDecile,
                    PowerDecile = powerDecile,
                    Year = when.Year,
                    Month = when.Month,
                    MedianTempC = batchMedianTempC,
                    SampleCount = batchSampleCount,
                });
            }
            else
            {
                int totalCount = existing.SampleCount + batchSampleCount;
                double weightedMedian = ((existing.MedianTempC * existing.SampleCount) + (batchMedianTempC * batchSampleCount)) / totalCount;
                list.Remove(existing);
                list.Add(new CoolingBucketEntry
                {
                    LoadDecile = loadDecile,
                    PowerDecile = powerDecile,
                    Year = when.Year,
                    Month = when.Month,
                    MedianTempC = weightedMedian,
                    SampleCount = totalCount,
                });
            }

            if (list.Count > MaxEntries)
                list = list.OrderBy(e => e.Year).ThenBy(e => e.Month).Skip(list.Count - MaxEntries).ToList();

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
