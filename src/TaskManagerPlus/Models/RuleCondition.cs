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

    // ----- #961: aggregate-over-history leaf shape -----------------------------------------
    //
    // When Aggregate is set, this leaf reads BackgroundHealthStoreService's health-history.jsonl
    // instead of the live metric bag: Metric names one of the compact fields
    // BackgroundHealthStoreService.GetMetricValue knows how to read off a HealthHistoryRow (the
    // same "cpu.percent"/"mem.percent"/"thermal.cpuPackageC"/... key style the live bag uses, plus
    // a couple of history-only keys - see that method), Aggregate computes one number over the
    // trailing OverSeconds window (max/mean/p95/countAbove), and Op/Value then compare that one
    // number exactly like any other leaf. Example (#961's own demonstration rule):
    // {"Metric":"thermal.cpuPackageC","Aggregate":"countAbove","AggregateThreshold":95,
    //  "OverSeconds":2592000,"Op":"gte","Value":3}
    // reads as "on how many distinct days in the last 30 days did the CPU package exceed 95C -
    // fire if that's >= 3 days".

    /// <summary>One of max/mean/p95/countAbove (case-insensitive) - null means "not an aggregate
    /// leaf, read the live metric bag as normal" (RulesEngineService.EvaluateCondition).</summary>
    public string? Aggregate { get; set; }

    /// <summary>The trailing window, in seconds, the aggregate is computed over. Defaults to one
    /// day (86400s) if unset on an aggregate leaf, rather than silently reading "all history".</summary>
    public int? OverSeconds { get; set; }

    /// <summary>Only meaningful for the "countAbove" aggregate - the per-sample threshold a row's
    /// value must exceed to count. (Op/Value below then compare the resulting *count*, not this
    /// threshold, to whatever the rule considers "fire-worthy".)</summary>
    public double? AggregateThreshold { get; set; }
}
