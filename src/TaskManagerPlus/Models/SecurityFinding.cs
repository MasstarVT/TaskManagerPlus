namespace TaskManagerPlus.Models;

public enum FindingSeverity
{
    Info,
    Low,
    Medium,
    High,
}

/// <summary>
/// One structured output of a security heuristic (#804) - severity, a one-sentence plain-English
/// reason, the exact registry key/file path it's about, and what disabling/removing it would
/// actually do. Shown in the Security tab's right-hand detail pane.
///
/// "Quick flag, not a verdict": every heuristic that produces one of these is a pattern-match on
/// otherwise-ambiguous data (an unusual Winlogon value, a non-empty AppInit_DLLs list, an unsigned
/// autorun, ...), never a confirmed detection of anything malicious - SecurityView.xaml's header
/// states this plainly, matching the same framing this app already uses for the outdated-driver/
/// CPU-throttle/AV-mitigation heuristics elsewhere.
/// </summary>
public sealed class SecurityFinding
{
    public FindingSeverity Severity { get; init; } = FindingSeverity.Info;
    public string Title { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string WhatDisablingDoes { get; init; } = string.Empty;

    /// <summary>Optional back-reference to the AutorunEntry this finding is about, so selecting a
    /// finding could highlight its row in the Persistence DataGrid - null for findings not tied to
    /// one specific entry.</summary>
    public AutorunEntry? RelatedEntry { get; init; }
}
