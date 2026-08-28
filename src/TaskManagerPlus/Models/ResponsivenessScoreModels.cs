namespace TaskManagerPlus.Models;

/// <summary>
/// Data shapes for suggestions.md #294/#295 (the composite responsiveness-score capstone of the
/// "DPC/ISR latency + interrupts + timers + hangs + frames + scheduler + locks/GC + page faults +
/// background activity" domain, items #201-293). Both scores are explicitly a *composite
/// heuristic* combining several already-computed signals from earlier chunks of this same domain -
/// "quick flag, not a verdict" - never a certified diagnosis. See ResponsivenessScoreService for
/// the (pure, no I/O) scoring math itself; ResponsivenessViewModel owns gathering each factor's
/// current value from whichever service/collection already computed it.
///
/// Score direction is the same for both: 0 = as bad as this heuristic can describe, 100 = no
/// signal of a problem from any factor this heuristic knows how to check. A missing input signal
/// (a measurement session that was never started, an unavailable counter) is excluded from the
/// average entirely - never treated as a 0 or a 100 - per CLAUDE.md's "degrade to Unknown, never
/// fabricate" rule. ExcludedFactors lists which inputs were skipped and why, for the tooltip.
/// </summary>
public sealed class ProcessResponsivenessScore
{
    /// <summary>0-100, higher is better (this process shows less signal of contributing to system
    /// lag) - see the class remarks for why a missing factor is excluded, not defaulted.</summary>
    public double Score { get; init; }

    /// <summary>Which single factor contributed the most to this process's score being lower than
    /// 100 - e.g. "Message-pump latency" - shown in the tooltip so a user can tell *why* an app is
    /// flagged, not just that it is.</summary>
    public string WorstFactorName { get; init; } = string.Empty;

    /// <summary>Full breakdown for the column's tooltip: which factors were used, their individual
    /// contribution, and which were excluded (and why) for this specific process.</summary>
    public string TooltipText { get; init; } = string.Empty;
}

/// <summary>#295: the single Summary-tab "how responsive does this PC feel right now" figure -
/// Good/Fair/Poor band (thresholds documented on ResponsivenessScoreService) plus the worst
/// contributing factor, named, so the tile reads as a diagnosis-shaped pointer rather than just a
/// number. HasData is false only in the (practically unreachable, since run-queue pressure and the
/// hung-window count are always live) case where every single factor was excluded.</summary>
public sealed class SystemResponsivenessScore
{
    public bool HasData { get; init; }
    public double Score { get; init; }

    /// <summary>"Good", "Fair", or "Poor" - see ResponsivenessScoreService's banding thresholds.
    /// Bound directly against DataTriggers in SummaryView.xaml the same way HealthIssue.IsCritical
    /// already drives SuccessBrush/WarningBrush/DangerBrush there - see SummaryView.xaml's Health
    /// Check card.</summary>
    public string Band { get; init; } = "Unknown";

    public string WorstFactorName { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
}
