namespace TaskManagerPlus.Models;

/// <summary>
/// #959: one compact row appended to health-history.jsonl by BackgroundHealthCollectorService -
/// a handful of already-live key metrics (never re-sampled independently, see the collector's own
/// remarks) plus #966's self-measured cost of the collection cycle that produced this row. Kept
/// deliberately small (CLAUDE.md's "compact JSON row" requirement) - this is NOT the same store as
/// LoggingService's user-started, full-fidelity, 1Hz CSV log; this is the always-on, low-frequency,
/// small-footprint background collector's own store.
/// </summary>
public sealed class HealthHistoryRow
{
    public DateTime TimestampUtc { get; set; }

    public double CpuPercent { get; set; }
    public double RamPercent { get; set; }

    /// <summary>Null when no temperature sensor was found - degrade to absent, never a fabricated
    /// 0 (CLAUDE.md's "degrade to Unknown/0/hidden - never fabricate").</summary>
    public double? CpuTempC { get; set; }

    public double DiskQueueLength { get; set; }

    /// <summary>The worse of read/write latency for this tick - one compact number rather than two,
    /// matching #959's "a handful of key metrics" scope.</summary>
    public double DiskLatencyMs { get; set; }

    public bool NetworkHasErrors { get; set; }
    public int FailedServiceCount { get; set; }

    /// <summary>Optional lightweight top-CPU-process name for this tick - lets #962's "worst 10
    /// moments" table show what was running without storing a full process snapshot (which would
    /// violate #959's "compact" requirement). Null if the Processes list wasn't available this
    /// tick.</summary>
    public string? TopProcessName { get; set; }

    // ----- #966: this cycle's own self-measured cost, stored alongside the metrics it collected
    // so the Background Health panel's cost readout can be computed from real history rather than
    // only the current session's in-memory rolling average. -----

    /// <summary>Estimated CPU% this collection cycle cost, labeled as an estimate - see
    /// BackgroundHealthCollectorService's remarks for the calculation.</summary>
    public double CollectorCpuPercentEstimate { get; set; }

    public double CollectorDurationMs { get; set; }
}
