using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, #846: on-demand per-process address-space walk looking for executable memory that
/// isn't backed by any image/mapped file - i.e. MEM_PRIVATE pages, in the MEM_COMMIT state, with
/// PAGE_EXECUTE/PAGE_EXECUTE_READ/PAGE_EXECUTE_READWRITE/PAGE_EXECUTE_WRITECOPY protection. This is
/// exactly what a reflectively-loaded/manually-mapped payload or a classic shellcode injection looks
/// like from the outside - executable code that didn't arrive via the normal "load an image from
/// disk" path a signature/module check can see.
///
/// IMPORTANT, and stated again in the UI text (not just here): JITs are the single biggest source of
/// false positives for this scan. Every browser (V8/SpiderMonkey), the .NET runtime itself, Java, and
/// any other JIT-compiling runtime legitimately allocates MEM_PRIVATE executable pages for compiled
/// code - a modern browser tab can show dozens of such regions and multiple megabytes without
/// anything being wrong. This is a comparison signal ("does this number look unusually large for
/// what this process normally is"), never a verdict on its own.
///
/// Safety: the whole walk runs on its own abandoned background thread with a strict overall
/// wall-clock timeout, the same "never let a native/address-space walk hang the caller" discipline
/// HandleInspectionService uses for the system-wide handle-table walk - VirtualQueryEx itself isn't
/// known to hang, but a process with an enormous number of tiny regions (or one that's actively
/// mutating its own address space while walked) could otherwise make this scan run far longer than
/// is reasonable for an on-demand button click. Iteration is separately capped by count as a second,
/// independent bound.
/// </summary>
public static class UnbackedExecutableMemoryService
{
    private const int MaxRegions = 100_000;
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(5);

    public sealed record ScanResult(bool Completed, int RegionCount, long TotalBytes, int RegionsWalked, string? Note);

    /// <summary>Runs the walk on an abandoned background thread with a hard timeout - if the worker
    /// hasn't finished by OverallTimeout, this returns a "timed out" result and simply stops waiting;
    /// the worker thread is background (dies with the process) and never joined past the deadline.</summary>
    public static ScanResult Scan(int pid)
    {
        ScanResult? result = null;
        var worker = new Thread(() =>
        {
            try { result = ScanCore(pid); }
            catch (Exception ex) { result = new ScanResult(true, 0, 0, 0, $"Scan failed: {ex.Message}"); }
        })
        { IsBackground = true };

        worker.Start();
        bool finished = worker.Join(OverallTimeout + TimeSpan.FromSeconds(1)); // small grace margin over the inner deadline

        if (!finished || result is null)
        {
            return new ScanResult(false, 0, 0, 0,
                "Scan timed out and was abandoned - the process may have an unusually large or fast-changing address space.");
        }
        return result;
    }

    private static ScanResult ScanCore(int pid)
    {
        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
            if (hProcess == IntPtr.Zero)
                return new ScanResult(true, 0, 0, 0, "Couldn't open the process (access denied, or it has already exited).");

            var deadline = DateTime.UtcNow + OverallTimeout;
            long address = 0;
            int regionCount = 0;
            long totalBytes = 0;
            int walked = 0;
            int structSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

            while (walked < MaxRegions)
            {
                if (DateTime.UtcNow > deadline)
                    return new ScanResult(false, regionCount, totalBytes, walked, "Scan exceeded its 5-second time budget and stopped early - the totals below are a partial (lower-bound) count.");

                IntPtr written = VirtualQueryEx(hProcess, (IntPtr)address, out var mbi, (IntPtr)structSize);
                if (written == IntPtr.Zero)
                    break; // reached the end of the address space, or the address is no longer valid - either way, done.

                walked++;

                if (mbi.State == MemCommit && mbi.Type == MemPrivate && IsExecutableProtect(mbi.Protect))
                {
                    regionCount++;
                    totalBytes += (long)mbi.RegionSize;
                }

                long regionSize = (long)mbi.RegionSize;
                if (regionSize <= 0) break; // guard against a non-advancing/garbage region size
                long next = (long)mbi.BaseAddress + regionSize;
                if (next <= address) break; // guard against a non-advancing loop
                address = next;
            }

            return new ScanResult(true, regionCount, totalBytes, walked,
                walked >= MaxRegions ? $"Stopped after the {MaxRegions:N0}-region inspection cap - the totals below are a partial (lower-bound) count." : null);
        }
        finally
        {
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
        }
    }

    /// <summary>Matches PAGE_EXECUTE / PAGE_EXECUTE_READ / PAGE_EXECUTE_READWRITE /
    /// PAGE_EXECUTE_WRITECOPY, masking off the modifier bits (PAGE_GUARD/PAGE_NOCACHE/
    /// PAGE_WRITECOMBINE) that can be OR'd onto any base protection constant.</summary>
    private static bool IsExecutableProtect(uint protect)
    {
        uint baseProtect = protect & 0xFF;
        return baseProtect is PageExecute or PageExecuteRead or PageExecuteReadWrite or PageExecuteWriteCopy;
    }

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    private const uint MemCommit = 0x1000;
    private const uint MemPrivate = 0x20000;

    private const uint PageExecute = 0x10;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageExecuteWriteCopy = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
