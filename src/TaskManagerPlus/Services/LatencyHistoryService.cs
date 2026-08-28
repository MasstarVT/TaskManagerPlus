using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TaskManagerPlus.Services;

/// <summary>One recorded probe window's min/avg/max/loss for one target (#506) - same
/// persisted-JSON-list/trimming shape as NetworkHistoryService's network-history.json, but for
/// actual millisecond latency figures rather than a connection-count proxy: real per-ping
/// latency numbers exist here, unlike per-process byte-level bandwidth.</summary>
public sealed class LatencyHistoryEntry
{
    public string TimestampUtc { get; set; } = string.Empty; // round-trip ("o") format
    public string Target { get; set; } = string.Empty; // LatencyTier.ToString()
    public double MinMs { get; set; }
    public double AvgMs { get; set; }
    public double MaxMs { get; set; }
    public double LossPercent { get; set; }
}

/// <summary>One aggregated point for the #506 24h/7d history view - one bucket (an hour, for the
/// 24h view; a day, for the 7d view) averaged/maxed across every window recorded within it.</summary>
public sealed record LatencyHistoryPoint(DateTime BucketStartLocal, double AvgMs, double MaxMs, double LossPercent);

/// <summary>
/// Item #506: persisted latency/loss history, appended to on the same periodic cadence
/// NetworkViewModel flushes the #501 rolling window on, so "was the internet actually bad last
/// night, or does it just feel that way" has an actual answer instead of a feeling. Plain JSON
/// under %AppData%\TaskManagerPlus\latency-history.json, trimmed to the most recent 30 days -
/// same shape/trimming/fail-silent approach as NetworkHistoryService's network-history.json,
/// just with a shorter retention window since this is a per-window (minutes-granularity) log
/// rather than a once-a-day rollup.
/// </summary>
public static class LatencyHistoryService
{
    private static string HistoryPath => AppPaths.GetPath("latency-history.json");
    private const int MaxDays = 30;

    public static void RecordWindow(string target, double minMs, double avgMs, double maxMs, double lossPercent)
    {
        try
        {
            var entries = Load();
            entries.Add(new LatencyHistoryEntry
            {
                TimestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Target = target,
                MinMs = minMs,
                AvgMs = avgMs,
                MaxMs = maxMs,
                LossPercent = lossPercent,
            });

            var cutoff = DateTime.UtcNow.AddDays(-MaxDays);
            entries = entries.Where(e => TryParseUtc(e.TimestampUtc, out var t) && t >= cutoff).ToList();

            Save(entries);
        }
        catch
        {
            // Best-effort - a failed write shouldn't disrupt the probe loop it rides on.
        }
    }

    /// <summary>Hour-bucketed points over the last 24 hours for one target.</summary>
    public static List<LatencyHistoryPoint> GetLast24Hours(string target) =>
        Aggregate(target, DateTime.UtcNow.AddHours(-24), TimeSpan.FromHours(1));

    /// <summary>Day-bucketed points over the last 7 days for one target.</summary>
    public static List<LatencyHistoryPoint> GetLast7Days(string target) =>
        Aggregate(target, DateTime.UtcNow.AddDays(-7), TimeSpan.FromDays(1));

    private static List<LatencyHistoryPoint> Aggregate(string target, DateTime sinceUtc, TimeSpan bucketSize)
    {
        var withTimestamps = Load()
            .Where(e => e.Target == target)
            .Select(e => (Entry: e, Ok: TryParseUtc(e.TimestampUtc, out var t), When: t))
            .Where(x => x.Ok && x.When >= sinceUtc)
            .ToList();

        return withTimestamps
            .GroupBy(x => new DateTime((x.When.Ticks / bucketSize.Ticks) * bucketSize.Ticks, DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g => new LatencyHistoryPoint(
                g.Key.ToLocalTime(),
                g.Average(x => x.Entry.AvgMs),
                g.Max(x => x.Entry.MaxMs),
                g.Average(x => x.Entry.LossPercent)))
            .ToList();
    }

    private static bool TryParseUtc(string text, out DateTime result) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);

    private static List<LatencyHistoryEntry> Load()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var json = File.ReadAllText(HistoryPath);
                var entries = JsonSerializer.Deserialize<List<LatencyHistoryEntry>>(json);
                if (entries is not null) return entries;
            }
        }
        catch
        {
            // Corrupt/unreadable file - start fresh rather than blocking on it.
        }
        return new List<LatencyHistoryEntry>();
    }

    private static void Save(List<LatencyHistoryEntry> entries)
    {
        var dir = Path.GetDirectoryName(HistoryPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }
}
