using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 18, #368: per-file I/O attribution via a time-boxed real-time ETW session on
/// Microsoft-Windows-Kernel-File and Microsoft-Windows-Kernel-Disk, aggregating bytes read/written
/// by file path and by PID to produce "top 20 files" / "top 20 processes" rankings for the capture
/// window - a lower level of detail than ProcessesViewModel's per-process DiskBytesPerSec (which
/// only totals per process, not per file), and exactly the "no tool/WMI alternative exists" case
/// CLAUDE.md reserves raw/lower-level APIs for.
///
/// This chunk ships the capture PATH - service shape, Storage tab "I/O capture" panel with a
/// 10-60s duration picker, top-files/top-processes result grids - without a live ETW session wired
/// up underneath it. See StorPortTraceService's remarks (the same reasoning applies here: adding a
/// real-time ETW consumer needs either a new NuGet dependency or substantial raw ETW/TDH P/Invoke,
/// and a session that leaks past its capture window can leave a stuck kernel trace session running
/// until reboot - not something to ship untested in this chunk). RunAsync below always returns
/// Available = false with an explanatory status, never a fabricated ranking, per this round's
/// explicit "clearly-labeled partial is acceptable" scope note. A future chunk that budgets time to
/// test the real session lifecycle end-to-end can fill in this method's body without touching the
/// Storage tab's UI/ViewModel plumbing at all - StorageViewModel already calls this exact signature.
/// </summary>
public static class FileIoAttributionService
{
    /// <summary>False in this build - see the class remarks above.</summary>
    public const bool IsImplemented = false;

    public static Task<FileIoAttributionResult> RunAsync(int durationSeconds, CancellationToken cancellationToken)
    {
        return Task.FromResult(new FileIoAttributionResult
        {
            Available = false,
            StatusText = "Not implemented in this build - per-file I/O attribution needs a real-time " +
                         "ETW session on Microsoft-Windows-Kernel-File/Kernel-Disk, which needs either the " +
                         "Microsoft.Diagnostics.Tracing.TraceEvent NuGet package or raw ETW/TDH P/Invoke " +
                         "(see FileIoAttributionService's remarks). The duration picker and result grids " +
                         "below are wired up and ready for when that session logic is added.",
        });
    }
}
