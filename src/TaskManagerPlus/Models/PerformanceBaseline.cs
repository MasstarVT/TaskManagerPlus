namespace TaskManagerPlus.Models;

/// <summary>
/// #950: a richer, opt-in "performance baseline" capture - extends the existing #93/#94 system
/// snapshot (installed software/services/startup, see <see cref="SystemSnapshot"/>) with measured
/// performance figures (WinSAT sub-scores, last boot duration, idle CPU/RAM/disk-latency) plus a
/// hardware fingerprint (#953). Deliberately wraps a <see cref="SystemSnapshot"/> rather than
/// duplicating its software/services/startup fields, so BaselineService can hand two baselines'
/// embedded Snapshot straight to SnapshotService.Diff - the same "what changed" diff
/// SummaryViewModel's existing baseline-vs-current/A-vs-B flows already use, not a second,
/// incompatible diff engine.
///
/// Saved as its own JSON file under AppPaths.SettingsDirectory\Baselines\ (see BaselineService) -
/// a distinct, heavier, explicitly opt-in capture from the lightweight ad-hoc snapshot files the
/// Summary tab's "Save snapshot" button writes wherever the user points a SaveFileDialog.
/// </summary>
public sealed class PerformanceBaseline
{
    public DateTime CapturedAt { get; init; } = DateTime.Now;

    /// <summary>#955: optional label describing what's being captured - e.g. "Before: installing
    /// XYZ driver" for a change-window before/after pair - null for a routine/scheduled baseline.</summary>
    public string? Label { get; init; }

    /// <summary>#952: true when this baseline was captured by the automatic weekly schedule rather
    /// than a manual "Capture full baseline" click - purely informational (shown in the baseline
    /// list), doesn't change how it's used in a diff/trend.</summary>
    public bool WasAutomatic { get; init; }

    public SystemSnapshot Snapshot { get; init; } = new();
    public HardwareFingerprint Fingerprint { get; init; } = new();

    // #950: WinSAT sub-scores (Win32_WinSAT) - null when this machine has never run a formal
    // WinSAT assessment and this particular capture wasn't allowed to trigger one itself (see
    // WinSatService's remarks).
    public double? WinSatCpuScore { get; init; }
    public double? WinSatMemoryScore { get; init; }
    public double? WinSatDiskScore { get; init; }
    public double? WinSatOverallScore { get; init; }

    /// <summary>#950: the most recent boot's total duration in milliseconds, reusing
    /// BootPerformanceService's own adaptive Diagnostics-Performance/event-100 read - null when no
    /// such event was found in the last 30 days (see BootPerformanceService.ReadLatest's remarks).</summary>
    public double? LastBootDurationMs { get; init; }

    // #950/#957: idle-gated measurements - see Services/IdleRollingTracker's remarks for how
    // "sustained idle" is tracked.
    public double? IdleCpuPercent { get; init; }
    public double? IdleRamCommittedGb { get; init; }
    public double? IdleDiskLatencyMs { get; init; }

    /// <summary>#957: false means CPU hadn't been sustained-idle for the required window when this
    /// baseline was captured (a user-triggered capture that didn't wait for idle conditions) - the
    /// idle-gated fields above still hold real measured numbers (a manual capture is never blocked/
    /// refused, only flagged - see BaselineViewModel.CaptureAsync), but the Baselines UI must render
    /// this baseline's idle metrics as "captured under load - not directly comparable" rather than
    /// silently trending/diffing them like a genuinely idle capture. The automatic weekly capture
    /// (#952) only ever fires once the idle tracker already confirms sustained idle, so this is
    /// always true for WasAutomatic baselines.</summary>
    public bool WasIdleAtCapture { get; init; }
}

