using System.Diagnostics;
using System.Runtime.InteropServices;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #408: walks a single process's entire virtual address space with VirtualQueryEx, bucketing
/// every region by its MEM_COMMIT/MEM_RESERVE/MEM_FREE state and (for committed regions) its
/// MEM_PRIVATE/MEM_IMAGE/MEM_MAPPED type, plus tracks the largest contiguous free block. This is
/// the one honest way to tell apart three failure modes that all just look like "high memory"
/// from the outside: private committed bytes climbing (a genuine leaking heap), large MEM_RESERVE
/// regions that never actually commit (reserving address space up front, common for some
/// allocators/GC designs, not a leak), and a total free byte count that looks fine while no
/// single free block is big enough for the next large allocation (fragmentation).
///
/// Strictly on-demand, single-process, button-triggered - see ProcessesViewModel.ViewAddressSpaceCommand.
/// Never run on a tick: a full walk of a large, fragmented address space can be tens of thousands
/// of VirtualQueryEx calls, and 64-bit processes deliberately reserve address space far beyond
/// their committed footprint, so "how many regions" isn't bounded by "how much memory".
/// </summary>
public static class AddressSpaceInspectionService
{
    // Defensive bounds - VirtualQueryEx itself is a fast syscall (unlike NtQueryObject elsewhere
    // in this app, it isn't known to hang), but an extremely fragmented address space could still
    // mean an unreasonable number of tiny regions; cap both so a walk always finishes promptly.
    private const int MaxRegions = 250_000;
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(8);

    // The top of user-mode address space on 64-bit Windows (0x7FFFFFFEFFFF is the documented
    // maximum user-mode address); stops the walk cleanly rather than relying solely on
    // VirtualQueryEx returning 0.
    private const long UserModeAddressLimit = 0x7FFFFFFEFFFFL;

    public static AddressSpaceSummary Walk(int pid)
    {
        var summary = new AddressSpaceSummary { Pid = pid };
        try { summary.ProcessName = Process.GetProcessById(pid).ProcessName; } catch { /* best-effort label only */ }

        IntPtr process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (process == IntPtr.Zero)
        {
            summary.Error = "Couldn't open the process to read its address space (access denied, or it exited).";
            return summary;
        }

        var buckets = new Dictionary<string, AddressSpaceBucket>();
        void Add(string category, long size)
        {
            if (!buckets.TryGetValue(category, out var bucket))
            {
                bucket = new AddressSpaceBucket { Category = category };
                buckets[category] = bucket;
            }
            bucket.TotalBytes += size;
            bucket.RegionCount++;
        }

        try
        {
            var deadline = DateTime.UtcNow + OverallTimeout;
            long address = 0;
            int regions = 0;
            int structSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

            while (address < UserModeAddressLimit)
            {
                if (regions >= MaxRegions || DateTime.UtcNow > deadline)
                {
                    summary.WasCapped = true;
                    break;
                }

                IntPtr written = VirtualQueryEx(process, (IntPtr)address, out var info, (uint)structSize);
                if (written == IntPtr.Zero) break; // end of the address space, or the query itself failed.

                regions++;
                long regionSize = info.RegionSize.ToInt64();

                if (info.State == MemFree)
                {
                    Add("Free", regionSize);
                    if (regionSize > summary.LargestFreeBlockBytes) summary.LargestFreeBlockBytes = regionSize;
                }
                else if (info.State == MemReserve)
                {
                    Add("Reserved (not committed)", regionSize);
                }
                else if (info.State == MemCommit)
                {
                    string category = info.Type switch
                    {
                        MemImage => "Image (exe/dll)",
                        MemMapped => "Mapped file",
                        MemPrivate => "Private committed",
                        _ => "Committed (other)",
                    };
                    Add(category, regionSize);
                }
                else
                {
                    Add("(unknown state)", regionSize);
                }

                // Guard against a zero-size region (shouldn't happen, but would spin forever).
                if (regionSize <= 0) break;
                long next = info.BaseAddress.ToInt64() + regionSize;
                if (next <= address) break; // no forward progress - stop rather than loop.
                address = next;
            }

            summary.TotalRegionsScanned = regions;
        }
        catch (Exception ex)
        {
            summary.Error = $"Address-space walk failed partway through: {ex.Message}";
        }
        finally
        {
            CloseHandle(process);
        }

        summary.Buckets = buckets.Values.OrderByDescending(b => b.TotalBytes).ToList();
        return summary;
    }

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemFree = 0x10000;
    private const uint MemPrivate = 0x20000;
    private const uint MemMapped = 0x40000;
    private const uint MemImage = 0x1000000;

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
