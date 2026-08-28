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
    /// layout isn't a documented, versioned contract). #191: mutable (not init-only), like
    /// EventRecordRow's Kb* properties, so EventLogService.Query's second pass can overwrite this
    /// with the richer Microsoft-Windows-WER-SystemErrorReporting 1001 code when one is found
    /// nearby - the event-41 property-index guess stays the value here only when no richer event
    /// was found.</summary>
    public string? BugcheckCode { get; set; }

    /// <summary>#191: the richer bugcheck detail (all four parameters + dump path) recovered from a
    /// nearby Microsoft-Windows-WER-SystemErrorReporting 1001 event, when one was found - null
    /// leaves BugcheckCode as the existing event-41 best-effort guess with no further detail
    /// available, per CLAUDE.md's "degrade, never fabricate" rule.</summary>
    public BugcheckDetail? BugcheckDetail { get; set; }

    /// <summary>#168: a fuller, non-truncated parse of this event's own message - only ever populated
    /// for ".NET Runtime" event 1026 (managed exception type + top stack frames) and "Application
    /// Error" event 1000 (structured faulting application/module/exception-code/offset fields, since
    /// 1000 itself never carries a managed stack trace) - see
    /// WerReportService.ParseManagedExceptionDetail. Null for every other event, which is what
    /// DisplayDetail below falls back to Message (still truncated to 300 characters) for.</summary>
    public string? ExceptionDetail { get; init; }

    /// <summary>#168: what the UI should actually show for this event's detail/tooltip - the fuller
    /// ExceptionDetail parse when one was found, otherwise the existing truncated Message. Leaves
    /// every event ID other than 1026/1000 exactly as truncated as before.</summary>
    public string DisplayDetail => ExceptionDetail ?? Message;
}

/// <summary>One file under %SystemRoot%\Minidump (#3) - bugcheck code is filled in only when a
/// Kernel-Power event 41 was recorded within a few minutes of the dump's timestamp.</summary>
public sealed class MinidumpInfo
{
    public string FileName { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string? BugcheckCode { get; init; }

    /// <summary>#191: the richer bugcheck detail for this specific dump file, when a
    /// Microsoft-Windows-WER-SystemErrorReporting 1001 event names this exact file path (an
    /// authoritative match - Windows itself named the file) or, failing that, when one was found
    /// within a few minutes of this dump's timestamp (the same proximity heuristic
    /// EventLogService.ReadMinidumps already used for BugcheckCode alone). Null falls back to
    /// BugcheckCode's existing event-41-only value with no further detail.</summary>
    public BugcheckDetail? BugcheckDetail { get; init; }
}

/// <summary>#191: the full bugcheck detail Microsoft-Windows-WER-SystemErrorReporting's own event
/// 1001 carries in its message text - "0x00000133 (0x..., 0x..., 0x..., 0x...)" plus the dump file
/// path Windows itself wrote. Far more reliable than Kernel-Power 41's undocumented property-index
/// layout (EventLogService.ExtractBugcheckCode), but not logged on every Windows edition/crash, so
/// this is preferred when found and the event-41 guess stays as fallback - see
/// EventLogService.ReadWerBugcheckEvents.</summary>
public sealed class BugcheckDetail
{
    public string Code { get; init; } = string.Empty;
    public string Parameter1 { get; init; } = string.Empty;
    public string Parameter2 { get; init; } = string.Empty;
    public string Parameter3 { get; init; } = string.Empty;
    public string Parameter4 { get; init; } = string.Empty;
    public string? DumpFilePath { get; init; }
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

/// <summary>#122: one (Provider, EventId) pair from the event knowledge base's "seriously bad" set
/// that actually turned up in the lookback window - raw count/last-seen only, before being joined
/// with the KB entry's text; see EventLogService.ScanForKnownBadIds and
/// StabilityViewModel.BuildKnownBadIdScorecard.</summary>
public sealed class KnownBadIdScanHit
{
    public string Provider { get; init; } = string.Empty;
    public int EventId { get; init; }
    public int Count { get; init; }
    public DateTime LastSeen { get; init; }
}

/// <summary>#122: one row of the Stability tab's "Known-bad IDs present on this PC" scorecard - a
/// KnownBadIdScanHit joined with its knowledge-base entry's text, flattened to plain
/// strings/ints for binding (rather than referencing Models.EventKbEntry directly) so this Stability-
/// tab presentation model doesn't need to carry the Events tab's knowledge-base type shape.</summary>
public sealed class KnownBadIdScorecardRow
{
    public string Provider { get; init; } = string.Empty;
    public int EventId { get; init; }
    public int Count { get; init; }
    public DateTime LastSeen { get; init; }
    public string Meaning { get; init; } = string.Empty;
    public string NextStep { get; init; } = string.Empty;
    public string SeverityLabel { get; init; } = string.Empty;
    public int SeverityRank { get; init; }
}
