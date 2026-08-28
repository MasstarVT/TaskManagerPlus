namespace TaskManagerPlus.Models;

/// <summary>
/// #944: one "N of your M crashes/failures happened within this window of a change" headline,
/// surfaced at the top of the Timeline panel. Explicitly a coincidence count, not a causation
/// claim - see TimelineService.ComputeCorrelations' remarks and the wording baked into Headline
/// itself (both the UI text and this code comment state it plainly, per CLAUDE.md's "quick flag,
/// not a verdict" convention).
/// </summary>
public sealed class TimelineCorrelationFinding
{
    public required string Headline { get; init; }
    public required TimelineLane ChangeLane { get; init; }
    public required int MatchCount { get; init; }
    public required int TotalFailureCount { get; init; }
    public required double WindowHours { get; init; }
}
