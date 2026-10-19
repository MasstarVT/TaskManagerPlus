using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #799: diffs a process's own PATH/TEMP (read via ProcessEnvironmentService's PEB walk - the
/// only way to read an arbitrary already-running process's environment, see that service's own
/// remarks) against the machine+user environment as it stands right now
/// (EnvironmentHealthService.ReadEffectivePathAndTemp). A process only ever inherits its
/// environment once, at CreateProcess time - it never picks up a later PATH/TEMP edit on its own,
/// which is exactly the gap this flags: "this process inherited an environment from before your
/// last change - restart it to pick up the new value."
///
/// Only processes running as this app's own user in this app's own session are compared. The
/// baseline is HKLM plus *this* user's HKCU, so a SYSTEM/LocalService/NetworkService or other-user
/// process (C:\Windows\TEMP, no user PATH) would always "differ" - a false "restart it", not drift.
/// </summary>
public static class ProcessEnvironmentDriftService
{
    /// <summary>Checks one already-selected process (the Processes tab's own "View environment"
    /// flow, #799's per-row use) - cheap, a single PEB walk.</summary>
    public static ProcessEnvironmentDrift CheckSingle(int pid, string processName, List<string> processEnvironment)
    {
        if (processEnvironment.Count == 0 || !processEnvironment.Any(e => e.Contains('=')))
        {
            return new ProcessEnvironmentDrift { Pid = pid, ProcessName = processName, HasDrift = false, Detail = "Environment couldn't be read for this process (see above) - drift can't be determined." };
        }

        if (!IsSameUserAndSession(pid, CurrentUserSid(), CurrentSessionId()))
        {
            return new ProcessEnvironmentDrift { Pid = pid, ProcessName = processName, HasDrift = false, Detail = "Runs as a different account or in a different session (or its owner couldn't be read) - its environment comes from that account, so it isn't compared against yours." };
        }

        var (effectivePath, effectiveTemp) = EnvironmentHealthService.ReadEffectivePathAndTemp();
        return BuildDrift(pid, processName, processEnvironment, effectivePath, effectiveTemp);
    }

