using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 18, #363: rolling window (default 5 minutes) of per-disk latency samples, fed by
/// StorageViewModel on every PerformanceViewModel.Sampled tick, used to compute p50/p95/p99/max and
/// a bucketed histogram. Deliberately NOT inside HardwareMonitorService - that file's scope this
/// round is raw per-tick counter reads only - so this lives as its own small service, one instance
/// owned by StorageViewModel. In-memory only, not persisted: a session-long rolling window, not a
/// historical log (unlike, say, FreeSpaceHistoryService's daily low-water-mark file).
/// </summary>
public sealed class DiskLatencyHistoryService
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    /// <summary>&lt;1ms, 1-5ms, 5-20ms, 20-100ms, 100-500ms, &gt;500ms - fixed buckets matching the
    /// #364 stall threshold's default (500ms) so the top bucket and "is this a stall" line up.</summary>
    public static readonly string[] HistogramBucketLabels = { "<1 ms", "1-5 ms", "5-20 ms", "20-100 ms", "100-500 ms", ">500 ms" };

    private sealed class Sample
    {
        public DateTime TimestampUtc;
        public double ReadMs;
        public double WriteMs;
        public double TransferMs;
    }

    private readonly Dictionary<string, LinkedList<Sample>> _byDisk = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Window { get; set; } = DefaultWindow;

    /// <summary>Appends one sample for this tick and prunes anything older than Window - called
    /// once per disk per PerformanceViewModel tick.</summary>
    public void Record(string diskName, double readMs, double writeMs, double transferMs)
    {
        if (!_byDisk.TryGetValue(diskName, out var list))
        {
            list = new LinkedList<Sample>();
            _byDisk[diskName] = list;
        }

        var now = DateTime.UtcNow;
        list.AddLast(new Sample { TimestampUtc = now, ReadMs = readMs, WriteMs = writeMs, TransferMs = transferMs });

        var cutoff = now - Window;
        while (list.First is not null && list.First.Value.TimestampUtc < cutoff)
            list.RemoveFirst();
    }

    /// <summary>Null until at least one sample has been recorded for this disk (callers should
    /// treat a small SampleCount as "still collecting" rather than a meaningful percentile).</summary>
    public DiskLatencyPercentiles? GetPercentiles(string diskName)
    {
        if (!_byDisk.TryGetValue(diskName, out var list) || list.Count == 0) return null;

        var reads = list.Select(s => s.ReadMs).OrderBy(v => v).ToArray();
        var writes = list.Select(s => s.WriteMs).OrderBy(v => v).ToArray();
        var transfers = list.Select(s => s.TransferMs).ToArray();

        var bucketCounts = new double[HistogramBucketLabels.Length];
        foreach (var t in transfers)
        {
            int idx = t switch
            {
                < 1 => 0,
                < 5 => 1,
                < 20 => 2,
                < 100 => 3,
                < 500 => 4,
                _ => 5,
            };
            bucketCounts[idx]++;
        }
        var bucketPercents = new double[bucketCounts.Length];
        if (transfers.Length > 0)
            for (int i = 0; i < bucketCounts.Length; i++)
                bucketPercents[i] = bucketCounts[i] / transfers.Length * 100.0;

        return new DiskLatencyPercentiles
        {
            SampleCount = list.Count,
            WindowStartUtc = list.First!.Value.TimestampUtc,
            ReadP50Ms = Percentile(reads, 50),
            ReadP95Ms = Percentile(reads, 95),
            ReadP99Ms = Percentile(reads, 99),
            ReadMaxMs = reads.Length > 0 ? reads[^1] : 0,
            WriteP50Ms = Percentile(writes, 50),
            WriteP95Ms = Percentile(writes, 95),
            WriteP99Ms = Percentile(writes, 99),
            WriteMaxMs = writes.Length > 0 ? writes[^1] : 0,
            HistogramBucketPercents = bucketPercents,
        };
    }

    /// <summary>Linear-interpolated percentile over an already-sorted array (the "R-7"/Excel
    /// PERCENTILE.INC method) - close enough for a diagnostics readout, not claimed as a
    /// statistically rigorous estimator over what's a fairly small rolling sample.</summary>
    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];

        double rank = p / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];

        double frac = rank - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }
}
