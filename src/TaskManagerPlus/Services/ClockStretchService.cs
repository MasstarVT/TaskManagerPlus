using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>
/// #629: clock-stretching detector - runs a small, deterministic, fixed-iteration integer
/// workload and reports achieved operations/sec. Clock stretching (common under AMD electrical
/// limits and on some laptops) shows a normal *reported* frequency while real per-cycle
/// throughput drops - a discrepancy nothing else in this app can see, since it needs an actual
/// timed workload rather than a counter read. Deliberately no vendor MSR/SMU access (project
/// convention) - this is a benchmark-based inference, not a register read.
///
/// Pinning is done by temporarily restricting this whole process's Process.ProcessorAffinity to
/// one core - the same managed API ProcessControlService.SetAffinity already uses elsewhere in
/// this app, rather than new raw SetThreadAffinityMask P/Invoke. That does mean the UI thread is
/// briefly limited to one core too while the benchmark runs (well under a second), which is why
/// CpuViewModel only calls this from a slow, infrequent timer - never per-tick.
/// </summary>
public static class ClockStretchService
{
    // Fixed, deterministic iteration count so every run does exactly the same amount of integer
    // work - only wall-clock time (and therefore ops/sec) varies between runs. Chosen to land
    // comfortably under a second on any CPU this app targets.
    private const long IterationCount = 150_000_000;

    /// <summary>Runs the fixed workload, best-effort pinned to logical core 0 for the duration,
    /// and always restores the process's prior affinity afterward. Returns null if the workload
    /// couldn't be timed at all (never a fabricated number).</summary>
    public static double? RunMicrobenchmarkOpsPerSecond()
    {
        IntPtr originalAffinity;
        try
        {
            originalAffinity = Process.GetCurrentProcess().ProcessorAffinity;
        }
        catch
        {
            originalAffinity = IntPtr.Zero;
        }

        try
        {
            try { Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)1; }
            catch { /* best-effort pinning only - the benchmark still runs, just not pinned */ }

            long checksum = 0;
            var sw = Stopwatch.StartNew();
            for (long i = 0; i < IterationCount; i++)
                checksum = unchecked(checksum + ((i ^ (checksum << 1)) % 97));
            sw.Stop();

            // Forces the loop above to be observable (defeats dead-code elimination) without any
            // real side effect - this condition is never true in practice.
            if (checksum == long.MinValue) return null;

            double seconds = sw.Elapsed.TotalSeconds;
            return seconds > 0 ? IterationCount / seconds : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { Process.GetCurrentProcess().ProcessorAffinity = originalAffinity; }
            catch { /* best-effort restore */ }
        }
    }
}
