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

/// <summary>#974: what an action needs to be true before it's safe/meaningful to run - checked by
/// RemediationPreconditionService against live ViewModel state, never fabricated when that state
/// can't be read (see PreconditionCheckResult's own remarks on degrading to "couldn't check").</summary>
public enum PreconditionKind
{
    /// <summary>This app already runs fully elevated (app.manifest -> requireAdministrator), so
    /// this is a defensive check, not something any current action actually needs to declare.</summary>
    RequiresElevation,

    /// <summary>Parameter is a service name (ServiceRow.ServiceName) - fails when that service is
    /// no longer registered on this system (e.g. it was uninstalled between the finding firing and
    /// the review dialog being opened).</summary>
    RequiresServicePresent,

    /// <summary>Parameter is a drive letter ("C:") - fails when SystemSpecsViewModel.Volumes
    /// reports a file system other than NTFS for that volume.</summary>
    RequiresNtfsVolume,

    /// <summary>No parameter. Advisory rather than blocking (see
    /// RemediationPrecondition.Blocking's remarks) - System Protection being off just means the
    /// restore-point prompt's "Create restore point" attempt is expected to fail, not that running
    /// the action itself is unsafe (the existing "Skip - run without one" flow already covers
    /// that).</summary>
    RequiresSystemProtectionOn,

    /// <summary>No parameter. Fails while SystemSpecsViewModel.RebootPending is true - a handful of
    /// servicing operations (DISM's component-store repair, most notably) are documented to be
    /// unreliable while a prior update's reboot is still outstanding.</summary>
    RequiresNoRebootPending,
}

/// <summary>One requirement an action declares - Kind plus whatever context that kind needs
/// (a service name, a drive letter, or nothing). <see cref="Blocking"/> defaults to true (Run/
/// Preview disabled on failure); RequiresSystemProtectionOn is the one built-in exception, wired
/// as advisory-only in RemediationActionCatalog since the review dialog's own restore-point flow
/// already lets a Medium/High-risk action proceed without one.</summary>
public sealed class RemediationPrecondition
{
    public PreconditionKind Kind { get; init; }
    public string? Parameter { get; init; }
    public bool Blocking { get; init; } = true;
}

/// <summary>Result of evaluating one RemediationPrecondition against live system state -
/// <see cref="Passed"/> null means the check itself couldn't run (degrade to "unknown", never a
/// fabricated pass/fail), distinct from a check that ran and returned false.</summary>
public sealed class PreconditionCheckResult
{
    public required RemediationPrecondition Precondition { get; init; }
    public bool? Passed { get; init; }
    public string? Reason { get; init; }

    /// <summary>True when this result should keep Run/Preview disabled - a failed Blocking check,
    /// or a check whose Passed is still unknown (never treat "couldn't check" as "passed").</summary>
    public bool IsBlocked => Precondition.Blocking && Passed != true;
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

    // ----- #974 preconditions ---------------------------------------------------------------

    /// <summary>Empty for most actions - only the ones with a real, checkable requirement declare
    /// one (see RemediationActionCatalog's individual factories).</summary>
    public List<RemediationPrecondition> Preconditions { get; init; } = new();

    // ----- #977 live output streaming (sfc/DISM/chkdsk only) --------------------------------

    /// <summary>Set only for the catalog's long-running shelled-out tools (sfc/DISM/chkdsk) -
    /// identical contract to <see cref="Execute"/> except it also reports each stdout/stderr line
    /// as it arrives (second delegate parameter) instead of only the final joined Output. Null for
    /// every other action, which the review dialog falls back to <see cref="Execute"/> for.</summary>
    public Func<CancellationToken, Action<string>, Task<RemediationRunResult>>? ExecuteStreaming { get; init; }

    /// <summary>Streaming twin of <see cref="ExecutePreview"/> - null exactly when ExecutePreview
    /// itself is null (nothing to preview) or the preview is cheap enough not to need it (sfc
    /// /verifyonly and DISM /ScanHealth both stream too, since they run just as long as their
    /// mutating counterparts).</summary>
    public Func<CancellationToken, Action<string>, Task<RemediationRunResult>>? ExecutePreviewStreaming { get; init; }

    /// <summary>#977: parses a tool-specific progress line into a 0-100 percent, or null when the
    /// line carries no progress info - DISM's "[ XX.X% ]" / chkdsk's "XX percent complete". Left
    /// null for sfc, whose output isn't cleanly percentage-parseable (an honest indeterminate
    /// progress state, not a guessed number).</summary>
    public Func<string, double?>? ParseProgressPercent { get; init; }

    // ----- #979 deferred/scheduled queue -----------------------------------------------------

    /// <summary>True only for an action that genuinely needs the volume offline to do its real
    /// work (currently just the chkdsk "fix" variant, RemediationActionCatalog.ChkdskFix) - the
    /// review dialog's "Queue for next boot" option is offered only when this is set, since an
    /// action that already runs fine online (the /scan variant, sfc, DISM, ...) has no honest
    /// reason to defer instead of just running now.</summary>
    public bool SupportsDeferredQueue { get; init; }
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

    /// <summary>#977: true only when the user explicitly clicked Cancel mid-run - distinct from a
    /// plain failure so the review dialog (and the journal entry it writes) can say "cancelled"
    /// rather than implying the tool itself reported an error.</summary>
    public bool Cancelled { get; init; }

    public static RemediationRunResult Ok(string output, string? before = null, string? after = null) =>
        new() { Success = true, Output = output, BeforeValue = before, AfterValue = after };

    public static RemediationRunResult Fail(string output) => new() { Success = false, Output = output };

    public static RemediationRunResult Cancel(string output) => new() { Success = false, Cancelled = true, Output = output };
}
