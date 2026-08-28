using System.Diagnostics;
using System.Threading;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #695: N-thread deterministic compute loop whose output checksum is known in advance - each
/// worker thread runs an LCG (see DeterministicWorkload) seeded from its own thread index, stepping
/// it for as many iterations as fit in the run's duration. "Known in advance" is literal here: the
/// expected final state for whatever iteration count a thread actually reached is computed via
/// DeterministicWorkload.Advance's O(log N) jump-ahead - a closed-form-style composition, not a
/// second execution of the loop - so a checksum mismatch means the *worker's own arithmetic*
/// produced a different answer than the trusted independent reference, the direct signature of an
/// unstable overclock/undervolt flipping a bit mid-calculation.
///
/// Unlike ClockStretchService's single-core calibration benchmark (which pins itself to one logical
/// core via Process.ProcessorAffinity to get a clean, comparable ops/sec figure), this deliberately
/// does NOT pin threads - spinning >= core-count worker threads under sustained load already
/// saturates every core through the normal OS scheduler, which is the whole point of a
/// multi-thread torture test.
/// </summary>
public static class CpuTortureTestService
{
    // Iterations between elapsed-time/cancellation checks - large enough that Stopwatch.Elapsed
    // reads aren't a meaningful fraction of the work, small enough that Stop/an abort from
    // StressTestSafetyMonitor takes effect within a few tens of milliseconds, not seconds.
    private const int BatchSize = 4_000_000;

    public static async Task<CpuTortureResult> RunAsync(int threadCount, TimeSpan duration, CancellationToken ct)
    {
        threadCount = Math.Max(1, threadCount);
        var results = new CpuTortureThreadResult?[threadCount];
        string? faultMessage = null;

        var sw = Stopwatch.StartNew();
        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int threadIndex = t;
            tasks[t] = Task.Run(() =>
            {
                try
                {
                    results[threadIndex] = RunWorker(threadIndex, duration, ct);
                }
                catch (Exception ex)
                {
                    // A hard fault (e.g. an unexpected arithmetic/runtime exception mid-loop) is an
                    // immediate, unambiguous fail per #695 - first one wins, the rest are still
                    // reported per-thread below.
                    Interlocked.CompareExchange(ref faultMessage, $"Thread {threadIndex}: {ex.Message}", null);
                }
            });
        }

        await Task.WhenAll(tasks);
        sw.Stop();

        var completedResults = results.Where(r => r is not null).Select(r => r!).OrderBy(r => r.ThreadIndex).ToList();
        bool allPassed = faultMessage is null && completedResults.Count == threadCount && completedResults.All(r => r.Passed);

        return new CpuTortureResult
        {
            Completed = faultMessage is null,
            AllThreadsPassed = allPassed,
            FaultMessage = faultMessage,
            ThreadResults = completedResults,
            TotalIterations = completedResults.Sum(r => r.Iterations),
            ActualDuration = sw.Elapsed,
        };
    }

    private static CpuTortureThreadResult RunWorker(int threadIndex, TimeSpan duration, CancellationToken ct)
    {
        ulong seed = DeterministicWorkload.SeedFor(threadIndex);
        ulong state = seed;
        long iterations = 0;
        var sw = Stopwatch.StartNew();

        while (true)
        {
            for (int b = 0; b < BatchSize; b++)
                state = DeterministicWorkload.Step(state);
            iterations += BatchSize;

            if (ct.IsCancellationRequested || sw.Elapsed >= duration) break;
        }

        ulong expected = DeterministicWorkload.Advance(seed, iterations);
        return new CpuTortureThreadResult
        {
            ThreadIndex = threadIndex,
            Iterations = iterations,
            Expected = expected,
            Actual = state,
            Passed = expected == state,
        };
    }
}
