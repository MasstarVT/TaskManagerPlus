using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 16, #857: "who's holding a handle to lsass.exe" - reuses the same system-wide handle-table
/// walk HandleInspectionService already established (NtQuerySystemInformation/
/// SystemExtendedHandleInformation, now exposed as internal ReadSystemHandles for this second consumer), but
/// in the opposite direction: instead of "what does process X have open" (#12), this asks "which
/// processes have a handle open to process lsass.exe specifically".
///
/// A raw system handle entry only carries the HOLDING process's pid, a numeric object-type index
/// that isn't stably documented across Windows builds, and a granted-access mask - there's no cheap
/// way to filter "is this a process handle, and does it point at lsass" from the raw entry alone.
/// The reliable technique (the same one tools like Process Hacker/System Informer use): duplicate
/// the handle into this process via NtDuplicateObject, then ask GetProcessId what process it refers
/// to - a non-process handle (file, mutex, registry key, ...) just fails that call harmlessly
/// (ERROR_INVALID_HANDLE, GetProcessId returns 0), so no upfront handle-type lookup is needed at all.
///
/// Unlike HandleInspectionService's ResolveHandleType, this never calls NtQueryObject - only
/// NtDuplicateObject + GetProcessId. HandleInspectionService's own remarks note that ONLY
/// NtQueryObject is known to occasionally hang forever on certain handle types (a named pipe with no
/// listener is the classic case) - NtDuplicateObject itself is called synchronously there too, with
/// no abandon-on-background-thread guard, so this follows that same established judgment rather than
/// adding a second, unnecessary threading layer.
///
/// Still capped (per-holder-process and system-wide) and time-boxed regardless, since a busy system
/// can have well over 100,000 open handles system-wide - this is an on-demand, button-triggered
/// scan, not something that needs full coverage. "A short list to eyeball, not an alert" - legitimate
/// holders (AV/EDR, Defender, other system processes) are common and expected; this reports what it
/// finds without characterizing any of it as malicious.
/// </summary>
public static class LsassHandleWatchService
{
    private const int MaxHandlesPerHolderProcess = 400; // same per-process cap HandleInspectionService uses
    private const int MaxTotalHandlesInspected = 20000;
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(20);

    public sealed record Finding(
        int HolderPid, string HolderProcessName, string HolderSignatureStatus,
        uint GrantedAccess, string GrantedAccessText, int HandleCount);

    public static (List<Finding> Findings, string? Error) Scan()
    {
        int lsassPid;
        try
        {
            var candidates = Process.GetProcessesByName("lsass");
            try
            {
                if (candidates.Length == 0) return (new List<Finding>(), "Couldn't find lsass.exe on this system.");
                lsassPid = candidates[0].Id;
            }
            finally
            {
                foreach (var p in candidates) p.Dispose();
            }
        }
        catch (Exception ex)
        {
            return (new List<Finding>(), $"Couldn't look up lsass.exe: {ex.Message}");
        }

        // (holder pid) -> (all granted-access bits OR'd together, handle count) - a process holding
        // several handles to lsass collapses to one row, per the "short list to eyeball" framing.
        var byHolder = new Dictionary<int, (uint AccessMask, int Count)>();
        var deadline = DateTime.UtcNow + OverallTimeout;
        int totalInspected = 0;
        bool timedOut = false;

        try
        {
            var entries = HandleInspectionService.ReadSystemHandles();
            var groups = entries
                .Where(e => (long)e.UniqueProcessId != lsassPid) // a process's handles to itself aren't a "watcher"
                .GroupBy(e => (int)e.UniqueProcessId);

            foreach (var group in groups)
            {
                if (DateTime.UtcNow > deadline) { timedOut = true; break; }

                int holderPid = group.Key;
                IntPtr sourceProcess = IntPtr.Zero;
                try
                {
                    sourceProcess = OpenProcess(ProcessDupHandle, false, holderPid);
                    if (sourceProcess == IntPtr.Zero) continue; // access denied / already exited - best-effort, skip

                    int perProcessChecked = 0;
                    foreach (var entry in group)
                    {
                        if (perProcessChecked++ >= MaxHandlesPerHolderProcess) break;
                        if (++totalInspected > MaxTotalHandlesInspected || DateTime.UtcNow > deadline) { timedOut = true; break; }

                        if (!TryMatchesLsass(sourceProcess, entry.HandleValue, lsassPid)) continue;

                        if (byHolder.TryGetValue(holderPid, out var existing))
                            byHolder[holderPid] = (existing.AccessMask | entry.GrantedAccess, existing.Count + 1);
                        else
                            byHolder[holderPid] = (entry.GrantedAccess, 1);
                    }
                }
                catch
                {
                    // Best-effort per holder process - one failing to inspect shouldn't sink the scan.
                }
                finally
                {
                    if (sourceProcess != IntPtr.Zero) CloseHandle(sourceProcess);
                }

                if (timedOut) break;
            }
        }
        catch (Exception ex)
        {
            return (new List<Finding>(), $"Scan failed: {ex.Message}");
        }

        var findings = byHolder
            .Select(kv => BuildFinding(kv.Key, kv.Value.AccessMask, kv.Value.Count))
            .OrderByDescending(f => f.GrantedAccess)
            .ToList();

        string? error = timedOut ? "Scan stopped early after timing out or hitting its handle-count cap - results may be incomplete." : null;
        return (findings, error);
    }

