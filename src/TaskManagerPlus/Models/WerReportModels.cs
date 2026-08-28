namespace TaskManagerPlus.Models;

/// <summary>
/// Round 16, items 38-49: Windows Error Reporting archive/queue scanning - a crash record source
/// entirely separate from the event log (WER's own ReportArchive/ReportQueue folders, each
/// holding a Report.wer key=value text file plus whatever files WER attached alongside it -
/// typically a .dmp and/or a heap snapshot). See WerReportService, which generalizes the same
/// "scan a folder of Report.wer files" approach EventLogService.ResolveWerReport (item 2, Report
/// Id -> single folder) and MinidumpParserService.ResolveLiveKernelWerCode (item 22, dump file
/// name -> single folder) already use narrowly, into a full scan of every report this machine
/// and this user account currently have on disk.
/// </summary>
public enum WerReportSource
{
    MachineArchive,
    MachineQueue,
    UserArchive,
    UserQueue,
}

/// <summary>
/// Item 38: one WER report folder, generically parsed from its own Report.wer key=value text
/// file - direct top-level "Key=Value" lines (AppName/AppVersion/ModName/ModVersion/Offset/
/// EventType) first, falling back to the numbered Sig[N].Name/Sig[N].Value (and DynamicSig[N])
/// signature-parameter pairs WER also writes - the shape every AppHang report, and many newer
/// APPCRASH reports, actually use for these same fields - when a value isn't present as a direct
/// top-level key. Null fields mean the key genuinely wasn't found in this report (CLAUDE.md's
/// "degrade to Unknown, never fabricate"), not a parse failure. A record (not a plain class)
/// purely so WerReportService.JoinApplicationErrorEvents (item 47) can use a `with` expression to
/// attach the joined event fields without hand-copying every other property - the same reason
/// BugCheckRecord is a record.
/// </summary>
public sealed record WerReport
{
    public string ReportFolder { get; init; } = string.Empty;
    public WerReportSource Source { get; init; }

    /// <summary>Item 40: which of the four scanned roots this report came from - shown as its own
    /// column, since most non-elevated application crashes end up in the per-user store rather
    /// than the machine-wide one.</summary>
    public string SourceText => Source switch
    {
        WerReportSource.MachineArchive => "Machine (Archive)",
        WerReportSource.MachineQueue => "Machine (Queue)",
        WerReportSource.UserArchive => "User (Archive)",
        WerReportSource.UserQueue => "User (Queue)",
        _ => "Unknown",
    };

    public string? EventType { get; init; }

    /// <summary>Item 46: WER report types AppHang_XProcB1/AppHangB1/AppHangTransient are hangs,
    /// not crashes - a different signature set (no exception code/offset). Split into their own
    /// section rather than counted alongside real crashes in every summary number on this card.</summary>
    public bool IsHang { get; init; }

    public string? AppName { get; init; }
    public string? AppVersion { get; init; }
    public string? AppTimeStamp { get; init; }
    public string? ModName { get; init; }
    public string? ModVersion { get; init; }
    public string? ModTimeStamp { get; init; }
    public string? ExceptionCode { get; init; }
    public string? Offset { get; init; }

    /// <summary>Item 39: WER's own normalised crash-bucket signature, when this report's own
    /// Report.wer actually carries one under one of its several known key names. Grouping by this
    /// produces genuine crash clusters. Null on most client-side reports - a real bucket ID is
    /// typically assigned by Watson/WER server-side after upload, not written into the local
    /// Report.wer at capture time - in which case <see cref="ComputedSignature"/> is used as the
    /// grouping key instead; <see cref="HasRealBucketId"/> tells the UI which one is in play so a
    /// locally-derived signature is never presented as if it were WER's own bucket ID.</summary>
    public string? BucketId { get; init; }
    public bool HasRealBucketId => !string.IsNullOrEmpty(BucketId);

    /// <summary>A normalised app+version+module+version+offset+exception signature computed
    /// locally from whatever fields were actually found - used as the grouping key (item 39) when
    /// no real WER bucket ID is present, and always included in the "Copy crash signature" text
    /// (item 45) since it's useful context alongside a real bucket ID too.</summary>
    public string ComputedSignature { get; init; } = string.Empty;

    public string EffectiveBucketKey => BucketId ?? ComputedSignature;

    /// <summary>The report folder's own last-write time - WER doesn't reliably expose a separate
    /// "when did this crash happen" field across every report shape this app parses, and the
    /// folder's own timestamp is set once, at capture time, and never touched again.</summary>
    public DateTime ReportTimestamp { get; init; }

