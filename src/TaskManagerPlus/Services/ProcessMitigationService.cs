using System.Diagnostics;
using System.Runtime.InteropServices;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, #851: reads a process's exploit-mitigation policy flags via GetProcessMitigationPolicy
/// (kernel32) - DEP, ASLR, Control Flow Guard, Arbitrary Code Guard (the "dynamic code" policy),
/// the binary-signature ("block non-Microsoft binaries") policy, CET user-mode shadow stack,
/// extension-point (legacy AppInit_DLLs-style shim) blocking, and child-process creation blocking.
/// These are the same flags Task Manager's own "Details" tab exposes as its "Mitigation policies"
/// column set, plus a couple more.
///
/// Every PROCESS_MITIGATION_*_POLICY struct below is a minimal, deliberately narrow transcription of
/// the public winnt.h layout (verified against Microsoft's own structure-reference pages) - a plain
/// DWORD Flags field for every policy except DEP (which has one trailing BOOLEAN Permanent byte
/// after its DWORD). Only the specific bits this class actually reports are named; everything else
/// is masked directly off Flags rather than declared as C#-side bitfields, since C# has no native
/// bitfield syntax and hand-rolled bit shifts are easier to get right (and to review) than a
/// [StructLayout(Explicit)] approximation of one.
///
/// PROCESS_MITIGATION_USER_SHADOW_STACK_POLICY (CET) is the one policy here that's version-gated
/// (Windows 10 2004+) and comparatively new - it gets its own extra try/catch on top of the shared
/// safety net below, and any process/OS combination where it's unsupported or unrecognized degrades
/// to "Unknown" rather than guessing.
///
/// Safety: this is a single GetProcessMitigationPolicy call per policy (not a loop or a scan), so
/// unlike #846/#848 there's no need for the abandon-past-timeout background-thread pattern - but
/// each call is still individually wrapped so one policy failing (unsupported on this OS build,
/// access denied, a struct-size mismatch) can never take the rest of the badge row down with it.
/// "Quick flag, not a verdict" applies here too: a mitigation being off doesn't mean a process is
/// compromised, and one being on doesn't mean it can't be.
/// </summary>
public static class ProcessMitigationService
{
    public static List<MitigationFlag> ReadMitigations(int pid)
    {
        var flags = new List<MitigationFlag>();
        IntPtr hProcess = IntPtr.Zero;
        try
        {
            using var proc = Process.GetProcessById(pid);
            hProcess = OpenProcess(ProcessQueryInformation, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                flags.Add(MitigationFlag.Unknown("Mitigations"));
                return flags;
            }

            flags.Add(ReadDep(hProcess));
            flags.Add(ReadAslr(hProcess));
            flags.Add(ReadDynamicCode(hProcess));
            flags.Add(ReadControlFlowGuard(hProcess));
            flags.Add(ReadSignaturePolicy(hProcess));
            flags.Add(ReadShadowStack(hProcess));
            flags.Add(ReadExtensionPointDisable(hProcess));
            flags.Add(ReadChildProcessPolicy(hProcess));
        }
        catch
        {
            // Process exited mid-read, or something else unexpected - whatever was collected so far
            // (possibly nothing) is still shown rather than throwing past the caller.
        }
        finally
        {
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
        }
        return flags;
    }

    private static MitigationFlag ReadDep(IntPtr hProcess)
    {
        try
        {
            if (TryGetPolicy<DEP_POLICY>(hProcess, ProcessDEPPolicy, out var p))
                return MitigationFlag.Of("DEP", (p.Flags & 0x1) != 0);
        }
        catch { /* fall through to Unknown */ }
        return MitigationFlag.Unknown("DEP");
    }

    private static MitigationFlag ReadAslr(IntPtr hProcess)
    {
        try
        {
            if (TryGetPolicy<FLAGS_ONLY_POLICY>(hProcess, ProcessASLRPolicy, out var p))
                return MitigationFlag.Of("ASLR", (p.Flags & 0x1) != 0); // EnableBottomUpRandomization
        }
        catch { }
        return MitigationFlag.Unknown("ASLR");
    }

    private static MitigationFlag ReadDynamicCode(IntPtr hProcess)
    {
        try
        {
            if (TryGetPolicy<FLAGS_ONLY_POLICY>(hProcess, ProcessDynamicCodePolicy, out var p))
                return MitigationFlag.Of("ACG", (p.Flags & 0x1) != 0); // ProhibitDynamicCode
        }
        catch { }
        return MitigationFlag.Unknown("ACG");
    }

