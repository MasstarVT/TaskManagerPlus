using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>#749/#750/#751: one Service Control Manager failure/crash event (7000 failed to start,
/// 7001 depends on a service that failed, 7009 connection timeout, 7011 transaction-response
/// timeout, 7022 hung on starting, 7023/7024 terminated with error / service-specific error,
/// 7031/7034 terminated unexpectedly, 7043 did not shut down properly), attributed to a service by
/// parsing its own formatted message text - none of these templates put the service name at a
/// stable Properties[] index across the whole set (7009/7011 put a timeout value first instead),
/// so this is regex-extracted from the rendered message, the same "the display name is embedded in
/// the rendered message, not a documented indexed property" approach
/// EventLogService.ExtractFaultingModule already takes for Application-log crashes. See
/// EventLogService.ReadServiceFailureEvents (one shared scan backing #749/#750/#751) and
/// ServicesViewModel.ApplyFailureHistory (matches these back onto a ServiceRow by display name).</summary>
public sealed class ServiceScmEvent
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string ServiceDisplayName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    /// <summary>SCM's own "It has done this N time(s)" restart count (#750) - only ever populated
    /// for 7031/7034, null for every other event ID in this set.</summary>
    public int? RestartCount { get; init; }

    /// <summary>#750: the two "terminated unexpectedly" event IDs the crash-loop count is built
    /// from.</summary>
    public bool IsCrashEvent => EventId is 7031 or 7034;

    /// <summary>#750 explicitly asks for "SCM's own restart count from the event", not just this
    /// app's own derived 24h window count - so a crash event's label surfaces RestartCount directly
    /// wherever it's shown (the failure-history list), rather than leaving it only readable inside
    /// the raw message text.</summary>
    public string EventLabel => EventId switch
    {
        7000 => "Failed to start",
        7001 => "Blocked by a failed dependency",
        7009 => "Timed out waiting to connect",
        7011 => "Timed out waiting for a transaction response",
        7022 => "Hung on starting",
        7023 => "Terminated with an error",
        7024 => "Terminated with a service-specific error",
        7031 or 7034 => RestartCount is { } n ? $"Terminated unexpectedly (SCM restart count: {n})" : "Terminated unexpectedly",
        7043 => "Did not shut down properly",
        _ => $"Event {EventId}",
    };
}

/// <summary>#752/#753/#754: one service's static-registry-read quick flags, computed together in a
/// single pass over HKLM\SYSTEM\CurrentControlSet\Services (see
/// ServiceControlService.RunInventoryAudit) rather than three separate scans - a broken
/// DependOnService reference (#752), an ImagePath resolving to a binary that no longer exists
/// (#753), and an unquoted ImagePath containing a space before its .exe boundary (#754). "Quick
/// flag, not a verdict" (CLAUDE.md): each is a pattern match on registry data, not a confirmed
/// misconfiguration. Only produced for a service where at least one flag is actually set - see
/// RunInventoryAudit's remarks.</summary>
public sealed class ServiceInventoryFlags
{
    public string ServiceName { get; init; } = string.Empty;

    public bool HasBrokenDependency { get; init; }
    public string BrokenDependencyText { get; init; } = string.Empty;

    public bool IsOrphaned { get; init; }
    public string OrphanedImagePath { get; init; } = string.Empty;

    public bool HasUnquotedPath { get; init; }
    public string UnquotedPathOriginal { get; init; } = string.Empty;
    public string UnquotedPathCorrected { get; init; } = string.Empty;
}

/// <summary>#757: one service's recovery-action audit outlier(s), found by parsing `sc qfailure`'s
/// text output (ServiceControlService.ReadFailureActionsTextAsync, the same read the per-row
/// "Recovery actions" button already does - the bulk audit just runs it across every service and
/// keeps only the ones worth a second look) - see ServiceControlService.RunRecoveryActionAuditAsync.
/// Only produced for a service where at least one flag is set, the same "flagged entries only"
/// shape RunInventoryAudit already uses for #752/#753/#754. NoRecoveryConfigured is only ever set
/// for an Automatic-start service - a Manual/Disabled/trigger-start service having no recovery plan
/// is normal, not an outlier (see RunRecoveryActionAuditAsync's remarks).</summary>
public sealed class ServiceRecoveryAuditEntry
{
    public string ServiceName { get; init; } = string.Empty;

