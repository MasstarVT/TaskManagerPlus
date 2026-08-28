using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 18, #367: StorPort driver-level latency tracing via a real-time ETW session on
/// Microsoft-Windows-StorPort (events 10/11 carry per-request start/complete with LUN and
/// duration - the lowest-level per-I/O timing source available on Windows, below the filesystem
/// and cache layer entirely; exactly the "no tool/WMI alternative exists" case CLAUDE.md reserves
/// raw/lower-level APIs for).
///
/// This chunk ships the capture PATH - service shape, Storage tab button, duration/threshold
/// controls, results grid - without a live ETW session wired up underneath it. A real-time ETW
/// consumer needs either a new NuGet dependency (Microsoft.Diagnostics.Tracing.TraceEvent - not
/// currently referenced by this project; see TaskManagerPlus.csproj) or several hundred lines of
/// raw StartTrace/EnableTraceEx2/OpenTrace/ProcessTrace/TDH P/Invoke, and a session that isn't torn
/// down correctly on every exit path (natural completion, user cancellation, an exception mid-
/// capture, or the app closing mid-capture) can leave a stuck kernel trace session running
/// system-wide until reboot. Rather than ship that untested in this chunk, RunAsync below always
/// returns Available = false with an explanatory status - never a fabricated result - per this
/// round's explicit "clearly-labeled partial is acceptable" scope note. A future chunk that budgets
/// time to test the real session lifecycle end-to-end (including cancel-mid-capture and crash-mid-
/// capture) can fill in this method's body without touching the Storage tab's UI/ViewModel plumbing
/// at all - StorageViewModel already calls this exact signature.
/// </summary>
public static class StorPortTraceService
{
    /// <summary>False in this build - see the class remarks above. Exposed so the ViewModel/View
    /// can label the feature accurately rather than implying a capture that never runs anything.</summary>
    public const bool IsImplemented = false;

    public static Task<StorPortTraceResult> RunAsync(int durationSeconds, double thresholdMs, CancellationToken cancellationToken)
    {
        return Task.FromResult(new StorPortTraceResult
        {
            Available = false,
            StatusText = "Not implemented in this build - StorPort ETW tracing needs either the " +
                         "Microsoft.Diagnostics.Tracing.TraceEvent NuGet package or raw ETW/TDH P/Invoke " +
                         "(see StorPortTraceService's remarks). The capture button, duration/threshold " +
                         "controls, and results grid below are wired up and ready for when that session " +
                         "logic is added.",
        });
    }
}
