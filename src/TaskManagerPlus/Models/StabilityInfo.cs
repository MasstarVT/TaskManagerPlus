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

/// <summary>#713: one entry on the "Power & boot timeline" strip - correlates System-log events
/// 6005 (Event Log service started, i.e. a boot happened), 6006 (clean shutdown), 6008 (previous
/// shutdown was unexpected), 6013 (periodic uptime report), Kernel-Power 41 (no clean shutdown
/// recorded before this boot - the same event EventLogService.WasLastShutdownUnexpected already
/// reads for a different purpose), Kernel-Power 109 (a shutdown's reason code), and User32 1074
/// (who/what initiated a restart or shutdown, and why) into one chronological strip. See
/// PowerTimelineService.Read.</summary>
public sealed class PowerTimelineEntry
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string ProviderName { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;

    public string KindLabel => Kind switch
    {
        "Boot" => "Boot",
        "CleanShutdown" => "Clean shutdown",
        "UnexpectedShutdown" => "Unexpected shutdown",
        "Uptime" => "Uptime report",
        "NoCleanShutdown" => "No clean shutdown recorded",
        "ShutdownReason" => "Shutdown reason",
        "RestartInitiated" => "Restart initiated",
        _ => Kind,
    };

    /// <summary>Drives the strip's dot color - a plain informational marker (Boot/CleanShutdown/
    /// Uptime/RestartInitiated/ShutdownReason) vs. a flagged one (UnexpectedShutdown/
    /// NoCleanShutdown) - same "quick flag, not a verdict" idea as the rest of this app's
    /// heuristics, just surfaced as color instead of prose.</summary>
    public bool IsWarning => Kind is "UnexpectedShutdown" or "NoCleanShutdown";
}