    /// <summary>#799's Windows Health tab summary: an explicit, on-demand sweep of every currently
    /// running process (a PEB walk per process, so - like this app's other heavier on-demand scans,
    /// e.g. DISM's component-store analysis - this is gated behind its own button, never run on a
    /// tick). Best-effort: a process this app can't open (protected/elevated beyond this app's own
    /// elevation, or one that exits mid-scan), or one owned by another account/session (see the
    /// class remarks), is silently skipped rather than counted as checked or drifted.</summary>
    public static async Task<(int Checked, List<ProcessEnvironmentDrift> Drifted)> ScanAllAsync()
    {
        return await Task.Run(() =>
        {
            var (effectivePath, effectiveTemp) = EnvironmentHealthService.ReadEffectivePathAndTemp();
            var drifted = new List<ProcessEnvironmentDrift>();
            int checkedCount = 0;
            var mySid = CurrentUserSid();
            int mySession = CurrentSessionId();

            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return (0, drifted); }

            foreach (var proc in processes)
            {
                try
                {
                    int pid = proc.Id;
                    string name = proc.ProcessName;
                    if (!IsSameUserAndSession(pid, mySid, mySession)) continue;
                    var env = ProcessEnvironmentService.Read(pid);
                    // ProcessEnvironmentService degrades to a single explanatory placeholder line
                    // (no "=" in it) for anything it couldn't read - those aren't real environment
                    // data, so they're not counted as "checked" at all.
                    if (env.Count == 0 || !env.Any(e => e.Contains('='))) continue;

                    checkedCount++;
                    var drift = BuildDrift(pid, name, env, effectivePath, effectiveTemp);
                    if (drift.HasDrift) drifted.Add(drift);
                }
                catch { /* one process shouldn't abort the whole sweep */ }
                finally { proc.Dispose(); }
            }

            return (checkedCount, drifted);
        }).ConfigureAwait(false);
    }

    private static ProcessEnvironmentDrift BuildDrift(int pid, string processName, List<string> processEnvironment, string effectivePath, string effectiveTemp)
    {
        string? procPath = FindValue(processEnvironment, "PATH");
        string? procTemp = FindValue(processEnvironment, "TEMP") ?? FindValue(processEnvironment, "TMP");

        var mismatches = new List<string>();
        if (procPath is not null && !PathsEquivalent(procPath, effectivePath))
            mismatches.Add("PATH");
        if (procTemp is not null && !string.Equals(LongPath(procTemp).TrimEnd('\\'), LongPath(effectiveTemp).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            mismatches.Add("TEMP");

        bool hasDrift = mismatches.Count > 0;
        string detail = hasDrift
            ? $"This process's {string.Join(" and ", mismatches)} no longer match{(mismatches.Count == 1 ? "es" : "")} the current machine+user value - it inherited its environment before the last change. Restart it to pick up the new value."
            : "Matches the current machine+user environment.";

        return new ProcessEnvironmentDrift { Pid = pid, ProcessName = processName, HasDrift = hasDrift, Detail = detail };
    }

    /// <summary>Windows builds a logon's TEMP/TMP in 8.3 short form whenever the profile path has
    /// a long component (C:\Users\ADMINI~1\AppData\Local\Temp), while the registry value expands to
    /// the long form - the same directory, so compare both long. Falls back to the input when the
    /// path doesn't exist or can't be resolved.</summary>
    private static string LongPath(string path)
    {
        try
        {
            var buffer = new StringBuilder(512);
            uint length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder((int)length + 1);
                length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
            }
            return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
        }
        catch
        {
            return path;
        }
    }

    private static string? FindValue(List<string> env, string name)
    {
        string prefix = name + "=";
        var line = env.FirstOrDefault(e => e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return line?[prefix.Length..];
    }

    /// <summary>Order-insensitive comparison, since a process's inherited PATH segment order can
    /// legitimately differ slightly from a freshly re-composed one without anything actually being
    /// "wrong" - what matters for #799's purpose is whether the *set* of directories changed.</summary>
    private static bool PathsEquivalent(string a, string b)
    {
        var setA = a.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\\').ToLowerInvariant()).ToHashSet();
        var setB = b.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\\').ToLowerInvariant()).ToHashSet();
        return setA.SetEquals(setB);
    }

    private static SecurityIdentifier? CurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User;
        }
        catch
        {
            return null;
        }
    }

    private static int CurrentSessionId()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.SessionId;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>True only when the process is in <paramref name="sessionId"/> and its primary
    /// token's user SID is <paramref name="userSid"/>; false when either can't be read, so an
    /// unreadable owner is skipped rather than compared.</summary>
    private static bool IsSameUserAndSession(int pid, SecurityIdentifier? userSid, int sessionId)
    {
        if (userSid is null || sessionId < 0) return false;

        IntPtr hProcess = IntPtr.Zero, hToken = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            if (!ProcessIdToSessionId((uint)pid, out uint processSession) || processSession != (uint)sessionId) return false;

            hProcess = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (hProcess == IntPtr.Zero) return false;
            if (!OpenProcessToken(hProcess, TokenQuery, out hToken)) return false;

            GetTokenInformation(hToken, TokenUserClass, IntPtr.Zero, 0, out int size);
            if (size <= 0) return false;
            buffer = Marshal.AllocHGlobal(size);
            if (!GetTokenInformation(hToken, TokenUserClass, buffer, size, out _)) return false;

            // TOKEN_USER is { SID_AND_ATTRIBUTES User } - the SID pointer comes first.
            IntPtr sidPtr = Marshal.ReadIntPtr(buffer, 0);
            return sidPtr != IntPtr.Zero && new SecurityIdentifier(sidPtr).Equals(userSid);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUserClass = 1; // TOKEN_INFORMATION_CLASS.TokenUser

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