    public long SizeBytes { get; init; }
    public List<string> AttachedFiles { get; init; } = new();

    /// <summary>Item 47: the Application-log event 1000 (Application Error) joined to this report
    /// by matching app/module name within a short time window of the report's own timestamp - see
    /// WerReportService.JoinApplicationErrorEvents. Null when no matching event was found (a
    /// common, expected case - the 30-day event-log window is far shorter than how long a WER
    /// archive folder survives, item 48).</summary>
    public string? JoinedEventMessage { get; init; }
    public string? JoinedEventReportId { get; init; }
}

/// <summary>Item 39: WerReport rows grouped by EffectiveBucketKey - "this exact crash happened N
/// times" instead of one row per report. The primary grouped view of the Error reports card for
/// non-hang reports. A pure derived aggregation over the already-scanned report list, computed by
/// WerReportService.GroupByBucket.</summary>
public sealed class WerBucketGroup
{
    public string BucketKey { get; init; } = string.Empty;
    public bool HasRealBucketId { get; init; }
    public string AppName { get; init; } = "Unknown";
    public string ModName { get; init; } = "Unknown";
    public int Count { get; init; }
    public DateTime LastSeen { get; init; }
    public List<WerReport> Reports { get; init; } = new();
}

/// <summary>Items 41/44: "is Windows even collecting crash data" - the registry values that gate
/// whether a WER report is ever written at all, plus the two that gate whether a crash dialog
/// appears / whether reports are sent silently (item 44). Every nullable field being null means
/// "not set, Windows' own default applies" rather than a fabricated value (CLAUDE.md).</summary>
public sealed class WerCollectionStatus
{
    public bool? Disabled { get; init; }
    public bool? DontSendAdditionalData { get; init; }
    public string ServiceStatusText { get; init; } = "Unknown";

    /// <summary>True only when WerSvc's own Start registry value is 4 (Disabled) - WerSvc being
    /// merely Stopped is completely normal (it's a demand-start service by design) and is not
    /// itself a sign anything is wrong.</summary>
    public bool ServiceLooksBlocked { get; init; }

    public int? DefaultConsent { get; init; }
    public string DefaultConsentText { get; init; } = "Unknown";
    public bool? DontShowUi { get; init; }

    /// <summary>True when WER looks disabled/blocked enough that this app's own WER-based
    /// features (this card, plus the BugCheck-1001 WER join on the Minidumps card) are likely
    /// starved of data - drives the warning strip.</summary>
    public bool LooksDisabled => Disabled == true || ServiceLooksBlocked;
}

/// <summary>Item 43: total size/count of every report folder across all four scanned roots -
/// shown with an explicit purge action, since these folders can grow to gigabytes of stale
/// reports and heap dumps.</summary>
public sealed class WerQueueSizeInfo
{
    public int FolderCount { get; init; }
    public long TotalSizeBytes { get; init; }
}

/// <summary>Item 42: one LocalDumps configuration - either the global default (TargetExecutable
/// null, values read/written directly under the LocalDumps key) or a per-executable override (a
/// same-named subkey). Windows falls back to its own built-in defaults for any null field here.</summary>
public sealed class LocalDumpsConfig
{
    public string? TargetExecutable { get; init; }
    public bool Exists { get; init; }
    public string? DumpFolder { get; init; }
    public int? DumpCount { get; init; }
    public int? DumpType { get; init; }
    public int? CustomDumpFlags { get; init; }

    public string DumpTypeText => DumpType switch
    {
        0 => "Custom dump",
        1 => "Mini dump",
        2 => "Full dump (complete process memory)",
        null => "Not set (Windows default: Mini dump)",
        _ => $"{DumpType} (unrecognized)",
    };
}

/// <summary>Item 48: one day's WER-archive-derived crash count - built from report folder
/// timestamps rather than the event log, so it isn't capped at the 30-day event-log lookback
/// window every other chart on this tab uses (WER archive folders aren't subject to log
/// rollover).</summary>
public sealed class WerDailyCount
{
    public DateTime Date { get; init; }
    public int Count { get; init; }
}

// Item 47's join target used to be its own small AppErrorEventInfo model (app/module/offset only).
// Round 17, item 50 replaced it with the fully structured ApplicationCrashEvent (Models/
// ApplicationCrashModels.cs) - a strict superset of the same fields (AppName/ModName/TimeCreated/
// ReportId/Message all still present under the same names), so WerReportService.
// JoinApplicationErrorEvents below just took the richer type with no other change needed.
