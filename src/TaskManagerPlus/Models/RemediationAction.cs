namespace TaskManagerPlus.Models;

/// <summary>
/// #967-971: one fix action in <see cref="Services.RemediationActionCatalog"/> - a real,
/// system-mutating operation this app can already perform (either shelled out to a known Windows
/// tool, or one of this app's own existing Services/* mutation methods), packaged with enough
/// metadata for the review dialog (#968-971) to show it honestly before running it. Unlike
/// <see cref="Rule"/> (loaded from JSON, no executable code attached), a RemediationAction is
/// always built in C# by RemediationActionCatalog - <see cref="Execute"/>/<see cref="ExecutePreview"/>
/// are plain delegates, not serialized, so the catalog is free to close over whatever runtime
/// context (a specific service name, pid, drive letter, StartupItem) a parameterized action needs.
/// </summary>
public enum RemediationRiskLevel
{
    Low,
    Medium,
    High,
}

public sealed class RemediationAction
{
    /// <summary>Stable id - what <see cref="Rule.ActionIds"/> references and what
    /// RemediationActionCatalog.Resolve keys a parameterized factory off of.</summary>
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string PlainEnglishDescription { get; init; } = string.Empty;

    /// <summary>#968: the exact, resolved command line (or a plain-text equivalent for an action
    /// that runs through a .NET API rather than a shelled-out tool, e.g. "sc stop/start" for
    /// ServiceControlService.Restart) - shown as selectable/copyable read-only text in the review
    /// dialog before Confirm/Run is ever reachable. Always a fully-resolved string (drive letter/
    /// service name/pid already substituted in) - never a template with placeholders left in.</summary>
    public string Command { get; init; } = string.Empty;

    public RemediationRiskLevel RiskLevel { get; init; } = RemediationRiskLevel.Low;

    public bool RequiresReboot { get; init; }

    public bool IsUndoable { get; init; }

    /// <summary>Shown next to a non-undoable action so "not reversible" reads as an honest
    /// statement, not just a missing button - e.g. "One-shot diagnostic/repair tool run - there's
    /// nothing to reverse." Null when IsUndoable is true.</summary>
    public string? NotUndoableReason { get; init; }

    /// <summary>#969: a safe, read-only (or otherwise non-mutating) equivalent command - null when
    /// no meaningful dry run exists for this action, in which case the review dialog shows "no dry
    /// run available for this action" rather than hiding the Preview button outright.</summary>
    public string? PreviewCommand { get; init; }

    /// <summary>#971: the full, hive-qualified registry key path (e.g.
    /// "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run") this action's
    /// <see cref="Execute"/> writes to directly - null for an action that doesn't touch the registry
    /// itself (a shelled-out tool's own internal registry writes aren't something this app can
    /// reliably back up, since it doesn't know that tool's exact key layout). When set, the review
    /// flow exports this key via `reg export` before running Execute.</summary>
    public string? RegistryKeyToBackup { get; init; }

    /// <summary>Runs the real operation. Not required to be a single shelled-out process - a
    /// service-restart/startup-toggle/priority-change action wraps the existing
    /// Services/*.cs mutation method directly, per this app's "reuse the real thing" convention.</summary>
    public Func<CancellationToken, Task<RemediationRunResult>>? Execute { get; init; }

    /// <summary>#969: runs <see cref="PreviewCommand"/> (or an equivalent safe read) and returns
    /// its output - null exactly when PreviewCommand is null.</summary>
    public Func<CancellationToken, Task<RemediationRunResult>>? ExecutePreview { get; init; }

    // ----- #972 journaling metadata ---------------------------------------------------------
    // A generic (id, structured-fields) shape rather than a big Id-keyed switch in the review
    // ViewModel, so a future catalog addition just fills these in rather than needing a matching
    // case added somewhere else too. Only the fields relevant to JournalKind are ever set by a
    // given factory - the rest stay null, mirroring ChangeJournalEntry's own "only the fields this
    // Kind needs" shape.

    public ChangeKind JournalKind { get; init; } = ChangeKind.OneShotToolRun;

    public string? ServiceName { get; init; }
    public int? Pid { get; init; }
    public string? ProcessName { get; init; }
    public string? StartupItemName { get; init; }
    public string? StartupItemCommand { get; init; }
    public string? StartupItemSource { get; init; }
}

/// <summary>Outcome of running (or previewing) a RemediationAction. BeforeValue/AfterValue are
/// optional - populated only by an action whose Execute captured a real before/after reading (e.g.
/// the min-processor-state change), so <see cref="Models.ChangeJournalEntry"/> can record an honest
/// value pair rather than a fabricated one (#972).</summary>
public sealed class RemediationRunResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = string.Empty;
    public string? BeforeValue { get; init; }
    public string? AfterValue { get; init; }

    public static RemediationRunResult Ok(string output, string? before = null, string? after = null) =>
        new() { Success = true, Output = output, BeforeValue = before, AfterValue = after };

    public static RemediationRunResult Fail(string output) => new() { Success = false, Output = output };
}
