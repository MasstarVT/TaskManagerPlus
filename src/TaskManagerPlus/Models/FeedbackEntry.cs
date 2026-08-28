namespace TaskManagerPlus.Models;

/// <summary>#935: one line of feedback.jsonl (AppPaths.SettingsDirectory) - a "Not a problem"
/// click on a Health Check finding. Purely local: nothing in this app ever reads this file back
/// over a network or uploads it anywhere - it exists only so a user's "that's not actually an
/// issue" reaction is recorded somewhere on their own machine, not lost the moment the finding
/// clears.</summary>
public sealed class FeedbackEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string RuleId { get; set; } = string.Empty;
    public string? Note { get; set; }
    public Dictionary<string, string> MetricValuesAtTime { get; set; } = new();
}
