namespace TaskManagerPlus.Models;

/// <summary>
/// #914: one symptom branch expressed as data - an ordered step-list factory plus a verdict
/// function, looked up by <see cref="SymptomId"/> from the small in-memory registry
/// <c>TroubleshootViewModel</c> builds once in its constructor. Adding a 14th symptom is adding one
/// more entry to that registry (a title/description plus a step-list/verdict factory) rather than a
/// new branch in a hand-written switch statement - <see cref="BuildSteps"/> still runs ordinary C#
/// to build the step list (it needs live ViewModel state like the shared PerformanceViewModel's
/// history buffers, the same as every branch before this round), but the branch *shape* itself -
/// symptom id, landing-page copy, steps, verdict - is one small object per symptom instead of a
/// case in <c>BuildBranch</c>'s switch plus a matching pair of hand-named methods.
///
/// Per-step branching within a branch (e.g. #911's three-way disk-bottleneck split, #912's
/// "no battery -> skip the rest" gate) is expressed via each <see cref="DiagnosticStep.ShouldRun"/>
/// predicate rather than procedural if/else here - see that property's remarks.
/// </summary>
public sealed class TroubleshootBranchDefinition
{
    public required string SymptomId { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required Func<List<DiagnosticStep>> BuildSteps { get; init; }
    public required Func<TroubleshootRun, string> BuildVerdict { get; init; }
}
