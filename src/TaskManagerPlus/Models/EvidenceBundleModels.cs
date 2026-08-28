namespace TaskManagerPlus.Models;

/// <summary>
/// #200: models backing EvidenceBundleService - a filtered, timestamped export folder combining
/// most of this domain's other services' output into one shareable package. See
/// EvidenceBundleService's remarks for exactly which existing service backs each piece.
/// </summary>

/// <summary>#200: what to export - a time window plus which channels to pull filtered .evtx exports
/// from. The caller (EventsViewModel/EvidenceBundleViewModel) is responsible for choosing a
/// sensible default channel set (System, Application, plus whatever the last anomaly/timeline scan
/// flagged) - this type is just the resolved request.</summary>
public sealed class EvidenceBundleRequest
{
    public List<string> Channels { get; init; } = new();
    public DateTime StartUtc { get; init; }
    public DateTime EndUtc { get; init; }
}

/// <summary>#200: one step's outcome within a bundle export - each of the several sub-tasks
/// (per-channel .evtx export, WER report copy, minidump copy, systeminfo/msinfo32/dxdiag capture,
/// SUMMARY.md generation) is wrapped independently so one slow/failing tool degrades to "skipped"
/// rather than aborting the whole bundle, per this app's "degrade, never fabricate" rule.</summary>
public sealed class EvidenceBundleStepResult
{
    public string StepName { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string Detail { get; init; } = string.Empty;
}

/// <summary>#200: the finished (or partially-finished) bundle - the folder it landed in plus a
/// per-step outcome log, so the UI can show exactly what was and wasn't captured rather than a bare
/// "done".</summary>
public sealed class EvidenceBundleResult
{
    public string FolderPath { get; init; } = string.Empty;
    public List<EvidenceBundleStepResult> Steps { get; init; } = new();
    public bool AnyStepFailed => Steps.Any(s => !s.Succeeded);
}
