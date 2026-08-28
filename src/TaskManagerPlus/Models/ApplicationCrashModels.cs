namespace TaskManagerPlus.Models;

/// <summary>
/// Round 17, item 50 (the anchor of this chunk): one Application-log event 1000 ("Application
/// Error"), fully parsed from its documented positional insertion strings - faulting application
/// name/version/timestamp, faulting module name/version/timestamp, exception code, fault offset,
/// process id, application path, module path, report id - rather than the single faulting-module
/// regex the flat "Recent critical / error events" grid already extracts (StabilityEvent.
/// FaultingModule). Every other item in this chunk (51/52/56/57/60) is a view, lookup or join over
/// this same parsed list - see EventLogService.ReadApplicationCrashEvents for the parse itself and
/// ApplicationCrashService for the enrichment pass. A record (not a plain class), same reason
/// BugCheckRecord/WerReport are records - item 56/57's enrichment attaches a few more fields via a
/// `with` expression rather than hand-copying every property forward.
/// </summary>
public sealed record ApplicationCrashEvent
{
    public DateTime TimeCreated { get; init; }

    public string? AppName { get; init; }
    public string? AppVersion { get; init; }
    public string? AppTimeStamp { get; init; }
    public string? ModName { get; init; }
    public string? ModVersion { get; init; }
    public string? ModTimeStamp { get; init; }
    public string? ExceptionCode { get; init; }

    /// <summary>Item 51: plain-English name for ExceptionCode (e.g. "0xC0000005
    /// (STATUS_ACCESS_VIOLATION)") - computed once at parse time via NtStatusLookup (round 15,
    /// item 30), the same NTSTATUS/SEH-exception-code table a bugcheck parameter already reuses.
    /// Always "Unknown" rather than null so the crash grid's Reason column never binds to an
    /// empty cell.</summary>
    public string ExceptionCodeText { get; init; } = "Unknown";

    public string? Offset { get; init; }
    public int? ProcessId { get; init; }
    public string? ApplicationPath { get; init; }
    public string? ModulePath { get; init; }
    public string? ReportId { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>Item 56: true when ModulePath's own directory lies outside both this event's own
    /// ApplicationPath directory and the Windows system directories - i.e. the crash's faulting
    /// module looks like injected or third-party code, not the app's own binary or a system DLL.
    /// "Quick flag, not a verdict" per CLAUDE.md - see ApplicationCrashService.IsForeignModule for
    /// exactly how conservative this check is. Always false until
    /// ApplicationCrashService.EnrichWithModuleForensics has run (the raw parse from
    /// EventLogService never sets it).</summary>
    public bool IsForeignModule { get; init; }
    public string? ForeignModuleReason { get; init; }

    /// <summary>Item 56: SignatureCheckService's own "Signed"/"Unsigned"/"Unknown" read of
    /// ModulePath, and the certificate's subject (vendor) name when one is present - only
    /// populated for a foreign module (see IsForeignModule); checking every crash's own app/
    /// module signature would just repeat what the Processes tab's own Signature column already
    /// shows for the same file.</summary>
    public string? ModuleSignatureStatus { get; init; }
    public string? ModuleVendor { get; init; }

    /// <summary>Item 57: set when ModulePath matches a known machine-wide injection surface
    /// (AppInit_DLLs, a registered shell extension, a Winlogon notification package) - see
    /// ApplicationCrashService.LoadInjectionSurfaces. Null when the module isn't foreign, or is
    /// foreign but doesn't match any known surface.</summary>
    public string? InjectionSurfaceNote { get; init; }
}

/// <summary>Round 17, item 52: ApplicationCrashEvent rows grouped by executable name (case-
/// insensitive) - count, first/last seen, how many distinct modules have faulted, and the mean
/// time between crashes. Sits above the raw grid on the Stability tab's "Application crashes"
/// card, answering "which app is actually broken on this PC" at a glance. A pure derived
/// aggregation over the already-parsed crash list - see StabilityViewModel's leaderboard builder,
/// no new event-log query.</summary>
public sealed class AppCrashLeaderboardRow
{
    public string ExecutableName { get; init; } = string.Empty;
    public int Count { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }
    public int DistinctFaultingModules { get; init; }

