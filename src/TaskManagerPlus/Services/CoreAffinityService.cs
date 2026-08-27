using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// Best-effort per-process core-affinity heatmap (Round 8 #24) - which logical cores a handful of
/// top-CPU processes' threads are currently scheduled to prefer. Windows exposes no live "which
/// core is this thread running on right now" query outside of ETW context-switch tracing - a much
/// heavier, higher-risk undertaking than anything else in this app takes on (this app has no ETW
/// consumer anywhere). GetThreadIdealProcessorEx instead reports the scheduler's *preferred* core
/// for each thread, which tracks actual scheduling closely on a typical desktop workload and is
/// the closest proxy available without ETW - framed in the UI as "preferred/ideal core", not "the
/// core it's running on this instant", the same "quick flag, not a verdict" tier as the CPU
/// throttle heuristic and the process signature check.
/// </summary>
public static class CoreAffinityService
{
    private const int ThreadQueryInformation = 0x0040;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(int dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadIdealProcessorEx(IntPtr hThread, out PROCESSOR_NUMBER lpIdealProcessor);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_NUMBER
    {
        public ushort Group;
        public byte Number;
        public byte Reserved;
    }

    /// <summary>Per logical-core-index list of (process name, pid) for every thread among the
    /// given processes whose ideal processor is that core. Callers should pass only a handful of
    /// processes (e.g. the current top few by CPU%) - this walks every thread of every process
    /// passed in, one native call each.</summary>
    public static Dictionary<int, List<(string ProcessName, int Pid)>> ComputeIdealProcessorLoad(IEnumerable<Process> processes)
    {
        var result = new Dictionary<int, List<(string, int)>>();

        foreach (var proc in processes)
        {
            string name;
            int pid;
            ProcessThreadCollection threads;
            try
            {
                name = proc.ProcessName;
                pid = proc.Id;
                threads = proc.Threads;
            }
            catch
            {
                continue; // process exited or is inaccessible - skip it
            }

            foreach (ProcessThread thread in threads)
            {
                IntPtr handle = IntPtr.Zero;
                try
                {
                    handle = OpenThread(ThreadQueryInformation, false, (uint)thread.Id);
                    if (handle == IntPtr.Zero) continue;
                    if (!GetThreadIdealProcessorEx(handle, out var idealProc)) continue;

                    int core = idealProc.Number;
                    if (!result.TryGetValue(core, out var list))
                        result[core] = list = new List<(string, int)>();
                    if (!list.Any(x => x.Item2 == pid))
                        list.Add((name, pid));
                }
                catch
                {
                    // Thread exited mid-scan, or access denied for a protected process - skip it.
                }
                finally
                {
                    if (handle != IntPtr.Zero) CloseHandle(handle);
                }
            }
        }

        return result;
    }
}
