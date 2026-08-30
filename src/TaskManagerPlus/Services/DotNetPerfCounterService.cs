using System.Diagnostics;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #274/#275/#276/#277: one shared per-process .NET CLR performance-counter resolver, feeding four
/// consumers off one instance-name sweep per tick - the same "one enumeration, several consumers"
/// shape DpcModuleMapService.GetModuleMap() already established for this app.
///
/// ".NET CLR LocksAndThreads(&lt;instance&gt;)" and ".NET CLR Memory(&lt;instance&gt;)" publish one
/// instance per managed process, named "processname" or "processname#N" for multiple instances of
/// the same exe - the well-established .NET convention for resolving one of these instances back to
/// a PID is to read the matching "Process(&lt;instance&gt;)\ID Process" counter (the "Process" and
/// ".NET CLR *" categories publish under the same instance-name scheme for a given process).
/// #1076: sampled as three whole-category CategorySampler reads per tick (locks, memory, and
/// Process for pid resolution) - never one PerformanceCounter object per value, since every raw
/// NextValue() re-reads its entire category from the provider (see CategorySampler's remarks).
///
/// #274: Contention Rate / sec, Total # of Contentions, Current Queue Length.
/// #275: % Time in GC, # Gen 0/1/2 Collections, # Induced GC, Allocated Bytes/sec - aggregate perf-
/// counter figures only, not a real per-pause millisecond duration (that needs the optional ETW
/// deep mode on Microsoft-Windows-DotNETRuntime's GC keyword, not implemented in this build - an
/// honestly-labeled partial per the task's own allowance). See ResponsivenessViewModel's GC-pause
/// card for how these are surfaced.
/// #276: GC mode ("Server"/"Workstation") and concurrent/background GC state, read once per newly-
/// seen managed pid (then cached forever, like ProcessMonitorService's own CommandLine/ParentPid
/// caches - environment variables can't change after launch) via the existing
/// ProcessEnvironmentService's PEB walk - reused directly, not re-derived. A heuristic, not a
/// certainty: a process configured via its own runtimeconfig.json instead won't show up here.
/// #277: thread-pool-starvation hint - a rolling per-pid history of "# of current logical Threads"
/// alongside the queue-length/contention signal, flagged when the logical-thread count has climbed
/// steadily over the last several samples while the queue/contention signal stays elevated.
///
/// Every field independently degrades (an absent counter reads 0/Unknown for that one field; a
/// pid that never resolves to a published instance simply never appears in the returned
/// dictionary) - callers show a blank/hidden state for a missing entry, never a fabricated one.
/// </summary>
public sealed class DotNetPerfCounterService : IDisposable
{
    private const string LocksCategory = ".NET CLR LocksAndThreads";
    private const string MemoryCategory = ".NET CLR Memory";
    private const string ProcessCategory = "Process";

    private const int StarvationHistoryLength = 5;
    private const int StarvationMinSamples = 4;

    public bool CategoriesAvailable { get; }

    // #1076: one CategorySampler per category, each read as a whole exactly once per Sample() call -
    // the shared replacement for the old one-PerformanceCounter-per-(category|counter|instance)
    // dictionary, whose every NextValue() re-read its entire category from the provider (the same
    // anti-pattern the 82e4bf2 perf pass removed elsewhere; see CategorySampler's remarks). The
    // "Process" sampler exists only to resolve instance names to PIDs via "ID Process" - one
    // category read per tick instead of one per managed instance. (ProcessPerfCounterService's
    // Processes-tick read of the same category can't be reused from here without new plumbing
    // through files owned elsewhere - a possible future consolidation.)
    private readonly CategorySampler _locksSampler = new(LocksCategory);
    private readonly CategorySampler _memorySampler = new(MemoryCategory);
    private readonly CategorySampler _processSampler = new(ProcessCategory);

    // #276: environment-derived GC mode/concurrency, cached forever per pid the first time it's
    // seen as managed - see the class remarks on why this never needs to be re-read.
    private readonly Dictionary<int, (string GcMode, string GcConcurrent)> _gcConfigCache = new();

    // #277: rolling per-pid logical-thread-count/queue-length history for the starvation heuristic.
    private readonly Dictionary<int, Queue<double>> _logicalThreadHistory = new();
    private readonly Dictionary<int, Queue<double>> _queueSignalHistory = new();

