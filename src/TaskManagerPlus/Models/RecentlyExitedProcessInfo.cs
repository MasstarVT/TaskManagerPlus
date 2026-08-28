namespace TaskManagerPlus.Models;

/// <summary>
/// Round 17, item 63: one process seen in a previous Processes-tab poll tick that's gone in the
/// current one - "my app closed itself" turned into "exit code 0xC0000005 (STATUS_ACCESS_
/// VIOLATION)" wherever an exit code was actually obtainable. ExitCode is set only when
/// ProcessesViewModel managed to hook System.Diagnostics.Process.Exited on this pid BEFORE it
/// exited (see ProcessesViewModel.TrackForExit/OnTrackedProcessExited) - the app has no live OS
/// notification for "a process just died" beyond that, so a pid that exited too quickly to ever
/// get hooked (or belonged to a protected process this app couldn't open a handle to) degrades to
/// null here rather than a fabricated code, per CLAUDE.md's "degrade to Unknown, never fabricate".
/// </summary>
public sealed class RecentlyExitedProcessInfo
{
    public int Pid { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTime ExitTime { get; init; }
    public int? ExitCode { get; init; }

    /// <summary>Pre-formatted once by ProcessesViewModel (never computed live from the model, to
    /// keep Services calls out of the Models layer) - a plain decimal number for an ordinary small
    /// exit code, or an NtStatusLookup-decoded "0xC0000005 (STATUS_ACCESS_VIOLATION)" string for
    /// an NTSTATUS-shaped one (round 15, item 30's table, reused per this chunk's own item 63
    /// instruction), or "Exit code unavailable" when ExitCode itself is null.</summary>
    public string ExitCodeText { get; init; } = "Exit code unavailable";
}
