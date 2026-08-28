using System.Text;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #294/#295: pure scoring math over already-computed inputs - no I/O, no service references, so
/// it's easy to reason about (and re-derive by hand) even without a test project in this repo. The
/// caller (ResponsivenessViewModel) is responsible for gathering each factor's current value from
/// whichever prior chunk's service/collection already produced it; this class only turns those
/// numbers into a 0-100 score plus a plain-English "what's driving it" explanation.
///
/// Every "penalty" below is a 0 (no problem) - 100 (worst this heuristic tracks) contribution for
/// one factor, using a documented-but-informal threshold (never a Microsoft-documented figure -
/// see each method's remarks), the same "quick flag, not a verdict" tier as
/// StandbyThrashingSuspected/InterruptStormDetected elsewhere in this domain. The final score is
/// 100 minus the *average* penalty across only the factors that were actually available - a
/// factor with no data point contributes nothing to the average in either direction, rather than
/// being scored as 0 (perfect) or 100 (worst), per CLAUDE.md's "degrade to Unknown, never
/// fabricate" rule.
/// </summary>
public static class ResponsivenessScoreService
{
    public const double GoodThreshold = 80;
    public const double FairThreshold = 50;

    public static string BandFor(double score) => score >= GoodThreshold ? "Good" : score >= FairThreshold ? "Fair" : "Poor";

    // ----- #294: per-process score --------------------------------------------------------------

    /// <summary>One process's already-gathered inputs for #294 - each nullable field is null when
    /// that factor genuinely has no data for this process (never a fabricated 0), see the remarks
    /// on each field for exactly what "no data" means for that factor.</summary>
    public sealed record ProcessScoreInputs(
        /// <summary>#236: the slowest recent SendMessageTimeout round-trip (ms) across this
        /// process's own top-level window(s) - null when this process owns no top-level window
        /// HungWindowService tracks (most background/service processes), not when the round-trip
        /// was simply fast.</summary>
        double? MessagePumpResponseMs,
        /// <summary>#235/#237: share (0-1) of this process's tracked top-level windows that are
        /// currently hung, and how long (seconds) the worst of them has been hung continuously -
        /// both null together under the same "no tracked window" condition as MessagePumpResponseMs.</summary>
        double? HungShare,
        double? HungForSeconds,
        /// <summary>#260-270: share (0-1) of this process's threads sitting in the Ready/
        /// DeferredReady scheduler state right now (wants to run, isn't yet scheduled - the
        /// CPU-starvation signal, distinct from a thread just idly Waiting on something) - null
        /// when the last system-wide scheduler sweep found no threads at all for this pid (sweep
        /// failed, or the process exited between samples).</summary>
        double? ReadyThreadRatio,
        /// <summary>#278/#279 fallback: this process's approximate (soft+hard combined - Windows
        /// exposes no per-process hard-fault-only counter) page-fault rate. Never null once
        /// PageFaultService's top-process list has loaded at least once this session - a process
        /// outside that top-15 list genuinely has a low rate relative to its peers, so it's scored
        /// as 0 rather than excluded (see ResponsivenessViewModel.GetProcessResponsivenessScore's
        /// remarks for why this one factor differs from the others here).</summary>
        double? PageFaultRatePerSec,
        /// <summary>#275: DotNetPerfCounterService's %-Time-in-GC reading for this process, 0-100 -
        /// null for a non-managed process, or one whose .NET CLR counters aren't published, never a
        /// fabricated 0 for a native process (per the item's own explicit instruction).</summary>
        double? PercentTimeInGc);