    public DotNetPerfCounterService()
    {
        try
        {
            CategoriesAvailable = PerformanceCounterCategory.Exists(LocksCategory) && PerformanceCounterCategory.Exists(MemoryCategory);
        }
        catch
        {
            CategoriesAvailable = false;
        }
    }

    /// <summary>Samples every currently-published managed-process instance, keyed by resolved PID.
    /// Safe to call from a background thread (Task.Run) - three whole-category reads per call,
    /// independent of how many managed processes exist.</summary>
    public Dictionary<int, DotNetProcessCounters> Sample()
    {
        var result = new Dictionary<int, DotNetProcessCounters>();
        if (!CategoriesAvailable) return result;

        try
        {
            // #1076: exactly three whole-category reads per tick, regardless of how many managed
            // processes are running. Rate counters (Contention Rate / sec, % Time in GC, Allocated
            // Bytes/sec) compute against the previous tick's sample kept inside each sampler -
            // NextValue()-equivalent semantics, with the same first-sight-reads-0 behavior the old
            // freshly-constructed counters had.
            _locksSampler.Tick();
            _memorySampler.Tick();
            _processSampler.Tick();

            var instanceNames = _locksSampler.InstanceNames("# of current logical Threads")
                .Where(n => !n.Equals("_Global_", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var seenPids = new HashSet<int>();

            foreach (var inst in instanceNames)
            {
                // The ".NET CLR *" categories publish under the same "processname#N" instance-name
                // scheme as "Process", so "Process(<instance>)\ID Process" resolves the PID.
                int pid = (int)_processSampler.Value("ID Process", inst);
                if (pid <= 0) continue;
                seenPids.Add(pid);

                double contentionRate = _locksSampler.Value("Contention Rate / sec", inst);
                double totalContentions = _locksSampler.Value("Total # of Contentions", inst);
                double queueLength = _locksSampler.Value("Current Queue Length", inst);
                double logicalThreads = _locksSampler.Value("# of current logical Threads", inst);

                double pctTimeInGc = _memorySampler.Value("% Time in GC", inst);
                double gen0 = _memorySampler.Value("# Gen 0 Collections", inst);
                double gen1 = _memorySampler.Value("# Gen 1 Collections", inst);
                double gen2 = _memorySampler.Value("# Gen 2 Collections", inst);
                double inducedGc = _memorySampler.Value("# Induced GC", inst);
                double allocBytesPerSec = _memorySampler.Value("Allocated Bytes/sec", inst);

                var (gcMode, gcConcurrent) = ResolveGcConfig(pid);
                bool starvationHint = ComputeStarvationHint(pid, logicalThreads, queueLength, contentionRate);

                result[pid] = new DotNetProcessCounters
                {
                    ContentionRatePerSec = Math.Round(contentionRate, 2),
                    TotalContentions = (long)totalContentions,
                    CurrentQueueLength = (int)queueLength,
                    PercentTimeInGc = Math.Round(Math.Clamp(pctTimeInGc, 0, 100), 1),
                    Gen0Collections = (int)gen0,
                    Gen1Collections = (int)gen1,
                    Gen2Collections = (int)gen2,
                    InducedGcCount = (int)inducedGc,
                    AllocatedBytesPerSec = allocBytesPerSec,
                    GcModeText = gcMode,
                    GcConcurrentText = gcConcurrent,
                    IsThreadPoolStarvationSuspect = starvationHint,
                };
            }

            PruneHistory(seenPids);
        }
        catch
        {
            return new Dictionary<int, DotNetProcessCounters>();
        }

        return result;
    }

    private void PruneHistory(HashSet<int> seenPids)
    {
        foreach (var pid in _logicalThreadHistory.Keys.ToList())
            if (!seenPids.Contains(pid)) _logicalThreadHistory.Remove(pid);
        foreach (var pid in _queueSignalHistory.Keys.ToList())
            if (!seenPids.Contains(pid)) _queueSignalHistory.Remove(pid);
        foreach (var pid in _gcConfigCache.Keys.ToList())
            if (!seenPids.Contains(pid)) _gcConfigCache.Remove(pid);
    }

    /// <summary>#276: reuses ProcessEnvironmentService's existing PEB walk directly, once per newly-
    /// seen managed pid, then caches forever (environment variables never change after launch) - a
    /// per-tick PEB walk for every managed process would be the exact "expensive read on a per-tick
    /// timer" CLAUDE.md's on-demand rule warns against, so this narrows that cost to "once per
    /// managed process for the lifetime of this app session".</summary>
    private (string GcMode, string GcConcurrent) ResolveGcConfig(int pid)
    {
        if (_gcConfigCache.TryGetValue(pid, out var cached)) return cached;

        string gcMode = "Unknown";
        string gcConcurrent = "Unknown";
        try
        {
            var entries = ProcessEnvironmentService.Read(pid);
            foreach (var entry in entries)
            {
                int eq = entry.IndexOf('=');
                if (eq <= 0) continue;
                string name = entry[..eq];
                string value = entry[(eq + 1)..];

                if (name.Equals("DOTNET_gcServer", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("COMPlus_gcServer", StringComparison.OrdinalIgnoreCase))
                    gcMode = ParseBoolFlag(value) switch { true => "Server", false => "Workstation", null => gcMode };

                if (name.Equals("DOTNET_gcConcurrent", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("COMPlus_gcConcurrent", StringComparison.OrdinalIgnoreCase))
                    gcConcurrent = ParseBoolFlag(value) switch { true => "Concurrent", false => "Non-concurrent", null => gcConcurrent };
            }

            // No explicit DOTNET_gcServer/COMPlus_gcServer means Workstation GC's own documented
            // default applies - a real, positive fact (not a guess) whenever the environment block
            // itself was actually readable (i.e. didn't return one of ProcessEnvironmentService's
            // own "(couldn't...)" placeholder lines).
            if (gcMode == "Unknown" && entries.Count > 0 && !entries[0].StartsWith('('))
                gcMode = "Workstation (default)";
            if (gcConcurrent == "Unknown" && entries.Count > 0 && !entries[0].StartsWith('('))
                gcConcurrent = "Concurrent (default)";
        }
        catch
        {
            // leave both "Unknown" - best-effort, never fabricated.
        }

        var result = (gcMode, gcConcurrent);
        _gcConfigCache[pid] = result;
        return result;
    }

    private static bool? ParseBoolFlag(string value)
    {
        value = value.Trim();
        if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    /// <summary>#277: a rolling, bounded (StarvationHistoryLength-sample) window per pid - flags a
    /// steady climb in logical-thread count over the last few samples while the queue-length/
    /// contention signal stays elevated, the outward signature of blocking calls starving the
    /// thread pool (the pool keeps injecting new threads because none of the existing ones are
    /// coming back). Deliberately simple/sampled per the coordinator's "don't overengineer"
    /// guidance - a hint, not a diagnosis.</summary>
    private bool ComputeStarvationHint(int pid, double logicalThreads, double queueLength, double contentionRate)
    {
        var threadHistory = _logicalThreadHistory.TryGetValue(pid, out var th) ? th : (_logicalThreadHistory[pid] = new Queue<double>());
        threadHistory.Enqueue(logicalThreads);
        while (threadHistory.Count > StarvationHistoryLength) threadHistory.Dequeue();

        double queueSignal = queueLength + contentionRate;
        var queueHistory = _queueSignalHistory.TryGetValue(pid, out var qh) ? qh : (_queueSignalHistory[pid] = new Queue<double>());
        queueHistory.Enqueue(queueSignal);
        while (queueHistory.Count > StarvationHistoryLength) queueHistory.Dequeue();

        if (threadHistory.Count < StarvationMinSamples) return false;

        var samples = threadHistory.ToArray();
        bool steadyClimb = true;
        for (int i = 1; i < samples.Length; i++)
        {
            if (samples[i] < samples[i - 1]) { steadyClimb = false; break; }
        }
        bool netIncrease = samples[^1] > samples[0] + 1; // at least a couple more threads than a few samples ago
        bool queueStillElevated = queueHistory.Average() > 0.5;

        return steadyClimb && netIncrease && queueStillElevated;
    }

    public void Dispose()
    {
        // #1076: nothing to dispose anymore - CategorySampler holds no PerformanceCounter objects,
        // only the previous tick's samples. Kept so the owning ViewModel's Dispose wiring stands.
    }
}
