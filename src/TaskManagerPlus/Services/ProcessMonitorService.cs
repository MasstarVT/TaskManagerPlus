using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Samples running processes and computes per-process CPU% the same way Task
/// Manager does: (CPU time consumed since last sample) / (wall time elapsed *
/// logical processor count).
/// </summary>
public sealed class ProcessMonitorService : IDisposable
{
    private sealed class CpuSample
    {
        public TimeSpan CpuTime;
        public DateTime SampledAtUtc;

        /// <summary>Total (read+write) I/O bytes as of the last sample (#26) - piggybacks on
        /// the same per-pid sample/elapsed-time bookkeeping the CPU% calculation already does,
        /// rather than a second dictionary.</summary>
        public ulong IoBytes;
    }

    private readonly Dictionary<int, CpuSample> _lastSamples = new();
    // #14: rolling per-pid working-set history for the leak detector - see ComputeLeakSuspect.
    private readonly Dictionary<int, Queue<long>> _memoryHistory = new();
    // #11: rolling ~10s CPU% window per-pid - see ComputeCpuAverage.
    private readonly Dictionary<int, Queue<double>> _cpuHistory = new();
    // #2: when a process first started reporting "Not responding" - lets the UI show a duration
    // ("Not responding (12s)") rather than just a flat flag, so a genuinely stuck window stands
    // out from one that ghosts for a split second and recovers.
    private readonly Dictionary<int, DateTime> _notRespondingSince = new();
    private readonly Dictionary<int, string> _ownerCache = new();
    private readonly Dictionary<int, string?> _commandLineCache = new();
    // Parent process ID never changes after launch, same caching shape as command line (#52).
    private readonly Dictionary<int, int> _parentPidCache = new();
    private readonly int _logicalProcessors = Environment.ProcessorCount;
    private DateTime _lastGlobalSampleUtc = DateTime.UtcNow;

    // #36: per-process GPU usage, keyed by "GPU Engine" perf-counter instance name (which churns
    // far more than the CPU core instances HardwareMonitorService tracks - engines come and go
    // as processes start/stop using the GPU) - see ReadGpuUsageByPid.
    private readonly Dictionary<string, PerformanceCounter> _gpuEngineCounters = new();
    private static readonly Regex GpuEnginePidRegex = new(@"pid_(\d+)_", RegexOptions.Compiled);

    /// <summary>Well-known high-privilege service accounts - flagged distinctly from an
    /// ordinary signed-in user account when auditing the process list for something unexpected.</summary>
    private static readonly string[] HighPrivilegeAccounts = { "SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE" };

