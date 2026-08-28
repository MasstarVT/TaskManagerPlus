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

/// <summary>One file under %SystemRoot%\Minidump. Bugcheck data comes from the authoritative
/// BugCheck-provider 1001 event when its own dump path matches this file (round 13, item 1 - see
/// EventLogService.ReadBugCheckRecords), falling back to the old ±10-minute Kernel-Power-41
/// timestamp correlation only when no matching record was found (an older Windows version without
/// the provider, or a log that already rolled the event off).</summary>
public sealed class MinidumpInfo
{
    public string FileName { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string? BugcheckCode { get; init; }

    /// <summary>Round 13, item 1: all four bugcheck parameters from the matched BugCheck 1001 (or
    /// WER-SystemErrorReporting fallback, item 8) record - empty when no matching record was found
    /// (the old timestamp-correlation path only ever recovers the stop code, not the parameters).</summary>
    public string[] BugcheckParameters { get; init; } = Array.Empty<string>();

    /// <summary>Round 13, item 2: the WER ReportArchive record joined via the BugCheck record's
    /// Report Id GUID - null collapses the "Full crash record" expander entirely on the Stability
    /// tab (no report folder found, or no matching BugCheck record at all).</summary>
    public WerReportFolderMetadata? WerReport { get; init; }

    /// <summary>True when BugcheckCode came from the BugCheck provider's own 1001 event (item 1)
    /// rather than the WER-SystemErrorReporting fallback (item 8, FromWerSummary) or the old
    /// nearby-timestamp guess - shown as a small "confirmed" hint vs. a plainer label.</summary>
    public bool IsAuthoritative { get; init; }

    /// <summary>Round 15, items 28-37: the fully decoded bugcheck (labelled parameters, guidance,
    /// per-code sub-lines) - see BugcheckDecoder. Null only when BugcheckCode itself is null (the
    /// old nearby-timestamp fallback found no bugcheck code at all for this dump).</summary>
    public BugcheckDecodedInfo? Decoded { get; init; }

    /// <summary>Round 15, item 33 (generalized by item 69): true when a Kernel-Power sleep/resume
    /// event (42/107/187) was found within a few minutes of this dump's own crash time - see
    /// EventLogService.ReadSleepResumeEventTimes. Originally gated to DRIVER_POWER_STATE_FAILURE
    /// (0x9F) only; item 69 widened the join to every stop code, since a crash of any kind that
    /// happens to coincide with a sleep/resume transition is worth the same "occurred during
    /// resume" flag, not just the one bugcheck code that's classically caused by it.</summary>
    public bool HappenedDuringSleepResume { get; init; }

    /// <summary>Round 15, item 36: for a WHEA_UNCORRECTABLE_ERROR (0x124) bugcheck, the nearest
    /// WHEA-Logger hardware-error record's own decoded description - reuses the WHEA card's
    /// existing CPER decode (EventLogService.DecodeWheaErrorRecord) rather than re-parsing
    /// anything. Null when the code isn't 0x124, or no WHEA-Logger record was found near the
    /// crash time.</summary>
    public string? WheaJoinText { get; init; }

    /// <summary>#191: the richer bugcheck detail for this specific dump file, when a
    /// Microsoft-Windows-WER-SystemErrorReporting 1001 event names this exact file path (an
    /// authoritative match - Windows itself named the file) or, failing that, when one was found
    /// within a few minutes of this dump's timestamp (the same proximity heuristic
    /// EventLogService.ReadMinidumps already used for BugcheckCode alone). Null falls back to
    /// BugcheckCode's existing event-41-only value with no further detail.</summary>
    public BugcheckDetail? BugcheckDetail { get; init; }
}

/// <summary>
/// Round 13, item 1: one authoritative crash record read directly from the `BugCheck` provider's
/// System-log event 1001 ("The computer has rebooted from a bugcheck") - the stop code, all four
/// bugcheck parameters, the dump file path and a WER Report Id, all as insertion strings on a
/// legacy classic ETW provider (positional access, like EventLogService.ExtractBugcheckCode already
/// uses for Kernel-Power 41). When the BugCheck provider entry itself isn't present in the log
/// (older Windows versions, or a log that's already rolled the event off), item 8's
/// WER-SystemErrorReporting 1001 "BlueScreen" summary entry is used as a second, independent
/// source instead - <see cref="FromWerSummary"/> distinguishes which source a given record came
/// from, since the WER summary parse can't recover a Report Id (so <see cref="WerReport"/> is
/// always null for those).
/// </summary>
/// <summary>A record (not a plain class) purely so EventLogService.EnrichBugCheckRecord (round
/// 15, items 33/36) can use a `with` expression to attach the sleep/resume/WHEA join fields
/// without hand-copying every other property - every existing call site's `new BugCheckRecord {
/// ... }` object-initializer syntax is unaffected by this.</summary>
public sealed record BugCheckRecord
{
    public DateTime TimeCreated { get; init; }
    public string StopCode { get; init; } = string.Empty; // "0x000000EF"
    public string[] Parameters { get; init; } = Array.Empty<string>();
    public string? DumpPath { get; init; }
    public string? ReportId { get; init; }
    public bool FromWerSummary { get; init; }
    public WerReportFolderMetadata? WerReport { get; init; }

