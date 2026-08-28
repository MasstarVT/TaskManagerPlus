namespace TaskManagerPlus.Models;

/// <summary>
/// #161-167: Windows Error Reporting - the WER report queue/archive explorer, bucket-signature
/// grouping, top-crashing-applications ranking, hang detection, storage footprint, and the
/// LocalDumps/reporting-configuration registry reads (and the one explicit registry write, #165's
/// LocalDumps toggle) - see Services/WerReportService. Every "quick flag, not a verdict" and
/// "degrade to Unknown/0/hidden, never fabricate" convention CLAUDE.md documents applies throughout
/// this family, same as the #117 knowledge base and #137-145 timeline models.
/// </summary>

/// <summary>#161: one parsed Report.wer file from %ProgramData%\Microsoft\Windows\WER\ReportQueue or
/// \ReportArchive - a simple `Key=Value` file (some Windows versions add `[Basic]`-style section
/// headers) with no formally documented, versioned schema, so every field here is a best-effort,
/// lenient extraction (see WerReportService.ParseReportFolder) and null/empty rather than guessed
/// when a report doesn't carry that field. Timestamp is the Report.wer file's own last-write time
/// (the report's internal EventTime/UploadTime fields use an undocumented, version-varying encoding -
/// the file's own filesystem timestamp is the one universally reliable signal, the same tradeoff
/// EventLogService.ReadMinidumps already makes for minidump files).</summary>
public sealed class WerReportInfo
{
    public string FolderPath { get; init; } = string.Empty;

    /// <summary>False = ReportQueue (pending upload), true = ReportArchive (already processed).</summary>
    public bool IsArchived { get; init; }

    public DateTime Timestamp { get; init; }

    public string? EventType { get; init; }
    public string? AppName { get; init; }
    public string? AppPath { get; init; }
    public string? AppVersion { get; init; }
    public string? ModName { get; init; }
    public string? ModVersion { get; init; }
    public string? ExceptionCode { get; init; }
    public string? ExceptionOffset { get; init; }

    /// <summary>#162: Windows' own bucket identifier, when the report carries one - preferred over
    /// this app's own App+Module+Code composite signature when present, since it's the more precise
    /// grouping WER itself computed.</summary>
    public string? BucketId { get; init; }
    public string? BucketParameter { get; init; }

    /// <summary>Every Sig[N].Name/Sig[N].Value pair found in the file, in file order - the raw
    /// signature parameters WER itself recorded, shown in the report detail view beyond the handful
    /// of named fields already broken out above.</summary>
    public List<(string Name, string Value)> SignatureParameters { get; init; } = new();

    /// <summary>#162: the grouping key used to cluster "five crashes of the same shape" into one
    /// bucket row - BucketId when the report has one, otherwise a composite of app+module+exception
    /// code (more precise than any existing FaultingModule-only grouping elsewhere in this app).</summary>
    public string SignatureKey => !string.IsNullOrWhiteSpace(BucketId)
        ? BucketId!
        : $"{AppName ?? "Unknown app"}|{ModName ?? "Unknown module"}|{ExceptionCode ?? "Unknown code"}";

    public string DisplayTitle => $"{AppName ?? "Unknown app"} ({EventType ?? "Unknown"})";
}

/// <summary>#162: one bucket row - "N crashes of this shape" - clustered from WerReportInfo.SignatureKey.
/// A different, more precise lens than StabilityViewModel's existing #66 FaultingModuleSummary
/// (which only ever groups by faulting module name off the fixed event-log digest).</summary>
public sealed class WerCrashBucket
{
    public string AppName { get; init; } = "Unknown app";
    public string ModName { get; init; } = "Unknown module";
    public string? ExceptionCode { get; init; }
    public string? BucketId { get; init; }
    public int Count { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }

    public string SignatureText => BucketId is { Length: > 0 } b
        ? $"{AppName} — {ModName} ({ExceptionCode ?? "unknown code"}) · bucket {b}"
        : $"{AppName} — {ModName} ({ExceptionCode ?? "unknown code"})";
}

/// <summary>#163: one application's crash tally, combining WER report counts with Application-log
/// "Application Error" 1000 entries (read via the same EventLogExplorerService.ReadPage the Events
/// tab already uses - no third event-log reader added for this).</summary>
public sealed class TopCrashingApplication
{
    public string AppName { get; init; } = string.Empty;
    public int CrashCount { get; init; }
    public string? MostCommonModule { get; init; }
    public DateTime LastCrashTime { get; init; }
}

