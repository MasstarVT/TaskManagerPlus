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

    /// <summary>ThreadsChecked counts only threads whose start address was actually read and
    /// classified; ThreadsUnreadable counts the ones that couldn't be (access denied - e.g. a
    /// protected process - or the query timed out). Error is set when nothing could be judged at
    /// all, and then Findings is always empty.</summary>
    public sealed record ScanResult(
        List<ForeignThreadFinding> Findings,
        int ThreadsChecked,
        int ThreadsUnreadable,
        int ThreadsTotal,
        bool TimedOut,
        string? Note,
        string? Error = null);

    private enum StartAddressRead { Read, Unreadable, Exited }

    public static ScanResult Scan(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);

            // Process.Modules builds its whole list up front, so a throw here leaves no ranges at
            // all - every readable thread would then look "unbacked". Nothing can be judged.
            var ranges = new List<(long Base, long End)>();
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
                return Failed($"Couldn't enumerate this process's loaded modules ({ex.Message}) - thread origins can't be judged without them.");
            }
            if (ranges.Count == 0)
                return Failed("Couldn't enumerate this process's loaded modules - thread origins can't be judged without them.");

            var threadIds = new List<int>();
            try
            {
                foreach (ProcessThread thread in proc.Threads) threadIds.Add(thread.Id);
            }
            catch (Exception ex)
            {
                return Failed($"Couldn't enumerate threads: {ex.Message}");
            }

            var findings = new List<ForeignThreadFinding>();
            var deadline = DateTime.UtcNow + OverallTimeout;
            int attempted = 0, checkedCount = 0, unreadable = 0, exited = 0;
            bool timedOut = false;

            foreach (int tid in threadIds)
            {
                if (attempted >= MaxThreads) break;
                if (DateTime.UtcNow > deadline) { timedOut = true; break; }
                attempted++;

                var (read, startAddress) = ReadThreadStartAddress(tid);
                if (read == StartAddressRead.Exited) { exited++; continue; }
                if (read == StartAddressRead.Unreadable) { unreadable++; continue; }
                checkedCount++;

                bool backed = ranges.Any(r => startAddress >= r.Base && startAddress < r.End);
                if (!backed)
                    findings.Add(new ForeignThreadFinding(tid, startAddress));
            }

            int total = threadIds.Count - exited;
            var notes = new List<string>();
            if (unreadable > 0)
                notes.Add($"Start address unreadable for {unreadable} of {total} threads (access denied, or the query timed out) - those weren't checked.");
            if (timedOut)
                notes.Add($"Stopped after the {OverallTimeout.TotalSeconds:0}-second time budget - {threadIds.Count - attempted} thread(s) weren't attempted.");
            else if (threadIds.Count > MaxThreads)
                notes.Add($"Stopped after the {MaxThreads}-thread inspection cap - {threadIds.Count - attempted} thread(s) weren't attempted.");

            string? error = checkedCount == 0
                ? (total == 0 ? "The process's threads all exited during the scan - nothing to check." : $"Couldn't read the start address of any of this process's {total} threads (access denied - a protected process - or the queries timed out). No result.")
                : null;

            if (error is not null) return new ScanResult(new List<ForeignThreadFinding>(), 0, unreadable, total, timedOut, null, error);
            return new ScanResult(findings, checkedCount, unreadable, total, timedOut, notes.Count > 0 ? string.Join(" ", notes) : null);
        }
        catch (Exception ex)
        {
            return Failed($"Couldn't scan this process: {ex.Message}");
        }
    }

    private static ScanResult Failed(string error) =>
        new(new List<ForeignThreadFinding>(), 0, 0, 0, false, null, error);

    /// <summary>Queries one thread's start address off its own abandoned background thread with a
    /// strict timeout - see the class remarks for why NtQueryInformationThread can't be trusted to
    /// always return promptly. A duplicate/leaked handle from a query that never returns is a small,
    /// bounded cost (capped thread count above) versus risking a hang.</summary>
    private static (StartAddressRead Read, long Address) ReadThreadStartAddress(int tid)
    {
        IntPtr hThread;
        try
        {
            // ThreadQuerySetWin32StartAddress needs THREAD_QUERY_INFORMATION - with only
            // THREAD_QUERY_LIMITED_INFORMATION every query returns STATUS_ACCESS_DENIED.
            hThread = OpenThread(ThreadQueryInformation, false, (uint)tid);
            if (hThread == IntPtr.Zero)
            {
                // ERROR_INVALID_PARAMETER: the thread id no longer exists - it exited after the
                // thread list was taken, so it's not counted as unreadable.
                return (Marshal.GetLastWin32Error() == ErrorInvalidParameter ? StartAddressRead.Exited : StartAddressRead.Unreadable, 0);
            }
        }
        catch
        {
            return (StartAddressRead.Unreadable, 0);
        }

        long? result = null;
        var worker = new Thread(() =>
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(IntPtr.Size);
                int status = NtQueryInformationThread(hThread, ThreadQuerySetWin32StartAddress, buffer, (uint)IntPtr.Size, out _);
                long address = status == 0 ? Marshal.ReadIntPtr(buffer).ToInt64() : 0;
                if (address != 0) result = address; // a zero start address isn't an address to classify
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

        return result is { } address ? (StartAddressRead.Read, address) : (StartAddressRead.Unreadable, 0);
    }

    private const uint ThreadQueryInformation = 0x0040;
    private const int ErrorInvalidParameter = 87;
    private const int ThreadQuerySetWin32StartAddress = 9;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(IntPtr threadHandle, int threadInformationClass, IntPtr threadInformation, uint threadInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
