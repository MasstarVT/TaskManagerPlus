using System.Runtime.InteropServices;
using System.Text;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 16, #853: token privilege audit - lists a process's token privileges (OpenProcessToken +
/// GetTokenInformation(TokenPrivileges), returning a TOKEN_PRIVILEGES struct with an array of
/// LUID_AND_ATTRIBUTES; each LUID is resolved back to a friendly name via LookupPrivilegeName) and
/// flags the handful that are dangerous-if-unexpected (SeDebugPrivilege, SeTcbPrivilege,
/// SeLoadDriverPrivilege, SeBackupPrivilege, SeImpersonatePrivilege) ONLY when both (a) actually
/// ENABLED (the SE_PRIVILEGE_ENABLED attribute bit, not just present-but-disabled) and (b) the
/// process isn't Microsoft-signed - plenty of legitimate system processes legitimately hold these,
/// so bare presence isn't itself a signal. Every privilege found is still reported (state text
/// alongside each), not just the flagged ones - simplest safe approach per #853's own guidance,
/// rather than filtering the list down and losing context.
///
/// The Microsoft-signed check is passed in by the caller (ProcessesViewModel), which reuses
/// ProcessRow.Publisher - already computed every tick by ProcessMonitorService/SignatureCheckService
/// - rather than this service re-running a signature check of its own.
///
/// A single OpenProcessToken + GetTokenInformation call, no loop/scan over a large table - same
/// safety tier as ProcessMitigationService/ProcessTokenInspectionService (every step wrapped,
/// degrades to an empty result with an error message rather than throwing past the caller); unlike
/// HandleInspectionService's system-handle walk, this needs no abandon-on-background-thread
/// treatment since nothing here is known to hang.
/// </summary>
public static class TokenPrivilegeAuditService
{
    private static readonly string[] WatchList =
    {
        "SeDebugPrivilege", "SeTcbPrivilege", "SeLoadDriverPrivilege", "SeBackupPrivilege", "SeImpersonatePrivilege",
    };

    public static (List<TokenPrivilegeInfo> Privileges, string? Error) ReadPrivileges(int pid, bool isMicrosoftSigned)
    {
        IntPtr hProcess = IntPtr.Zero;
        IntPtr hToken = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            hProcess = OpenProcess(ProcessQueryInformation, false, pid);
            if (hProcess == IntPtr.Zero)
                return (new List<TokenPrivilegeInfo>(), "Couldn't open this process to read its token (access denied).");

            if (!OpenProcessToken(hProcess, TokenQuery, out hToken))
                return (new List<TokenPrivilegeInfo>(), "Couldn't open this process's token (access denied).");

            GetTokenInformation(hToken, TokenPrivileges, IntPtr.Zero, 0, out int size);
            if (size <= 0) return (new List<TokenPrivilegeInfo>(), "Couldn't read token privileges.");

            buffer = Marshal.AllocHGlobal(size);
            if (!GetTokenInformation(hToken, TokenPrivileges, buffer, size, out _))
                return (new List<TokenPrivilegeInfo>(), "Couldn't read token privileges.");

            // TOKEN_PRIVILEGES is { DWORD PrivilegeCount; LUID_AND_ATTRIBUTES Privileges[]; } -
            // LUID_AND_ATTRIBUTES has no pointer-sized member (unlike, e.g.,
            // HandleInspectionService's SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX), so its natural alignment is
            // 4 bytes and the array starts immediately at offset 4 with no x64 padding gap.
            int count = Marshal.ReadInt32(buffer, 0);
            IntPtr arrayPtr = IntPtr.Add(buffer, 4);
            int entrySize = Marshal.SizeOf<LUID_AND_ATTRIBUTES>();

            var result = new List<TokenPrivilegeInfo>(count);
            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<LUID_AND_ATTRIBUTES>(IntPtr.Add(arrayPtr, i * entrySize));
                string name = LookupName(entry.Luid) ?? "(unknown privilege)";

                bool enabled = (entry.Attributes & SePrivilegeEnabled) != 0;
                bool defaultEnabled = (entry.Attributes & SePrivilegeEnabledByDefault) != 0;
                string stateText = enabled ? (defaultEnabled ? "Enabled (default)" : "Enabled") : "Disabled";
                bool watchListed = WatchList.Contains(name, StringComparer.OrdinalIgnoreCase);

                result.Add(new TokenPrivilegeInfo
                {
                    Name = name,
                    StateText = stateText,
                    Enabled = enabled,
                    IsWatchListed = watchListed,
                    IsFlagged = watchListed && enabled && !isMicrosoftSigned,
                });
            }

            return (result.OrderByDescending(p => p.IsFlagged).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(), null);
        }
        catch (Exception ex)
        {
            return (new List<TokenPrivilegeInfo>(), $"Couldn't read token privileges: {ex.Message}");
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
        }
    }

    private static string? LookupName(LUID luid)
    {
        int size = 256;
        var sb = new StringBuilder(size);
        return LookupPrivilegeName(null, ref luid, sb, ref size) ? sb.ToString() : null;
    }

    private const uint TokenQuery = 0x0008;
    private const int TokenPrivileges = 3;
    // OpenProcessToken is documented as requiring PROCESS_QUERY_INFORMATION specifically (not the
    // _LIMITED_ variant) - matches ProcessMitigationService's own precedent for this same kind of
    // token/policy-style OpenProcess call.
    private const uint ProcessQueryInformation = 0x0400;
    private const uint SePrivilegeEnabledByDefault = 0x1;
    private const uint SePrivilegeEnabled = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeName(string? lpSystemName, ref LUID lpLuid, StringBuilder lpName, ref int cchName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
