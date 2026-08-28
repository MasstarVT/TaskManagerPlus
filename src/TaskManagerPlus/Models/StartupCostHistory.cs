namespace TaskManagerPlus.Models;

/// <summary>#748: one persisted measured-delay sample for a startup item, recorded once per
/// Startup-tab scan (Refresh) - written to startup-history.json, keyed by item name. See
/// StartupHistoryService.</summary>
public sealed class StartupCostSample
{
    public DateTime RecordedAtUtc { get; set; }
    public double DelaySeconds { get; set; }
}

/// <summary>#748: computed stats over a startup item's persisted sample history - a median plus a
/// sparkline (as a ready-to-bind "x,y x,y ..." point string, see
/// StartupHistoryService.BuildSparkline) instead of one volatile single-scan number, plus a "grown
/// from Xs to Ys over your last N boots" flag when the trend looks like real growth rather than
/// scan-to-scan noise. Quick flag, not a verdict - see StartupHistoryService.BuildStats.</summary>
public sealed class StartupCostStats
{
    public double MedianDelaySeconds { get; init; }
    public int SampleCount { get; init; }
    public string SparklinePointsText { get; init; } = string.Empty;
    public string? TrendFlag { get; init; }

    public string MedianText => $"Median {MedianDelaySeconds:0.#}s ({SampleCount} sample{(SampleCount == 1 ? "" : "s")})";
}
