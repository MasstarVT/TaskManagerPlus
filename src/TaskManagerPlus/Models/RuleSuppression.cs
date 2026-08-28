namespace TaskManagerPlus.Models;

/// <summary>#924: one "snooze"/"ignore" record in suppressions.json - a plain List&lt;RuleSuppression&gt;
/// on disk. RulesEngineService filters any finding whose rule has a non-expired entry here out of
/// the Health Check card's main list (into the "N findings suppressed" panel instead, see
/// RuleEvaluationResult).</summary>
public sealed class RuleSuppression
{
    public string RuleId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Null = permanent ("Ignore on this machine"); set = "Snooze for N days".</summary>
    public DateTime? ExpiresUtc { get; set; }
}
