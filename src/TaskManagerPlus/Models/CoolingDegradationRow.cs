namespace TaskManagerPlus.Models;

/// <summary>
/// One display row on the "Cooling degradation" card (#611) - the delta between the most
/// recently recorded month and the earliest recorded month for one (load, power) bucket.
/// Computed on demand by EnergyThermalsViewModel from CoolingBaselineService's persisted
/// buckets; not itself persisted.
/// </summary>
public sealed class CoolingDegradationRow
{
    public string BucketText { get; init; } = string.Empty;
    public double DeltaC { get; init; }
    public string ComparisonText { get; init; } = string.Empty;
}
