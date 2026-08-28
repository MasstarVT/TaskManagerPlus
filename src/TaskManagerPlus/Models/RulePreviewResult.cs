namespace TaskManagerPlus.Models;

/// <summary>One rule's outcome from RulesEngineService.PreviewAll - used both for the rule editor's
/// live "would currently fire" indicator (#922) and for testing a pack against a saved metric-bag
/// snapshot (#925). Unlike RuleEvaluationResult (SummaryViewModel's Health Check feed), this is
/// every loaded+enabled rule's raw outcome, before suppression filtering - suppression is a display
/// concern for the live card, not part of "does this rule's condition currently hold".</summary>
public sealed class RulePreviewResult
{
    public required LoadedRule Rule { get; init; }
    public bool WouldFire { get; init; }

    /// <summary>Set when the condition tree itself had a problem at evaluation time (should be
    /// rare - RulesEngineService's load-time validation already catches bad operators/missing
    /// fields, so this is mostly a defensive backstop).</summary>
    public string? Error { get; init; }
}
