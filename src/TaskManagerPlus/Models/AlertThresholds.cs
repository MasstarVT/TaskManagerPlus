namespace TaskManagerPlus.Models;

/// <summary>
/// User-configurable threshold-alert settings (#72) - a toast fires once when a metric crosses
/// above its threshold, leveraging the same already-polled data the Summary tab's Health Check
/// card and the logging infrastructure both already read, no new sampling. Persisted the same way
/// ThemeColors is (plain JSON, defaults on a missing/corrupt file).
/// </summary>
public sealed class AlertThresholds
{
    public bool CpuEnabled { get; set; }
    public double CpuPercent { get; set; } = 90;

    public bool MemoryEnabled { get; set; }
    public double MemoryPercent { get; set; } = 90;

    public bool TempEnabled { get; set; }
    public double TempC { get; set; } = 90;

    // #355: low-free-space alerts - not per-drive-letter configurable (there's no per-volume UI
    // for that yet), one shared pair of thresholds checked against every fixed volume on each
    // Storage-tab sampler tick, the same "one global figure" shape CpuPercent/MemoryPercent/TempC
    // above already use. Owned/persisted by StorageViewModel (see its PersistAlertThresholds,
    // which merges onto whatever's on disk so it can't clobber a concurrent edit to the Cpu/
    // Memory/Temp fields above that SummaryViewModel owns) rather than SummaryViewModel, since
    // evaluation happens on StorageViewModel's own Performance.Sampled subscription.
    public bool FreeSpacePercentEnabled { get; set; }
    public double FreeSpacePercentThreshold { get; set; } = 10;

    public bool FreeSpaceAbsoluteEnabled { get; set; }
    public double FreeSpaceAbsoluteGbThreshold { get; set; } = 10;

    // Round 18, #364: disk stall detector - a sample where a disk's "Avg. Disk sec/Transfer"
    // exceeds this threshold gets logged to the Storage tab's stall timeline (plus an edge-
    // triggered toast, same "one toast per crossing" shape the thresholds above use). On by
    // default (unlike the opt-in toggles above) since a stall the user isn't watching for is
    // exactly the case this exists to catch. Owned/persisted by StorageViewModel, same reasoning
    // as FreeSpacePercentEnabled/FreeSpacePercentThreshold above.
    public bool DiskStallDetectionEnabled { get; set; } = true;
    public double DiskStallThresholdMs { get; set; } = 500;

    /// <summary>#414: "any process grows more than X MB over Y minutes" - evaluated from the
    /// #402 per-image-name private-bytes slope (ProcessRow.LeakSlopeMbPerHour) projected over
    /// this window, the same straight-line-extrapolation approach #415's growth summary uses.</summary>
    public bool LeakGrowthEnabled { get; set; }
    public double LeakGrowthMb { get; set; } = 200;
    public double LeakGrowthMinutes { get; set; } = 60;

    /// <summary>#414: "any process exceeds N handles" - a flat ceiling on ProcessRow.HandleCount,
    /// independent of the slope-based handle-leak heuristic (#403) so a process that jumps
    /// straight to a huge handle count (rather than climbing steadily) still gets caught.</summary>
    public bool LeakHandleCountEnabled { get; set; }
    public double LeakHandleCountThreshold { get; set; } = 5000;

    public static AlertThresholds Defaults => new();
}
