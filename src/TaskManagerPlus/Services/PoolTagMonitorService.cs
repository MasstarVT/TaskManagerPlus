using System.Runtime.InteropServices;
using System.Text;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 19, item 87: poolmon-style per-tag kernel pool allocation counts via the undocumented
/// NtQuerySystemInformation(SystemPoolTagInformation) call - no documented tool or WMI class
/// exposes this (poolmon.exe itself, when present, is just a WDK sample that calls the very same
/// API), so this is the "no tool/WMI exists" justified raw-interop case CLAUDE.md carves out,
/// following the same grow-the-buffer-and-retry pattern
/// HandleInspectionService.ReadSystemHandles already establishes for SystemExtendedHandleInformation
/// (whose own header is two pointer-sized fields - ULONG_PTR NumberOfHandles + ULONG_PTR Reserved -
/// rather than this API's padded ULONG Count; see the parse below).
///
/// Deliberately on-demand only (per this chunk's own instructions and CLAUDE.md's "on-demand vs.
/// polled" convention) - the full system pool-tag table commonly has several thousand entries, so
/// this is called from an explicit "Sample" button, never a timer.
/// </summary>
public static class PoolTagMonitorService
{
    private const int SystemPoolTagInformation = 22;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>Takes one sample of every pool tag currently known to the kernel. Returns null on
    /// total failure (API unavailable/access denied) - a real, expected outcome on a locked-down
    /// system, per CLAUDE.md's "degrade to Unknown, never fabricate".</summary>
    public static PoolTagSnapshot? Sample()
    {
        try
        {
            var tags = QueryPoolTags();
            return tags is null ? null : new PoolTagSnapshot { TakenAt = DateTime.Now, Tags = tags };
        }
        catch
        {
            return null;
        }
    }

    private static List<PoolTagSample>? QueryPoolTags()
    {
        int size = 1 << 20; // 1 MB starting guess - a real system commonly has 1,500-3,000 tags
        for (int attempt = 0; attempt < 10; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemPoolTagInformation, buffer, size, out int returnLength);
                if (status == StatusInfoLengthMismatch)
                {
                    size = returnLength > size ? returnLength + 0x10000 : size * 2;
                    continue;
                }
                if (status != 0) return null;

                // SYSTEM_POOLTAG_INFORMATION: ULONG Count, then SYSTEM_POOLTAG TagInfo[Count] - the
                // array's own 8-byte alignment requirement (SIZE_T members) pads Count out to an
                // 8-byte header on x64. (Not the header shape HandleInspectionService.ReadSystemHandles
                // parses for SystemExtendedHandleInformation - that one is two pointer-sized fields,
                // ULONG_PTR NumberOfHandles + ULONG_PTR Reserved.)
                int count = Marshal.ReadInt32(buffer);
                int entrySize = Marshal.SizeOf<SYSTEM_POOLTAG>();
                IntPtr entryPtr = IntPtr.Add(buffer, 8);

                var list = new List<PoolTagSample>(count);
                for (int i = 0; i < count; i++)
                {
                    var raw = Marshal.PtrToStructure<SYSTEM_POOLTAG>(IntPtr.Add(entryPtr, i * entrySize));
                    list.Add(new PoolTagSample
                    {
                        Tag = TagToString(raw.PoolTag),
                        PagedAllocs = raw.PagedAllocs,
                        PagedFrees = raw.PagedFrees,
                        PagedUsedBytes = (long)raw.PagedUsed,
                        NonPagedAllocs = raw.NonPagedAllocs,
                        NonPagedFrees = raw.NonPagedFrees,
                        NonPagedUsedBytes = (long)raw.NonPagedUsed,
                    });
                }
                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    /// <summary>The 4-byte tag union prints in its natural (non-reversed) byte order - it's read
    /// straight off the same little-endian buffer the kernel wrote, so no byte-swap is needed
    /// (unlike PoolTagLookup's own tag extraction, which has to try both orders because it starts
    /// from an already-formatted hex string of uncertain provenance). A non-printable byte (rare,
    /// but some internal tags use control bytes) falls back to a hex representation.</summary>
    private static string TagToString(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (bytes.All(b => b is >= 0x20 and <= 0x7E))
            return Encoding.ASCII.GetString(bytes);
        return $"0x{value:X8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POOLTAG
    {
        public uint PoolTag;
        public uint PagedAllocs;
        public uint PagedFrees;
        public UIntPtr PagedUsed;
        public uint NonPagedAllocs;
        public uint NonPagedFrees;
        public UIntPtr NonPagedUsed;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);
}
