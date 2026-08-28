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