    /// <summary>Round 15, items 28-37: the fully decoded bugcheck (labelled parameters, guidance,
    /// per-code sub-lines) for this record's own StopCode/Parameters - see BugcheckDecoder. Set by
    /// EventLogService.EnrichBugCheckRecord, not by ReadBugCheckRecords/ReadWerSummaryBugChecks
    /// themselves.</summary>
    public BugcheckDecodedInfo? Decoded { get; init; }

    /// <summary>Round 15, item 33 (generalized by item 69): true when this record's own
    /// TimeCreated falls within a few minutes of a Kernel-Power sleep/resume event (42/107/187) -
    /// see EventLogService.ReadSleepResumeEventTimes. No longer gated to 0x9F - see MinidumpInfo's
    /// own remarks on the same field for why item 69 widened this.</summary>
    public bool HappenedDuringSleepResume { get; init; }

    /// <summary>Round 15, item 36: for a WHEA_UNCORRECTABLE_ERROR (0x124) record, the nearest
    /// WHEA-Logger hardware-error record's own decoded description - see
    /// EventLogService.DecodeWheaErrorRecord (items 9/10), reused rather than reimplemented.</summary>
    public string? WheaJoinText { get; init; }
}

/// <summary>
/// Round 13, item 2: metadata from the WER ReportArchive folder joined to a BugCheckRecord by its
/// Report Id GUID (EventLogService.ResolveWerReport) - a best-effort text parse of the folder's own
/// Report.wer key=value file plus a directory listing of whatever files WER archived alongside it
/// (typically including the .dmp itself), not a full WER API integration. Null/empty fields when
/// the folder or a given key isn't present, never a guessed value.
/// </summary>
public sealed class WerReportFolderMetadata
{
    public string ReportFolder { get; init; } = string.Empty;
    public string? OsVersion { get; init; }
    public string? SecureBootState { get; init; }
    public List<string> AttachedFiles { get; init; } = new();
}

/// <summary>Round 13, item 3: Kernel-Power 41's own named properties, decoded instead of treating
/// every occurrence as "a crash" - see EventLogService.ClassifyPowerEvent for exactly how (and how
/// tentatively) each value is read. "Quick flag, not a verdict" per CLAUDE.md: this is a heuristic
/// over undocumented per-Windows-version property ordering, not a guaranteed classification.</summary>
public enum ShutdownCause
{
    Unknown,
    Bugcheck,
    PowerButtonHeld,
    PowerLoss,
    HardHang,
}

/// <summary>Round 13, items 3/4: one occurrence of an unexpected shutdown (Kernel-Power 41) across
/// the full lookback window, not just the most recent one - see
/// EventLogService.ReadUnexpectedShutdowns.</summary>
public sealed class UnexpectedShutdownRecord
{
    public DateTime TimeCreated { get; init; }
    public ShutdownCause Cause { get; init; }
    public string? BugcheckCode { get; init; }

