namespace TaskManagerPlus.Models;

/// <summary>
/// #169-174: models backing the Stability tab's Reliability Monitor cards - Windows' own hourly
/// SystemStabilityIndex (Win32_ReliabilityStabilityMetrics, #169), the full Win32_ReliabilityRecords
/// feed (#170), the WMIEnable disabled-collection check (#172), and the software-change/crash-
/// cluster correlation derived from #170's informational records (#173). See
/// Services/ReliabilityMonitorService for how each is read - every read there degrades to
/// empty/Unknown/hidden on failure rather than fabricating a value, the same rule the rest of this
/// app's WMI/registry reads already follow (CLAUDE.md's "degrade to Unknown/0/hidden").
/// </summary>

/// <summary>#169: one hourly SystemStabilityIndex sample from Win32_ReliabilityStabilityMetrics -
/// Windows' own 1-10 reliability index. Verified live against a real machine while building this:
/// the class is sampled roughly hourly (~24 rows/calendar day), even though Reliability Monitor's
/// own graph only ever shows one point per day - see
/// ReliabilityMonitorService.BuildDailyIndex for how this gets folded down to one value per
/// calendar day so it can share an X axis with the app's own daily Reliability History chart.</summary>
public sealed class ReliabilityStabilitySample
{
    public DateTime TimeGenerated { get; init; }
    public double SystemStabilityIndex { get; init; }
}

/// <summary>#170: this app's own best-effort bucketing of a Win32_ReliabilityRecords row - the WMI
/// class itself carries no severity/category field, only SourceName (effectively the originating
/// event provider/source) and EventIdentifier (that provider's own event ID) - see
/// ReliabilityMonitorService.Classify for exactly how these get bucketed. "Quick flag, not a
/// verdict" applies here the same way it applies to this app's other pattern-matched labels
/// (ClassifyBootType, DescribeConsent, CategorizeChange) - Other is a real, expected bucket for a
/// source/message shape this app doesn't recognize, never hidden.</summary>
public enum ReliabilityRecordCategory
{
    Other,
    Informational,
    Warning,
    Failure,
}

/// <summary>#170: one row of the full Reliability Monitor feed - application failures, Windows
/// failures, miscellaneous failures, warnings, and informational entries (software installs/
/// updates/uninstalls) all come through this one WMI class. This is the one source that puts "you
/// installed X" and "then Y started crashing" in the same list (#170's own framing).</summary>
public sealed class ReliabilityRecordInfo
{
    public DateTime TimeGenerated { get; init; }
    public string SourceName { get; init; } = string.Empty;
    public int EventIdentifier { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public ReliabilityRecordCategory Category { get; init; }

    public string CategoryLabel => Category switch
    {
        ReliabilityRecordCategory.Informational => "Informational",
        ReliabilityRecordCategory.Warning => "Warning",
        ReliabilityRecordCategory.Failure => "Failure",
        _ => "Other",
    };

    /// <summary>#173: set only on an Informational-category row that ReliabilityMonitorService.
    /// CorrelateChangesWithCrashClusters found within its correlation window before a crash cluster
    /// from the unified incident timeline (#137) - null the rest of the time. Explicitly correlation,
    /// not causation, same as #139's "changes shortly before this crash" card.</summary>
    public string? PrecedesCrashClusterNote { get; set; }

    public bool IsFlaggedBeforeCrash => PrecedesCrashClusterNote is not null;
}

/// <summary>#172: whether Reliability Monitor's own WMI aggregation is turned off, read from
/// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Reliability Analysis\WMI\WMIEnable. Verified
/// live while building this: on a stock Windows install the "Reliability Analysis" key doesn't
/// exist at all (Windows' own default is collection enabled without needing the key present) - only
/// an explicit value of 0 means "someone turned this off" (common on tuned/debloated systems, per
/// #172's own framing); the key/value being entirely absent is deliberately NOT treated as
/// disabled.</summary>
public sealed class ReliabilityAnalysisStatus
{
    public bool KeyExists { get; init; }
    public int? WmiEnableValue { get; init; }

    public bool IsCollectionDisabled => WmiEnableValue == 0;

    public string SummaryText => IsCollectionDisabled
        ? "Reliability Monitor's data collection is turned off on this PC (WMIEnable=0) - it isn't recording anything new."
        : "Reliability Monitor's data collection looks enabled on this PC.";
}
