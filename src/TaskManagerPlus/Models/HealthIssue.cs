namespace TaskManagerPlus.Models;

/// <summary>
/// One plain-language finding on the Summary tab's Health Check card (#64) - a rule-based
/// aggregation of conditions that already exist elsewhere on other tabs, re-surfaced in one
/// place so a user doesn't have to know which tab to check first. Each rule is independent and
/// best-effort: a data source that's unavailable simply contributes no issue, not an error.
/// </summary>
public sealed class HealthIssue
{
    public string Message { get; init; } = string.Empty;

    /// <summary>True renders in the danger color, false in the warning color - both are
    /// "something to look at", not "the app is broken".</summary>
    public bool IsCritical { get; init; }
}