    /// <summary>Returns null only when literally none of the five factors above have any data for
    /// this pid (e.g. a process the app briefly glimpsed between two ticks) - the caller shows
    /// "—" in that case rather than a misleading 100.</summary>
    public static ProcessResponsivenessScore? ComputeProcessScore(ProcessScoreInputs inputs)
    {
        var factors = new List<(string Name, double Penalty)>();
        var excluded = new List<string>();

        // #236: a 250ms cap is HungWindowService's own SendMessageTimeout ceiling (see
        // HungWindowRow.ResponseMs's remarks) - a round-trip pinned at the cap scores the max
        // penalty, matching "hung" in spirit even if IsHung's own separate check didn't also fire.
        if (inputs.MessagePumpResponseMs is { } respMs)
            factors.Add(("Message-pump latency", Clamp01To100(respMs / 250.0)));
        else
            excluded.Add("Message-pump latency (no top-level window tracked for this process)");

        // #237: half from "what fraction of this process's windows are hung right now", half from
        // "how long has the worst one been hung" (capped at 10s) - two views of the same signal,
        // averaged so neither alone dominates.
        if (inputs.HungShare is { } share)
        {
            double durationPenalty = inputs.HungForSeconds is { } s ? Clamp01To100(s / 10.0) : 0;
            factors.Add(("Hung-window time", Clamp0To100(share * 100.0 * 0.5 + durationPenalty * 0.5)));
        }
        else
        {
            excluded.Add("Hung-window time (no top-level window tracked for this process)");
        }

        // A rough rule of thumb, not a documented Windows figure: a process with half or more of
        // its threads sitting Ready (wants the CPU, isn't getting it) is under real scheduling
        // pressure.
        if (inputs.ReadyThreadRatio is { } ratio)
            factors.Add(("Thread scheduling pressure", Clamp01To100(ratio / 0.5)));
        else
            excluded.Add("Thread scheduling data (last scheduler sweep found no threads for this process)");

        // 500 faults/sec is well above what a healthy idle-ish process shows on this counter (see
        // PageFaultService's remarks) - not a documented threshold, just a "clearly a lot" anchor.
        if (inputs.PageFaultRatePerSec is { } pf)
            factors.Add(("Page-fault rate", Clamp01To100(pf / 500.0)));

        if (inputs.PercentTimeInGc is { } gc)
            factors.Add(("GC pause time", Clamp0To100(gc)));
        else
            excluded.Add("GC pause time (not a managed process, or its .NET CLR counters aren't published)");

        if (factors.Count == 0) return null;

        double avgPenalty = factors.Average(f => f.Penalty);
        double score = Clamp0To100(100.0 - avgPenalty);
        var worst = factors.OrderByDescending(f => f.Penalty).First();

        return new ProcessResponsivenessScore
        {
            Score = score,
            WorstFactorName = worst.Name,
            TooltipText = BuildProcessTooltip(score, factors, excluded),
        };
    }

    private static string BuildProcessTooltip(double score, List<(string Name, double Penalty)> factors, List<string> excluded)
    {
        var sb = new StringBuilder();
        sb.Append("Responsiveness score: ").Append(score.ToString("0")).AppendLine("/100 (composite heuristic, not a verdict).");
        sb.AppendLine("Higher is better - lower means more signal that this process is contributing to system lag.");
        sb.AppendLine("Factors used:");
        foreach (var (name, penalty) in factors.OrderByDescending(f => f.Penalty))
            sb.Append("  - ").Append(name).Append(": ").Append(penalty.ToString("0")).AppendLine("/100 impact");
        if (excluded.Count > 0)
        {
            sb.AppendLine("Not available for this process:");
            foreach (var e in excluded) sb.Append("  - ").AppendLine(e);
        }
        return sb.ToString().TrimEnd();
    }

    // ----- #295: system-wide responsiveness index -----------------------------------------------