    /// <summary>
    /// Builds the current snapshot of processes. Safe to call from a background thread.
    /// </summary>
    public List<ProcessRow> Sample()
    {
        var now = DateTime.UtcNow;
        var processes = Process.GetProcesses();
        var rows = new List<ProcessRow>(processes.Length);
        var seenPids = new HashSet<int>(processes.Length);
        var gpuUsageByPid = ReadGpuUsageByPid();

        foreach (var proc in processes)
        {
            try
            {
                int pid = proc.Id;
                seenPids.Add(pid);

                TimeSpan cpuTime;
                try
                {
                    cpuTime = proc.TotalProcessorTime;
                }
                catch (Exception)
                {
                    // Access denied (protected/system process) - still list it, just no CPU%.
                    cpuTime = TimeSpan.Zero;
                }

                ulong ioBytesNow = ReadIoBytes(proc);

                double cpuPercent = 0;
                double diskBytesPerSec = 0;
                if (_lastSamples.TryGetValue(pid, out var last))
                {
                    var elapsed = (now - last.SampledAtUtc).TotalMilliseconds;
                    if (elapsed > 0)
                    {
                        var cpuDeltaMs = (cpuTime - last.CpuTime).TotalMilliseconds;
                        cpuPercent = Math.Max(0, cpuDeltaMs / elapsed / _logicalProcessors * 100.0);

                        if (ioBytesNow >= last.IoBytes)
                            diskBytesPerSec = (ioBytesNow - last.IoBytes) / (elapsed / 1000.0);
                    }
                }
                _lastSamples[pid] = new CpuSample { CpuTime = cpuTime, SampledAtUtc = now, IoBytes = ioBytesNow };

                long memoryBytes = 0;
                int threadCount = 0;
                int handleCount = 0;
                DateTime? startTime = null;
                string? filePath = null;
                // Round 8 #38: working set (already sampled above) vs. private bytes vs. virtual
                // size, side by side - three genuinely distinct Process figures, all already
                // exposed by .NET with no extra interop. Modern Windows doesn't expose a fourth,
                // truly separate "commit charge" for a process beyond private bytes (Task
                // Manager's own "Commit size" column reads the same underlying figure), so virtual
                // size is shown as the third column instead of a redundant duplicate of private
                // bytes.
                long privateBytes = 0;
                long virtualBytes = 0;
                // Round 8 #39: per-process kernel pool usage - .NET's Process class already
                // exposes these (PROCESS_MEMORY_COUNTERS' QuotaNonPagedPoolUsage/
                // QuotaPagedPoolUsage under the hood), no native interop needed.
                long nonpagedPoolBytes = 0;
                long pagedPoolBytes = 0;
                try { memoryBytes = proc.WorkingSet64; } catch { /* ignore */ }
                try { privateBytes = proc.PrivateMemorySize64; } catch { /* ignore */ }
                try { virtualBytes = proc.VirtualMemorySize64; } catch { /* ignore */ }
                try { nonpagedPoolBytes = proc.NonpagedSystemMemorySize64; } catch { /* ignore */ }
                try { pagedPoolBytes = proc.PagedSystemMemorySize64; } catch { /* ignore */ }
                try { threadCount = proc.Threads.Count; } catch { /* ignore */ }
                try { handleCount = proc.HandleCount; } catch { /* ignore */ }
                try { startTime = proc.StartTime; } catch { /* ignore, protected process */ }
                try { filePath = proc.MainModule?.FileName; } catch { /* ignore, protected/x-bit mismatch */ }

                // Round 7 #6/#7/#8: priority class name, GDI/USER handle counts, and best-effort
                // suspended state - all cheap reads piggybacked onto this same per-process pass
                // rather than a second sweep.
                string priorityClassName = "Unknown";
                try { priorityClassName = proc.PriorityClass.ToString(); } catch { /* protected process */ }

                int gdiHandles = 0, userHandles = 0;
                try { (gdiHandles, userHandles) = ProcessControlService.ReadGuiResourceCounts(proc.Handle); } catch { /* ignore */ }

                bool isSuspended = false;
                try { isSuspended = ProcessControlService.IsSuspended(proc); } catch { /* ignore */ }

                // Round 15, #852(a)/(c): integrity level + AppContainer flag, reusing this same
                // already-open process handle (proc.Handle, the same one ReadGuiResourceCounts just
                // used above) - both are cheap single-token-query calls, safe for a per-tick column.
                // #852(b): protection level (PS_PROTECTION) opens its own short-lived handle since it
                // needs PROCESS_QUERY_LIMITED_INFORMATION specifically - see
                // ProcessTokenInspectionService's remarks.
                string integrityLevel = "Unknown";
                bool isAppContainer = false;
                string protectionLevel = "Unknown";
                try { integrityLevel = ProcessTokenInspectionService.ReadIntegrityLevel(proc.Handle); } catch { /* ignore */ }
                try { isAppContainer = ProcessTokenInspectionService.ReadIsAppContainer(proc.Handle); } catch { /* ignore */ }
                try { protectionLevel = ProcessTokenInspectionService.ReadProtectionLevel(pid); } catch { /* ignore */ }

                string status = "Running";
                int notRespondingSeconds = 0;
                try
                {
                    if (proc.Responding == false)
                    {
                        status = "Not responding";
                        if (!_notRespondingSince.TryGetValue(pid, out var since))
                            _notRespondingSince[pid] = since = now;
                        notRespondingSeconds = Math.Max(0, (int)(now - since).TotalSeconds);
                    }
                    else
                    {
                        _notRespondingSince.Remove(pid);
                    }
                }
                catch { /* ignore */ }

                string owner = GetOwnerCached(pid);

                double cpuPercentClamped = Math.Round(Math.Min(cpuPercent, 100.0 * _logicalProcessors), 1);

                // #837: publisher (subject CN, falling back to issuer CN) - piggybacks on the
                // same cached SignatureCheckService lookup SignatureStatus below already performs,
                // so this costs nothing extra beyond the first check of a given file path.
                string processName = SafeName(proc);
                var signer = SignatureCheckService.GetSignerInfo(filePath);
                string publisher = signer.SubjectCn ?? signer.IssuerCn ?? "Unknown";

                // #840: expected-Microsoft-binary / near-miss-name check - see
                // ProcessTrustService's remarks on why this stays cheap for a per-tick poll.
                string? trustWarning = ProcessTrustService.Evaluate(processName, filePath);

                rows.Add(new ProcessRow
                {
                    Pid = pid,
                    Name = processName,
                    CpuPercent = cpuPercentClamped,
                    MemoryBytes = memoryBytes,
                    PrivateBytes = privateBytes,
                    VirtualBytes = virtualBytes,
                    NonpagedPoolBytes = nonpagedPoolBytes,
                    PagedPoolBytes = pagedPoolBytes,
                    DiskBytesPerSec = Math.Round(diskBytesPerSec, 0),
                    Status = status,
                    NotRespondingSeconds = notRespondingSeconds,
                    User = owner,
                    ThreadCount = threadCount,
                    HandleCount = handleCount,
                    StartTime = startTime,
                    FilePath = filePath,
                    CommandLine = GetCommandLineCached(pid),
                    SignatureStatus = SignatureCheckService.GetStatus(filePath),
                    Publisher = publisher,
                    IsSelfSigned = signer.SelfSigned,
                    TrustWarning = trustWarning,
                    IsHighPrivilege = HighPrivilegeAccounts.Contains(owner, StringComparer.OrdinalIgnoreCase),
                    ParentPid = GetParentPidCached(pid),
                    IsLeakSuspect = ComputeLeakSuspect(pid, memoryBytes),
                    GpuPercent = gpuUsageByPid.TryGetValue(pid, out var gpu) ? Math.Round(Math.Min(gpu, 100.0), 1) : 0,
                    CpuPercent10sAvg = ComputeCpuAverage(pid, cpuPercentClamped),
                    PriorityClassName = priorityClassName,
                    GdiHandleCount = gdiHandles,
                    UserHandleCount = userHandles,
                    IsSuspended = isSuspended,
                    IntegrityLevel = integrityLevel,
                    IsAppContainer = isAppContainer,
                    ProtectionLevel = protectionLevel,
                });
            }
            catch (Exception)
            {
                // Process exited mid-enumeration or is otherwise inaccessible - skip it.
            }
            finally
            {
                proc.Dispose();
            }
        }

        // #52: resolve each row's parent name from this same batch (a second pass, since the
        // parent may appear later in the enumeration order than its child) - falls back to
        // "(exited)" for a parent that's no longer running rather than leaving it blank.
        var namesByPid = rows.ToDictionary(r => r.Pid, r => r.Name);
        foreach (var row in rows)
        {
            row.ParentName = row.ParentPid > 0 && namesByPid.TryGetValue(row.ParentPid, out var parentName)
                ? parentName
                : "(exited)";
        }

        ComputeSpawnGroups(rows);
        ComputeDuplicateInstances(rows);

        // Drop cached samples/owners/command lines for processes that no longer exist. The
        // signature cache is keyed by file path, not pid, so it isn't pruned here - it's small
        // (one entry per distinct executable seen) and a stale entry just saves a re-check if
        // the same binary starts again later.
        PruneStaleEntries(_lastSamples, seenPids);
        PruneStaleEntries(_ownerCache, seenPids);
        PruneStaleEntries(_commandLineCache, seenPids);
        PruneStaleEntries(_parentPidCache, seenPids);
        PruneStaleEntries(_memoryHistory, seenPids);
        PruneStaleEntries(_cpuHistory, seenPids);
        PruneStaleEntries(_notRespondingSince, seenPids);

        _lastGlobalSampleUtc = now;
        return rows;
    }

