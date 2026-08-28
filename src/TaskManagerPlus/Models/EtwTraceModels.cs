namespace TaskManagerPlus.Models;

/// <summary>
/// #146: one provider enabled on a running ETW session, parsed out of that session's own
/// `logman query "&lt;name&gt;" -ets` detail dump - the per-provider block under a session lists
/// Name/Provider Guid/Level/KeywordsAny at minimum. Kept on <see cref="EtwSessionRow"/> so #148's
/// "who's listening" cross-reference (<c>EtwTraceService.FindListeningSessions</c>) never has to
/// shell out a second time - it just filters whatever #146's session-detail read already parsed.
/// </summary>
public sealed class EtwSessionProviderInfo
{
    public string Name { get; set; } = "";
    public string Guid { get; set; } = "";
    public string Level { get; set; } = "Unknown";
    public string Keywords { get; set; } = "Unknown";
}

/// <summary>
/// #146: one running kernel/user ETW session, from `logman query -ets` (Name/Type/Status columns)
/// merged with a per-session detail query (`logman query "&lt;name&gt;" -ets`) for provider count,
/// buffer size/count, the real-time flag, the log file, and - the actual point of this card -
/// events lost and buffers lost. A session losing events is a genuine and usually invisible cause
/// of background CPU/disk load (a provider firing faster than its session's buffers can drain),
/// which is exactly why this exists as its own card rather than being folded into the Processes or
/// Services tabs.
///
/// logman's per-session text output is a real, documented tool but its exact field wording has
/// drifted across Windows releases (unlike its CSV/table list output, which is a stable contract)
/// - so every field below is parsed defensively (a handful of label variants tried per field, see
/// <c>EtwTraceService.ParseSessionDetail</c>) and left at "Unknown"/null rather than guessed when
/// nothing matches. <see cref="RawDetailText"/> always keeps the full unparsed dump around so a
/// user can read the real output for themselves if a parsed field looks wrong - the same
/// "never fabricate, show the raw source" fallback this app already uses for WMI/registry reads
/// that can't be fully trusted to a fixed schema.
/// </summary>
public sealed class EtwSessionRow
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Trace";
    public string Status { get; set; } = "Unknown";

    /// <summary>True once <see cref="EtwTraceService.QuerySessionDetailAsync"/> has filled in the
    /// fields below - the initial `-ets` list only has Name/Type/Status.</summary>
    public bool DetailLoaded { get; set; }

    public int ProviderCount { get; set; }
    public List<EtwSessionProviderInfo> Providers { get; set; } = new();

    public string BufferSizeText { get; set; } = "Unknown";
    public string BufferCountText { get; set; } = "Unknown";
    public bool? IsRealTime { get; set; }
    public string LogFileName { get; set; } = "";

    public long? EventsLost { get; set; }
    public long? BuffersLost { get; set; }

    /// <summary>True when either loss counter is a confirmed nonzero value - drives the row's
    /// "lossy" highlight in the grid. Never true from an unparsed/Unknown value.</summary>
    public bool HasLoss => (EventsLost is > 0) || (BuffersLost is > 0);

    public string RawDetailText { get; set; } = "";
    public string? DetailError { get; set; }
}

/// <summary>
/// #147: one boot-start trace session ("autologger"), read from
/// HKLM\SYSTEM\CurrentControlSet\Control\WMI\Autologger\&lt;name&gt; - these aren't running ETW
/// sessions (they're dormant configuration the kernel reads at boot to start tracing before any
/// service, including this app, could shell out to logman at all), so the registry is the only way
/// to see them outside of a live session; there's no working `logman query autologger` list-all
/// command on current Windows despite older docs suggesting otherwise (verified against a live
/// 10.0.26100 install while building this - see EtwTraceService.ReadAutologgers's remarks).
/// Explains persistent background tracing (DiagTrack, WdiContextLog, SleepStudy, EventLog-* itself,
/// third-party vendor loggers) that silently re-arms on every boot.
/// </summary>
public sealed class AutologgerRow
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string LogFileName { get; set; } = "Unknown";
    public string MaxFileSizeText { get; set; } = "Unknown";
    public string BufferSizeText { get; set; } = "Unknown";
    public List<string> ProviderNames { get; set; } = new();
    public int ProviderCount => ProviderNames.Count;
}

