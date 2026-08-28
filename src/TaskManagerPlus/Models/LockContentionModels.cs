namespace TaskManagerPlus.Models;

/// <summary>
/// Data shapes for suggestions.md #271-277 (Lock contention, deadlocks and GC pauses) - Wait Chain
/// Traversal results (#271), the stuck-thread/deadlock heuristic (#272), the optional kernel
/// Synchronization perf-counter category (#273), and the shared .NET CLR perf-counter data behind
/// #274-277 - see WaitChainTraversalService, SchedulerService's stuck-process detection,
/// SynchronizationCountersService, and DotNetPerfCounterService respectively.
/// </summary>

/// <summary>#271: one node in a Wait Chain Traversal result, in chain order - either a thread or the
/// lock object it's blocked on. A flat indented-list rendering (IndentLevel) rather than a real
/// WPF TreeView, per the item's own "your call on presentation" framing.</summary>
public sealed class WaitChainNodeRow
{
    public int IndentLevel { get; init; }
    public bool IsThreadNode { get; init; }
    public string Description { get; init; } = string.Empty;
}

/// <summary>#271: a completed (or failed) wait-chain-traversal result for one thread. IsDeadlock
/// mirrors the WCT API's own isCycle output verbatim - a real cycle detection from Windows itself,
/// not a sampled heuristic (unlike #272's flag below).</summary>
public sealed class WaitChainResult
{
    public bool Success { get; init; }
    public bool IsDeadlock { get; init; }
    public List<WaitChainNodeRow> Nodes { get; init; } = new();
    public string StatusText { get; init; } = string.Empty;
}

/// <summary>#272: one "looks stuck" sample for a process - every thread has been in a Wr* wait
/// state for several consecutive scheduler sweeps with no context-switch activity. Explicitly a
/// sampled inference over a few ticks, same "quick flag, not a verdict" tier as #264's priority-
/// inversion hint - see SchedulerService.DetectStuckProcesses.</summary>
public sealed class StuckProcessHint
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public int ConsecutiveSamples { get; init; }
    public string HintText { get; init; } = string.Empty;
}

/// <summary>#273: the optional kernel "Synchronization" performance-counter category - a rarely-
/// enabled category requiring a boot-time kernel flag, so IsAvailable is false on most machines by
/// default. Per CLAUDE.md's "degrade to hidden, never show zeros as if real" rule, the card this
/// backs is hidden entirely (not shown with zeroed values) when IsAvailable is false - see
/// SynchronizationCountersService.</summary>
public sealed class SynchronizationCountersInfo
{
    public bool IsAvailable { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public double SpinlockAcquiresPerSec { get; init; }
    public double SpinlockContentionsPerSec { get; init; }
    public double ExecResourceContentionsPerSec { get; init; }
}

/// <summary>#274/#275/#276/#277: one managed process's .NET CLR LocksAndThreads/.NET CLR Memory
/// performance-counter reading for the current Processes-tab tick, plus the environment-variable-
/// based GC mode read (#276) and the thread-pool-starvation sampled hint (#277) - see
/// DotNetPerfCounterService.Sample. Only ever produced for a pid that actually resolved to a
/// published ".NET CLR LocksAndThreads" instance; a process with no entry in the dictionary this
/// lives in is either non-managed or the categories aren't available on this machine - never a
/// fabricated/zeroed row for either case.</summary>
public sealed class DotNetProcessCounters
{
    // #274
    public double ContentionRatePerSec { get; init; }
    public long TotalContentions { get; init; }
    public int CurrentQueueLength { get; init; }

    // #275 - aggregate perf-counter figures only (no real per-pause millisecond duration - that
    // needs the optional ETW deep mode, not implemented in this build; see ResponsivenessViewModel's
    // GC-pause-monitor card remarks).
    public double PercentTimeInGc { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public int InducedGcCount { get; init; }
    public double AllocatedBytesPerSec { get; init; }

    // #276 - "Unknown" means the environment block couldn't be read or didn't carry the variable,
    // never a guess. See the class remarks: a process configured via its own runtimeconfig.json
    // instead of an environment variable won't show up here - this is a heuristic, not a certainty.
    public string GcModeText { get; init; } = "Unknown";
    public string GcConcurrentText { get; init; } = "Unknown";

    // #277 - a sampled hint (rising logical-thread count alongside an elevated queue/contention
    // signal over several consecutive ticks), never a diagnosis.
    public bool IsThreadPoolStarvationSuspect { get; init; }
}

/// <summary>#275: one managed process's row in the Responsiveness tab's GC-pause-monitor grid -
/// the same DotNetProcessCounters fields, just paired with the process's name/pid for display
/// (ResponsivenessViewModel reads ProcessesViewModel's already-sampled counters dictionary rather
/// than re-querying, see ResponsivenessViewModel.SampleLight's remarks).</summary>
public sealed class GcProcessRow
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public double PercentTimeInGc { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public int InducedGcCount { get; init; }
    public double AllocatedBytesPerSec { get; init; }
}
