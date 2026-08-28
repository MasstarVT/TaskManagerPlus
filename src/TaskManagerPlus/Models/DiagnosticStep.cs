using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>
/// #901: lifecycle of one <see cref="DiagnosticStep"/> as a Troubleshoot run executes it in
/// order. Pending until the runner reaches it, Running while its check is in flight, then one of
/// the terminal states - TimedOut is distinct from Failed so the UI can say "this took too long"
/// rather than implying the check itself found a problem.
/// </summary>
public enum DiagnosticStepStatus
{
    Pending,
    Running,
    Passed,
    Warning,
    Failed,
    Skipped,
    TimedOut,
}

/// <summary>
/// What one <see cref="DiagnosticStep"/>'s check produced - a status plus a plain-language
/// summary and optional supporting evidence lines (raw values, event timestamps, file paths, ...)
/// shown under the step in the UI. Every check in <c>TroubleshootService</c> returns one of these
/// rather than throwing, even on failure - "the check itself couldn't run" (e.g. a denied
/// registry key or an unavailable WMI class) is reported as a Warning/Skipped result with an
/// explanatory summary, never a raw exception surfaced to the user.
/// </summary>
public sealed record DiagnosticStepResult(DiagnosticStepStatus Status, string Summary, IReadOnlyList<string>? Evidence = null)
{
    public static DiagnosticStepResult Pass(string summary, IReadOnlyList<string>? evidence = null) => new(DiagnosticStepStatus.Passed, summary, evidence);
    public static DiagnosticStepResult Warn(string summary, IReadOnlyList<string>? evidence = null) => new(DiagnosticStepStatus.Warning, summary, evidence);
    public static DiagnosticStepResult Fail(string summary, IReadOnlyList<string>? evidence = null) => new(DiagnosticStepStatus.Failed, summary, evidence);
    public static DiagnosticStepResult Skip(string summary, IReadOnlyList<string>? evidence = null) => new(DiagnosticStepStatus.Skipped, summary, evidence);
}

/// <summary>
/// #901: one ordered check within a <see cref="TroubleshootRun"/>. <see cref="Check"/> is the
/// actual work (usually a thin wrapper around one or more <c>TroubleshootService</c> methods,
/// reusing existing services per CLAUDE.md's "prefer a known tool/existing service" convention);
/// <see cref="TroubleshootViewModel"/>'s runner wraps every call in a race against
/// <see cref="Timeout"/> so one hung WMI/tool call can never freeze the rest of the run - see its
/// RunAsync for the Task.WhenAny/CancellationTokenSource plumbing. This class is itself an
/// ObservableObject (not just a DTO) so the step list can bind directly to live Status/ResultText
/// updates as the run progresses, the same "mutate in place" shape ProcessRow uses for per-tick
/// updates.
/// </summary>
public sealed class DiagnosticStep : ObservableObject
{
    public required string Id { get; init; }
    public required string Label { get; init; }

    /// <summary>Short one-line description shown under the label before the step has run -
    /// what this check actually looks at, so the step list reads as a real investigation rather
    /// than an opaque progress bar.</summary>
    public string Description { get; init; } = string.Empty;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>#905: true for a step in a layered "stop at the first failing layer" branch (e.g.
    /// "No internet") - the runner marks every remaining Pending step Skipped and ends the run
    /// early the first time a StopOnFailure step comes back Failed, rather than working through
    /// layers that are meaningless once an earlier one has already failed.</summary>
    public bool StopOnFailure { get; init; }

    public required Func<CancellationToken, Task<DiagnosticStepResult>> Check { get; init; }

    private DiagnosticStepStatus _status = DiagnosticStepStatus.Pending;
    public DiagnosticStepStatus Status { get => _status; set => SetProperty(ref _status, value); }

    private string _resultText = string.Empty;
    public string ResultText { get => _resultText; set => SetProperty(ref _resultText, value); }

    private IReadOnlyList<string> _evidence = Array.Empty<string>();
    public IReadOnlyList<string> Evidence
    {
        get => _evidence;
        set { if (SetProperty(ref _evidence, value)) OnPropertyChanged(nameof(HasEvidence)); }
    }

    public bool HasEvidence => Evidence.Count > 0;
}
