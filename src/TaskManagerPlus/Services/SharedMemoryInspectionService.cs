using System.Diagnostics;
using System.Runtime.InteropServices;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #411: enumerates every Section handle system-wide (NtQuerySystemInformation,
/// SystemHandleInformation - the same system-wide handle table HandleInspectionService already
/// walks for its per-pid #12 handle-type breakdown, reused here via
/// HandleInspectionService.ReadSystemHandlesAll rather than a second copy of that P/Invoke),
/// resolves each one's type and name with HandleInspectionService's existing timeout-guarded
/// NtQueryObject path, and groups by section name - showing which pagefile-backed or file-backed
/// sections are large, and which processes currently hold a handle to each one.
///
/// This is a heavier scan than the per-pid handle inspector it's built on: a busy system's handle
/// table can run into the hundreds of thousands of entries across every process, and each
/// candidate handle needs its own process-open + duplicate + query round-trip. Strictly behind an
/// explicit "Scan shared memory" button on the Memory tab, never a tick - see MemoryViewModel.
/// </summary>
public static class SharedMemoryInspectionService
{
    private const uint ProcessDupHandle = 0x0040;

    // Global bounds so one scan always finishes in a bounded time even on a system with an
    // enormous handle table - a partial result (WasCapped) is far more useful than either hanging
    // the UI or refusing to run at all.
    private const int MaxHandlesToResolve = 6000;
    private const int MaxHandlesPerProcess = 800;
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(20);

    public static SharedMemoryScanResult Scan()
    {
        var sections = new Dictionary<string, SharedMemorySection>(StringComparer.Ordinal);
        var processNameCache = new Dictionary<int, string>();
        var deadline = DateTime.UtcNow + OverallTimeout;
        int resolved = 0;
        bool capped = false;

        List<(int ProcessId, ushort HandleValue)> allHandles;
        try
        {
            allHandles = HandleInspectionService.ReadSystemHandlesAll();
        }
        catch
        {
            return new SharedMemoryScanResult { Sections = new List<SharedMemorySection>(), Error = "Couldn't read the system handle table." };
        }

        foreach (var group in allHandles.GroupBy(h => h.ProcessId))
        {
            if (resolved >= MaxHandlesToResolve || DateTime.UtcNow > deadline) { capped = true; break; }

            int pid = group.Key;
            IntPtr sourceProcess = OpenProcess(ProcessDupHandle, false, pid);
            if (sourceProcess == IntPtr.Zero) continue; // protected/inaccessible process - skip it, not an error.

            try
            {
                string processName = GetProcessNameCached(pid, processNameCache);

                int perProcessCount = 0;
                foreach (var (_, handleValue) in group)
                {
                    if (perProcessCount >= MaxHandlesPerProcess) break;
                    if (resolved >= MaxHandlesToResolve || DateTime.UtcNow > deadline) { capped = true; break; }

                    resolved++;
                    perProcessCount++;

                    string typeName = HandleInspectionService.ResolveHandleType(sourceProcess, handleValue);
                    if (!typeName.Equals("Section", StringComparison.OrdinalIgnoreCase)) continue;

                    string? name = HandleInspectionService.ResolveHandleName(sourceProcess, handleValue);
                    if (string.IsNullOrEmpty(name)) continue; // unnamed section - nothing to group it by.

                    if (!sections.TryGetValue(name, out var section))
                    {
                        section = new SharedMemorySection
                        {
                            Name = name,
                            SizeBytes = HandleInspectionService.ResolveSectionSizeBytes(sourceProcess, handleValue),
                        };
                        sections[name] = section;
                    }
                    section.HandleCount++;
                    if (!section.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
                        section.ProcessNames.Add(processName);
                }
            }
            finally
            {
                CloseHandle(sourceProcess);
            }
        }

        var ordered = sections.Values
            .OrderByDescending(s => s.SizeBytes ?? 0)
            .ThenByDescending(s => s.ProcessNames.Count)
            .ToList();

        return new SharedMemoryScanResult { Sections = ordered, WasCapped = capped };
    }

    private static string GetProcessNameCached(int pid, Dictionary<int, string> cache)
    {
        if (cache.TryGetValue(pid, out var cached)) return cached;
        string name;
        try { using var proc = Process.GetProcessById(pid); name = proc.ProcessName; }
        catch { name = $"(pid {pid})"; }
        cache[pid] = name;
        return name;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Result of one #411 "Scan shared memory" pass - a named-section list plus whether the
/// scan hit one of its defensive caps before finishing (a real, expected outcome on a busy
/// system, not a bug - see SharedMemoryInspectionService's remarks).</summary>
public sealed class SharedMemoryScanResult
{
    public List<SharedMemorySection> Sections { get; set; } = new();
    public bool WasCapped { get; set; }
    public string? Error { get; set; }
}
