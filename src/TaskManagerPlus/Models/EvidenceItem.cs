namespace TaskManagerPlus.Models;

/// <summary>#930: one supporting fact attached to a fired finding, shown as an expandable
/// "Evidence (N items)" section in the Health Check card's drill-down panel. For the built-in
/// rule pack's rules (simple metric-threshold checks) this is just the metric bag key/value
/// pair(s) the condition read (RulesEngineService.Evaluate populates it straight from the same
/// #929 condition-reading capture) - legitimate evidence in its own right. A future check with
/// real event-log rows or process names/PIDs could populate richer labels here without changing
/// this shape or any of the code that renders it.</summary>
public sealed class EvidenceItem
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}
