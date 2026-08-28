using System.Diagnostics;
using System.Runtime.InteropServices;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Items 260-265/267: one system-wide per-thread sweep (NtQuerySystemInformation,
/// SystemProcessInformation) shared across every scheduler/thread-wait diagnostic in this chunk -
/// "one syscall, several consumers", the same shape DpcModuleMapService.GetModuleMap() already
/// takes. There is no documented Win32/WMI equivalent that returns every thread on the system in
/// one pass (Process/ProcessThread only covers one process at a time, meaning an N+1 walk), so raw
/// P/Invoke here is the documented exception to CLAUDE.md's "prefer a known tool" rule - same tier
/// as CpuTopologyService/HandleInspectionService/DpcModuleMapService.
///
/// #261: per-process wait-reason breakdown (BuildWaitBreakdown).
/// #262: longest-blocked threads ranked by WaitTime (RankLongestBlocked).
/// #263: per-thread context-switch rate, diffed between successive sweeps, keyed by (pid, tid) -
/// the same stateful-diffing idiom PerCoreDpcService/DwmCompositionService already use elsewhere in
/// this app (ComputeContextSwitchRates).
/// #264: priority-inversion *hint* - a high-priority thread sitting Ready across several
/// consecutive sweeps while a lower-priority thread is actually running (DetectPriorityInversions).
/// Explicitly a sampled heuristic, not a traced one - "quick flag, not a verdict."
/// #265: context-switch storm attribution - aggregates #263's per-thread rates up to the owning
/// process (AttributeByProcess).
///
/// WaitTime is in units of the kernel's clock-interval tick, not milliseconds - Windows doesn't
/// document an exact, guaranteed-stable tick length, so the ~15.625ms conversion used here
/// (ClockTickMs) is a widely-used approximation, not an exact figure; treat WaitSecondsApprox as
/// "roughly how long", not a certified duration.
///
/// Degrades to an empty sweep (never throws) on any NtQuerySystemInformation failure, same as
/// DpcModuleMapService/HandleInspectionService.
/// </summary>
public sealed class SchedulerService
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>Documented-approximate clock-tick length used to convert WaitTime/ContextSwitches'
    /// tick-based counters to real time - see the class remarks. Not exact; good enough for ranking
    /// and rough duration display, which is all this chunk's items need.</summary>
    public const double ClockTickMs = 15.625;

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CLIENT_ID
    {
        public IntPtr UniqueProcess;
        public IntPtr UniqueThread;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_THREAD_INFORMATION
    {
        public long KernelTime;
        public long UserTime;
        public long CreateTime;
        public uint WaitTime;
        public IntPtr StartAddress;
        public CLIENT_ID ClientId;
        public int Priority;
        public int BasePriority;
        public uint ContextSwitches;
        public uint ThreadState;
        public uint WaitReason;
    }

    // Fixed (non-trailing-array) portion of SYSTEM_PROCESS_INFORMATION - the Threads[] array
    // follows immediately at this struct's own size, same "walk past the fixed part" technique
    // DpcModuleMapService/HandleInspectionService use for their own variable-length native tables.
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_INFORMATION_FIXED
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UNICODE_STRING ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public IntPtr UniqueProcessKey;
        public IntPtr PeakVirtualSize;
        public IntPtr VirtualSize;
        public uint PageFaultCount;
        public IntPtr PeakWorkingSetSize;
        public IntPtr WorkingSetSize;
        public IntPtr QuotaPeakPagedPoolUsage;
        public IntPtr QuotaPagedPoolUsage;
        public IntPtr QuotaPeakNonPagedPoolUsage;
        public IntPtr QuotaNonPagedPoolUsage;
        public IntPtr PagefileUsage;
        public IntPtr PeakPagefileUsage;
        public IntPtr PrivatePageCount;
        public long ReadOperationCount;
        public long WriteOperationCount;
        public long OtherOperationCount;
        public long ReadTransferCount;
        public long WriteTransferCount;
        public long OtherTransferCount;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    /// <summary>One thread's scheduling state from a single sweep - the shared raw shape every
    /// #261-265/267 consumer builds its own view from.</summary>
    public sealed record ThreadSnapshot(
        int Pid, string ProcessName, int Tid,
        int ThreadState, int WaitReason, uint WaitTimeTicks,
        uint ContextSwitches, long StartAddress, int Priority, int BasePriority);

    /// <summary>Snapshots every thread on the system in one syscall. Grow-and-retry on
    /// STATUS_INFO_LENGTH_MISMATCH, empty list on any other failure - never throws, matching
    /// DpcModuleMapService.GetModuleMap/HandleInspectionService.ReadSystemHandles.</summary>
    public List<ThreadSnapshot> Sweep()
    {
        int size = 4 << 20; // 4 MB starting guess - this table is much bigger than the module/handle tables
        int fixedSize = Marshal.SizeOf<SYSTEM_PROCESS_INFORMATION_FIXED>();
        int threadSize = Marshal.SizeOf<SYSTEM_THREAD_INFORMATION>();

        for (int attempt = 0; attempt < 8; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out int returnLength);
                if (status == StatusInfoLengthMismatch)
                {
                    size = returnLength > size ? returnLength + 0x20000 : size * 2;
                    continue;
                }
                if (status != 0) return new List<ThreadSnapshot>();

                var list = new List<ThreadSnapshot>(4096);
                IntPtr entry = buffer;
                while (true)
                {
                    var proc = Marshal.PtrToStructure<SYSTEM_PROCESS_INFORMATION_FIXED>(entry);
                    int pid = proc.UniqueProcessId.ToInt32();
                    string name = proc.ImageName.Buffer != IntPtr.Zero && proc.ImageName.Length > 0
                        ? Marshal.PtrToStringUni(proc.ImageName.Buffer, proc.ImageName.Length / 2) ?? string.Empty
                        : (pid == 0 ? "System Idle Process" : string.Empty);

                    IntPtr threadPtr = IntPtr.Add(entry, fixedSize);
                    for (uint t = 0; t < proc.NumberOfThreads; t++)
                    {
                        var th = Marshal.PtrToStructure<SYSTEM_THREAD_INFORMATION>(threadPtr);
                        list.Add(new ThreadSnapshot(
                            pid, name, th.ClientId.UniqueThread.ToInt32(),
                            (int)th.ThreadState, (int)th.WaitReason, th.WaitTime,
                            th.ContextSwitches, th.StartAddress.ToInt64(), th.Priority, th.BasePriority));
                        threadPtr = IntPtr.Add(threadPtr, threadSize);
                    }

                    if (proc.NextEntryOffset == 0) break;
                    entry = IntPtr.Add(entry, (int)proc.NextEntryOffset);
                }
                return list;
            }
            catch
            {
                return new List<ThreadSnapshot>();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return new List<ThreadSnapshot>();
    }

    // KTHREAD_STATE (undocumented but stable across Windows versions - the same "reversed but
    // widely relied upon" tier as the handle-type/module-table structs elsewhere in this app).
    public static string ThreadStateName(int state) => state switch
    {
        0 => "Initialized",
        1 => "Ready",
        2 => "Running",
        3 => "Standby",
        4 => "Terminated",
        5 => "Waiting",
        6 => "Transition",
        7 => "DeferredReady",
        8 => "GateWaitObsolete",
        9 => "WaitingForProcessInSwap",
        _ => $"Unknown ({state})",
    };

    // KWAIT_REASON - same tier as ThreadStateName above.
    public static string WaitReasonName(int reason) => reason switch
    {
        0 => "Executive",
        1 => "FreePage",
        2 => "PageIn",
        3 => "PoolAllocation",
        4 => "DelayExecution",
        5 => "Suspended",
        6 => "UserRequest",
        7 => "WrExecutive",
        8 => "WrFreePage",
        9 => "WrPageIn",
        10 => "WrPoolAllocation",
        11 => "WrDelayExecution",
        12 => "WrSuspended",
        13 => "WrUserRequest",
        14 => "WrEventPair",
        15 => "WrQueue",
        16 => "WrLpcReceive",
        17 => "WrLpcReply",
        18 => "WrVirtualMemory",
        19 => "WrPageOut",
        20 => "WrRendezvous",
        21 => "WrKeyedEvent",
        22 => "WrTerminated",
        23 => "WrProcessInSwap",
        24 => "WrCpuRateControl",
        25 => "WrCalloutStack",
        26 => "WrKernel",
        27 => "WrResource",
        28 => "WrPushLock",
        29 => "WrMutex",
        30 => "WrQuantumEnd",
        31 => "WrDispatchInt",
        32 => "WrPreempted",
        33 => "WrYieldExecution",
        34 => "WrFastMutex",
        35 => "WrGuardedMutex",
        36 => "WrRundown",
        37 => "WrAlertByThreadId",
        38 => "WrDeferredPreempt",
        _ => $"Unknown ({reason})",
    };

    private const int ThreadStateWaiting = 5;
    private const int ThreadStateRunning = 2;
    private const int ThreadStateStandby = 3;
    private const int ThreadStateReady = 1;

    private static string BucketName(ThreadSnapshot t) =>
        t.ThreadState == ThreadStateWaiting ? $"Waiting: {WaitReasonName(t.WaitReason)}" : ThreadStateName(t.ThreadState);

    private static string NameOrPid(string name, int pid) => string.IsNullOrEmpty(name) ? $"(pid {pid})" : name;

    /// <summary>#261: per-process breakdown of thread count by state/wait-reason bucket, worst
    /// (largest) bucket first.</summary>
    public static List<ThreadWaitBreakdownRow> BuildWaitBreakdown(List<ThreadSnapshot> snapshot, int pid) =>
        snapshot.Where(t => t.Pid == pid)
            .GroupBy(BucketName)
            .Select(g => new ThreadWaitBreakdownRow { BucketName = g.Key, ThreadCount = g.Count() })
            .OrderByDescending(r => r.ThreadCount)
            .ToList();

    /// <summary>#262: every Waiting thread ranked by WaitTime, worst first - a thread stuck for
    /// minutes becomes immediately visible. <paramref name="kernelModules"/> is passed in (rather
    /// than fetched here) so a caller sampling both this and #263/#265 in the same tick only pays
    /// for one DpcModuleMapService.GetModuleMap() call.</summary>
    public static List<LongestBlockedThreadRow> RankLongestBlocked(
        List<ThreadSnapshot> snapshot, List<DpcModuleMapService.LoadedModule> kernelModules, int top = 25) =>
        snapshot.Where(t => t.ThreadState == ThreadStateWaiting && t.WaitTimeTicks > 0)
            .OrderByDescending(t => t.WaitTimeTicks)
            .Take(top)
            .Select(t => new LongestBlockedThreadRow
            {
                Pid = t.Pid,
                ProcessName = NameOrPid(t.ProcessName, t.Pid),
                ThreadId = t.Tid,
                WaitReasonText = WaitReasonName(t.WaitReason),
                WaitSecondsApprox = t.WaitTimeTicks * ClockTickMs / 1000.0,
                StartAddressText = "0x" + t.StartAddress.ToString("X"),
                ModuleName = ResolveModule(t.Pid, t.StartAddress, kernelModules) ?? string.Empty,
            })
            .ToList();

    // #263: last-seen context-switch count per (pid, tid), for diffing between successive sweeps -
    // the same stateful-diffing idiom PerCoreDpcService/DwmCompositionService already use.
    private readonly Dictionary<(int Pid, int Tid), (uint ContextSwitches, DateTime SampleTimeUtc)> _prevContextSwitches = new();

    /// <summary>#263: per-thread context-switch rate since the previous sweep - a thread
    /// ping-ponging thousands of times a second is the signature of a spin-wait/livelock. Stateful
    /// (instance method, not static) since it needs the previous sweep's counts; the first call
    /// after construction (or after a thread first appears) produces no row for that thread yet,
    /// same "needs two samples" limitation every rate-from-a-cumulative-counter reading in this app
    /// already has (see HardwareMonitorService's remarks).</summary>
    public List<ThreadCsRateRow> ComputeContextSwitchRates(List<ThreadSnapshot> snapshot)
    {
        var now = DateTime.UtcNow;
        var rows = new List<ThreadCsRateRow>();
        var seen = new HashSet<(int, int)>(snapshot.Count);

        foreach (var t in snapshot)
        {
            var key = (t.Pid, t.Tid);
            seen.Add(key);

            if (_prevContextSwitches.TryGetValue(key, out var prev))
            {
                double elapsedSec = (now - prev.SampleTimeUtc).TotalSeconds;
                if (elapsedSec > 0.05)
                {
                    uint delta = t.ContextSwitches >= prev.ContextSwitches ? t.ContextSwitches - prev.ContextSwitches : 0;
                    rows.Add(new ThreadCsRateRow
                    {
                        Pid = t.Pid,
                        ProcessName = NameOrPid(t.ProcessName, t.Pid),
                        ThreadId = t.Tid,
                        ContextSwitchesPerSec = delta / elapsedSec,
                        StartAddress = t.StartAddress,
                    });
                }
            }
            _prevContextSwitches[key] = (t.ContextSwitches, now);
        }

        // Prune threads that no longer exist so this dictionary doesn't grow unbounded over a
        // long-running session - the same pruning HungWindowService.RunProbeCycleAsync does for
        // its own per-window state.
        foreach (var key in _prevContextSwitches.Keys.ToList())
            if (!seen.Contains(key)) _prevContextSwitches.Remove(key);

        return rows.OrderByDescending(r => r.ContextSwitchesPerSec).ToList();
    }

    /// <summary>#263: resolves module names for just the top <paramref name="top"/> already-sorted
    /// rows from ComputeContextSwitchRates, bounding the (per-process, on a miss) module-walk cost
    /// to a handful of rows instead of every thread on the system.</summary>
    public static List<ThreadCsRateRow> ResolveTopModules(List<ThreadCsRateRow> sortedRates, List<DpcModuleMapService.LoadedModule> kernelModules, int top = 25) =>
        sortedRates.Take(top)
            .Select(r => r with { ModuleName = ResolveModule(r.Pid, r.StartAddress, kernelModules) ?? string.Empty })
            .ToList();

    /// <summary>#265: aggregates #263's already-computed per-thread rates up to the owning process -
    /// "who's actually responsible for this context-switch rate", not a second diffing pass.</summary>
    public static List<ContextSwitchAttributionRow> AttributeByProcess(List<ThreadCsRateRow> rates, int top = 10)
    {
        double total = rates.Sum(r => r.ContextSwitchesPerSec);
        return rates.GroupBy(r => (r.Pid, r.ProcessName))
            .Select(g =>
            {
                double sum = g.Sum(x => x.ContextSwitchesPerSec);
                return new ContextSwitchAttributionRow
                {
                    Pid = g.Key.Pid,
                    ProcessName = g.Key.ProcessName,
                    ContextSwitchesPerSec = sum,
                    PercentOfTotal = total > 0 ? sum / total * 100.0 : 0,
                };
            })
            .OrderByDescending(r => r.ContextSwitchesPerSec)
            .Take(top)
            .ToList();
    }

    // #264: consecutive-sweep streak per (pid, tid) currently seen Ready at a high priority -
    // separate state from #263's dictionary above so the two diffs can't interfere with each other.
    private readonly Dictionary<(int Pid, int Tid), int> _highPriorityReadyStreak = new();

    /// <summary>#264: a thread priority above this is treated as "high priority" for the heuristic
    /// below - above the Normal(8)/AboveNormal band, roughly where a process's High/Realtime
    /// priority class threads land. A rough cutoff, not a documented boundary.</summary>
    private const int HighPriorityThreshold = 11;
    private const int SustainedSamplesRequired = 3;
    private const int MaxInversionHints = 5;

    /// <summary>#264: samples for the pattern where a high-priority thread has been sitting Ready
    /// (not running) across several consecutive sweeps while a lower-priority thread is actually
    /// running/about to run - explicitly a sampled inference over a few ticks, never a traced one.
    /// "Quick flag, not a verdict": this cannot see whether the lower-priority thread is actually
    /// holding a resource the higher one needs, only that the pattern (high priority, ready, not
    /// running; lower priority, running) is occurring at the same time.</summary>
    public List<PriorityInversionHint> DetectPriorityInversions(List<ThreadSnapshot> snapshot)
    {
        var runningOrStandby = snapshot.Where(t => t.ThreadState is ThreadStateRunning or ThreadStateStandby).ToList();
        var seen = new HashSet<(int, int)>();
        var hints = new List<PriorityInversionHint>();

        foreach (var t in snapshot)
        {
            bool isHighReady = t.ThreadState == ThreadStateReady && t.Priority >= HighPriorityThreshold;
            if (!isHighReady) continue;

            var key = (t.Pid, t.Tid);
            seen.Add(key);
            int streak = _highPriorityReadyStreak.TryGetValue(key, out var s) ? s + 1 : 1;
            _highPriorityReadyStreak[key] = streak;
            if (streak < SustainedSamplesRequired) continue;

            var lower = runningOrStandby
                .Where(r => r.Priority < t.Priority && r.Pid != t.Pid)
                .OrderBy(r => r.Priority)
                .FirstOrDefault();
            if (lower is null) continue;

            hints.Add(new PriorityInversionHint
            {
                HighPriorityProcess = NameOrPid(t.ProcessName, t.Pid),
                HighPriorityThreadId = t.Tid,
                HighPriority = t.Priority,
                LowerPriorityProcess = NameOrPid(lower.ProcessName, lower.Pid),
                LowerPriorityThreadId = lower.Tid,
                LowerPriority = lower.Priority,
                ConsecutiveSamples = streak,
            });
        }

        foreach (var key in _highPriorityReadyStreak.Keys.ToList())
            if (!seen.Contains(key)) _highPriorityReadyStreak.Remove(key);

        return hints.OrderByDescending(h => h.ConsecutiveSamples).Take(MaxInversionHints).ToList();
    }

    /// <summary>Resolves a thread's start address to its owning module - first against the kernel
    /// module map (DpcModuleMapService, covers System-process/driver-worker kernel-mode threads),
    /// then, for an ordinary user-mode process, against that process's own loaded modules
    /// (Process.Modules - a documented BCL wrapper, not new interop). Null (never a guess) when
    /// neither source covers the address - normal for a short-lived process that has already
    /// exited by the time this resolves, or a 32-bit process's modules under a 64-bit read.</summary>
    public static string? ResolveModule(int pid, long address, List<DpcModuleMapService.LoadedModule> kernelModules)
    {
        var kernelHit = DpcModuleMapService.ResolveDriverName(kernelModules, unchecked((ulong)address));
        if (kernelHit is not null) return kernelHit;
        if (pid <= 4) return null;

        try
        {
            using var proc = Process.GetProcessById(pid);
            foreach (ProcessModule m in proc.Modules)
            {
                long baseAddr = m.BaseAddress.ToInt64();
                if (address >= baseAddr && address < baseAddr + m.ModuleMemorySize) return m.ModuleName;
            }
        }
        catch
        {
            // Process exited, access denied, or a 32/64-bit module-enumeration mismatch - leave unresolved.
        }
        return null;
    }
}
