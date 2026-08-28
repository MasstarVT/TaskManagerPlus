namespace TaskManagerPlus.Models;

/// <summary>
/// #918: one node of a rule's condition tree - a small hand-rolled expression syntax, not an
/// embedded scripting engine. Either a leaf (<see cref="Metric"/>/<see cref="Op"/>/<see cref="Value"/>)
/// that reads one key out of the metric bag (see RulesEngineService.BuildMetricBag), or a
/// combinator (<see cref="All"/>/<see cref="Any"/>/<see cref="Not"/>) wrapping child conditions -
/// exactly one of the two shapes should be populated per node; RulesEngineService.EvaluateCondition
/// checks the combinators first and falls through to the leaf fields otherwise.
///
/// <see cref="Value"/> is `object?` rather than a fixed type because a rule pack is plain JSON: when
/// this is deserialized straight off a pack file (or off the rule editor's condition textbox,
/// #922) System.Text.Json hands back a boxed <see cref="System.Text.Json.JsonElement"/> here, not a
/// raw double/bool/string - RulesEngineService's comparison helpers unbox either representation
/// (a live-authored `new RuleCondition { Value = 90 }`, e.g. from the built-in pack seed, works too).
/// </summary>
public sealed class RuleCondition
{
    public string? Metric { get; set; }

    /// <summary>One of eq/ne/lt/lte/gt/gte/exists (#918) - case-insensitive.</summary>
    public string? Op { get; set; }

    public object? Value { get; set; }

    public List<RuleCondition>? All { get; set; }
    public List<RuleCondition>? Any { get; set; }
    public RuleCondition? Not { get; set; }
}
