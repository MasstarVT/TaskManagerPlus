namespace TaskManagerPlus.Models;

/// <summary>#919: severity a rule (or a per-rule override, #923) carries. Only Critical renders in
/// the danger color on the Health Check card (HealthIssue.IsCritical) - Info and Warning both read
/// as "something to look at", not "the app is broken", matching the pre-rules-engine IsCritical
/// convention this enum now sits alongside.</summary>
public enum RuleSeverity
{
    Info,
    Warning,
    Critical,
}