    // Round 7 #2: job-object/process-group detection has no direct Windows API a per-process
    // sampler can query (a process's job-object membership isn't exposed the way its parent pid
    // is) - this is a heuristic proxy instead: processes sharing the same parent pid and the same
    // executable name, whose start times cluster within a short window of each other, are almost
    // always the result of one spawn event (a launcher fanning out several worker/helper
    // processes at once), whether or not Windows actually put them in a shared job object. A
    // "quick flag, not a verdict" in the same family as the CPU throttle heuristic and the
    // process signature check - a coincidental unrelated launch that happens to land in the same
    // window would also match.
    private static readonly TimeSpan SpawnClusterWindow = TimeSpan.FromSeconds(3);

    private static void ComputeSpawnGroups(List<ProcessRow> rows)
    {
        var groups = rows.Where(r => r.ParentPid > 0 && r.StartTime.HasValue)
            .GroupBy(r => (r.ParentPid, r.Name));

        foreach (var group in groups)
        {
            var ordered = group.OrderBy(r => r.StartTime!.Value).ToList();
            int i = 0;
            while (i < ordered.Count)
            {
                int j = i;
                while (j + 1 < ordered.Count && (ordered[j + 1].StartTime!.Value - ordered[j].StartTime!.Value) <= SpawnClusterWindow)
                    j++;

                int clusterSize = j - i + 1;
                if (clusterSize >= 2)
                {
                    for (int k = i; k <= j; k++)
                        ordered[k].SpawnGroupSize = clusterSize;
                }
                i = j + 1;
            }
        }
    }

