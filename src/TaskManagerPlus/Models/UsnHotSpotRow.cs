namespace TaskManagerPlus.Models;

/// <summary>
/// #347: one aggregated "hot spot" from a bounded USN-journal read - the changed file's own name
/// (directly present on every USN_RECORD) plus the raw Parent File Reference Number of its directory
/// at the time of the most recent record seen for it. Resolving that FRN into a real path would need
/// an OpenFileById-style lookup (raw interop with no existing Windows-tool equivalent) - out of scope
/// for this pass, so it's shown as a hex reference alongside the filename rather than silently
/// dropped, per UsnJournalService's remarks.
/// </summary>
public sealed class UsnHotSpotRow
{
    public string FileName { get; init; } = string.Empty;
    public int ChangeCount { get; init; }
    public string ParentFrnText { get; init; } = string.Empty;
    public string ReasonBreakdownText { get; init; } = string.Empty;
}

/// <summary>The full result of one #347 hot-spot read - a status line (record counts, any early-stop
/// note) alongside the top-20 rows, so a "0 rows because nothing changed" result reads differently
/// from a "0 rows because the read failed" one.</summary>
public sealed class UsnHotSpotResult
{
    public List<UsnHotSpotRow> Rows { get; init; } = new();
    public string StatusText { get; init; } = string.Empty;
}