    /// <summary>Duplicates one handle and asks GetProcessId whether it refers to lsassPid - see the
    /// class remarks for why this needs no NtQueryObject call (and so no hang-guard) at all.</summary>
    private static bool TryMatchesLsass(IntPtr sourceProcess, IntPtr handleValue, int lsassPid)
    {
        IntPtr dup = IntPtr.Zero;
        try
        {
            int status = NtDuplicateObject(sourceProcess, handleValue, GetCurrentProcess(),
                out dup, 0, 0, DuplicateSameAccess);
            if (status != 0 || dup == IntPtr.Zero) return false;

            uint pid = GetProcessId(dup);
            return pid != 0 && pid == (uint)lsassPid;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (dup != IntPtr.Zero) { try { CloseHandle(dup); } catch { /* best-effort */ } }
        }
    }

    private static Finding BuildFinding(int pid, uint accessMask, int handleCount)
    {
        string name = "(exited)";
        string? filePath = null;
        try
        {
            using var proc = Process.GetProcessById(pid);
            name = proc.ProcessName;
            try { filePath = proc.MainModule?.FileName; } catch { /* protected process - leave null, degrades to Unknown below */ }
        }
        catch
        {
            // Process exited between the scan and this lookup - report what little is known.
        }

        return new Finding(pid, name, SignatureCheckService.GetStatus(filePath), accessMask, DecodeAccessMask(accessMask), handleCount);
    }

    // ---- PROCESS_* access-right bits (winnt.h) - a friendly-name decode, deliberately not
    // exhaustive per #857's own guidance ("doesn't need to be exhaustive"). ----
    private const uint ProcessAllAccess = 0x1FFFFF; // post-Vista value

    private static readonly (uint Bit, string Name)[] AccessBits =
    {
        (0x0001, "PROCESS_TERMINATE"),
        (0x0002, "PROCESS_CREATE_THREAD"),
        (0x0008, "PROCESS_VM_OPERATION"),
        (0x0010, "PROCESS_VM_READ"),
        (0x0020, "PROCESS_VM_WRITE"),
        (0x0040, "PROCESS_DUP_HANDLE"),
        (0x0080, "PROCESS_CREATE_PROCESS"),
        (0x0100, "PROCESS_SET_QUOTA"),
        (0x0200, "PROCESS_SET_INFORMATION"),
        (0x0400, "PROCESS_QUERY_INFORMATION"),
        (0x0800, "PROCESS_SUSPEND_RESUME"),
        (0x1000, "PROCESS_QUERY_LIMITED_INFORMATION"),
    };

    private static string DecodeAccessMask(uint mask)
    {
        if ((mask & ProcessAllAccess) == ProcessAllAccess) return "PROCESS_ALL_ACCESS";

        var names = AccessBits.Where(b => (mask & b.Bit) != 0).Select(b => b.Name).ToList();
        return names.Count > 0 ? string.Join(", ", names) : $"0x{mask:X}";
    }

    private const uint ProcessDupHandle = 0x0040;
    private const uint DuplicateSameAccess = 2;

    [DllImport("ntdll.dll")]
    private static extern int NtDuplicateObject(IntPtr sourceProcessHandle, IntPtr sourceHandle, IntPtr targetProcessHandle,
        out IntPtr targetHandle, uint desiredAccess, uint attributes, uint options);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
