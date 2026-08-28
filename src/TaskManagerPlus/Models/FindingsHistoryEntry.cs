namespace TaskManagerPlus.Models;

/// <summary>#936: one line of findings-history.jsonl (AppPaths.SettingsDirectory) - a state
/// transition for one rule id between consecutive SummaryViewModel.RefreshHealthIssues passes.
/// "first-seen" and "resolved" are edge-triggered (written once, the moment a rule starts/stops
/// firing); "still-firing" is a valid value in this schema but deliberately never written on
/// every ~2s tick a chronic finding keeps firing, so the log doesn't grow unbounded for a single
/// sustained condition - see RefreshHealthIssues's remarks.</summary>
public sealed class FindingsHistoryEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string RuleId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>"first-seen" | "still-firing" | "resolved".</summary>
    public string Transition { get; set; } = string.Empty;
}
