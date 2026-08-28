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

/// <summary>#153: one provider's event count from a tracerpt summary.txt. #154 adds
/// <see cref="PercentOfTotal"/>, filled in by <c>EtwTraceService.ParseTracerptSummary</c> once the
/// full list is known (never guessed from a single row in isolation) - purely derived from the
/// existing summary parse, no new ETL parsing.</summary>
public sealed class TracerptProviderCount
{
    public string ProviderName { get; set; } = "";
    public long EventCount { get; set; }

    /// <summary>#154: this provider's share of the trace's total event count, 0-100. Left at 0
    /// (never guessed) when the total couldn't be determined.</summary>
    public double PercentOfTotal { get; set; }
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

    /// <summary>#153's raw per-provider counts - sorted descending by <c>EtwTraceService.
    /// ParseTracerptSummary</c> (with <see cref="TracerptProviderCount.PercentOfTotal"/> filled in)
    /// specifically so #154's "what dominated this trace" ranked bar list can bind straight to this
    /// without re-sorting in the ViewModel or XAML.</summary>
    public List<TracerptProviderCount> EventsPerProvider { get; set; } = new();

    public string? SummaryTextPath { get; set; }
    public string? HtmlReportPath { get; set; }
    public string RawSummaryText { get; set; } = "";

    /// <summary>#154: a one-line "what dominated this trace" callout, derived purely from the
    /// already-sorted <see cref="EventsPerProvider"/> - null (hidden in the UI) unless the top
    /// provider's share is large enough to be worth calling out by name (&gt;= 20% of all events)
    /// rather than always narrating the top row even when the distribution is basically flat.</summary>
    public string? DominantProviderSummary => EventsPerProvider.Count > 0 && EventsPerProvider[0].PercentOfTotal >= 20
        ? $"\"{EventsPerProvider[0].ProviderName}\" accounted for {EventsPerProvider[0].PercentOfTotal:0.#}% of all events in this trace."
        : null;
}

/// <summary>#157: one growth-watchdog sample of the in-progress capture's on-disk footprint - see
/// <c>EtwTraceService.SampleCaptureSize</c>'s remarks for exactly what "footprint" means while WPR
/// is still recording (its own %TEMP%\WPR scratch folder, not the final output path, until Stop
/// merges it). <see cref="Available"/> is false when the size couldn't be determined at all -
/// distinct from a genuine 0-byte reading, so the UI can show "Unknown" rather than a misleading
/// "0 B" during that state.</summary>
public sealed class EtwCaptureSizeSample
{
    public DateTime SampledAtUtc { get; set; }
    public long TotalBytes { get; set; }
    public bool Available { get; set; }
}

/// <summary>
/// #158: one user-editable capture recipe - a tool + its raw command-line arguments, so a user can
/// add their own wpr/logman/netsh trace recipes (e.g. `netsh trace start scenario=NetConnection
/// capture=yes`) without touching code, by editing etw-recipes.json directly or through this
/// panel's Add/Remove controls. Deliberately a different, more general shape than #150's
/// <see cref="EtwCapturePreset"/> (which only ever assembles `-start &lt;WprProfile&gt;` arguments
/// for <c>EtwTraceService.StartCaptureAsync</c>'s own specific profile-list builder) rather than a
/// replacement for it - reshaping the already-wired preset UI/ViewModel/service signature around
/// this more general shape would have been a much larger refactor for comparatively little gain, so
/// this exists alongside the WPR-profile presets as a separate, tool-agnostic "run any recorded
/// command line" list instead. <see cref="Arguments"/> may contain the literal token
/// <c>%OUTPUT%</c>, substituted with the chosen (quoted) output path before running - see
/// <c>EtwTraceService.RunRecipeAsync</c>.
/// </summary>
public sealed class EtwCaptureRecipe
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Tool { get; set; } = "wpr.exe";
    public string Arguments { get; set; } = "";

    /// <summary>Rough hand-entered "expected size/minute" estimate, same "labelled as an estimate,
    /// never measured live" spirit as #150's EtwCapturePreset.DiskEstimate.</summary>
    public string ExpectedSizePerMinute { get; set; } = "";

    /// <summary>True for the seeded defaults - purely cosmetic (a "Built-in" tag in the UI), never
    /// used to block editing/removing: once loaded, a built-in recipe is just as editable/removable
    /// as a user's own, the same "edit/duplicate/delete like your own" precedent #108's saved event
    /// filters already established for their own built-in starter presets.</summary>
    public bool IsBuiltIn { get; set; }
}

