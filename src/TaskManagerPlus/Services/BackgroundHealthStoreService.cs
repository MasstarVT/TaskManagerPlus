using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #959/#960/#961/#962: the always-on background health collector's own store - a rolling
/// health-history.jsonl (the live/currently-appended-to file) plus dated, gzipped segments under a
/// HealthHistory\ subfolder once the live file crosses a size threshold. Reuses LoggingService's
/// existing "close, gzip, delete the plain file" rotation technique (#960's own instruction) rather
/// than reinventing it, and prunes the oldest gzipped segments once the combined size (live file +
/// every segment) exceeds the configured disk budget (#960).
///
/// Deliberately a completely separate store from LoggingService's user-started CSV log - see
/// CLAUDE.md/this chunk's instructions on why the two must stay independent.
/// </summary>
public static class BackgroundHealthStoreService
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    private static string LiveLogPath => AppPaths.GetPath("health-history.jsonl");
    private static string SegmentsDir => AppPaths.GetPath("HealthHistory");

    // Rows are tiny (~200-300 bytes each) - at the default 60s interval that's roughly 300-400KB/
    // day, so a 5MB roll threshold is roughly two weeks per segment, keeping each gzip pass cheap
    // and each segment a reasonable, boundable read for #962's date-range chart.
    private const long RollThresholdBytes = 5L * 1024 * 1024;

    // ----- #959: append ------------------------------------------------------------------------

    public static void AppendRow(HealthHistoryRow row, int budgetMb)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.AppendAllText(LiveLogPath, JsonSerializer.Serialize(row, JsonOpts) + Environment.NewLine);

            var info = new FileInfo(LiveLogPath);
            if (info.Exists && info.Length >= RollThresholdBytes)
                RollAndPrune(budgetMb);
        }
        catch
        {
            // Best-effort - a failed append never blocks the collector's next tick.
        }
    }

    // ----- #960: roll + gzip (LoggingService.RotateFile's technique) + budget-based pruning -----

    private static void RollAndPrune(int budgetMb)
    {
        try
        {
            Directory.CreateDirectory(SegmentsDir);
            string segPath = Path.Combine(SegmentsDir, $"health-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl.gz");
            using (var source = File.OpenRead(LiveLogPath))
            using (var destination = File.Create(segPath))
            using (var gzip = new GZipStream(destination, CompressionLevel.Optimal))
            {
                source.CopyTo(gzip);
            }
            File.Delete(LiveLogPath);
        }
        catch
        {
            // Best-effort - if rolling fails, keep appending to the growing live file rather than
            // losing rows; the next successful roll will catch up.
            return;
        }

        PruneToBudget(budgetMb);
    }

    private static void PruneToBudget(int budgetMb)
    {
        try
        {
            long budgetBytes = Math.Max(1, budgetMb) * 1024L * 1024L;
            var segments = Directory.Exists(SegmentsDir)
                ? Directory.GetFiles(SegmentsDir, "*.jsonl.gz").Select(f => new FileInfo(f)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<FileInfo>();
            long liveSize = File.Exists(LiveLogPath) ? new FileInfo(LiveLogPath).Length : 0;
            long total = liveSize + segments.Sum(f => f.Length);

            // Oldest-named segment first (the timestamped filename sorts chronologically) - never
            // touches the live file, only ever prunes already-rolled/gzipped segments.
            int i = 0;
            while (total > budgetBytes && i < segments.Count)
            {
                try { total -= segments[i].Length; File.Delete(segments[i].FullName); }
                catch { /* one locked/inaccessible segment shouldn't stop the rest of the sweep */ }
                i++;
            }
        }
        catch
        {
            // Best-effort - a failed prune just means the budget is exceeded until the next roll.
        }
    }

    /// <summary>#960: "using X.X MB of a Y MB budget, covering N days" readout for the Settings
    /// drawer / Background Health panel.</summary>
    public static (double UsedMb, int BudgetMb, int CoveredDays) GetUsageSummary(int budgetMb)
    {
        double usedBytes = 0;
        DateTime? earliest = null;
        try
        {
            if (File.Exists(LiveLogPath)) usedBytes += new FileInfo(LiveLogPath).Length;
            if (Directory.Exists(SegmentsDir))
                foreach (var f in Directory.GetFiles(SegmentsDir, "*.jsonl.gz"))
                    usedBytes += new FileInfo(f).Length;

            earliest = FindEarliestTimestampUtc();
        }
        catch
        {
            // Best-effort - a failed enumeration just reports 0 MB / 0 days.
        }

        int coveredDays = earliest is { } e ? Math.Max(0, (int)Math.Ceiling((DateTime.UtcNow - e).TotalDays)) : 0;
        return (usedBytes / (1024.0 * 1024.0), budgetMb, coveredDays);
    }

    private static DateTime? FindEarliestTimestampUtc()
    {
        // Segment filenames are health-yyyyMMdd-HHmmss.jsonl.gz (the roll time, i.e. the LATEST
        // timestamp that segment could hold) - the oldest-named segment is the one most likely to
        // hold the earliest surviving row, so only that one needs decompressing for this estimate
        // rather than every segment on disk.
        try
        {
            if (Directory.Exists(SegmentsDir))
            {
                var oldest = Directory.GetFiles(SegmentsDir, "*.jsonl.gz").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (oldest is not null)
                {
                    foreach (var row in ReadGzipRows(oldest))
                        return row.TimestampUtc; // first row in file order is the earliest in that segment
                }
            }
        }
        catch { /* fall through to the live file */ }

        try
        {
            if (File.Exists(LiveLogPath))
                foreach (var line in File.ReadLines(LiveLogPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var row = JsonSerializer.Deserialize<HealthHistoryRow>(line, JsonOpts);
                        if (row is not null) return row.TimestampUtc;
                    }
                    catch { /* skip a malformed line */ }
                }
        }
        catch { }

        return null;
    }

    // ----- reading (used by #961's aggregate conditions and #962's chart/worst-moments) --------

    /// <summary>Every stored row (live file + gzipped segments) with TimestampUtc in
    /// [sinceUtc, untilUtc], ascending by time. This is a genuinely on-demand parse each call, not
    /// a cache - see #961's own instruction ("parse/aggregate on demand ... not a separate
    /// always-computed cache") - callers on a hot path should go through
    /// <see cref="ComputeAggregateCached"/> instead of calling this directly every tick.</summary>
    public static List<HealthHistoryRow> ReadRows(DateTime sinceUtc, DateTime? untilUtc = null)
    {
        DateTime until = untilUtc ?? DateTime.MaxValue;
        var results = new List<HealthHistoryRow>();

        try
        {
            if (Directory.Exists(SegmentsDir))
            {
                foreach (var f in Directory.GetFiles(SegmentsDir, "*.jsonl.gz").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    // The filename's timestamp is when that segment was rolled (its LATEST row) -
                    // a segment rolled before `sinceUtc` minus one full roll window couldn't
                    // possibly hold anything newer than `sinceUtc`, so it's skipped without ever
                    // decompressing it. This is a heuristic prune (segments can span more or less
                    // time depending on collector interval), not exact - worst case it reads one
                    // extra segment, never too few.
                    // sinceUtc can legitimately be DateTime.MinValue ("All available") - guard the
                    // lookback subtraction so that never overflows past DateTime.MinValue.
                    DateTime lookbackFloor = sinceUtc > DateTime.MinValue.AddDays(1) ? sinceUtc.AddDays(-1) : DateTime.MinValue;
                    if (TryParseSegmentRollTimeUtc(f, out var rollTime) && rollTime < lookbackFloor)
                        continue;

                    foreach (var row in ReadGzipRows(f))
                        if (row.TimestampUtc >= sinceUtc && row.TimestampUtc <= until) results.Add(row);
                }
            }
        }
        catch { /* best-effort */ }

        try
        {
            if (File.Exists(LiveLogPath))
                foreach (var line in File.ReadLines(LiveLogPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var row = JsonSerializer.Deserialize<HealthHistoryRow>(line, JsonOpts);
                        if (row is not null && row.TimestampUtc >= sinceUtc && row.TimestampUtc <= until) results.Add(row);
                    }
                    catch { /* skip a malformed line */ }
                }
        }
        catch { /* best-effort */ }

        results.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        return results;
    }

    private static IEnumerable<HealthHistoryRow> ReadGzipRows(string gzPath)
    {
        List<HealthHistoryRow> rows = new();
        try
        {
            using var source = File.OpenRead(gzPath);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var row = JsonSerializer.Deserialize<HealthHistoryRow>(line, JsonOpts);
                    if (row is not null) rows.Add(row);
                }
                catch { /* skip a malformed line */ }
            }
        }
        catch { /* unreadable/corrupt segment - contributes no rows */ }
        return rows;
    }

    private static bool TryParseSegmentRollTimeUtc(string path, out DateTime rollTimeUtc)
    {
        rollTimeUtc = default;
        var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)); // strip .gz then .jsonl
        const string prefix = "health-";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return DateTime.TryParseExact(name[prefix.Length..], "yyyyMMdd-HHmmss",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out rollTimeUtc);
    }

    // ----- #961: metric-key mapping + aggregate math --------------------------------------------

    /// <summary>Maps a RuleCondition.Metric-style key (the same naming style
    /// RulesEngineService.BuildMetricBag uses for the live bag, plus a couple of history-only
    /// keys this compact store also carries) to one HealthHistoryRow's value for that metric.
    /// Null means "this row has no value for that metric" (e.g. no temperature sensor that tick) -
    /// degrade to absent, never fabricate.</summary>
    public static double? GetMetricValue(HealthHistoryRow row, string metric) => metric.Trim().ToLowerInvariant() switch
    {
        "cpu.percent" => row.CpuPercent,
        "mem.percent" => row.RamPercent,
        "thermal.cpupackagec" => row.CpuTempC,
        "disk.queuelength" => row.DiskQueueLength,
        "disk.latencyms" => row.DiskLatencyMs,
        "services.failedcount" => row.FailedServiceCount,
        "network.haserrors" => row.NetworkHasErrors ? 1.0 : 0.0,
        _ => null,
    };

    /// <summary>#961's four required aggregates over rows in the trailing `overSeconds` window.
    /// "countAbove" counts distinct UTC calendar days that had at least one qualifying sample
    /// (rather than raw sample count) - a closer match to how the demonstration rule
    /// ("...on multiple days this month") and most real uses of this aggregate actually read, and
    /// insensitive to the collector's own poll interval. Returns null when there's no data in the
    /// window at all, so a leaf condition against it degrades to "not exists" rather than a
    /// fabricated 0.</summary>
    public static double? ComputeAggregate(string metric, string aggregate, int overSeconds, double? countAboveThreshold)
    {
        var since = DateTime.UtcNow.AddSeconds(-Math.Max(1, overSeconds));
        var rows = ReadRows(since);
        if (rows.Count == 0) return null;

        switch (aggregate.Trim().ToLowerInvariant())
        {
            case "max":
            {
                var values = rows.Select(r => GetMetricValue(r, metric)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
                return values.Count > 0 ? values.Max() : null;
            }
            case "mean":
            {
                var values = rows.Select(r => GetMetricValue(r, metric)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
                return values.Count > 0 ? values.Average() : null;
            }
            case "p95":
            {
                var sorted = rows.Select(r => GetMetricValue(r, metric)).Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
                if (sorted.Count == 0) return null;
                int idx = (int)Math.Ceiling(0.95 * sorted.Count) - 1;
                return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
            }
            case "countabove":
            {
                double threshold = countAboveThreshold ?? double.MaxValue;
                return rows
                    .Where(r => GetMetricValue(r, metric) is { } v && v > threshold)
                    .Select(r => r.TimestampUtc.Date)
                    .Distinct()
                    .Count();
            }
            default:
                return null;
        }
    }

    // #961: a short-lived memo cache, NOT an always-computed background cache - the rules engine
    // ticks every ~2s (SummaryViewModel's health timer), and re-parsing potentially months of
    // gzipped history on every single tick for one aggregate rule would defeat #959/#966's whole
    // "must stay lightweight" point. A ~30s TTL means an aggregate rule's condition is still
    // genuinely computed on demand (per this chunk's own instruction) - it just isn't recomputed
    // more often than a human could notice anyway for a window that's typically hours-to-months
    // long.
    private static readonly ConcurrentDictionary<string, (DateTime ComputedUtc, double? Value)> AggregateCache = new();
    private static readonly TimeSpan AggregateCacheTtl = TimeSpan.FromSeconds(30);

    public static double? ComputeAggregateCached(string metric, string aggregate, int overSeconds, double? countAboveThreshold)
    {
        string key = $"{metric}|{aggregate}|{overSeconds}|{countAboveThreshold}";
        if (AggregateCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.ComputedUtc < AggregateCacheTtl)
            return cached.Value;

        double? value = ComputeAggregate(metric, aggregate, overSeconds, countAboveThreshold);
        AggregateCache[key] = (DateTime.UtcNow, value);
        return value;
    }
}
