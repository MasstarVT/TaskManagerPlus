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
///
/// #928-937: extended with a confidence word, a captured condition/evidence drill-down trail, an
/// optional counter-evidence line, and an optional honest impact figure - all null/empty for a
/// finding (rule-engine or hand-rolled) that has none of these to honestly offer, per this app's
/// "degrade, never fabricate" convention.
/// </summary>
public sealed class HealthIssue
{
    public string Message { get; init; } = string.Empty;

    /// <summary>True renders in the danger color, false in the warning color - both are
    /// "something to look at", not "the app is broken". For a rule-engine finding this mirrors
    /// Severity == High; kept as its own field (rather than a computed property) so the couple of
    /// hand-rolled checks that predate the rules engine can keep setting it directly.</summary>
    public bool IsCritical { get; init; }

    // #919: rule-engine metadata - null/default for the hand-rolled checks noted above.
    public string? RuleId { get; init; }
    public string? Title { get; init; }
    public RuleSeverity Severity { get; init; } = RuleSeverity.Medium;
    public int Confidence { get; init; } = 100;
    public string? Category { get; init; }
    public string? DocsUrl { get; init; }
    public string? GroupKey { get; init; }

    /// <summary>#928: Confidence rendered as a word next to the severity chip - "quick flag, not
    /// a verdict" reads more honestly as prose than a bare percentage on its own.</summary>
    public string ConfidenceWord => Confidence switch
    {
        >= 80 => "likely",
        >= 50 => "possible",
        _ => "uncertain",
    };

    /// <summary>#929: every metric-bag leaf the firing condition read, with the actual value and
    /// the threshold it was compared against - captured once at fire time (see
    /// RulesEngineService.EvaluateConditionCapturing). Empty for the hand-rolled checks noted
    /// above, which have no condition tree to walk.</summary>
    public List<ConditionReading> ConditionReadings { get; init; } = new();

    /// <summary>#930: supporting facts for this finding, shown as an expandable
    /// "Evidence (N items)" section in the drill-down panel - for a rule-engine finding this is
    /// ConditionReadings restated as label/value pairs (legitimate evidence in its own right).</summary>
    public List<EvidenceItem> Evidence { get; init; } = new();

    /// <summary>#931: "what else could explain this" - set only when the firing rule's own
    /// definition (Rule.CounterEvidence) carries one. Rendered directly under the message
    /// wherever findings show, so a finding doesn't read as a confirmed diagnosis.</summary>
    public string? CounterEvidence { get; init; }

    /// <summary>#932: an honest, concrete impact figure in units the user feels (e.g. "12% of
    /// your RAM") - set only when the firing rule's Rule.ImpactTemplate resolved cleanly against
    /// every metric key it referenced (RulesEngineService's strict placeholder resolution). Never
    /// a fabricated or partially-filled-in figure - a rule with nothing honest to report here
    /// just leaves it null.</summary>
    public string? ImpactText { get; init; }

    /// <summary>#967: the firing rule's Rule.ActionIds, copied straight through - null/empty for
    /// every finding whose rule declares no fix (which is most of them), or for the couple of
    /// hand-rolled checks noted above that have no Rule behind them at all.</summary>
    public List<string>? ActionIds { get; init; }

    /// <summary>#967: whether the Health Check card should render a "Fix this" button for this
    /// finding - a plain computed property (like ConfidenceWord above) rather than a second
    /// ActionIds-is-non-empty check duplicated in XAML.</summary>
    public bool HasFixAction => ActionIds is { Count: > 0 };
}
