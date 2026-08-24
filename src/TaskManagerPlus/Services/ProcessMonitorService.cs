using System.Diagnostics;
using System.Management;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Samples running processes and computes per-process CPU% the same way Task
/// Manager does: (CPU time consumed since last sample) / (wall time elapsed *
/// logical processor count).
/// </summary>
public sealed class ProcessMonitorService
{
    private sealed class CpuSample
    {
        public TimeSpan CpuTime;
        public DateTime SampledAtUtc;
    }

    private readonly Dictionary<int, CpuSample> _lastSamples = new();
    private readonly Dictionary<int, string> _ownerCache = new();
    private readonly int _logicalProcessors = Environment.ProcessorCount;
    private DateTime _lastGlobalSampleUtc = DateTime.UtcNow;

    /// <summary>
    /// Builds the current snapshot of processes. Safe to call from a background thread.
    /// </summary>
    public List<ProcessRow> Sample()
    {
        var now = DateTime.UtcNow;
        var processes = Process.GetProcesses();
        var rows = new List<ProcessRow>(processes.Length);
        var seenPids = new HashSet<int>(processes.Length);

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

                double cpuPercent = 0;
                if (_lastSamples.TryGetValue(pid, out var last))
                {
                    var elapsed = (now - last.SampledAtUtc).TotalMilliseconds;
                    if (elapsed > 0)
                    {
                        var cpuDeltaMs = (cpuTime - last.CpuTime).TotalMilliseconds;
                        cpuPercent = Math.Max(0, cpuDeltaMs / elapsed / _logicalProcessors * 100.0);
                    }
                }
                _lastSamples[pid] = new CpuSample { CpuTime = cpuTime, SampledAtUtc = now };

                long memoryBytes = 0;
                int threadCount = 0;
                DateTime? startTime = null;
                string? filePath = null;
                try { memoryBytes = proc.WorkingSet64; } catch { /* ignore */ }
                try { threadCount = proc.Threads.Count; } catch { /* ignore */ }
                try { startTime = proc.StartTime; } catch { /* ignore, protected process */ }
                try { filePath = proc.MainModule?.FileName; } catch { /* ignore, protected/x-bit mismatch */ }

                string status = "Running";
                try
                {
                    if (proc.Responding == false)
                        status = "Not responding";
                }
                catch { /* ignore */ }

                rows.Add(new ProcessRow
                {
                    Pid = pid,
                    Name = SafeName(proc),
                    CpuPercent = Math.Round(Math.Min(cpuPercent, 100.0 * _logicalProcessors), 1),
                    MemoryBytes = memoryBytes,
                    Status = status,
                    User = GetOwnerCached(pid),
                    ThreadCount = threadCount,
                    StartTime = startTime,
                    FilePath = filePath,
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

        // Drop cached samples/owners for processes that no longer exist.
        PruneStaleEntries(_lastSamples, seenPids);
        PruneStaleEntries(_ownerCache, seenPids);

        _lastGlobalSampleUtc = now;
        return rows;
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
}