    /// <summary>Best-effort "minutes powered on before this shutdown" reading - see
    /// ClassifyPowerEvent's remarks on why this isn't read from one fixed, versioned property
    /// index. Null when no plausible value was found.</summary>
    public TimeSpan? UptimeBeforeCrash { get; init; }
}

/// <summary>
/// Round 13, items 5/6: one entry in the "shutdown &amp; restart timeline" - either a User32 1074
/// "initiated" shutdown/restart (with the requesting process/user/reason, item 5), an EventLog
/// service start/stop marker (6005/6006/6009/6013), or a boot (Kernel-General 12 / Kernel-Boot
/// 20/27) whose preceding clean-shutdown marker (Kernel-General 13) is missing - flagged dirty
/// (item 6) even when Kernel-Power 41 itself was never logged for that boot (e.g. a hang held past
/// the point the OS could log anything at all).
/// </summary>
public sealed class ShutdownTimelineEntry
{
    public DateTime TimeCreated { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string? Process { get; init; }
    public string? User { get; init; }
    public string? Reason { get; init; }
    public bool IsDirtyBoot { get; init; }

    /// <summary>Item 68: set only on a dirty boot ("Boot" entry with IsDirtyBoot true) that also
    /// matches the "freeze without crash" pattern - the nearest Kernel-Power 41 recorded no
    /// bugcheck code, and no minidump file was written near that time. Distinguishes a true hard
    /// hang or sudden power loss (Windows itself never got to bugcheck) from an ordinary dirty
    /// boot, where a real BugCheckRecord/MinidumpInfo elsewhere on this tab already explains what
    /// happened. Null on every other entry (not a freeze, or not even a dirty boot). See
    /// EventLogService.DetectFreezeWithoutCrash.</summary>
    public string? FreezeWithoutCrashLabel { get; init; }

    /// <summary>Item 68: the last handful of System-log events recorded before the silence leading
    /// up to this boot - context for FreezeWithoutCrashLabel so the label can be sanity-checked
    /// rather than trusted blindly ("quick flag, not a verdict" per CLAUDE.md). Empty unless
    /// FreezeWithoutCrashLabel is set.</summary>
    public List<string> EventsBeforeSilence { get; init; } = new();
}

/// <summary>Round 13, item 7: a volmgr 161/162 "dump creation failed" System-log event - explains
/// the common "I had a BSOD but there's no dump file" case that a bare Minidump-folder listing
/// can't. NtStatus is a best-effort regex pull of the first hex status code out of the event's own
/// formatted message (the legacy volmgr provider doesn't expose it as a separate named property).</summary>
public sealed class DumpFailureEvent
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string? NtStatus { get; init; }
}

/// <summary>
/// Round 13, items 9/10: one Microsoft-Windows-WHEA-Logger hardware-error event (17/18/19/47) -
/// corrected and uncorrectable machine-check, memory and PCIe errors, often logged for weeks before
/// a 0x124 (WHEA_UNCORRECTABLE_ERROR) bugcheck. Severity/Source are pulled from the event's own
/// formatted message text (item 9); Decoded is a best-effort *partial* decode of the binary
/// ErrorRecord blob attached to the event - see EventLogService.DecodeWheaErrorRecord for exactly
/// what is (and, honestly, isn't) decoded with confidence. "Quick flag, not a verdict" per
/// CLAUDE.md: corrected-error counts are framed in the UI as an early warning, not a diagnosis.
/// </summary>
public sealed class WheaErrorEvent
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string Severity { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Decoded { get; init; } = "Unknown hardware error section";
}

/// <summary>Round 13, item 9: WheaErrorEvent rows grouped by (Severity, Source) with a count and
/// last-seen time - the same "flat list -&gt; grouped summary" shape FaultingModuleSummary already
/// uses for repeated app crashes, applied to hardware errors instead. A pure derived aggregation
/// over the already-read WHEA event list, no new query.</summary>
public sealed class WheaSummaryRow
{
    public string Severity { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public int Count { get; init; }
    public DateTime LastSeen { get; init; }
}

/// <summary>Round 13, item 11: one day's Microsoft-computed Reliability Monitor stability index
/// (Win32_ReliabilityStabilityMetrics.SystemStabilityIndex, 0-10) - plotted as a second series on
/// the existing Reliability History chart alongside this app's own computed daily Critical/Error
/// count, so the two "how stable has this PC been" views sit side by side instead of the app's
/// heuristic being the only number shown.</summary>
public sealed class ReliabilityMetricPoint
{
    public DateTime Date { get; init; }
    public double Index { get; init; }
}

/// <summary>
/// Round 13, item 12: "is the 30-day lookback window even trustworthy" event-log health check - a
/// log that was cleared recently, or is small enough that its actual retention doesn't cover the
/// full lookback window, means "no crashes found" elsewhere on this tab can be a hollow result
/// rather than a clean bill of health. WasClearedRecently/LastClearedTime come from System-log
/// event 104 (Eventlog provider, "The System log file was cleared"); OldestRecordTime is read
/// directly off the log's own oldest record; MaxSizeBytes comes from `wevtutil gl System`.
/// </summary>
public sealed class EventLogHealth
{
    public DateTime? OldestRecordTime { get; init; }
    public long? MaxSizeBytes { get; init; }
    public bool WasClearedRecently { get; init; }
    public DateTime? LastClearedTime { get; init; }
}

/// <summary>Round 15, item 34: one Display-provider event 4101 (TDR) occurrence, with the display
/// driver and (when the event's own insertion strings carry one) the application whose GPU
/// context was reset - see EventLogService.ReadTdrEventDetails. Driver/Application are best-
/// effort (regex fallback on the formatted message when the named property isn't present), null
/// when neither source found a value.</summary>
public sealed class TdrEventDetail
{
    public DateTime TimeCreated { get; init; }
    public string? Driver { get; init; }
    public string? Application { get; init; }
}

/// <summary>Round 15, item 34: the three registry values under
/// HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers that actually control TDR's own timeout
/// behavior - null fields mean the value isn't set (Windows then falls back to its own
/// undocumented built-in default), not a fabricated number. TdrLevelText is a plain-English
/// label for TdrLevel's small documented enum (0-3).</summary>
public sealed class TdrRegistrySettings
{
    public int? TdrDelaySeconds { get; init; }
    public int? TdrDdiDelaySeconds { get; init; }
    public int? TdrLevel { get; init; }
    public string TdrLevelText { get; init; } = "Unknown";
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

