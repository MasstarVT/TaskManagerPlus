using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// #201: resolves a kernel routine address (as reported by a DPC/ISR ETW event - see
/// DpcLatencyService) to the driver (.sys) that owns it, via the loaded-module base/size table
/// from NtQuerySystemInformation(SystemModuleInformation). There is no documented Win32 API for
/// this - EnumDeviceDrivers/GetDeviceDriverFileNameA gives file paths but not base+size together in
/// one convenient call the way this does, and every other native-interop service in this app
/// (CpuTopologyService, HandleInspectionService, NetworkConnectionsService) already takes the same
/// "no tool/WMI equivalent exists" exception to the "prefer a known tool" rule for exactly this kind
/// of low-level system-table read. Same defensive shape as HandleInspectionService.ReadSystemHandles
/// (grow-and-retry on STATUS_INFO_LENGTH_MISMATCH, empty list rather than throw on any other
/// failure) since it's the same NtQuerySystemInformation call, just a different information class.
/// </summary>
public static class DpcModuleMapService
{
    private const int SystemModuleInformation = 11;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    // RTL_PROCESS_MODULE_INFORMATION - Section is "not filled in" per Microsoft's own header
    // comment, so it (and MappedBase, unused here) are only present to keep field offsets correct.
    [StructLayout(LayoutKind.Sequential)]
    private struct RTL_PROCESS_MODULE_INFORMATION
    {
        public IntPtr Section;
        public IntPtr MappedBase;
        public IntPtr ImageBase;
        public uint ImageSize;
        public uint Flags;
        public ushort LoadOrderIndex;
        public ushort InitOrderIndex;
        public ushort LoadCount;
        public ushort OffsetToFileName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] FullPathName;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    public sealed record LoadedModule(string FileName, ulong Base, uint Size);

    /// <summary>Snapshots the currently loaded kernel-mode module list. Cheap enough to call once
    /// per sample (a single syscall, a few hundred KB buffer) - not cached across samples, since a
    /// driver can load/unload between them.</summary>
    public static List<LoadedModule> GetModuleMap()
    {
        int size = 1 << 20; // 1 MB starting guess, grown below if needed
        for (int attempt = 0; attempt < 8; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemModuleInformation, buffer, size, out int returnLength);
                if (status == StatusInfoLengthMismatch)
                {
                    size = returnLength > size ? returnLength + 0x10000 : size * 2;
                    continue;
                }
                if (status != 0) return new List<LoadedModule>();

                int count = Marshal.ReadInt32(buffer); // ULONG NumberOfModules
                var list = new List<LoadedModule>(count);
                int entrySize = Marshal.SizeOf<RTL_PROCESS_MODULE_INFORMATION>();
                IntPtr entryPtr = IntPtr.Add(buffer, 8); // 4 bytes padding before the array on x64, same as HandleInspectionService's handle table
                for (int i = 0; i < count; i++)
                {
                    var m = Marshal.PtrToStructure<RTL_PROCESS_MODULE_INFORMATION>(IntPtr.Add(entryPtr, i * entrySize));
                    string full = System.Text.Encoding.ASCII.GetString(m.FullPathName).TrimEnd('\0');
                    int slash = Math.Max(full.LastIndexOf('\\'), full.LastIndexOf('/'));
                    string fileName = slash >= 0 ? full[(slash + 1)..] : full;
                    if (fileName.Length == 0) continue;
                    list.Add(new LoadedModule(fileName, (ulong)m.ImageBase.ToInt64(), m.ImageSize));
                }
                return list;
            }
            catch
            {
                return new List<LoadedModule>();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return new List<LoadedModule>();
    }

    /// <summary>Finds the module whose [Base, Base+Size) range contains the given routine address.
    /// Returns null (never a guess) when no loaded module covers it.</summary>
    public static string? ResolveDriverName(List<LoadedModule> map, ulong address)
    {
        foreach (var m in map)
            if (address >= m.Base && address < m.Base + m.Size) return m.FileName;
        return null;
    }
}
