namespace TaskManagerPlus.Models;

/// <summary>
/// Round 17, item 58: one Service Control Manager crash/failure event - the service-side
/// equivalent of an application crash. Covers five distinct SCM event IDs, each with its own
/// insertion-string shape (see EventLogService.ReadServiceFailureEvents/ParseServiceFailureEvent):
/// 7031/7034 ("the X service terminated unexpectedly, this is the Nth time" plus, for 7031, the
/// corrective action SCM is about to take), 7024 (service-specific exit code), 7000/7009 (failed/
/// timed out to start). Not every field applies to every EventId - see EventKindText for the
/// plain-English label and RecoveryAction/ExitCode's own remarks for which event ids populate them.
/// </summary>
public sealed class ServiceFailureEvent
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string? ServiceName { get; init; }

    /// <summary>7031/7034 only: "this is the Nth time" - SCM's own running count of how many
    /// times this service has terminated unexpectedly since it was last started (not a count
    /// this app computes itself).</summary>
    public int? RestartCount { get; init; }

    /// <summary>7031 only: the corrective action SCM is about to take (e.g. "Restart the
    /// service"), read from the event's own insertion strings. Null for 7034 (no action, no
    /// property to read one from) and every other EventId.</summary>
    public string? RecoveryAction { get; init; }

    /// <summary>7024 only: the service-specific exit code the service itself reported.</summary>
    public string? ExitCode { get; init; }

    public string Message { get; init; } = string.Empty;

    public string EventKindText => EventId switch
    {
        7034 => "Terminated unexpectedly",
        7031 => "Terminated unexpectedly (recovery action taken)",
        7024 => "Terminated with a service-specific error",
        7000 => "Failed to start",
        7009 => "Timed out connecting to the Service Control Manager",
        _ => "Service failure",
    };
}

/// <summary>
/// Round 17, item 59: a chronic restart loop for one service, detected by finding the densest
/// 60-minute sliding window of 7031/7034 ("terminated unexpectedly") events for that service name
/// across the whole lookback window - see StabilityViewModel.DetectServiceRestartLoops. A service
/// that keeps crashing and being auto-restarted by SCM never surfaces as a user-visible crash
/// dialog, so this is the only way this kind of chronic fault becomes visible at all. "Quick flag,
/// not a verdict" per CLAUDE.md: a dense window of terminations is suspicious, not a confirmed
/// diagnosis of what's wrong with the service.
/// </summary>
public sealed class ServiceRestartLoopWarning
{
    public string ServiceName { get; init; } = string.Empty;
    public int OccurrencesInWindow { get; init; }
    public DateTime WindowStart { get; init; }
    public DateTime WindowEnd { get; init; }
    public DateTime LastSeen { get; init; }
}
