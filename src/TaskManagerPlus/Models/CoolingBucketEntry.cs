namespace TaskManagerPlus.Models;

/// <summary>
/// One (CPU-load decile x package-power decile) bucket's rolling median steady-state CPU
/// package temperature for one calendar month (#611) - persisted to cooling-baseline.json
/// (CoolingBaselineService). Bucketing by load and power removes workload as a confounder when
/// comparing month to month: comparing the current month's bucket against the earliest recorded
/// month for the *same* bucket answers "is this specific workload running hotter than it used
/// to," which a naive "average temp is up" comparison can't - that's equally well explained by
/// simply running the machine harder more often lately.
/// </summary>
public sealed class CoolingBucketEntry
{
    /// <summary>0-9: CPU load percent rounded down to the nearest 10%.</summary>
    public int LoadDecile { get; init; }

    /// <summary>0-9: package power as a percentage of this session's highest observed package
    /// power (PowerSessionMaxW), rounded down to the nearest 10% - self-normalizing across very
    /// different CPUs (a 15W laptop part vs. a 250W desktop part) without a hardcoded wattage
    /// scale.</summary>
    public int PowerDecile { get; init; }

    public int Year { get; init; }
    public int Month { get; init; }

    /// <summary>Weighted-average-of-medians across every batch recorded into this bucket/month -
    /// an approximation of a true rolling median across sessions, since raw samples aren't kept
    /// between app runs (only each session's own batch median is). Documented as an
    /// approximation deliberately, not presented as an exact median.</summary>
    public double MedianTempC { get; init; }

    public int SampleCount { get; init; }
}
