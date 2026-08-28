using System.Runtime.InteropServices;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #225/#228/#234: raw P/Invoke timer-resolution/QPC reads - per CLAUDE.md's "prefer a known tool"
/// rule, this is the documented exception for this chunk (same tier as
/// ForegroundContextService/CpuTopologyService already in this codebase): there is no Windows
/// tool or WMI class that reports the current multimedia-timer resolution or checks QPC against
/// the OS clock, only the undocumented-but-stable ntdll/kernel32 calls below.
///
/// #225: NtQueryTimerResolution reports the system's current/finest/coarsest timer resolution in
/// 100ns units - Windows' documented default is ~15.6ms, and any app (media players, games,
/// Chromium-based browsers, some anti-cheat/DRM stacks) can raise it via timeBeginPeriod for as
/// long as it holds a handle open, at the cost of more frequent CPU wake-ups system-wide (battery
/// drain). #226 identifies who's currently holding a raised request.
///
/// #228: QueryPerformanceFrequency/QueryPerformanceCounter (the QPC clock every frame-time/
/// latency measurement on this whole tab is built on) checked against
/// GetSystemTimePreciseAsFileTime over a short window - on some virtualized/older hardware QPC can
/// occasionally be a less-stable clock source than the OS's own corrected system clock, which
/// would quietly invalidate every microsecond-scale reading elsewhere in this tab. On-demand only
/// (CheckQpcDriftAsync) - a ~1-2s measurement, deliberately not part of the light tick.
///
/// #234: no documented API/tool reports actual timer-coalescing state directly, so the
/// CoalescingInferenceText this produces is an explicitly-labeled threshold inference from the
/// current resolution, never a real state read.
/// </summary>
public static class TimerResolutionService
{
    // Windows' well-documented default system timer resolution (ms) when nothing has raised it -
    // used only to phrase the "vs. default" text, not as a hard cutoff.
    private const double DefaultResolutionMs = 15.6;

    // A raised resolution below this is "meaningfully" raised for #225's flag / #234's coalescing
    // inference - kept with headroom under the ~15.6ms default so ordinary scheduling jitter can't
    // false-positive this.
    private const double RaisedThresholdMs = 14.0;

    // A few hundred ppm of noise is expected from the two clock reads not landing at exactly the
    // same instant each side of the window (microsecond-scale skew against a ~1-2s window); only a
    // meaningfully larger deviation is flagged as a possibly-unstable QPC source.
    private const double DriftStableThresholdPpm = 500.0;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint maximumTime, out uint minimumTime, out uint currentTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryPerformanceFrequency(out long lpFrequency);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    [DllImport("kernel32.dll")]
    private static extern void GetSystemTimePreciseAsFileTime(out long lpSystemTimeAsFileTime);

    /// <summary>#225/#234: a cheap syscall - safe to call every light-timer tick (rides
    /// ResponsivenessViewModel.SampleLight - see its own remarks).</summary>
    public static TimerResolutionInfo Read()
    {
        try
        {
            int status = NtQueryTimerResolution(out uint maxTime, out uint minTime, out uint curTime);
            if (status != 0 || curTime == 0)
            {
                return new TimerResolutionInfo
                {
                    StatusText = $"Unknown - NtQueryTimerResolution failed (status 0x{status:X8}).",
                };
            }

            double currentMs = curTime / 10000.0;
            // NtQueryTimerResolution's "MinimumTime" is the smallest interval value = the finest
            // achievable resolution; its "MaximumTime" is the largest interval value = the
            // coarsest (un-raised default) resolution - the naming refers to the time value, not
            // to "best"/"worst".
            double finestMs = minTime / 10000.0;
            double coarsestMs = maxTime / 10000.0;
            double wakeups = currentMs > 0 ? 1000.0 / currentMs : 0;
            bool raised = currentMs < RaisedThresholdMs;

            string statusText = raised
                ? $"Raised to {currentMs:0.###} ms (default is ~{DefaultResolutionMs:0.#} ms) - about {wakeups:0} timer wake-up(s)/sec system-wide instead of the default ~64/sec. A permanently raised resolution costs battery and points at some running app holding it open (see \"Who raised it\" below)."
                : $"{currentMs:0.###} ms - at (or close to) the Windows default of ~{DefaultResolutionMs:0.#} ms, about {wakeups:0} wake-up(s)/sec.";

            return new TimerResolutionInfo
            {
                CurrentMs = currentMs,
                FinestMs = finestMs,
                CoarsestMs = coarsestMs,
                WakeupsPerSec = wakeups,
                IsRaised = raised,
                StatusText = statusText,
                // #234: threshold-based inference only - no documented API/tool reports actual
                // coalescing state.
                CoalescingInferenceText = raised
                    ? $"Inference (not a direct read): a resolution this fine ({currentMs:0.###} ms, below ~15 ms) generally defeats Windows' own timer coalescing for anything waiting on this clock, trading battery life for responsiveness."
                    : "Inference (not a direct read): at the default resolution, Windows' own timer coalescing is very likely active for background/periodic timers.",
            };
        }
        catch (Exception ex)
        {
            return new TimerResolutionInfo { StatusText = $"Unknown - {ex.Message}" };
        }
    }

    /// <summary>#228: on-demand only - see ResponsivenessViewModel.CheckQpcCommand. Deliberately not
    /// part of the light tick since this is a blocking-ish short measurement.</summary>
    public static async Task<QpcDriftResult> CheckQpcDriftAsync(TimeSpan window, CancellationToken ct)
    {
        try
        {
            if (!QueryPerformanceFrequency(out long freq) || freq <= 0)
                return new QpcDriftResult { StatusText = "Unknown - QueryPerformanceFrequency failed." };

            QueryPerformanceCounter(out long qpcStart);
            GetSystemTimePreciseAsFileTime(out long ftStart);

            await Task.Delay(window, ct);

            QueryPerformanceCounter(out long qpcEnd);
            GetSystemTimePreciseAsFileTime(out long ftEnd);

            double qpcElapsedSec = (qpcEnd - qpcStart) / (double)freq;
            double ftElapsedSec = (ftEnd - ftStart) / 10_000_000.0; // FILETIME is 100ns units.
            if (ftElapsedSec <= 0 || qpcElapsedSec <= 0)
                return new QpcDriftResult { FrequencyHz = freq, StatusText = $"QPC frequency: {freq:N0} Hz. Drift check: measurement window was too short to compare." };

            double ratio = qpcElapsedSec / ftElapsedSec;
            double driftPpm = (ratio - 1.0) * 1_000_000.0;
            bool stable = Math.Abs(driftPpm) < DriftStableThresholdPpm;

            return new QpcDriftResult
            {
                FrequencyHz = freq,
                DriftPpm = driftPpm,
                LooksStable = stable,
                StatusText = stable
                    ? $"QPC frequency: {freq:N0} Hz. Drift check: OK ({driftPpm:+0.#;-0.#} ppm vs. the system clock over {window.TotalSeconds:0.#}s)."
                    : $"QPC frequency: {freq:N0} Hz. Drift check: {driftPpm:+0.#;-0.#} ppm off the system clock over {window.TotalSeconds:0.#}s - quick flag, not a verdict: a drifting QPC would make every microsecond-scale reading on this tab suspect.",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new QpcDriftResult { StatusText = $"Unknown - {ex.Message}" };
        }
    }
}
