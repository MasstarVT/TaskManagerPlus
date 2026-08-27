using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// Actions performed *on* a single already-known process (trim working set, suspend/resume,
/// priority, affinity, GDI/User handle counts) - a sibling to ProcessMonitorService, which only
/// samples. Kept separate because these are one-shot commands triggered from the UI (a button or
/// context-menu click), not something read every tick.
///
/// Suspend/Resume use NtSuspendProcess/NtResumeProcess (ntdll.dll) - undocumented but stable and
/// widely relied upon (Task Manager's own "Suspend process" Details-tab action, and every process
/// utility going back to Sysinternals' pslist, use the same two calls; there is no documented
/// Win32 equivalent for suspending every thread in a process atomically). Same interop-risk tier as
/// CpuTopologyService/NetworkConnectionsService's native calls elsewhere in this app - every call
/// here is wrapped so a failure (access denied, protected process, already exited) degrades to a
/// (false, message) result rather than throwing past this class.
/// </summary>
public static class ProcessControlService
{
    /// <summary>Trims a process's working set (#4) - the same effect as the old "Empty Working
    /// Set" tool, useful for troubleshooting a process that's ballooned in RAM without killing it.
    /// Windows will let it grow back on demand; this doesn't reduce a process's actual memory
    /// need, just how much of it is currently resident.</summary>
    public static (bool Success, string? Error) TrimWorkingSet(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (!EmptyWorkingSet(proc.Handle))
                return (false, "The OS declined the request (often access-denied on a protected/system process).");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>#8: pause every thread in a process without ending it - handy for a runaway process
    /// you want to inspect (or just stop from consuming CPU) without losing its state the way
    /// killing it would.</summary>
    public static (bool Success, string? Error) Suspend(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            int status = NtSuspendProcess(proc.Handle);
            return status == 0 ? (true, null) : (false, $"NTSTATUS 0x{status:X8}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Success, string? Error) Resume(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            int status = NtResumeProcess(proc.Handle);
            return status == 0 ? (true, null) : (false, $"NTSTATUS 0x{status:X8}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>#6: change a process's scheduling priority class. Process.PriorityClass is a plain
    /// managed wrapper over SetPriorityClass - no raw interop needed here.</summary>
    public static (bool Success, string? Error) SetPriority(int pid, ProcessPriorityClass priority)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.PriorityClass = priority;
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>#5: current CPU affinity mask, one bit per logical processor (0 = not runnable on
    /// that core). Returns null when it can't be read (access denied, exited).</summary>
    public static long? GetAffinity(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return (long)proc.ProcessorAffinity;
        }
        catch
        {
            return null;
        }
    }

    public static (bool Success, string? Error) SetAffinity(int pid, long mask)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.ProcessorAffinity = (IntPtr)mask;
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>#7: GDI and USER object handle counts, matching the optional "GDI objects"/"USER
    /// objects" Details-tab columns in real Task Manager. GetGuiResources needs
    /// PROCESS_QUERY_INFORMATION access to the target - denied for a handful of protected system
    /// processes even while elevated, in which case both counts degrade to 0 rather than
    /// throwing.</summary>
    public static (int Gdi, int User) ReadGuiResourceCounts(IntPtr processHandle)
    {
        try
        {
            int gdi = GetGuiResources(processHandle, GrObjectsGdi);
            int user = GetGuiResources(processHandle, GrObjectsUser);
            return (Math.Max(gdi, 0), Math.Max(user, 0));
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>Best-effort "is this process currently suspended" check - true only when it has at
    /// least one thread and every thread reports Wait/Suspended, mirroring how Task Manager itself
    /// infers a suspended status (there's no single "process state" flag on Windows).</summary>
    public static bool IsSuspended(Process proc)
    {
        try
        {
            if (proc.Threads.Count == 0) return false;
            foreach (ProcessThread thread in proc.Threads)
            {
                try
                {
                    if (thread.ThreadState != System.Diagnostics.ThreadState.Wait ||
                        thread.WaitReason != ThreadWaitReason.Suspended)
                        return false;
                }
                catch
                {
                    return false; // a thread we can't inspect - don't guess
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private const uint GrObjectsGdi = 0;
    private const uint GrObjectsUser = 1;

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetGuiResources(IntPtr hProcess, uint uiFlags);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);
}
