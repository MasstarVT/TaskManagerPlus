namespace TaskManagerPlus.Models;

/// <summary>#695-#700: which of the Stress test panel's four test types a run/history entry is.</summary>
public enum StressTestType
{
    CpuTorture,
    MemoryVerify,
    GpuLoad,
    CombinedSoak,
}

/// <summary>
/// #699: user-adjustable pass/fail and safety-abort criteria, persisted to stress-test.json (same
/// small-JSON-file, fail-silently-to-defaults pattern as psu.json/PsuSettingsService). The safety
/// ABORT CHECK ITSELF is not something this settings object can switch off - see
/// StressTestSafetyMonitor's remarks - only where the thresholds sit is adjustable here.
/// </summary>
public sealed class StressTestSettings
{
    /// <summary>The abort-trigger temperature. Zero/negative means "not yet customized by the
    /// user" - StressTestViewModel resolves the effective ceiling from
    /// EnergyThermalsViewModel.CpuThrottlePointReferenceC (5°C of margin below it) the first time
    /// a run needs one, falling back to 90°C when no reported/inferred throttle-point reference
    /// exists at all. Once resolved, the effective value is written back here so it stays stable
    /// across runs unless the user changes it.</summary>
    public double TempCeilingC { get; set; }

    public bool AbortOnWheaDelta { get; set; } = true;
    public bool AbortOnTdr { get; set; } = true;
    public bool RequireSustainedClockAtOrAboveBase { get; set; } = true;
    public int DefaultDurationSeconds { get; set; } = 60;

    /// <summary>0 means "use Environment.ProcessorCount".</summary>
    public int CpuThreadCount { get; set; }

    public double MemoryTestShareOfFreePercent { get; set; } = 25.0;
}

/// <summary>#700: one sampled point of a run's full trace - temperature, clock, power, throttle
/// percentage, fan RPM, a representative rail voltage, and the WHEA/TDR event counts observed
/// since the run started (a running total, not a per-sample delta, so a chart of this column reads
/// as a step function at the moment either occurs). Any field can be null when that particular
/// signal wasn't available at sample time (no sensor, or a test type - GPU-only - that doesn't
/// drive CPU clock/power at all) - never fabricated.</summary>
public sealed class StressTestTraceSample
{
    public DateTime Timestamp { get; init; }
    public double? TempC { get; init; }
    public double? ClockGhz { get; init; }
    public double? PackagePowerW { get; init; }
    public double? ThrottlePercent { get; init; }
    public double? FanRpm { get; init; }
    public double? RailVoltage { get; init; }

    /// <summary>#697: the busiest GPU adapter's overall utilization percent (GpuViewModel's own
    /// per-adapter TotalUtilizationPercent, max across adapters) - a live check that the GPU load
    /// test's render loop is actually generating load, not just a status label saying it's
    /// running.</summary>
    public double? GpuUtilizationPercent { get; init; }

    public int WheaEventsSinceStart { get; init; }
    public int TdrEventsSinceStart { get; init; }
}

/// <summary>#695: one worker thread's checksum-verification result - see CpuTortureTestService's
/// remarks for how Expected is computed independently of the thread's own O(N) loop (an LCG
/// jump-ahead closed form, not a second execution of the loop).</summary>
public sealed class CpuTortureThreadResult
{
    public int ThreadIndex { get; init; }
    public bool Passed { get; init; }
    public long Iterations { get; init; }
    public ulong Expected { get; init; }
    public ulong Actual { get; init; }
}

public sealed class CpuTortureResult
{
    public bool Completed { get; init; }
    public bool AllThreadsPassed { get; init; }

    /// <summary>Non-null on a hard fault (an unhandled exception from a worker thread) - a
    /// checksum mismatch alone leaves this null; AllThreadsPassed already carries that.</summary>
    public string? FaultMessage { get; init; }

    public List<CpuTortureThreadResult> ThreadResults { get; init; } = new();
    public long TotalIterations { get; init; }
    public TimeSpan ActualDuration { get; init; }

    public bool Passed => Completed && AllThreadsPassed && FaultMessage is null;
}

/// <summary>#696: one verified-word mismatch, with its byte offset into the tested region - the
/// direct "here's exactly where the bad bit is" diagnostic passive monitoring can't produce.</summary>
public sealed class MemoryVerifyMismatch
{
    public string PatternName { get; init; } = string.Empty;
    public long ByteOffset { get; init; }
    public ulong Expected { get; init; }
    public ulong Actual { get; init; }
}