    // Round 7 #11: "unusually high" is deliberately generous - a legitimate multi-process
    // Chromium-family browser (Chrome/Edge/many Electron apps) routinely runs a few dozen
    // renderer/GPU/utility processes that all share one exe path, which would otherwise dominate
    // this flag with false positives. A real runaway-launcher bug (a script or updater re-spawning
    // itself in a crash loop) tends to blow well past even this generous bar, so the threshold is
    // tuned to catch that case rather than tuned tight - a real, documented limitation, not a
    // guarantee every outlier is actually a bug.
    private const int DuplicateInstanceOutlierThreshold = 20;

    private static void ComputeDuplicateInstances(List<ProcessRow> rows)
    {
        var groups = rows.Where(r => !string.IsNullOrEmpty(r.FilePath))
            .GroupBy(r => r.FilePath!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            int count = group.Count();
            if (count < 2) continue;
            bool outlier = count >= DuplicateInstanceOutlierThreshold;
            foreach (var row in group)
            {
                row.DuplicateInstanceCount = count;
                row.IsDuplicateInstanceOutlier = outlier;
            }
        }
    }

    private const int CpuHistoryWindow = 10; // ~10s of samples at the ~1s poll tick

    /// <summary>Rolling ~10s average CPU% per process (#11) - "what's actually been eating CPU
    /// over the last several seconds", steadier than a single instantaneous tick that can catch a
    /// bursty process mid-spike or mid-idle.</summary>
    private double ComputeCpuAverage(int pid, double cpuPercent)
    {
        if (!_cpuHistory.TryGetValue(pid, out var history))
        {
            history = new Queue<double>();
            _cpuHistory[pid] = history;
        }
        history.Enqueue(cpuPercent);
        while (history.Count > CpuHistoryWindow) history.Dequeue();

        return Math.Round(history.Average(), 1);
    }

    // #14: a rolling window's worth of samples (at the ~1s poll tick, ~2 minutes) - long enough
    // that a real leak's slope is distinguishable from ordinary allocate/GC noise, short enough
    // that memory use is still small per-process (one long per sample).
    private const int MemoryHistoryWindow = 120;

    // Below this, a "monotonic" climb is just measurement noise on an otherwise-flat process,
    // not a meaningful leak - a real leak worth flagging keeps growing well past this.
    private const long LeakGrowthThresholdBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Flags a process whose working set has grown without ever giving memory back over the
    /// whole tracked window (#14) - a real leak keeps climbing; anything that dips at any point
    /// (a GC pass, a cache eviction, normal alloc/free churn) is disqualified outright, since a
    /// genuine unbounded leak is exactly the kind of allocation that's never freed. This is a
    /// heuristic tuned for "flag something worth a second look", not a definitive diagnosis - a
    /// process that legitimately needs more memory over time (e.g. loading a large dataset) will
    /// also match.
    /// </summary>
    private bool ComputeLeakSuspect(int pid, long memoryBytes)
    {
        if (!_memoryHistory.TryGetValue(pid, out var history))
        {
            history = new Queue<long>();
            _memoryHistory[pid] = history;
        }
        history.Enqueue(memoryBytes);
        while (history.Count > MemoryHistoryWindow) history.Dequeue();

        if (history.Count < MemoryHistoryWindow) return false;

        var samples = history.ToArray();
        for (int i = 1; i < samples.Length; i++)
        {
            if (samples[i] < samples[i - 1]) return false;
        }
        return samples[^1] - samples[0] >= LeakGrowthThresholdBytes;
    }