    /// <summary>Round 13, items 1/2/8: the most recent authoritative bugcheck record (BugCheck
    /// provider 1001, or the WER-SystemErrorReporting fallback) - null when neither source found
    /// anything in the lookback window (not necessarily "no crash", just "no record of one").</summary>
    public BugCheckRecord? LatestBugCheck { get; init; }

    /// <summary>Round 13, item 3: cause classification of the single most recent Kernel-Power 41
    /// occurrence - drives the labelled badge on the unexpected-shutdown banner. Null when the most
    /// recent unexpected shutdown was only ever seen as a legacy EventLog 6008 entry (no named
    /// properties to classify) or there was none at all.</summary>
    public ShutdownCause? LastShutdownCause { get; init; }

    /// <summary>Round 13, item 4: every Kernel-Power 41 occurrence in the lookback window, not just
    /// the most recent - feeds the Stability tab's "Unexpected shutdowns" card.</summary>
    public List<UnexpectedShutdownRecord> UnexpectedShutdowns { get; init; } = new();

    /// <summary>Round 13, items 5/6: the merged shutdown/restart/boot timeline - see
    /// EventLogService.ReadShutdownTimeline.</summary>
    public List<ShutdownTimelineEntry> ShutdownTimeline { get; init; } = new();

    /// <summary>Round 13, item 7: volmgr 161/162 "dump creation failed" events in the lookback
    /// window - surfaced as an inline warning on the Minidumps card.</summary>
    public List<DumpFailureEvent> DumpFailures { get; init; } = new();

