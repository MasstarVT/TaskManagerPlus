namespace TaskManagerPlus.Models;

/// <summary>
/// Round 20, item 89: which already-loaded crash/fault source a <see cref="CrashTimelineRow"/> was
/// projected from - drives the unified timeline's own source-filter chips. Deliberately mirrors
/// exactly the list item 89 itself names ("bugchecks, live kernel events, WER reports, application
/// crashes/hangs, service failures, TDRs and WHEA errors") plus UnexpectedShutdown (round 13's own
/// distinct card, already a first-class source on this tab) - not every other flavor of event this
/// tab tracks (e.g. managed-exception events are a strict subset of the same Application-crash
/// event stream, so folding them in separately would just double-count the same fault under a
/// different name).
/// </summary>
public enum CrashTimelineSourceType
{
    Bugcheck,
    LiveKernelReport,
    WerCrash,
    WerHang,
    ApplicationCrash,
    ApplicationHang,
    ServiceFailure,
    Tdr,
    Whea,
    UnexpectedShutdown,
}

public enum CrashTimelineSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// Round 20, item 89: one row of the unified crash timeline - the single common shape every
/// per-source model (MinidumpInfo, LiveKernelReportInfo, WerReport, ApplicationCrashEvent, ...) is
/// mapped into by CrashCorrelationService.BuildTimeline, so one chronological list can merge
/// sources that otherwise live on entirely separate cards. Replaces the need to read four separate
/// cards to reconstruct a bad afternoon.
/// </summary>
public sealed class CrashTimelineRow
{
    public DateTime Timestamp { get; init; }
    public CrashTimelineSourceType SourceType { get; init; }
    public string SourceTypeText { get; init; } = string.Empty;
    public CrashTimelineSeverity Severity { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? Detail { get; init; }

    /// <summary>Round 20, item 90: the clustering key CrashCorrelationService.BuildClusters groups
    /// this row under - null for source types item 90 doesn't cluster at all (service failures,
    /// TDRs, WHEA errors, unexpected shutdowns aren't kernel bugchecks or WER-bucketed user-mode
    /// faults, so there's no meaningful signature to group them by).</summary>
    public string? ClusterKey { get; init; }
}

/// <summary>
/// Round 20, item 90: one distinct "problem" - kernel faults clustered by (bugcheck code + blamed
/// module + a coarse matching-parameter shape), user-mode faults clustered by WER's own bucket key
/// (a real WER bucket ID when one is present, else the same locally-computed signature the Error
/// reports card already falls back to - see WerReport.EffectiveBucketKey). "Three distinct
/// problems" instead of thirty rows. See CrashCorrelationService.BuildClusters.
/// </summary>
public sealed class CrashCluster
{
    public string ClusterKey { get; init; } = string.Empty;
    public bool IsKernelFault { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Count { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }
    public string CadenceText { get; init; } = string.Empty;
    public List<CrashTimelineRow> Occurrences { get; init; } = new();
}

/// <summary>Round 20, item 91: one bucket of the "how long did the machine survive before crashing
/// this time" histogram.</summary>
public sealed class UptimeAtCrashBucket
{
    public string Label { get; init; } = string.Empty;
    public int Count { get; init; }
}

/// <summary>
/// Round 20, item 91: mean time between failures plus the longest crash-free streak, computed over
/// whichever machine-level crash timestamps CrashCorrelationService.BuildUptimeHistogramAndMtbf was
/// given (bugchecks + live kernel reports + bugcheck-caused unexpected shutdowns, deduplicated) -
/// see that method's own remarks for exactly what counts as "a crash" here. Null MTBF/streak fields
/// mean there wasn't enough data (fewer than two distinct crash timestamps) to compute a meaningful
/// figure, not a fabricated zero.
/// </summary>
public sealed class MtbfSummary
{
    public int CrashCount { get; init; }
    public int BootCount { get; init; }
    public TimeSpan? MeanTimeBetweenFailures { get; init; }
    public TimeSpan? LongestCrashFreeStreak { get; init; }

    /// <summary>Crashes for which no preceding boot marker could be found at all (a boot before
    /// this app's own lookback window, or a boot that was never logged) - not counted anywhere in
    /// the histogram above, called out separately rather than silently dropped.</summary>
    public int UnknownUptimeCount { get; init; }
}

/// <summary>
/// Round 20, items 92-94: one "what changed" entry found in the 48 hours before a cluster's first
/// occurrence - a driver install (item 92), a Windows Update install (item 93, KB number in
/// Detail when one was found) or a third-party application install (item 94). Computed on demand,
/// per cluster, only when the user actually expands that cluster's own "What changed before this
/// started" panel - see CrashCorrelationService.BuildWhatChangedAsync.
/// </summary>
public sealed class WhatChangedEntry
{
    public DateTime Timestamp { get; init; }

    /// <summary>"Driver install" / "Windows Update" / "Application install".</summary>
    public string Category { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;
    public string? Detail { get; init; }

    /// <summary>Which underlying query found this entry (e.g. "setupapi.dev.log",
    /// "Microsoft-Windows-UserPnp/20001", "Win32_QuickFixEngineering") - shown as a small provenance
    /// hint, since the same real-world change is sometimes visible from more than one of these
    /// sources with slightly different precision.</summary>
    public string Source { get; init; } = string.Empty;
}

/// <summary>Round 20, items 92-94: the full "what changed" result for one cluster - Entries empty
/// with ComputedOk true genuinely means nothing changed in the window (a real, useful answer),
/// while ComputedOk false means the scan itself couldn't run at all (see ErrorText).</summary>
public sealed class WhatChangedResult
{
    public bool ComputedOk { get; init; } = true;
    public string? ErrorText { get; init; }
    public List<WhatChangedEntry> Entries { get; init; } = new();
}

/// <summary>Round 20, item 95: one point pulled out of this app's own CSV telemetry (a manual log
/// or the always-on rolling buffer) in the two minutes before a crash timestamp - see
/// CrashCorrelationService.BuildLogCorrelationAsync / LogReplayService.</summary>
public sealed class CrashLogCorrelationPoint
{
    public DateTime Timestamp { get; init; }
    public double CpuPercent { get; init; }
    public double RamPercent { get; init; }
    public double? TemperatureC { get; init; }
    public double? PowerW { get; init; }
}

/// <summary>Round 20, item 95: HasCoverage false means no logged CSV (manual log or rolling
/// buffer) found on disk actually spans this crash's own timestamp - a common, expected case
/// (logging simply wasn't running at the time), not a failure.</summary>
public sealed class CrashLogCorrelationResult
{
    public bool HasCoverage { get; init; }
    public string? SourceFileName { get; init; }
    public List<CrashLogCorrelationPoint> Points { get; init; } = new();
    public string StatusText { get; init; } = string.Empty;
}
