namespace TaskManagerPlus.Models;

/// <summary>
/// #137-145: models backing the Stability tab's unified cross-channel incident timeline and the
/// sleep/resume-chain, who-rebooted, and per-boot-session ledger cards derived from the same
/// underlying event reads (see Services/EventTimelineService). Every flag here is explicitly
/// correlation, not causation - CLAUDE.md's "quick flag, not a verdict" convention applies to this
/// whole family, the same as the #117 knowledge base and #127-134's anomaly detection.
/// </summary>

/// <summary>#137: which data source a TimelineEntry came from - drives per-source colour coding
/// (TimelineSourceToBrushConverter) and the timeline's filter chips. WerReport is wired into
/// BuildTimeline as of #161 (see EventTimelineService.BuildTimeline's werReports parameter).
/// DriverInstall/UpdateInstall/ServiceInstall are still declared but not wired - no data source for
/// them exists in this codebase until a later chunk (items 169-183) adds one.</summary>
public enum TimelineSource
{
    EventLog,
    Minidump,
    Boot,
    Shutdown,
    CsvLog,
    WerReport,

    // Not wired into BuildTimeline yet - see items 169-183.
    DriverInstall,
    UpdateInstall,
    ServiceInstall,
}

/// <summary>#137: one row of the unified incident timeline - a merge of whatever sources are
/// actually wired (see TimelineSource's remarks), sorted newest-first. Deliberately flat/UI-agnostic
/// (a Title/Detail pair rather than a reference to each source's own richer model) so the timeline
/// can render every source with one DataTemplate; SourceEvent carries the original row back for the
/// sources that have one, so #138/#139/#140's drill-down actions don't need to re-parse Title/Detail.</summary>
public sealed class TimelineEntry
{
    public TimelineSource Source { get; init; }
    public DateTime Timestamp { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;

    /// <summary>Filter-chip / legend label for this entry's source - see TimelineFilterChip.</summary>
    public string SourceLabel { get; init; } = string.Empty;

    /// <summary>Set only for Source==EventLog entries - lets #138's crash-window drill-down reuse
    /// the original TimeCreated/ChannelName rather than re-deriving them from Title/Detail text.</summary>
    public EventRecordRow? SourceEvent { get; init; }

    /// <summary>True for an entry this app treats as "a crash" - the #138 drill-down and #139
    /// change-attribution actions are only offered on these (Kernel-Power 41 / EventLog 6008 /
    /// WER 1001 event-log entries, and every minidump file - a minidump's mere existence is itself
    /// crash evidence, unlike every other TimelineSource here).</summary>
    public bool IsCrash { get; init; }
}

/// <summary>#139: one "change shortly before this crash" hit - a driver install, update install, or
/// new-service record found in the 7 days preceding a crash. Explicitly framed as correlation, not
/// causation - see StabilityView.xaml's card copy.</summary>
public sealed class PreCrashChange
{
    public DateTime Timestamp { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public int EventId { get; init; }
    public string SampleMessage { get; init; } = string.Empty;

    public TimeSpan TimeBeforeCrash { get; init; }
}

/// <summary>#142: one sleep/resume cycle reconstructed from Kernel-Power 42 (entering sleep) paired
/// with the next 107/187/507 (resumed) or 41 (rebooted instead of resuming) - the standard "wakes up
/// to a black screen / reboots instead of resuming" pattern. BootTypeHint is a best-effort read of
/// the nearest Kernel-Boot 20/27 record's own rendered text near the resume/reboot moment - null
/// when none was found nearby or its wording didn't match a recognized pattern (never guessed).</summary>
public sealed class SleepResumeCycle
{
    public DateTime SleepTime { get; init; }
    public DateTime? ResumeTime { get; init; }
    public bool ResumedCleanly { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string? BootTypeHint { get; init; }

    public TimeSpan? Duration => ResumeTime.HasValue ? ResumeTime - SleepTime : null;
}

/// <summary>#143: plain-English answer to "who rebooted this PC" for one shutdown/restart instance,
/// parsed from User32 1074 (process/user/reason/comment) alongside Kernel-General 13 and EventLog
/// 6006/6008 - Answer is always populated (falls back to "Not a clean shutdown..." when none of the
/// three clean-shutdown markers were found), the structured fields are best-effort text parses of
/// 1074's own rendered message and are left null rather than guessed when the wording didn't match.</summary>
public sealed class RebootAttribution
{
    public DateTime Timestamp { get; init; }
    public string Answer { get; init; } = string.Empty;
    public string? InitiatingProcess { get; init; }
    public string? InitiatingUser { get; init; }
    public string? ReasonText { get; init; }
    public string? Comment { get; init; }
    public bool WasCleanShutdown { get; init; }
    public bool WasWindowsUpdate { get; init; }
}

/// <summary>#144: one per-boot session, from an EventLog 6005 ("Event log service started") to the
/// next 6006 (clean stop) / 6008 (unclean stop) - EndTime/EndedCleanly/EndReason are null/false/
/// "still running..." when no end marker turned up in the scanned window (the current session, or a
/// gap the log's retention already rotated past). BootType is a best-effort Kernel-Boot 27 text read,
/// same caveats as SleepResumeCycle.BootTypeHint - it matters because Fast Startup means "restart"
/// and "shut down" aren't really the same reset.</summary>
public sealed class BootSessionRow
{
    public DateTime StartTime { get; init; }
    public DateTime? EndTime { get; init; }
    public string BootType { get; init; } = "Unknown";
    public bool EndedCleanly { get; init; }
    public string EndReason { get; set; } = string.Empty;

    public TimeSpan? Duration => EndTime.HasValue ? EndTime - StartTime : null;
}
