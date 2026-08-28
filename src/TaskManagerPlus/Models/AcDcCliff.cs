namespace TaskManagerPlus.Models;

/// <summary>
/// #693: one measured steady-state sample (clock/package power/temperature) captured while the CPU
/// was under sustained load, tagged with whether the machine was on AC or battery at the moment -
/// see AcDcCliffService's remarks for the sustained-load gate that decides when a sample is worth
/// recording. Persisted (not just in-memory) since a laptop's own AC and battery sustained-load
/// sessions are often days or weeks apart - a single app session rarely sees both.
/// </summary>
public sealed class AcDcSteadyStateSample
{
    public DateTime Timestamp { get; init; }
    public bool OnBattery { get; init; }
    public double ClockGhz { get; init; }
    public double PackagePowerW { get; init; }
    public double TempC { get; init; }
}

/// <summary>#693: AC-vs-battery steady-state averages, computed from whatever AcDcSteadyStateSample
/// history exists so far - the measured (not configured) performance cliff. HasAcData/HasDcData are
/// false until AcDcCliffService.MinSamplesForSummary samples exist on that side, so a single stray
/// sample can't produce a misleadingly confident-looking average.</summary>
public sealed class AcDcCliffSummary
{
    public bool HasAcData { get; init; }
    public bool HasDcData { get; init; }
    public double AcClockGhz { get; init; }
    public double DcClockGhz { get; init; }
    public double AcPackagePowerW { get; init; }
    public double DcPackagePowerW { get; init; }
    public double AcTempC { get; init; }
    public double DcTempC { get; init; }
    public int AcSampleCount { get; init; }
    public int DcSampleCount { get; init; }

    public static readonly AcDcCliffSummary Empty = new();
}
