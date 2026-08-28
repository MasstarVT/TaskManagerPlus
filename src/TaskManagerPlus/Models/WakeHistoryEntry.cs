namespace TaskManagerPlus.Models;

/// <summary>#654: one wake-history row, combining `powercfg /lastwake`'s own one-line summary with
/// the Kernel-Power event 107 (resume) and Microsoft-Windows-Power-Troubleshooter event 1
/// (sleep/wake timestamps + wake-source insertion string) event-log entries -
/// EventLogService.ReadWakeHistoryEvents reads both; Power-Troubleshooter's event 1 is the single
/// best record of *why* Windows woke, when it names a source at all ("Unknown" is a common, honest
/// value it reports itself, not a gap in this app's own parsing).</summary>
public sealed class WakeHistoryEntry
{
    /// <summary>Null for a plain Kernel-Power 107 resume marker, which carries no matching
    /// sleep-entry timestamp of its own.</summary>
    public DateTime? SleepTime { get; init; }

    public DateTime WakeTime { get; init; }

    public string WakeSource { get; init; } = string.Empty;

    /// <summary>"Power-Troubleshooter" (has a real wake-source string and a sleep timestamp) or
    /// "Kernel-Power 107" (resume marker only, no source attribution) - shown so a plain resume
    /// marker doesn't read as a confirmed "Unknown" source from the richer event.</summary>
    public string RecordSource { get; init; } = string.Empty;
}
