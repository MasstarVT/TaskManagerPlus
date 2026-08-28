namespace TaskManagerPlus.Models;

/// <summary>
/// #346: one volume's USN change-journal status, from `fsutil usn queryjournal &lt;vol&gt;`.
/// Available=false when the volume has no active journal at all (a common, normal state - nothing
/// has ever enabled journaling on it) or the query otherwise failed - degrades to that rather than
/// fabricating zeroed facts, same as every other fsutil-backed fact in this file's sibling
/// NtfsFilesystemService.
/// </summary>
public sealed class UsnJournalStatus
{
    public bool Available { get; init; }
    public string UnavailableReason { get; init; } = string.Empty;

    public ulong? JournalId { get; init; }
    public ulong? FirstUsn { get; init; }
    public ulong? NextUsn { get; init; }
    public ulong? LowestValidUsn { get; init; }
    public ulong? MaxUsn { get; init; }
    public ulong? MaximumSizeBytes { get; init; }
    public ulong? AllocationDeltaBytes { get; init; }

    /// <summary>Quick flag, not a verdict: a fresh journal's First Usn is 0, so First Usn &gt; 0
    /// means the journal has already discarded its earliest records at least once (wrapped past its
    /// maximum size, or was deleted/recreated) - the usual reason a backup/search/indexing tool that
    /// tracks its own last-seen USN suddenly falls back to a full rescan.</summary>
    public bool LikelyWrapped => FirstUsn is > 0;

    public string RawText { get; init; } = string.Empty;
}
