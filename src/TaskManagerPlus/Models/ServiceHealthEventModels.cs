namespace TaskManagerPlus.Models;

/// <summary>
/// #192-194: models backing ServiceHealthEventService - service crash-loop detection (SCM 7000/
/// 7009/7024/7031/7034), the ServicesPipeTimeout start-timeout explanation, and the DistributedCOM
/// CLSID/APPID resolver. See ServiceHealthEventService's remarks for the exact event families each
/// one reads.
/// </summary>

/// <summary>#192: one service's SCM 7000/7009/7024/7031/7034 tally over the lookback window -
/// "crashed and restarted repeatedly" is IsCrashLooping below, a quick flag over these raw counts,
/// never a verdict. RecoveryActionsText is filled in lazily (on demand, one `sc qfailure` shell-out
/// per flagged service - see ServiceControlService.ReadFailureActionsTextAsync, reused as-is rather
/// than re-derived) only for services the scan actually flags, not every service on the system.</summary>
public sealed class ServiceCrashLoopInfo
{
    public string ServiceName { get; init; } = string.Empty;

    /// <summary>SCM 7031/7034 - "service terminated unexpectedly" (7031 also carries a restart
    /// count in its own insertion strings, best-effort parsed into LastRestartCount below).</summary>
    public int TerminatedCount { get; init; }

    /// <summary>SCM 7000 - "failed to start".</summary>
    public int FailedToStartCount { get; init; }

    /// <summary>SCM 7009 - "did not respond in a timely fashion" (the start-timeout case #193
    /// extends EventLogService.ReadServiceStartDurations with).</summary>
    public int TimeoutCount { get; init; }

    /// <summary>SCM 7024 - "terminated with service-specific error %%N".</summary>
    public int ServiceSpecificExitCodeCount { get; init; }

    /// <summary>Best-effort restart count parsed from the most recent 7031 event's own insertion
    /// strings - null when none of the terminated events parsed cleanly (never guessed).</summary>
    public int? LastRestartCount { get; init; }

    /// <summary>Best-effort service-specific exit code from the most recent 7024 event.</summary>
    public string? LastServiceSpecificExitCode { get; init; }

    public DateTime LastEventTime { get; init; }
    public int TotalCount => TerminatedCount + FailedToStartCount + TimeoutCount + ServiceSpecificExitCodeCount;

    /// <summary>#192: "crashed and restarted repeatedly" - flagged when termination/timeout events
    /// recurred at least 3 times in the 30-day lookback. A quick flag, not a verdict: a service that
    /// legitimately restarts often (some watchdog-style services do) will also trip this.</summary>
    public bool IsCrashLooping => TerminatedCount + TimeoutCount >= 3;

    /// <summary>`sc qfailure &lt;name&gt;` output - loaded on demand (see ServicesViewModel), empty
    /// until requested, same "expensive, so make it explicit" tradeoff as ServiceRow.
    /// FailureActionsText. Mutable so the scan result can be enriched in place after the initial
    /// SCM-event tally.</summary>
    public string RecoveryActionsText { get; set; } = string.Empty;
}

/// <summary>#193: the current effective start-timeout Windows applies before logging an SCM 7009 -
/// HKLM\SYSTEM\CurrentControlSet\Control\ServicesPipeTimeout, or the undocumented-but-well-known
/// 30000ms default when that value doesn't exist (most machines never set it).</summary>
public sealed class ServiceStartTimeoutInfo
{
    public int EffectiveTimeoutMs { get; init; } = 30000;
    public bool IsCustomized { get; init; }
}

/// <summary>#194: one CLSID/APPID GUID pulled out of a DistributedCOM event's own message text,
/// resolved to a friendly component name via the matching HKCR\CLSID\{...} or HKCR\AppID\{...}
/// key's (Default) value. FriendlyName is null when the GUID doesn't resolve (uninstalled
/// component, or a remote/never-registered CLSID) - never a guessed name.</summary>
public sealed class DcomComponentResolution
{
    public string Guid { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty; // "CLSID" or "APPID"
    public string? FriendlyName { get; init; }
}
