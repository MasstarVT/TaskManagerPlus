namespace TaskManagerPlus.Models;

/// <summary>#937: one enabled rule RulesEngineService.Evaluate could not meaningfully evaluate
/// this pass, because its condition referenced a metric BuildMetricBag marked unavailable (a
/// collector timed out, was access-denied, or returned null/Unknown - see BuildMetricBag's
/// unavailableMetrics out-parameter). Kept out of both the fired-findings list and the "clean"
/// state entirely and surfaced in its own "couldn't check" list under the Health Check card, so a
/// missing data source never silently reads as "checked and fine".</summary>
public sealed class CouldNotEvaluateInfo
{
    public string RuleId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public List<string> UnavailableMetrics { get; init; } = new();
}