    /// <summary>Null when fewer than two crashes were seen for this executable (a mean of one
    /// interval isn't meaningful) - otherwise the average gap, in hours, between consecutive
    /// crash timestamps across the whole lookback window.</summary>
    public double? MeanTimeBetweenCrashesHours { get; init; }

    public string MeanTimeBetweenCrashesText => MeanTimeBetweenCrashesHours switch
    {
        null => "n/a (only seen once)",
        { } h when h < 1 => $"{h * 60:0}m",
        { } h when h < 48 => $"{h:0.0}h",
        { } h => $"{h / 24:0.0}d",
    };
}

/// <summary>
/// Round 17, item 53: one Application-log event 1002 ("Application Hang") - a separate fault
/// class from a crash (event 1000), with its own signature shape (no exception code/offset).
/// ProcessName/Version/ProcessId/ApplicationPath/ReportId are read from the event's own formatted
/// message text (regex, like several other legacy-provider parses in EventLogService already do)
/// since the raw property layout for this event isn't a documented, versioned contract.
/// HangType/HangSignature are NOT guessed from that same undocumented property layout - instead
/// they're joined in from the matching WER AppHang report (item 46's existing scan) by Report Id,
/// whose own EventType (AppHangB1/AppHangXProcB1/AppHangTransient) and Sig[]/DynamicSig[] fields
/// are a far more reliable source for "what kind of hang was this" - see
/// WerReportService.JoinApplicationHangEvents.
/// </summary>
public sealed class ApplicationHangEvent
{
    public DateTime TimeCreated { get; init; }
    public string? ProcessName { get; init; }
    public string? Version { get; init; }
    public string? ProcessId { get; init; }
    public string? ApplicationPath { get; init; }
    public string? ReportId { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>Item 53: joined from the matching WER AppHang report's own EventType - null when
    /// no matching report was found in the current WER scan (see
    /// WerReportService.JoinApplicationHangEvents).</summary>
    public string? HangType { get; init; }

    /// <summary>Item 53: joined from the matching WER AppHang report's computed/real bucket
    /// signature - the closest thing WER has to a "hang signature". Null when no matching report
    /// was found.</summary>
    public string? HangSignature { get; init; }
}

/// <summary>
/// Round 17, item 54: one ".NET Runtime" provider event (1026 "unhandled exception", or 1023,
/// which carries the same Application/Framework Version/Exception Info shape) - parsed from the
/// event's own formatted message text via regex, the same "well-known, stable text shape, not a
/// positional property contract" tradeoff several other legacy-provider parses in this app already
/// make (e.g. EventLogService's shutdown-timeline User32 1074 parse). Dramatically more actionable
/// than the generic "Application Error, faulting module clr.dll" an unhandled managed exception
/// otherwise shows up as on the plain crash grid.
/// </summary>
public sealed class ManagedExceptionEvent
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string? ApplicationName { get; init; }
    public string? FrameworkVersion { get; init; }
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }

    /// <summary>Up to the first 5 "   at ..." managed stack frames captured from the event's own
    /// message text - empty when the event didn't carry a stack trace (some hosts/configurations
    /// suppress it).</summary>
    public List<string> TopStackFrames { get; init; } = new();

    public string TopFrameText => TopStackFrames.Count > 0 ? TopStackFrames[0] : "(no stack trace captured)";

    public string Message { get; init; } = string.Empty;
}

/// <summary>Round 17, item 55: ManagedExceptionEvent rows clustered by (ExceptionType, top stack
/// frame) with a count - the same "flat list -> grouped cluster" shape WerReportService.
/// GroupByBucket already uses for WER crash buckets, applied to managed exceptions instead so
/// "System.NullReferenceException at Contoso.App.Foo.Bar()" shows once with a count instead of
/// forcing a scroll through a dozen near-identical entries. A pure derived aggregation over the
/// already-parsed ManagedExceptionEvent list, no new query.</summary>
public sealed class ManagedExceptionClusterRow
{
    public string ExceptionType { get; init; } = "Unknown";
    public string TopFrame { get; init; } = "(no stack trace captured)";
    public int Count { get; init; }
    public DateTime LastSeen { get; init; }
    public string? ApplicationName { get; init; }
}