    /// <summary>Round 13, items 9/10: WHEA-Logger hardware-error events in the lookback window.</summary>
    public List<WheaErrorEvent> WheaErrors { get; init; } = new();

    /// <summary>Round 13, item 11: Microsoft's own per-day Reliability Monitor stability index -
    /// plotted as a second series on the Reliability History chart.</summary>
    public List<ReliabilityMetricPoint> ReliabilityMetrics { get; init; } = new();

    /// <summary>Round 13, item 12: "is the lookback window even trustworthy" health check.</summary>
    public EventLogHealth? LogHealth { get; init; }

    /// <summary>Round 15, item 34: per-event TDR detail (driver/app/time) beyond the plain
    /// TdrEventCount/LastTdrEvent tile above - see EventLogService.ReadTdrEventDetails.</summary>
    public List<TdrEventDetail> TdrEventDetails { get; init; } = new();

    /// <summary>Round 15, item 34: the live TdrDelay/TdrDdiDelay/TdrLevel registry settings that
    /// actually control TDR's timeout behavior on this machine.</summary>
    public TdrRegistrySettings? TdrSettings { get; init; }

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

    /// <summary>#447: Microsoft-Windows-WHEA-Logger corrected-memory-error events (event ID 47)
    /// within the lookback window - see EventLogService.ReadCorrectedMemoryErrors. Also read
    /// independently by SystemSpecsService for the System Specs memory section, so both tabs stay
    /// in sync without a ViewModel-to-ViewModel dependency.</summary>
    public int CorrectedMemoryErrorCount { get; init; }
    public DateTime? LastCorrectedMemoryError { get; init; }
    public List<CorrectedMemoryErrorEvent> CorrectedMemoryErrors { get; init; } = new();

    /// <summary>#451: how many of RecentEvents' Kernel-Power 41 bugcheck codes match the small
    /// memory-related STOP code set - see EventLogService.MemoryRelatedBugcheckCodes.</summary>
    public int MemoryRelatedBugcheckCount { get; init; }

    /// <summary>#464: boot-start/system-start driver load failures (SCM 7000/7001/7026, kernel PnP
    /// event 219) within the lookback window - see EventLogService.ReadBootDriverLoadFailures. Also
    /// read independently by the Devices &amp; Drivers tab, the same dual-read pattern
    /// CorrectedMemoryErrors above already uses.</summary>
    public List<BootDriverLoadFailure> BootDriverLoadFailures { get; init; } = new();

    /// <summary>#487: every Microsoft-Windows-WHEA-Logger record found (any event ID) within the
    /// lookback window, decoded via CperDecoder where possible - see
    /// EventLogService.ReadWheaHardwareErrors. CorrectedMemoryErrors above stays as its own
    /// narrower, message-text-based read of just event 47; this broad list includes event 47's
    /// records too (now cross-checked against their own binary payload).</summary>
    public List<WheaHardwareErrorEvent> WheaHardwareErrors { get; init; } = new();

    /// <summary>#488: corrected-severity WHEA records per day across the lookback window, oldest
    /// first - the same zero-filled daily-bucket shape as DailyCounts above, just filtered to
    /// WheaHardwareErrors entries whose decoded Severity is Corrected.</summary>
    public List<DailyEventCount> DailyWheaCorrectedCounts { get; init; } = new();

