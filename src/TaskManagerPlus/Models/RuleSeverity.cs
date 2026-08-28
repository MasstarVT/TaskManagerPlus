namespace TaskManagerPlus.Models;

/// <summary>#919, extended #928: four-level severity a rule (or a per-rule override, #923)
/// carries. Only High renders in the danger color on the Health Check card
/// (HealthIssue.IsCritical mirrors Severity == High) - Info/Low/Medium all read as "something to
/// look at", not "the app is broken", the same convention the pre-rules-engine IsCritical flag
/// already used with its own two tiers before #916-919 introduced this enum.</summary>
public enum RuleSeverity
{
    Info,
    Low,
    Medium,
    High,
}