    private static MitigationFlag ReadControlFlowGuard(IntPtr hProcess)
    {
        try
        {
            if (TryGetPolicy<FLAGS_ONLY_POLICY>(hProcess, ProcessControlFlowGuardPolicy, out var p))
                return MitigationFlag.Of("CFG", (p.Flags & 0x1) != 0); // EnableControlFlowGuard
        }
        catch { }
        return MitigationFlag.Unknown("CFG");
    }

    private static MitigationFlag ReadSignaturePolicy(IntPtr hProcess)
    {
        try
        {
            if (TryGetPolicy<FLAGS_ONLY_POLICY>(hProcess, ProcessSignaturePolicy, out var p))
                return MitigationFlag.Of("MS-signed only", (p.Flags & 0x1) != 0); // MicrosoftSignedOnly
        }
        catch { }
        return MitigationFlag.Unknown("MS-signed only");
    }

    /// <summary>CET user-mode shadow stack - Windows 10 2004+ only, so this gets its own extra
    /// defensive wrapper beyond the shared one, per #851's own guidance.</summary>
    private static MitigationFlag ReadShadowStack(IntPtr hProcess)
    {
        try
        {
            if (TryGetPolicy<FLAGS_ONLY_POLICY>(hProcess, ProcessUserShadowStackPolicy, out var p))
                return MitigationFlag.Of("CET", (p.Flags & 0x1) != 0); // EnableUserShadowStack
        }
        catch
        {
            // Struct/enum value unrecognized by this OS build, or the call itself failed for this
            // policy specifically - degrade to Unknown rather than guess.
        }
        return MitigationFlag.Unknown("CET");
    }

    private static MitigationFlag ReadExtensionPointDisable(IntPtr hProcess)
    {
        try
        {
            if (TryGetPolicy<FLAGS_ONLY_POLICY>(hProcess, ProcessExtensionPointDisablePolicy, out var p))
                return MitigationFlag.Of("Extension points blocked", (p.Flags & 0x1) != 0); // DisableExtensionPoints
        }
        catch { }
        return MitigationFlag.Unknown("Extension points blocked");
    }

    private static MitigationFlag ReadChildProcessPolicy(IntPtr hProcess)
    {
        try
        {
            if (TryGetPolicy<FLAGS_ONLY_POLICY>(hProcess, ProcessChildProcessPolicy, out var p))
                return MitigationFlag.Of("Child processes blocked", (p.Flags & 0x1) != 0); // NoChildProcessCreation
        }
        catch { }
        return MitigationFlag.Unknown("Child processes blocked");
    }

    private static bool TryGetPolicy<T>(IntPtr hProcess, int policy, out T result) where T : struct
    {
        result = default;
        int size = Marshal.SizeOf<T>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0); // zero-init defensively
            bool ok = GetProcessMitigationPolicy(hProcess, policy, buffer, (IntPtr)size);
            if (!ok) return false;
            result = Marshal.PtrToStructure<T>(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---- PROCESS_MITIGATION_POLICY ordinals (winnt.h / ntddk.h - verified sequential, 0-based) ----
    private const int ProcessDEPPolicy = 0;
    private const int ProcessASLRPolicy = 1;
    private const int ProcessDynamicCodePolicy = 2;
    private const int ProcessExtensionPointDisablePolicy = 6;
    private const int ProcessControlFlowGuardPolicy = 7;
    private const int ProcessSignaturePolicy = 8;
    private const int ProcessChildProcessPolicy = 13;
    private const int ProcessUserShadowStackPolicy = 15;

    private const uint ProcessQueryInformation = 0x0400;

    /// <summary>PROCESS_MITIGATION_DEP_POLICY: DWORD Flags (bit 0 = Enable) + BOOLEAN Permanent.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DEP_POLICY
    {
        public uint Flags;
        public byte Permanent;
    }

    /// <summary>Every other policy struct used here is just a single DWORD Flags bitfield with no
    /// trailing members - reused across ASLR/DynamicCode/CFG/Signature/ShadowStack/ExtensionPoint/
    /// ChildProcess since the C# side only ever needs bit 0 of each.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FLAGS_ONLY_POLICY
    {
        public uint Flags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMitigationPolicy(IntPtr hProcess, int mitigationPolicy, IntPtr lpBuffer, IntPtr dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
