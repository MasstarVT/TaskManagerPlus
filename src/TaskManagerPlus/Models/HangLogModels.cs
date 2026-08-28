namespace TaskManagerPlus.Models;

/// <summary>
/// #237: one completed hang - a window that #235's IsHungAppWindow flagged and later recovered
/// from. Persisted to hang-log.json (see HangLogService) so the rolling history survives a
/// restart; "hangs today"/"longest hang" counters are computed from this list on the fly
/// (ResponsivenessViewModel), not stored separately, the same "derived, not duplicated" approach
/// StabilityViewModel.ComputeStabilityIndex already uses.
/// </summary>
public sealed class HangLogEntry
{
    public string AppName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public DateTime StartTime { get; init; }
    public double DurationSeconds { get; init; }
}
