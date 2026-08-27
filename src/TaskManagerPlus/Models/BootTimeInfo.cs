namespace TaskManagerPlus.Models;

/// <summary>One named boot-time component read from the most recent Diagnostics-Performance
/// event 100 (#89) - e.g. "MainPathBootTime", "BootPostBootTime". Field names/count aren't a
/// documented, versioned contract (see BootPerformanceService's remarks), so this is a plain
/// adaptive list rather than fixed named properties - whatever the event actually reported.</summary>
public sealed class BootTimeComponent
{
    public string Label { get; init; } = string.Empty;
    public int Milliseconds { get; init; }
}

/// <summary>Boot time breakdown for the most recent boot (#89).</summary>
public sealed class BootTimeBreakdown
{
    public DateTime BootTime { get; init; }
    public List<BootTimeComponent> Components { get; init; } = new();

    /// <summary>The largest named component found - in practice the one that represents the
    /// overall total (sub-phases are smaller slices of it), since this event's exact field
    /// naming isn't a documented contract this app can rely on matching by name alone.</summary>
    public int? TotalMs => Components.Count == 0 ? null : Components.Max(c => c.Milliseconds);
}

/// <summary>One persisted boot-time-trend sample (#90) - just enough to chart "is my boot time
/// getting worse over time" across sessions.</summary>
public sealed class BootHistoryEntry
{
    public DateTime Timestamp { get; init; }
    public int TotalMs { get; init; }
}
