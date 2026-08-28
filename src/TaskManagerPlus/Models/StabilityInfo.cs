namespace TaskManagerPlus.Models;

/// <summary>One Critical/Error entry pulled from the System or Application event log (#1/#8).</summary>
public sealed class StabilityEvent
{
    public DateTime TimeCreated { get; init; }
    public string LogName { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
    public int EventId { get; init; }
    public string Level { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    /// <summary>Best-effort "Faulting module name: X" extraction from an Application-log crash
    /// entry's own formatted message (#8) - null when the message doesn't match that shape (not
    /// every Error/Critical entry is an app crash).</summary>
    public string? FaultingModule { get; init; }

    /// <summary>Bugcheck code, only ever populated for a Kernel-Power event 41 - see
    /// EventLogService.ExtractBugcheckCode for why this is best-effort (the insertion-string
    /// layout isn't a documented, versioned contract).</summary>
    public string? BugcheckCode { get; init; }
}

/// <summary>One file under %SystemRoot%\Minidump (#3) - bugcheck code is filled in only when a
/// Kernel-Power event 41 was recorded within a few minutes of the dump's timestamp.</summary>
public sealed class MinidumpInfo
{
    public string FileName { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string? BugcheckCode { get; init; }
}

/// <summary>One day's worth of Critical/Error event counts (#1 - Reliability History) - bucketed
/// from the same 30-day event query everything else on this tab already runs, no second query.</summary>
public sealed class DailyEventCount
{
    public DateTime Date { get; init; }
    public int Count { get; init; }
}

/// <summary>Point-in-time result of querying the System/Application event logs for stability
/// diagnostics. Queried on demand (event log reads aren't cheap), not on a live timer - see
/// StabilityViewModel.</summary>
public sealed class StabilitySnapshot
{
    public List<StabilityEvent> RecentEvents { get; init; } = new();

    /// <summary>True when the shutdown immediately preceding this boot looks unexpected (#4) -
    /// a Kernel-Power 41 or EventLog 6008 entry timestamped within a few minutes of the
    /// system's last boot time.</summary>
    public bool WasLastShutdownUnexpected { get; init; }
    public DateTime? LastUnexpectedShutdown { get; init; }

    /// <summary>GPU driver timeout/reset (TDR, event 4101) count and most recent occurrence (#5)
    /// within the lookback window.</summary>
    public int TdrEventCount { get; init; }
    public DateTime? LastTdrEvent { get; init; }

    /// <summary>Timestamp of the most recent crash-like event (unexpected shutdown or a Windows
    /// Error Reporting BlueScreen entry) found within the lookback window (#6) - null means none
    /// found in that window, not "never crashed".</summary>
    public DateTime? LastCrashTime { get; init; }

    public List<MinidumpInfo> Minidumps { get; init; } = new();

    /// <summary>Daily Critical/Error counts across the lookback window (#1), oldest first - feeds
    /// the Stability tab's Reliability History chart. Bucketed from the same capped event list
    /// already read above (best-effort: a day busier than the per-log cap won't have every one of
    /// its events counted, the same tradeoff RecentEvents itself already makes).</summary>
    public List<DailyEventCount> DailyCounts { get; init; } = new();

    /// <summary>Round 8 #40: count and most recent occurrence of a low-memory resource-exhaustion
    /// event (Microsoft-Windows-Resource-Exhaustion-Detector, typically event ID 2004/2005) within
    /// the lookback window - see EventLogService.ReadLowMemoryEvents. These are logged at Warning
    /// level, not Critical/Error, so this is a second, separate targeted query rather than a
    /// bucket of RecentEvents above.</summary>
    public int LowMemoryEventCount { get; init; }
    public DateTime? LastLowMemoryEvent { get; init; }

    /// <summary>#427: the classic pool-starvation event signature (Srv 2019/2020, event 333, and
    /// Resource-Exhaustion-Detector entries) found within the lookback window, most recent first -
    /// see EventLogService.ReadPoolExhaustionEvents.</summary>
    public List<PoolExhaustionEvent> PoolExhaustionEvents { get; init; } = new();

    /// <summary>#439: Resource-Exhaustion-Detector event 2004 specifically (the "Windows
    /// successfully diagnosed a low virtual memory condition" entry, which also records the
    /// ranked top commit consumers at that moment) - a separate, more specific query from
    /// ReadLowMemoryEvents (which counts every event ID from this provider) and
    /// ReadPoolExhaustionEvents (which folds this provider's events into the pool-exhaustion list
    /// without parsing the consumer list) - see EventLogService.ReadOutOfMemoryIncidents.</summary>
    public List<OutOfMemoryIncident> OutOfMemoryIncidents { get; init; } = new();
}

/// <summary>#439: one process from event 2004's ranked "consumed the most virtual memory" list.</summary>
public sealed class OomTopConsumer
{
    public string ProcessName { get; init; } = string.Empty;
    public int Pid { get; init; }
    public long Bytes { get; init; }
}

/// <summary>#439: one Resource-Exhaustion-Detector event 2004 entry - Windows' own record of which
/// processes were consuming the most committed memory at the moment it detected a low-virtual-
/// memory condition. TopConsumers is parsed out of the event's own formatted message text via a
/// best-effort regex (the message format isn't a documented, versioned contract, mirroring
/// EventLogService.ExtractBugcheckCode's same caveat for a different event) - when parsing finds
/// nothing, RawMessage is shown instead so nothing is fabricated, just less structured.</summary>
public sealed class OutOfMemoryIncident
{
    public DateTime TimeCreated { get; init; }
    public List<OomTopConsumer> TopConsumers { get; init; } = new();
    public string RawMessage { get; init; } = string.Empty;
}

/// <summary>#66 (Round 10): repeated application crashes grouped by faulting module, with a count -
/// the same StabilityEvent.FaultingModule extraction the flat "Recent critical / error events" grid
/// already carries, just aggregated here so "outlook.exe keeps crashing on ntdll.dll" reads as one
/// row with a count instead of forcing a scroll through a dozen near-identical entries. A pure
/// derived read over the already-loaded event list - no new event-log query.</summary>
public sealed class FaultingModuleSummary
{
    public string Module { get; init; } = string.Empty;
    public int Count { get; init; }
    public DateTime LastSeen { get; init; }
}

/// <summary>Round 7 #13: an approximate measured service start duration, mined from Service
/// Control Manager 7036 event-log entries - see EventLogService.ReadServiceStartDurations for
/// exactly how this is derived and its limitations (an approximation of "time between a stop and
/// the following running state," not a true measured start latency).</summary>
public sealed class ServiceStartDuration
{
    public string ServiceName { get; init; } = string.Empty;
    public double LastStartDurationMs { get; init; }
    public double AvgStartDurationMs { get; init; }
    public int SampleCount { get; init; }
}

/// <summary>#427: one entry from the System log matching the classic pool-starvation signature -
/// `Srv` event 2019 (nonpaged pool exhausted) / 2020 (paged pool exhausted), event 333 (registry
/// couldn't flush changes to disk, a common secondary symptom of pool/disk exhaustion), or a
/// Microsoft-Windows-Resource-Exhaustion-Detector entry - see EventLogService.ReadPoolExhaustionEvents.
/// Explanation is a fixed, plain-English sentence keyed off EventId/ProviderName, not anything
/// parsed out of the event's own message text.</summary>
public sealed class PoolExhaustionEvent
{
    public DateTime TimeCreated { get; init; }
    public string ProviderName { get; init; } = string.Empty;
    public int EventId { get; init; }
    public string Explanation { get; init; } = string.Empty;
}
