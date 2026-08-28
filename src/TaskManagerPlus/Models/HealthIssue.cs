namespace TaskManagerPlus.Models;

/// <summary>
/// One plain-language finding on the Summary tab's Health Check card (#64) - originally a
/// hand-rolled rule-based aggregation of conditions read straight off other ViewModels; #916-919
/// moved the bulk of that logic into RulesEngineService, which now produces most of these findings
/// from JSON rule definitions instead. This stays the ONE finding shape everywhere findings render
/// (rather than adding a second parallel type): a rule-engine finding populates every field below,
/// while the handful of checks that don't cleanly fit the metric-bag/condition shape (the
/// statistical CPU/RAM/Disk anomaly check, the Defender-scan heuristic, the reboot-pending-vs-
/// snapshot-diff correlation - see SummaryViewModel.RefreshHealthIssues) still construct a
/// HealthIssue directly and just leave the rule-metadata fields at their defaults.
/// </summary>
public sealed class HealthIssue
{
    public string Message { get; init; } = string.Empty;

    /// <summary>True renders in the danger color, false in the warning color - both are
    /// "something to look at", not "the app is broken". For a rule-engine finding this mirrors
    /// Severity == Critical; kept as its own field (rather than a computed property) so the
    /// couple of hand-rolled checks that predate the rules engine can keep setting it directly.</summary>
    public bool IsCritical { get; init; }

    // #919: rule-engine metadata - null/default for the hand-rolled checks noted above.
    public string? RuleId { get; init; }
    public string? Title { get; init; }
    public RuleSeverity Severity { get; init; } = RuleSeverity.Warning;
    public int Confidence { get; init; } = 100;
    public string? Category { get; init; }
    public string? DocsUrl { get; init; }
    public string? GroupKey { get; init; }
}