    /// <summary>
    /// Sums each process's GPU engine utilization (#36) - Task Manager's own "GPU" column reads
    /// this same "GPU Engine" perf-counter category, since there's no other public API for
    /// per-process GPU usage. Instance names look like
    /// "pid_1234_luid_0x...0x..._phys_0_eng_0_engtype_3D" - a process can own several engine
    /// instances at once (3D, Copy, VideoDecode, ...), summed here the same way Task Manager
    /// totals them for its single GPU column. Unlike the static per-core CPU counters in
    /// HardwareMonitorService, engine instances churn constantly as processes start/stop using
    /// the GPU, so counters are created lazily and kept only as long as their instance still
    /// exists; a newly-seen instance is skipped for one tick (same "prime before trusting a rate
    /// counter" rule as every other counter in this app) rather than reported as a false 0.
    /// </summary>
    private Dictionary<int, double> ReadGpuUsageByPid()
    {
        var result = new Dictionary<int, double>();
        try
        {
            var instances = new PerformanceCounterCategory("GPU Engine").GetInstanceNames();
            var seen = new HashSet<string>(instances);

            foreach (var stale in _gpuEngineCounters.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _gpuEngineCounters[stale].Dispose();
                _gpuEngineCounters.Remove(stale);
            }

            foreach (var instance in instances)
            {
                if (!_gpuEngineCounters.TryGetValue(instance, out var counter))
                {
                    try
                    {
                        var newCounter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                        newCounter.NextValue(); // prime it - a rate counter's first-ever sample is meaningless (always 0), so consume it now rather than let the next tick's read report a false 0.
                        _gpuEngineCounters[instance] = newCounter;
                    }
                    catch { /* instance disappeared between GetInstanceNames() and here - skip it */ }
                    continue;
                }

                var match = GpuEnginePidRegex.Match(instance);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out int pid)) continue;

                double value;
                try { value = counter.NextValue(); }
                catch { continue; }

                result[pid] = result.TryGetValue(pid, out var existing) ? existing + value : value;
            }
        }
        catch
        {
            // The "GPU Engine" category can be entirely missing on an old/unusual driver stack -
            // degrade to "no GPU data" rather than failing the whole process sample.
        }
        return result;
    }

    /// <summary>Total (read+write) I/O bytes for a process via the native GetProcessIoCounters
    /// call (#26) - .NET's Process class doesn't expose this. Best-effort: a protected/
    /// inaccessible process just reports 0, the same as every other per-process field here that
    /// can be access-denied.</summary>
    private static ulong ReadIoBytes(Process proc)
    {
        try
        {
            if (GetProcessIoCounters(proc.Handle, out var counters))
                return counters.ReadTransferCount + counters.WriteTransferCount;
        }
        catch
        {
            // Access denied, or the process exited mid-call - leave it at 0.
        }
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    private static void PruneStaleEntries<TValue>(Dictionary<int, TValue> dict, HashSet<int> livePids)
    {
        if (dict.Count == 0) return;
        List<int>? toRemove = null;
        foreach (var pid in dict.Keys)
        {
            if (!livePids.Contains(pid))
                (toRemove ??= new List<int>()).Add(pid);
        }
        if (toRemove is null) return;
        foreach (var pid in toRemove)
            dict.Remove(pid);
    }

    private static string SafeName(Process proc)
    {
        try { return proc.ProcessName; }
        catch { return "(unknown)"; }
    }

    private string GetOwnerCached(int pid)
    {
        if (_ownerCache.TryGetValue(pid, out var cached))
            return cached;

        string owner = "SYSTEM";
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Handle FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject mo in searcher.Get())
            {
                var args = new object[] { string.Empty, string.Empty };
                var result = (uint)mo.InvokeMethod("GetOwner", args);
                if (result == 0)
                    owner = (string)args[0];
            }
        }
        catch
        {
            owner = "N/A";
        }

        _ownerCache[pid] = owner;
        return owner;
    }

    /// <summary>Command-line arguments a process was launched with, fetched via WMI (.NET's
    /// Process class doesn't expose this) and cached per-pid since a running process's command
    /// line never changes after launch.</summary>
    private string? GetCommandLineCached(int pid)
    {
        if (_commandLineCache.TryGetValue(pid, out var cached))
            return cached;

        string? commandLine = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject mo in searcher.Get())
                commandLine = mo["CommandLine"] as string;
        }
        catch
        {
            // Access denied or the process exited mid-query - leave it null.
        }

        _commandLineCache[pid] = commandLine;
        return commandLine;
    }

    /// <summary>Parent process ID (#52), fetched via WMI (like command line, .NET's Process class
    /// doesn't expose this) and cached per-pid since it never changes after launch.</summary>
    private int GetParentPidCached(int pid)
    {
        if (_parentPidCache.TryGetValue(pid, out var cached))
            return cached;

        int parentPid = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject mo in searcher.Get())
                parentPid = Convert.ToInt32(mo["ParentProcessId"] ?? 0);
        }
        catch
        {
            // Access denied or the process exited mid-query - leave it 0 ("unknown").
        }

        _parentPidCache[pid] = parentPid;
        return parentPid;
    }

    /// <summary>Ends a single process.</summary>
    public static (bool Success, string? Error) EndProcess(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Ends a process and its entire descendant tree.</summary>
    public static (bool Success, string? Error) EndProcessTree(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void Dispose()
    {
        foreach (var counter in _gpuEngineCounters.Values) counter.Dispose();
        _gpuEngineCounters.Clear();
    }
}
