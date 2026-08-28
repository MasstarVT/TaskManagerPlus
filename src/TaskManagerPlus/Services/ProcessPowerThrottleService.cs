using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// #266: EcoQoS / power-throttling status per process, via the documented
/// GetProcessInformation(hProcess, ProcessPowerThrottling, ...) kernel32 API - a real Win32 API
/// (processthreadsapi.h), not an undocumented struct guess, so this is the "known API" tier rather
/// than the NtQuerySystemInformation/NtQueryInformationProcess tier SchedulerService/
/// ProcessPriorityService take. "This app is slow because Windows classified it as background" is
/// otherwise undiagnosable - EcoQoS parks a process's work on E-cores/low clocks with no UI
/// indication anywhere else in Windows.
///
/// Reuses the process's own already-open handle (System.Diagnostics.Process.Handle) rather than
/// opening a second one - ProcessMonitorService's per-tick sample loop already opens/reuses this
/// same handle for ProcessControlService.ReadGuiResourceCounts, so this rides that same handle at
/// no extra OpenProcess/CloseHandle cost per process per tick.
/// </summary>
public static class ProcessPowerThrottleService
{
    private const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(IntPtr hProcess, int processInformationClass, ref PROCESS_POWER_THROTTLING_STATE processInformation, uint processInformationSize);

    /// <summary>"Throttled (EcoQoS)", "Not throttled", or "Unknown" (access denied, unsupported
    /// Windows build, or the process has already exited) - never a guessed true/false.</summary>
    public static string ReadStatus(IntPtr processHandle)
    {
        if (processHandle == IntPtr.Zero) return "Unknown";
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE();
            uint size = (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>();
            if (!GetProcessInformation(processHandle, ProcessPowerThrottling, ref state, size))
                return "Unknown";

            bool throttled = (state.ControlMask & PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0
                           && (state.StateMask & PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0;
            return throttled ? "Throttled (EcoQoS)" : "Not throttled";
        }
        catch
        {
            return "Unknown";
        }
    }
}
