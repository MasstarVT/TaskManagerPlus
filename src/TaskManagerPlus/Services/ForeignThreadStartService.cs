using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, #848: flags a thread whose start address falls outside every loaded module's
/// [base, base+size) range - i.e. a thread that began executing somewhere that isn't part of any DLL
/// or the main image, such as a manually-mapped payload or a classic CreateRemoteThread injection
/// target. Reuses Process.Threads (thread IDs) and Process.Modules (base-address/size ranges) - both
/// already-available .NET APIs, no need to re-derive either from scratch.
///
/// Getting a thread's start address requires NtQueryInformationThread with the undocumented-but-
/// stable ThreadQuerySetWin32StartAddress info class (9) - same interop-risk tier as NtQueryObject in
/// HandleInspectionService (documented behavior, undocumented guarantee it will always return
/// promptly), so this uses the exact same safety discipline: every per-thread query runs on its own
/// abandoned background thread with a strict timeout, never joined past it, plus an overall
/// wall-clock cap and a cap on how many threads get processed at all - a process with many hundreds
/// of threads is inspected on a best-effort/partial basis rather than making an on-demand click take
/// arbitrarily long.
/// </summary>
public static class ForeignThreadStartService
{
    private const int MaxThreads = 500;
    private static readonly TimeSpan PerThreadTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(5);

    public sealed record ForeignThreadFinding(int ThreadId, long StartAddress);

    public sealed record ScanResult(
        List<ForeignThreadFinding> Findings,
        int ThreadsScanned,
        int ThreadsTotal,
        bool TimedOut,
        string? Note);

    public static ScanResult Scan(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);

            var ranges = new List<(long Base, long End)>();
            string? moduleNote = null;
            try
            {
                foreach (ProcessModule module in proc.Modules)
                {
                    long baseAddr = module.BaseAddress.ToInt64();
                    ranges.Add((baseAddr, baseAddr + module.ModuleMemorySize));
                }
            }
            catch (Exception ex)
            {
                moduleNote = $"Couldn't fully enumerate loaded modules ({ex.Message}) - findings below may be unreliable (a thread could be misreported as unbacked simply because its real module couldn't be listed).";
            }

            var threadIds = new List<int>();
            try
            {
                foreach (ProcessThread thread in proc.Threads) threadIds.Add(thread.Id);
            }
            catch (Exception ex)
            {
                return new ScanResult(new List<ForeignThreadFinding>(), 0, 0, false, $"Couldn't enumerate threads: {ex.Message}");
            }

            var findings = new List<ForeignThreadFinding>();
            var deadline = DateTime.UtcNow + OverallTimeout;
            int scanned = 0;
            bool timedOut = false;

            foreach (int tid in threadIds)
            {
                if (scanned >= MaxThreads) break;
                if (DateTime.UtcNow > deadline) { timedOut = true; break; }
                scanned++;

                long? startAddress = ReadThreadStartAddress(tid);
                if (startAddress is null) continue; // unresolved (denied/timed out/exited) - not reported as a finding either way

                bool backed = ranges.Any(r => startAddress.Value >= r.Base && startAddress.Value < r.End);
                if (!backed)
                    findings.Add(new ForeignThreadFinding(tid, startAddress.Value));
            }

            string? note = moduleNote ?? (timedOut
                ? $"Stopped after the {OverallTimeout.TotalSeconds:0}-second time budget - only {scanned} of {threadIds.Count} threads were checked."
                : threadIds.Count > MaxThreads
                    ? $"Stopped after the {MaxThreads}-thread inspection cap - only {scanned} of {threadIds.Count} threads were checked."
                    : null);

            return new ScanResult(findings, scanned, threadIds.Count, timedOut, note);
        }
        catch (Exception ex)
        {
            return new ScanResult(new List<ForeignThreadFinding>(), 0, 0, false, $"Couldn't scan this process: {ex.Message}");
        }
    }

    /// <summary>Queries one thread's start address off its own abandoned background thread with a
    /// strict timeout - see the class remarks for why NtQueryInformationThread can't be trusted to
    /// always return promptly. A duplicate/leaked handle from a query that never returns is a small,
    /// bounded cost (capped thread count above) versus risking a hang.</summary>
    private static long? ReadThreadStartAddress(int tid)
    {
        IntPtr hThread;
        try
        {
            hThread = OpenThread(ThreadQueryLimitedInformation, false, (uint)tid);
            if (hThread == IntPtr.Zero) return null;
        }
        catch
        {
            return null;
        }

        long? result = null;
        var worker = new Thread(() =>
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(IntPtr.Size);
                int status = NtQueryInformationThread(hThread, ThreadQuerySetWin32StartAddress, buffer, (uint)IntPtr.Size, out _);
                if (status == 0)
                    result = Marshal.ReadIntPtr(buffer).ToInt64();
            }
            catch { /* leave result null */ }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        })
        { IsBackground = true };

        worker.Start();
        bool finished = worker.Join(PerThreadTimeout);

        // Only close the handle if the query actually returned - closing mid-hung-call could itself
        // hang or corrupt state (same reasoning as HandleInspectionService.ResolveHandleType).
        if (finished)
        {
            try { CloseHandle(hThread); } catch { /* ignore */ }
        }

        return result;
    }

    private const uint ThreadQueryLimitedInformation = 0x0800;
    private const int ThreadQuerySetWin32StartAddress = 9;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(IntPtr threadHandle, int threadInformationClass, IntPtr threadInformation, uint threadInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