    public bool NoRecoveryConfigured { get; init; }
    public bool RebootsOnFailure { get; init; }
    public bool RunsMissingProgram { get; init; }
    public string MissingProgramPath { get; init; } = string.Empty;

    public string SummaryText => string.Join("; ", new[]
    {
        NoRecoveryConfigured ? "No recovery action configured" : null,
        RebootsOnFailure ? "Reboots the computer on failure" : null,
        RunsMissingProgram ? $"Runs a missing program: {MissingProgramPath}" : null,
    }.Where(s => s is not null));
}

/// <summary>#763: `sc queryex`-derived diagnosis for a service currently stuck in
/// START_PENDING/STOP_PENDING - see ServiceControlService.DiagnoseHangAsync, which takes two
/// CHECKPOINT samples a few seconds apart to tell "still making progress" from "stuck" rather than
/// guessing off a single snapshot. PendingDuration is only ever populated from
/// ServicesViewModel's own live-observed "since when has this row been pending" tracking (never
/// fabricated - a service already pending before this app started watching it shows an honest
/// "unknown duration" instead of a guessed one).</summary>
public sealed class HungServiceDiagnosis
{
    public string ServiceName { get; init; } = string.Empty;
    public bool IsPending { get; init; }
    public string StateText { get; init; } = string.Empty;
    public bool CheckpointAdvancing { get; init; }
    public uint Checkpoint { get; init; }
    public uint WaitHintMs { get; init; }
    public int ServicesPipeTimeoutMs { get; init; } = 30000;
    public int HostProcessId { get; init; }
    public TimeSpan? PendingDuration { get; init; }
    public string? Error { get; init; }

    public string SummaryText
    {
        get
        {
            if (Error is not null) return $"Couldn't diagnose {ServiceName}: {Error}";
            if (!IsPending) return $"{ServiceName} is not currently pending (state: {StateText}) - nothing to diagnose.";

            string durationText = PendingDuration is { } d
                ? Formatting.FormatSpanMinutes(d)
                : "an unknown duration (this app only just started watching it)";
            string advancingText = CheckpointAdvancing
                ? "the checkpoint is advancing - it looks like it's still making progress"
                : "the checkpoint is NOT advancing";
            return $"Stuck in {StateText} for {durationText} - {advancingText} " +
                   $"(checkpoint {Checkpoint}, wait hint {WaitHintMs}ms, ServicesPipeTimeout {ServicesPipeTimeoutMs / 1000}s).";
        }
    }
}

/// <summary>#759: one System-log Service Control Manager event 7040 ("the start type of the X
/// service was changed from Y to Z") - a structured, indexed-property event (unlike the #749
/// failure-event family, which needs regex extraction from the rendered message - see
/// ServiceScmEvent's remarks), so this reads Properties[0..2] directly. 7040 does not record which
/// account made the change (that needs Security-log object-access auditing, a separate, far more
/// invasive audit policy this app doesn't turn on) - Account is deliberately absent here rather
/// than fabricated. See EventLogService.ReadStartTypeChangeEvents.</summary>
public sealed class ServiceStartTypeChangeEvent
{
    public DateTime TimeCreated { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string OldStartType { get; init; } = string.Empty;
    public string NewStartType { get; init; } = string.Empty;
}

/// <summary>#760: one System-log Service Control Manager event 7045 ("a service was installed in
/// the system") - also a structured, indexed-property event. SignatureStatus is correlated on
/// afterward from SignatureCheckService against the resolved image path (StartupManagerService's
/// own quoted/unquoted command-line parsing, reused rather than duplicated). See
/// EventLogService.ReadNewServiceInstallEvents.</summary>
public sealed class NewServiceInstallEvent
{
    public DateTime TimeCreated { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public string ImagePath { get; init; } = string.Empty;
    public string ServiceType { get; init; } = string.Empty;
    public string StartType { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;

    public string SignatureStatus { get; set; } = "Unknown";
}
