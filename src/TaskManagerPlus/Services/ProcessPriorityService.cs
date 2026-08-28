using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// #270: I/O priority and memory priority per process, via NtQueryInformationProcess's
/// ProcessIoPriority (33) and ProcessMemoryPriority (39) information classes - undocumented but
/// stable, no Win32/WMI equivalent exists for either (the documented
/// GetProcessInformation/ProcessMemoryPriority class covers memory priority alone, but not I/O
/// priority, so both are read the same way here for one consistent call shape rather than mixing
/// two different APIs). Same "documented exception to prefer-a-known-tool" tier as
/// SchedulerService/CpuTopologyService/HandleInspectionService. Read-only - this app never calls
/// NtSetInformationProcess to change either.
///
/// Reuses the process's own already-open handle (System.Diagnostics.Process.Handle), the same
/// "no extra OpenProcess/CloseHandle per process per tick" shape ProcessPowerThrottleService takes.
/// </summary>
public static class ProcessPriorityService
{
    private const int ProcessIoPriority = 33;
    private const int ProcessMemoryPriorityClass = 39;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, IntPtr processInformation, int processInformationLength, out int returnLength);

    /// <summary>IO_PRIORITY_HINT - 0=Very Low (the classic "background mode" value, see
    /// PROCESS_MODE_BACKGROUND_BEGIN's documented semantics), 1=Low, 2=Normal, 3=High, 4=Critical
    /// (kernel-only, never observed on a user process in practice).</summary>
    private static string IoPriorityName(int v) => v switch
    {
        0 => "Very Low",
        1 => "Low",
        2 => "Normal",
        3 => "High",
        4 => "Critical",
        _ => $"Unknown ({v})",
    };

    /// <summary>MEMORY_PRIORITY_* - 0=Lowest through 5=Normal (the default for a foreground
    /// process).</summary>
    private static string MemoryPriorityName(int v) => v switch
    {
        0 => "Lowest",
        1 => "Very Low",
        2 => "Low",
        3 => "Medium",
        4 => "Below Normal",
        5 => "Normal",
        _ => $"Unknown ({v})",
    };

    /// <summary>Reads both priorities in one pass. Each field independently degrades to "Unknown"
    /// (access denied, unsupported class, or the process has already exited) rather than a guessed
    /// value - IsBackgroundIo is only ever true when the I/O priority read actually succeeded and
    /// came back Very Low/Low.</summary>
    public static (string IoPriorityText, bool IsBackgroundIo, string MemoryPriorityText) Read(IntPtr processHandle)
    {
        if (processHandle == IntPtr.Zero) return ("Unknown", false, "Unknown");

        string ioText = "Unknown";
        bool isBackground = false;
        IntPtr ioBuf = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (NtQueryInformationProcess(processHandle, ProcessIoPriority, ioBuf, sizeof(int), out _) == 0)
            {
                int io = Marshal.ReadInt32(ioBuf);
                ioText = IoPriorityName(io);
                isBackground = io is 0 or 1;
            }
        }
        catch { /* leave "Unknown" */ }
        finally { Marshal.FreeHGlobal(ioBuf); }

        string memText = "Unknown";
        IntPtr memBuf = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (NtQueryInformationProcess(processHandle, ProcessMemoryPriorityClass, memBuf, sizeof(int), out _) == 0)
            {
                int mem = Marshal.ReadInt32(memBuf);
                memText = MemoryPriorityName(mem);
            }
        }
        catch { /* leave "Unknown" */ }
        finally { Marshal.FreeHGlobal(memBuf); }

        return (ioText, isBackground, memText);
    }
}