    /// <summary>System-wide inputs for #295 - see each field's remarks for which are "always
    /// available" vs. gated behind an opt-in measurement session from an earlier chunk.</summary>
    public sealed record SystemScoreInputs(
        /// <summary>#260: always available - PerformanceViewModel samples Processor Queue Length
        /// every tick regardless of anything else in this app being armed/running.</summary>
        double ProcessorQueueLength,
        int LogicalProcessorCount,
        /// <summary>#202: DpcLatencyService.HighestDpcUs - null (excluded) unless the DPC/ISR
        /// measurement session (#213) has been started at least once this run; it defaults to 0,
        /// which would otherwise look like a perfect reading rather than "never measured".</summary>
        double? DpcHighestUs,
        double DpcThresholdUs,
        /// <summary>#248: null when DwmCompositionInfo.IsAvailable is false (composition disabled,
        /// a remote-desktop session, or an unsupported DWM_TIMING_INFO layout).</summary>
        double? DroppedMissedFramesPerSec,
        /// <summary>#278: null when HardFaultRateInfo.IsAvailable is false (the perf-counter
        /// category isn't published on this machine).</summary>
        double? HardFaultsPerSec,
        /// <summary>#235: always available - 0 is a legitimate "nothing hung right now" reading,
        /// not a missing signal.</summary>
        int HungWindowCount);

    // Rough "clearly bad" anchors, documented as approximate the same way
    // StandbyDepletedPercentOfRam/HardFaultElevatedPerSec are in ResponsivenessViewModel - not
    // Microsoft-documented thresholds, just a reasonable "this is clearly no longer fine" line.
    private const double RunQueueRatioGoodAt = 1.0;
    private const double RunQueueRatioBadAt = 4.0;
    private const double DroppedFramesBadAtPerSec = 5.0;
    private const double HardFaultsBadAtPerSec = 50.0; // matches ResponsivenessViewModel.HardFaultElevatedPerSec
    private const double HungWindowsBadAtCount = 3.0;

    public static SystemResponsivenessScore ComputeSystemScore(SystemScoreInputs inputs)
    {
        var factors = new List<(string Name, double Penalty)>();
        var excluded = new List<string>();

        double readyPerCore = inputs.LogicalProcessorCount > 0 ? inputs.ProcessorQueueLength / inputs.LogicalProcessorCount : 0;
        double queuePenalty = Clamp0To100((readyPerCore - RunQueueRatioGoodAt) / (RunQueueRatioBadAt - RunQueueRatioGoodAt) * 100.0);
        factors.Add(("Run-queue pressure", queuePenalty));

        if (inputs.DpcHighestUs is { } dpcUs && inputs.DpcThresholdUs > 0)
            factors.Add(("DPC latency", Clamp01To100(dpcUs / inputs.DpcThresholdUs)));
        else
            excluded.Add("DPC latency (the DPC/ISR measurement session hasn't been started this run)");

        if (inputs.DroppedMissedFramesPerSec is { } drop)
            factors.Add(("Dropped composition frames", Clamp01To100(drop / DroppedFramesBadAtPerSec)));
        else
            excluded.Add("Dropped composition frames (DWM composition timing isn't available)");

        if (inputs.HardFaultsPerSec is { } hf)
            factors.Add(("Hard-fault rate", Clamp01To100(hf / HardFaultsBadAtPerSec)));
        else
            excluded.Add("Hard-fault rate (the Memory\\Pages Input/sec counter isn't available)");

        factors.Add(("Hung windows", Clamp01To100(inputs.HungWindowCount / HungWindowsBadAtCount)));

        double avgPenalty = factors.Average(f => f.Penalty);
        double score = Clamp0To100(100.0 - avgPenalty);
        var worst = factors.OrderByDescending(f => f.Penalty).First();
        string band = BandFor(score);

        string status = $"Mostly limited by: {worst.Name}.";
        if (excluded.Count > 0)
            status += $" ({excluded.Count} factor{(excluded.Count == 1 ? "" : "s")} not measured: {string.Join("; ", excluded)}.)";

        return new SystemResponsivenessScore
        {
            HasData = true,
            Score = score,
            Band = band,
            WorstFactorName = worst.Name,
            StatusText = status,
        };
    }

    private static double Clamp0To100(double v) => Math.Clamp(v, 0, 100);
    private static double Clamp01To100(double ratio) => Math.Clamp(ratio * 100.0, 0, 100);
}
