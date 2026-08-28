namespace TaskManagerPlus.Models;

/// <summary>
/// Round 18, #363: p50/p95/p99/max latency plus a bucketed histogram for one physical disk, computed
/// over DiskLatencyHistoryService's rolling window - the flat mean the existing bottleneck-diagnostics
/// tiles show hides exactly the kind of stutter this surfaces.
/// </summary>
public sealed class DiskLatencyPercentiles
{
    public int SampleCount { get; init; }
    public DateTime WindowStartUtc { get; init; }

    public double ReadP50Ms { get; init; }
    public double ReadP95Ms { get; init; }
    public double ReadP99Ms { get; init; }
    public double ReadMaxMs { get; init; }

    public double WriteP50Ms { get; init; }
    public double WriteP95Ms { get; init; }
    public double WriteP99Ms { get; init; }
    public double WriteMaxMs { get; init; }

    /// <summary>Percent of samples (combined read+write "Avg. Disk sec/Transfer" readings) falling
    /// into each of DiskLatencyHistoryService.HistogramBucketLabels' buckets, same order.</summary>
    public double[] HistogramBucketPercents { get; init; } = Array.Empty<double>();
}
