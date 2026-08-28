namespace TaskManagerPlus.Models;

/// <summary>
/// #916-919: one rule definition loaded from a JSON rule pack under
/// AppPaths.SettingsDirectory\Rules\*.json (see RulesEngineService). A rule evaluating true against
/// the live metric bag produces a HealthIssue-equivalent finding carrying this metadata - HealthIssue
/// itself was extended (rather than adding a second parallel finding type) with the fields below so
/// there's exactly one finding shape other domain code can render.
/// </summary>
public sealed class Rule
{
    /// <summary>Stable identifier - must be unique across every loaded pack (RulesEngineService
    /// flags and skips a duplicate at load time, see RuleValidationResult), and is what #923's
    /// enable/disable overrides, #924's suppressions, and #927's `finding.&lt;id&gt;.fired`
    /// synthetic metric keys all key off.</summary>
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Plain-English message. May reference metric-bag keys as `{metricKey}` placeholders
    /// (e.g. "{mem.availablePercent}% RAM available") resolved against the live bag when the rule
    /// fires (RulesEngineService.ResolveBody) - a placeholder whose key isn't in the bag is left
    /// as-is rather than fabricating a value.</summary>
    public string Body { get; set; } = string.Empty;

    public RuleSeverity Severity { get; set; } = RuleSeverity.Medium;

    /// <summary>0-100, "quick flag, not a verdict" - how confident this heuristic is, not a
    /// probability of anything specific being wrong.</summary>
    public int Confidence { get; set; } = 100;

    public string Category { get; set; } = string.Empty;

    public string? DocsUrl { get; set; }

    public RuleCondition Condition { get; set; } = new();

    /// <summary>#934: findings from rules sharing a non-null GroupKey collapse into one expandable
    /// parent row on the Health Check card (e.g. several storage rules all firing about the same
    /// drive) instead of each rendering as its own top-level row.</summary>
    public string? GroupKey { get; set; }

    /// <summary>#931: optional "what else could explain this" line - "this can also be caused
    /// by...". Rendered directly under a fired finding's message wherever findings show, framed
    /// so a fired rule doesn't read as a confirmed diagnosis (see this project's "quick flag, not
    /// a verdict" convention). Set on a handful of the built-in pack's rules whose condition alone
    /// can't rule out an innocent cause (see RulesEngineService.BuiltInPackJson) - most rules
    /// simply leave this null.</summary>
    public string? CounterEvidence { get; set; }

    /// <summary>#932: optional Body-style template (same `{metric.key}` placeholder syntax as
    /// Body) resolved into the fired finding's HealthIssue.ImpactText - but only when every
    /// placeholder it references is actually present in the live metric bag
    /// (RulesEngineService.TryResolveImpactText). A template with any unresolvable placeholder
    /// just leaves ImpactText null rather than showing a fabricated or partially-filled-in figure.
    /// Most rules have no honest impact figure to report and simply leave this null.</summary>
    public string? ImpactTemplate { get; set; }

    /// <summary>#920: a rule with this set only fires once its condition has held true across
    /// enough recent samples (from PerformanceViewModel's existing rolling history buffers) to
    /// cover this many seconds - see RulesEngineService.EvaluateWithSustain for exactly which
    /// metrics/condition shapes support real dwell-time evaluation vs. degrading to instantaneous.</summary>
    public int? SustainedForSeconds { get; set; }

    /// <summary>#926: set only on a rule that arrived via the rule editor's Import flow - renders
    /// as a small "imported from &lt;file&gt;, not verified by this app" badge in the rule list.
    /// Persisted (not JsonIgnore) so the badge survives an app restart.</summary>
    public bool ImportedFromFile { get; set; }

    public string? ImportSourceFileName { get; set; }
}
