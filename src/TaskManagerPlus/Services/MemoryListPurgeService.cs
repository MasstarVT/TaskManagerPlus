using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// #435: RAMMap-style "Empty standby list" / "Empty modified page list" / "Empty working sets"
/// actions via NtSetSystemInformation(SystemMemoryListInformation, &amp;command, sizeof(command)) -
/// the same undocumented-but-widely-used API RAMMap.exe and the community EmptyStandbyList.exe
/// tool are built on (there is no documented Win32 equivalent). SYSTEM_MEMORY_LIST_COMMAND's
/// values are a fixed, well-known enumeration order (0=CaptureAccessedBits,
/// 1=CaptureAndResetAccessedBits, 2=EmptyWorkingSets, 3=FlushModifiedList, 4=PurgeStandbyList,
/// 5=PurgeLowPriorityStandbyList) - not part of any public Windows SDK header, so it's hardcoded
/// here rather than referenced from one, the same tradeoff KernelObjectTypeService/
/// HandleInspectionService already take for other undocumented NT struct layouts.
///
/// This one is destructive/side-effecting rather than a read, so unlike this app's other native-
/// interop calls it's never run on an abandoned background thread with a timeout - a hung worker
/// there is fine to leak; a hung worker here would still be mutating live kernel memory lists out
/// from under a caller that gave up waiting. It's cheap to call directly instead: these are
/// documented (informally) to run synchronously and return promptly, not to be one of the calls
/// known to occasionally hang forever. The privileges these commands need
/// (SeProfileSingleProcessPrivilege for the standby/modified-list commands,
/// SeIncreaseQuotaPrivilege for the working-set command) are held by this app's elevated admin
/// token but not enabled by default - both are explicitly enabled before every call, mirroring
/// what RAMMap itself does.
/// </summary>
public static class MemoryListPurgeService
{
    private enum MemoryListCommand
    {
        EmptyWorkingSets = 2,
        FlushModifiedList = 3,
        PurgeStandbyList = 4,
        PurgeLowPriorityStandbyList = 5,
    }

    public static (bool Success, string? Error) PurgeStandbyList() => Execute(MemoryListCommand.PurgeStandbyList);
    public static (bool Success, string? Error) PurgeLowPriorityStandbyList() => Execute(MemoryListCommand.PurgeLowPriorityStandbyList);
    public static (bool Success, string? Error) FlushModifiedList() => Execute(MemoryListCommand.FlushModifiedList);
    public static (bool Success, string? Error) EmptyWorkingSets() => Execute(MemoryListCommand.EmptyWorkingSets);

    private static (bool Success, string? Error) Execute(MemoryListCommand command)
    {
        try
        {
            EnablePrivilege("SeProfileSingleProcessPrivilege");
            EnablePrivilege("SeIncreaseQuotaPrivilege");

            IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(buffer, (int)command);
                int status = NtSetSystemInformation(SystemMemoryListInformation, buffer, sizeof(int));
                return status == 0
                    ? (true, null)
                    : (false, $"NtSetSystemInformation failed (status 0x{status:X8}).");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Best-effort - if enabling the privilege fails (denied, already enabled, doesn't
    /// exist in this token), the subsequent NtSetSystemInformation call is left to succeed or fail
    /// on its own; this never throws out to the caller.</summary>
    private static void EnablePrivilege(string privilegeName)
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
                return;
            try
            {
                if (!LookupPrivilegeValue(null, privilegeName, out LUID luid))
                    return;

                var tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED,
                };
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch { /* best-effort */ }
    }

    private const int SystemMemoryListInformation = 0x50; // 80
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