/// <summary>
/// #148: one registered ETW provider from `logman query providers` - name + GUID only. This is a
/// deliberately different, smaller catalog than #113's "Event Providers" panel (which reads
/// message/level/keyword *metadata* for building human-readable event descriptions via
/// System.Diagnostics.Eventing.Reader.ProviderMetadata); this one is logman's own ETW
/// session-registration view, used to answer "is anything tracing this provider right now" via
/// <see cref="EtwProviderSessionUsage"/>. Named "ETW Providers" in the UI (vs. #113's
/// "Event Providers") specifically so the two aren't confused.
/// </summary>
public sealed class EtwProviderRow
{
    public string Name { get; set; } = "";
    public string Guid { get; set; } = "";
}

/// <summary>#148: one running session that has a given provider enabled, plus the level/keywords
/// it enabled it at - built by cross-referencing #146's already-parsed per-session provider lists
/// in memory (<c>EtwTraceService.FindListeningSessions</c>), not a separate shell-out. `logman
/// query providers &lt;guid&gt;` itself (verified live) only returns the provider's keyword/level
/// *legend* and a PID/Image registration list - it does not report which sessions have the
/// provider enabled, so the #146 session data is the only real source for "who's listening".</summary>
public sealed class EtwProviderSessionUsage
{
    public string SessionName { get; set; } = "";
    public string Level { get; set; } = "Unknown";
    public string Keywords { get; set; } = "Unknown";
}

/// <summary>#150: one named scenario-capture preset - the plain-English name/description/rough
/// per-minute disk estimate are all static, hand-written strings (labelled as an estimate in the
/// UI), not measured live.</summary>
public sealed class EtwCapturePreset
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string DiskEstimate { get; set; } = "";
    public string[] WprProfiles { get; set; } = Array.Empty<string>();
}

/// <summary>#152: parsed `wpr -status profiles collectors -details` output - just enough to drive
/// the "another capture is already running" guard and the rescue button, not a full collector
/// dump (RawText carries the rest for anyone who wants to read it).</summary>
public sealed class WprStatusResult
{
    public bool IsRecording { get; set; }
    public List<string> ActiveProfiles { get; set; } = new();
    public string RawText { get; set; } = "";
    public string? ErrorMessage { get; set; }
}

/// <summary>#153: one provider's event count from a tracerpt summary.txt.</summary>
public sealed class TracerptProviderCount
{
    public string ProviderName { get; set; } = "";
    public long EventCount { get; set; }
}

/// <summary>#153: the parsed result of running tracerpt against a finished capture - event counts
/// per provider, lost events, and trace duration read out of summary.txt, plus paths to the raw
/// dumpfile.xml/summary.txt/report.html tracerpt produced so the report can be opened directly.</summary>
public sealed class TracerptSummary
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public TimeSpan? TraceDuration { get; set; }
    public string TraceDurationText => TraceDuration is { } d ? d.ToString(d.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss") : "Unknown";
    public long TotalEvents { get; set; }
    public long LostEvents { get; set; }
    public List<TracerptProviderCount> EventsPerProvider { get; set; } = new();
    public string? SummaryTextPath { get; set; }
    public string? HtmlReportPath { get; set; }
    public string RawSummaryText { get; set; } = "";
}

/// <summary>
/// #151: the small persisted marker that survives the reboot a boot trace was armed for - written
/// to <c>AppPaths.GetPath("etw-boot-trace-pending.json")</c> right after `wpr -addboot` succeeds,
/// and read back once at startup by EtwCaptureViewModel's constructor to decide whether to show
/// the "collect your boot trace" reminder banner. Same small-JSON-under-AppPaths shape every other
/// persisted setting in this app uses (see PollIntervalSettingsService), just with a narrower
/// purpose - this is state about one pending action, not a user preference.
/// </summary>
public sealed class BootTraceMarker
{
    public string EtlPath { get; set; } = "";
    public DateTime ArmedAtUtc { get; set; }
}
