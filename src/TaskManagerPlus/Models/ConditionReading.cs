namespace TaskManagerPlus.Models;

/// <summary>#929: one metric-bag leaf a fired rule's condition read, captured at evaluation time
/// (see RulesEngineService.EvaluateConditionCapturing) so the Health Check card's "Why am I
/// seeing this?" drill-down can show exactly what was read and what threshold it was compared
/// against - not just the rule's static JSON. A bare `exists` check has no threshold to show and
/// is skipped rather than captured (see EvaluateConditionCapturing's remarks).</summary>
public sealed class ConditionReading
{
    public string Metric { get; init; } = string.Empty;

    /// <summary>eq/ne/lt/lte/gt/gte, lower-cased - see RuleCondition.Op.</summary>
    public string Op { get; init; } = string.Empty;

    public string ActualValueText { get; init; } = string.Empty;
    public string ThresholdText { get; init; } = string.Empty;

    /// <summary>True when this leaf's metric key was absent from the bag at evaluation time -
    /// the reading still gets captured (so the drill-down can show "this one wasn't available"
    /// rather than just omitting it), distinct from #937's stronger "collector actively failed"
    /// signal (RulesEngineService's unavailableMetrics set), which instead keeps the whole rule
    /// out of the fired-findings list altogether.</summary>
    public bool WasUnavailable { get; init; }
}
