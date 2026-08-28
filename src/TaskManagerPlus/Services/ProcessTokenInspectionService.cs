using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, #852: three cheap, single-syscall-per-process token/process reads, reused against the
/// process handle ProcessMonitorService already has open each tick for other purposes (the same
/// handle ReadGuiResourceCounts/IsSuspended already read from) - all three are genuinely inexpensive
/// (one OpenProcessToken + one or two GetTokenInformation calls, or one NtQueryInformationProcess
/// call), so unlike #846-#848/#851 these run as per-tick DataGrid columns rather than needing an
/// on-demand button.
///
/// (a) Integrity level: TokenIntegrityLevel's SID RID, mapped to the documented threshold ranges
///     (Untrusted/Low/Medium/MediumPlus/High/System/ProtectedProcess).
/// (b) Protection level: read via NtQueryInformationProcess(ProcessProtectionInformation = 61), which
///     returns a single-byte PS_PROTECTION struct (Type/Audit/Signer packed into one byte) - a clean,
///     well-documented, single-call read (used by tools like Process Hacker/System Informer), so this
///     is used directly rather than the access-denied-heuristic proxy #852 offers as a fallback for
///     when no simple direct read exists. If the call or class is ever unrecognized (older Windows,
///     unexpected failure), this degrades to "Unknown" rather than guessing.
/// (c) AppContainer: TokenIsAppContainer - a plain DWORD, nonzero = true.
///
/// Every read here is wrapped in its own try/catch and degrades to "Unknown"/false on any failure
/// (access denied on a handful of protected system processes even while elevated is expected and
/// normal) - never fabricated, per this app's "degrade to Unknown, never fabricate" rule.
/// </summary>
public static class ProcessTokenInspectionService
{
    /// <summary>#852(a): friendly integrity-level text from the process's primary token, using the
    /// documented RID thresholds (Untrusted=0, Low=0x1000, Medium=0x2000, MediumPlus=0x2100,
    /// High=0x3000, System=0x4000, ProtectedProcess=0x5000). Falls back to "Unknown" for anything
    /// that doesn't land cleanly on a recognized threshold rather than guessing.</summary>
    public static string ReadIntegrityLevel(IntPtr processHandle)
    {
        IntPtr hToken = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out hToken)) return "Unknown";

            GetTokenInformation(hToken, TokenIntegrityLevel, IntPtr.Zero, 0, out int size);
            if (size <= 0) return "Unknown";
            buffer = Marshal.AllocHGlobal(size);
            if (!GetTokenInformation(hToken, TokenIntegrityLevel, buffer, size, out _)) return "Unknown";

            // TOKEN_MANDATORY_LABEL is { SID_AND_ATTRIBUTES Label } - a pointer to the SID followed
            // by an attributes DWORD; the RID we need is the SID's last sub-authority.
            IntPtr sidPtr = Marshal.ReadIntPtr(buffer, 0);
            if (sidPtr == IntPtr.Zero) return "Unknown";

            byte subAuthorityCount = Marshal.ReadByte(sidPtr, 1);
            if (subAuthorityCount == 0) return "Unknown";

            // SID layout: Revision(1) + SubAuthorityCount(1) + IdentifierAuthority(6) + SubAuthority[n](4 each).
            int lastSubAuthorityOffset = 8 + 4 * (subAuthorityCount - 1);
            uint rid = unchecked((uint)Marshal.ReadInt32(sidPtr, lastSubAuthorityOffset));

            return rid switch
            {
                0 => "Untrusted",
                >= 0x1000 and < 0x2000 => "Low",
                >= 0x2000 and < 0x2100 => "Medium",
                >= 0x2100 and < 0x3000 => "Medium+",
                >= 0x3000 and < 0x4000 => "High",
                >= 0x4000 and < 0x5000 => "System",
                >= 0x5000 => "Protected",
                _ => "Unknown",
            };
        }
        catch
        {
            return "Unknown";
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
        }
    }

    /// <summary>#852(c): TokenIsAppContainer - a plain DWORD, nonzero = true.</summary>
    public static bool ReadIsAppContainer(IntPtr processHandle)
    {
        IntPtr hToken = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out hToken)) return false;

            int value = 0;
            int size = sizeof(int);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(hToken, TokenIsAppContainer, buffer, size, out _)) return false;
                value = Marshal.ReadInt32(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return value != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
        }
    }

    /// <summary>#852(b): real protection level via NtQueryInformationProcess(ProcessProtectionInformation),
    /// which returns a single-byte PS_PROTECTION: bits 0-2 = Type (0 None / 1 ProtectedLight (PPL) /
    /// 2 Protected), bit 3 = Audit, bits 4-7 = Signer. Not TOKEN-based like (a)/(c) above - a
    /// process-level, not token-level, query - so this opens its own short-lived handle rather than
    /// reusing the caller's, since PROCESS_QUERY_LIMITED_INFORMATION (what this needs) may differ
    /// from whatever access the caller's handle was opened with.</summary>
    public static string ReadProtectionLevel(int pid)
    {
        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (hProcess == IntPtr.Zero) return "Unknown";

            IntPtr buffer = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(buffer, 0, 0);
                int status = NtQueryInformationProcess(hProcess, ProcessProtectionInformation, buffer, 1, out int returnLength);
                if (status != 0 || returnLength < 1) return "Unknown";

                byte level = Marshal.ReadByte(buffer, 0);
                int type = level & 0x07;
                int signer = (level >> 4) & 0x0F;

                string typeText = type switch
                {
                    0 => "None",
                    1 => "PPL",
                    2 => "Protected",
                    _ => "Unknown",
                };
                if (type == 0) return "None";

                string? signerText = signer switch
                {
                    0 => "None",
                    1 => "Authenticode",
                    2 => "CodeGen",
                    3 => "Antimalware",
                    4 => "Lsa",
                    5 => "Windows",
                    6 => "WinTcb",
                    7 => "WinSystem",
                    8 => "App",
                    _ => null,
                };
                return signerText is null ? typeText : $"{typeText} ({signerText})";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return "Unknown";
        }
        finally
        {
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
        }
    }

    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int TokenIsAppContainer = 29;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessProtectionInformation = 61;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, IntPtr processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