public sealed class MemoryVerifyResult
{
    public bool Completed { get; init; }
    public bool Skipped { get; init; }
    public string? SkipReason { get; init; }
    public long BytesTested { get; init; }

    /// <summary>Capped to the first 50 - a genuinely bad memory region can produce thousands of
    /// mismatches; the point is knowing it happened and roughly where, not an exhaustive dump.</summary>
    public List<MemoryVerifyMismatch> Mismatches { get; init; } = new();

    public string? FaultMessage { get; init; }
    public TimeSpan ActualDuration { get; init; }

    public bool Passed => Completed && !Skipped && Mismatches.Count == 0 && FaultMessage is null;
}

/// <summary>#699: the explicit pass/fail criteria breakdown for one run - "no computation mismatch,
/// no WHEA corrected-error delta, no TDR, sustained clock at or above base, and peak temperature
/// below the throttle point." *Checked flags mean "this criterion applies to this test type" - a
/// GPU-only run never checks computation, and a memory-only run never checks sustained clock, so
/// those show as "N/A" rather than a false pass/fail.</summary>
public sealed class StressTestCriteria
{
    public bool ComputationChecked { get; init; }
    public bool ComputationOk { get; init; }
    public bool NoWheaDelta { get; init; }
    public bool NoTdr { get; init; }
    public bool ClockChecked { get; init; }
    public bool SustainedClockAtOrAboveBase { get; init; }
    public bool PeakTempBelowThrottlePoint { get; init; }
    public bool Aborted { get; init; }
    public string? AbortReason { get; init; }

    public bool OverallPass => !Aborted
        && (!ComputationChecked || ComputationOk)
        && NoWheaDelta && NoTdr
        && (!ClockChecked || SustainedClockAtOrAboveBase)
        && PeakTempBelowThrottlePoint;

    // Plain display-text properties (not just raw bools) so StressTestPanel.xaml can bind each row
    // directly without needing a multi-value converter for the *Checked/*Ok pairing.
    public string ComputationResultText => !ComputationChecked ? "N/A" : ComputationOk ? "Pass" : "Fail";
    public string WheaResultText => NoWheaDelta ? "Pass" : "Fail";
    public string TdrResultText => NoTdr ? "Pass" : "Fail";
    public string ClockResultText => !ClockChecked ? "N/A" : SustainedClockAtOrAboveBase ? "Pass" : "Fail";
    public string TempResultText => PeakTempBelowThrottlePoint ? "Pass" : "Fail";
}

/// <summary>#700: the full result of one stress-test run - everything the report/history services
/// need, assembled by StressTestViewModel once a run finishes (normally or aborted).</summary>
public sealed class StressTestRunResult
{
    public StressTestType TestType { get; init; }
    public DateTime StartedAt { get; init; }
    public TimeSpan RequestedDuration { get; init; }
    public TimeSpan ActualDuration { get; init; }
    public int ThreadCount { get; init; }
    public double EffectiveTempCeilingC { get; init; }
    public double? ThrottlePointReferenceC { get; init; }

    public List<StressTestTraceSample> Trace { get; init; } = new();
    public StressTestCriteria Criteria { get; init; } = new();
    public CpuTortureResult? CpuResult { get; init; }
    public MemoryVerifyResult? MemoryResult { get; init; }

    public double? PeakTempC { get; init; }
    public double? AvgClockGhz { get; init; }
    public double? PeakPowerW { get; init; }
    public double? PeakFanRpm { get; init; }

    public bool Passed => Criteria.OverallPass;
}

/// <summary>#700: one persisted summary row per run (stress-test-history.json) - the run-to-run
/// comparison data. Same load/append/cap-and-save JSON shape as GpuHangHistoryService/
/// PciLinkHistoryService. Deliberately a flat summary, not the full trace (that's exported
/// separately, on demand, via StressTestReportService) - this is the small always-kept record that
/// makes "same test, 12°C hotter and 400 MHz lower than three months ago" possible without needing
/// every past run's full trace sitting on disk.</summary>
public sealed class StressTestHistoryEntry
{
    public DateTime Timestamp { get; init; }
    public StressTestType TestType { get; init; }
    public double DurationSeconds { get; init; }
    public bool Passed { get; init; }
    public string? AbortReason { get; init; }
    public double? PeakTempC { get; init; }
    public double? AvgClockGhz { get; init; }
    public double? PeakPowerW { get; init; }
    public double? PeakFanRpm { get; init; }
    public int ThreadCount { get; init; }
}
