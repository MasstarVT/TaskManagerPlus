namespace TaskManagerPlus.Models;

/// <summary>#923: one rule's enable/disable + severity override, keyed by rule id in
/// rules-overrides.json (a plain Dictionary&lt;string, RuleOverride&gt; - no wrapper class needed,
/// System.Text.Json serializes a dictionary natively). Null fields mean "no override" - a rule with
/// no entry, or an entry with both fields null, behaves exactly as its pack defines it.</summary>
public sealed class RuleOverride
{
    public bool? Enabled { get; set; }
    public RuleSeverity? SeverityOverride { get; set; }
}