/// <summary>etw-recipes.json root - same shape as every other settings file in this app (a plain
/// serializable object with a static <see cref="Defaults"/> factory), loaded/saved by
/// EtwRecipeSettingsService. The built-ins below are seeded from #150's own five WPR scenario
/// presets (translated into their equivalent literal `wpr -start ... -filemode` command lines, so
/// the same intent is expressed once instead of duplicated as a second hardcoded preset list) plus
/// one netsh trace example, matching #158's own suggested starter set.</summary>
public sealed class EtwRecipeSettings
{
    public List<EtwCaptureRecipe> Recipes { get; set; } = new();

    public static EtwRecipeSettings Defaults => new()
    {
        Recipes = new List<EtwCaptureRecipe>
        {
            new()
            {
                Name = "Stutter / UI hangs",
                Description = "CPU, GPU, desktop composition, and disk I/O activity - use this when the desktop or an app visibly freezes or stutters.",
                Tool = "wpr.exe",
                Arguments = "-start CPU -start GPU -start DesktopComposition -start DiskIO -filemode",
                ExpectedSizePerMinute = "~50-150 MB/min (estimate)",
                IsBuiltIn = true,
            },
            new()
            {
                Name = "Disk latency",
                Description = "Disk I/O, file I/O, and minifilter (antivirus/backup driver) activity - use this for slow file opens/saves or a disk that feels sluggish.",
                Tool = "wpr.exe",
                Arguments = "-start DiskIO -start FileIO -start Minifilter -filemode",
                ExpectedSizePerMinute = "~30-100 MB/min (estimate)",
                IsBuiltIn = true,
            },
            new()
            {
                Name = "Network",
                Description = "Networking I/O activity - use this for slow downloads, dropped connections, or high network-related CPU use.",
                Tool = "wpr.exe",
                Arguments = "-start Network -filemode",
                ExpectedSizePerMinute = "~10-40 MB/min (estimate)",
                IsBuiltIn = true,
            },
            new()
            {
                Name = "Power / idle drain",
                Description = "Power usage and CPU activity - use this to investigate a laptop that drains its battery unusually quickly at idle.",
                Tool = "wpr.exe",
                Arguments = "-start Power -start CPU -filemode",
                ExpectedSizePerMinute = "~10-30 MB/min (estimate)",
                IsBuiltIn = true,
            },
            new()
            {
                Name = "Registry",
                Description = "Registry read/write activity - use this for slowdowns caused by heavy registry access (some antivirus/backup tools, some installers).",
                Tool = "wpr.exe",
                Arguments = "-start Registry -filemode",
                ExpectedSizePerMinute = "~10-30 MB/min (estimate)",
                IsBuiltIn = true,
            },
            new()
            {
                Name = "Network connection capture (netsh)",
                Description = "A netsh trace scenario capture of network connection activity - a lighter-weight built-in alternative to the WPR Network preset above.",
                Tool = "netsh.exe",
                Arguments = "trace start scenario=NetConnection capture=yes tracefile=%OUTPUT%",
                ExpectedSizePerMinute = "~5-20 MB/min (estimate)",
                IsBuiltIn = true,
            },
        },
    };
}

/// <summary>#159: one leftover trace artifact found under a known WMI/WPR/vendor trace-log
/// location - size and last-write time only (no attempt to determine whether it's still "in use";
/// a session actively writing to one of these would normally show up as a running session in #146
/// instead). <see cref="IsOld"/>/<see cref="IsOversized"/> are simple, documented threshold flags
/// ("quick flag, not a verdict") driving the row highlight in the UI, not a claim that the file is
/// definitely safe to delete.</summary>
public sealed class EtwStaleArtifact
{
    public string Path { get; set; } = "";
    public string Location { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }

    public bool IsOld => (DateTime.UtcNow - LastWriteUtc).TotalDays > 30;
    public bool IsOversized => SizeBytes > 200L * 1024 * 1024;
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
