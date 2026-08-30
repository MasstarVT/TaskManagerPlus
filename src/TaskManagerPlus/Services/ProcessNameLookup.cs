using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>
/// The one shared "what's this pid's process name" lookup (Round 18, #1087). Five services
/// carried their own copy - WaitChainTraversalService, HungWindowService and
/// PresentMonitorService byte-identical, ProcessBandwidthEtwService drifted (it omitted the
/// using, leaking the Process object every lookup), and SharedMemoryInspectionService cached on
/// top of the same body. The fallback text for an unresolvable pid is deliberately left to each
/// caller (they genuinely differ: "(exited)", "PID {pid}", "(pid {pid})", the raw window title);
/// only the lookup-and-dispose itself is shared.
/// </summary>
public static class ProcessNameLookup
{
    /// <summary>Returns the process name for <paramref name="pid"/> (no ".exe" suffix), or null
    /// when the process has already exited or is a protected/system process this app can't query.
    /// The Process object is disposed on every path.</summary>
    public static string? TryGetProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