/// <summary>#164: one "Application Hang" 1002 entry - kept as its own list, not folded into the
/// crash cards above, since "went white and unresponsive" and "disappeared" have different causes.
/// CorrelationNote is always populated (never a computed correlation) - this app has no historical
/// CPU/disk-pressure log a hang timestamp could be looked up against yet (LoggingService only ever
/// writes a CSV forward, it has no read-back/replay path), so #164's optional "correlate with
/// CPU/disk pressure at that timestamp" is honestly skipped rather than faked.</summary>
public sealed class WerHangInfo
{
    public DateTime Timestamp { get; init; }
    public string? ProcessName { get; init; }
    public string? ProcessId { get; init; }
    public string? HangType { get; init; }
    public string RawMessage { get; init; } = string.Empty;

    public string CorrelationNote { get; init; } =
        "CPU/disk pressure at this time not shown - this app has no historical performance log to look it up in yet.";
}

/// <summary>#166: total size/file count of the ReportQueue and ReportArchive trees - no deletion is
/// ever performed by this app, only a reveal-in-Explorer button (reusing
/// EtwTraceService.RevealInExplorer) so the user can clean up themselves.</summary>
public sealed class WerStorageFootprint
{
    public string QueuePath { get; init; } = string.Empty;
    public bool QueueExists { get; init; }
    public long QueueSizeBytes { get; init; }
    public int QueueFileCount { get; init; }

    public string ArchivePath { get; init; } = string.Empty;
    public bool ArchiveExists { get; init; }
    public long ArchiveSizeBytes { get; init; }
    public int ArchiveFileCount { get; init; }
}

/// <summary>#165: the HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps values that
/// control whether user-mode crash dumps are actually kept locally for a repro, or just uploaded and
/// discarded (the default). KeyExists=false means the subkey doesn't exist at all - Windows' own
/// default behavior applies, not "disabled" in any explicit sense. Also doubles as the JSON shape
/// persisted to wer-localdumps-backup.json for #165's one-click revert, so a revert survives an app
/// restart, not just the current session.</summary>
public sealed class LocalDumpsSettings
{
    public bool KeyExists { get; init; }
    public string? DumpFolder { get; init; }
    public int? DumpCount { get; init; }

    /// <summary>0 = Custom, 1 = Mini, 2 = Full (Microsoft's own documented DumpType values).</summary>
    public int? DumpType { get; init; }

    public string DumpTypeLabel => DumpType switch
    {
        0 => "Custom dump",
        1 => "Mini dump",
        2 => "Full dump (can be very large per crash)",
        _ => "Not set",
    };

    public string StatusLabel => KeyExists
        ? "Configured — crash dumps are being kept locally"
        : "Not configured — crashes are uploaded and discarded (Windows default)";
}

/// <summary>#167: read-only snapshot of whether error reporting is actually turned on for this
/// machine - HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting's Disabled/DontShowUI/consent
/// keys, plus the WerSvc service state (via the same System.ServiceProcess.ServiceController API
/// this app already uses for the Services tab). Informational only, per CLAUDE.md's "quick flag, not
/// a verdict" convention - IsReportingEffectivelyOff is a heuristic, not an authoritative reason WER
/// might not fire for a given crash (WerSvc is a manual/trigger-start service - it sitting Stopped is
/// normal and is deliberately NOT treated as "off" here, only its start type being Disabled is).</summary>
public sealed class WerConfigStatus
{
    public bool? Disabled { get; init; }
    public bool? DontShowUI { get; init; }
    public string? ConsentDescription { get; init; }
    public string WerSvcStatus { get; init; } = "Unknown";
    public string? WerSvcStartType { get; init; }
    public bool IsReportingEffectivelyOff { get; init; }

    private static string Tri(bool? v) => v is { } b ? (b ? "Yes" : "No") : "Unknown";
    public string DisabledLabel => Tri(Disabled);
    public string DontShowUILabel => Tri(DontShowUI);

    public string SummaryText => IsReportingEffectivelyOff
        ? "Error reporting looks turned off on this PC - crashes may not be recorded or reported at all."
        : "Error reporting looks active on this PC.";
}