/// <summary>#953: a lightweight hardware identity captured alongside every baseline - CPU name,
/// total RAM, the set of installed disk models, and GPU name(s), pulled from already-queried
/// SystemSpecsViewModel/PerformanceViewModel state (no new WMI reads of its own). Comparing two
/// baselines' fingerprints lets the Baselines UI warn loudly when a regression/trend number spans
/// a hardware change (a new drive, more RAM, a GPU swap, ...) rather than silently presenting a
/// misleading before/after. Note: no disk serial number is available anywhere else in this app's
/// SystemSpecs model (DiskInfo has no Serial field - Win32_DiskDrive.SerialNumber isn't currently
/// read by SystemSpecsService), so the disk side of this fingerprint is the *set* of disk model
/// strings, not true per-drive serials - still enough to catch "a drive was added/removed/replaced".</summary>
public sealed class HardwareFingerprint
{
    public string CpuName { get; init; } = string.Empty;
    public double RamTotalGb { get; init; }
    public List<string> DiskModels { get; init; } = new();
    public List<string> GpuNames { get; init; } = new();

    /// <summary>True when both fingerprints look like the same machine's hardware. Order-independent
    /// on the disk/GPU lists (a re-enumeration can return devices in a different order) and RAM is
    /// compared with a small tolerance (rounding differences between two independent reads of the
    /// same physical total) - otherwise an exact match, so even one differing disk/GPU counts as a
    /// hardware change.</summary>
    public bool MatchesHardware(HardwareFingerprint other)
    {
        if (!string.Equals(CpuName, other.CpuName, StringComparison.OrdinalIgnoreCase)) return false;
        if (Math.Abs(RamTotalGb - other.RamTotalGb) > 0.5) return false;
        if (!SameSet(DiskModels, other.DiskModels)) return false;
        if (!SameSet(GpuNames, other.GpuNames)) return false;
        return true;
    }

    private static bool SameSet(List<string> a, List<string> b)
    {
        var sa = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var sb = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        return sa.SetEquals(sb);
    }
}

/// <summary>One metric's before/after comparison - shared by #951's regression card, #955's
/// before/after change-window diff, and #956's exported report, so the same row shape backs all
/// three (they differ only in *which* two baselines are being compared and the surrounding label).</summary>
public sealed class BaselineMetricComparison
{
    public string MetricName { get; init; } = string.Empty;
    public double? BeforeValue { get; init; }
    public double? AfterValue { get; init; }
    public string Unit { get; init; } = string.Empty;

    /// <summary>True when a smaller number is the "better" direction for this metric (boot time,
    /// idle CPU%, disk latency) - false when a bigger number is better (a WinSAT score). Used to
    /// decide whether a given percent change reads as a regression or an improvement.</summary>
    public bool LowerIsBetter { get; init; } = true;

    public double? PercentChange => BeforeValue is > 0 && AfterValue is not null
        ? (AfterValue.Value - BeforeValue.Value) / BeforeValue.Value * 100.0
        : null;

    public bool IsRegression => PercentChange is { } pct && (LowerIsBetter ? pct > 0.5 : pct < -0.5);
    public bool IsImprovement => PercentChange is { } pct && (LowerIsBetter ? pct < -0.5 : pct > 0.5);

    /// <summary>#951's "boot is 42% slower than your 2026-03-14 baseline: 24 s → 34 s" wording -
    /// empty when either side is missing (nothing to compare) so the UI can simply skip the row.</summary>
    public string SummaryText
    {
        get
        {
            if (BeforeValue is not { } b || AfterValue is not { } a || PercentChange is not { } pct) return string.Empty;
            if (Math.Abs(pct) < 0.5) return $"{MetricName} is about the same as the baseline ({FormatValue(b)}{Unit} → {FormatValue(a)}{Unit})";

            bool durationLike = Unit.Trim() is "s" or "ms";
            string direction = IsRegression
                ? (durationLike ? "slower" : "worse")
                : (durationLike ? "faster" : "better");
            return $"{MetricName} is {Math.Abs(pct):0}% {direction} than the baseline: {FormatValue(b)}{Unit} → {FormatValue(a)}{Unit}";
        }
    }

    private static string FormatValue(double v) => v >= 100 ? v.ToString("0") : v.ToString("0.0");
}