    /// <summary>#492: crash/TDR/unexpected-shutdown events with at least one WHEA hardware-error
    /// record in the preceding correlation window - see
    /// EventLogService.BuildHardwareErrorCorrelations. A correlation, not a claimed cause.</summary>
    public List<HardwareErrorCorrelation> HardwareErrorCorrelations { get; init; } = new();
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

/// <summary>#239: one Windows Error Reporting AppHang report, parsed from a Report.wer file under
/// ReportArchive/ReportQueue - see AppHangReportService.Read's remarks for why the key names are
/// searched leniently rather than matched exactly. AppPath/AppVersion default to "(unknown)"
/// rather than blank so a parse that found the report but not every key still reads clearly.</summary>
public sealed class AppHangReportEntry
{
    public string AppPath { get; init; } = "(unknown)";
    public string AppVersion { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty; // AppHangB1 / AppHangXProcB1 / AppHangTransient
    public string HangSignature { get; init; } = string.Empty;
    public string FaultingModule { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string ReportFolder { get; init; } = string.Empty;
}

/// <summary>#240: one app's ranked "Application Hang" (event ID 1002) history over the lookback
/// window - see EventLogService.ReadApplicationHangEvents. Complements #239's richer per-report
/// detail, which Windows prunes from ReportQueue/ReportArchive sooner than the event log itself.</summary>
public sealed class AppHangEventSummary
{
    public string AppName { get; init; } = string.Empty;
    public int Count { get; init; }
    public DateTime LastSeen { get; init; }
    public string HangType { get; init; } = string.Empty;
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

/// <summary>#741: one resume-from-hibernate that looks like it failed - a Kernel-Boot event 27
/// boot type 2 (resume from hibernate, see BootType) followed, before the next recorded boot, by
/// a Kernel-Power 41 ("no clean shutdown recorded") or System-log 6008 ("previous shutdown was
/// unexpected") entry from the same Power &amp; boot timeline this correlates against - see
/// PowerTimelineService.ReadFailedResumes. A flag, not a verdict (CLAUDE.md's cross-cutting
/// conventions): a resume that failed for an unrelated reason (a hard power-button hold during a
/// legitimately slow resume) looks identical to this heuristic as a genuine resume bug.</summary>
public sealed class FailedResumeEntry
{
    public DateTime ResumeTime { get; init; }
    public DateTime FailureTime { get; init; }
    public string FailureKind { get; init; } = string.Empty;

    public string SummaryText => $"Resumed from hibernate at {ResumeTime:g}, then {FailureKind} at {FailureTime:g}.";
}

/// <summary>#781: "did an update break this?" - a KB/update installed (per
/// WindowsUpdateHistoryService.ReadUpdateClientHistory, event 19) within 48 hours before a faulting
/// module (see StabilityEvent.FaultingModule) started recurring (2+ occurrences) in this tab's own
/// crash timeline. Pure post-processing over two already-read lists - no new query. A quick flag,
/// not a verdict (CLAUDE.md's cross-cutting conventions): plenty of recurring crashes have nothing
/// to do with the update that happened to land beforehand - this is "worth testing by uninstalling",
/// not a confirmed cause. See WindowsUpdateHistoryService.CorrelateWithStabilityFailures.</summary>
public sealed class UpdateBreakageFlag
{
    public DateTime InstallTime { get; init; }
    public string UpdateTitle { get; init; } = string.Empty;
    public string FaultingModule { get; init; } = string.Empty;
    public DateTime FirstFailureTime { get; init; }
    public int FailureCount { get; init; }

    public string SummaryText =>
        $"{(UpdateTitle.Length > 0 ? UpdateTitle : "An update")} installed {InstallTime:g}, then {FaultingModule} " +
        $"started crashing repeatedly ({FailureCount} times since {FirstFailureTime:g}) " +
        $"{(FirstFailureTime - InstallTime).TotalHours:0.#}h later. Worth testing by uninstalling it - not a confirmed cause.";
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
