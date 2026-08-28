namespace TaskManagerPlus.Models;

/// <summary>
/// #339: one entry from `fsutil repair enumerate &lt;volume&gt; $Corrupt` - a corruption NTFS
/// self-healing has logged for the volume. Microsoft's own docs for `fsutil repair enumerate` don't
/// publish a worked output example, so this is a best-effort paragraph split of the raw text (see
/// NtfsFilesystemService.ParseCorruptionRecords) rather than a strict field-by-field parse - Index is
/// this app's own 1-based display ordinal (not a value fsutil assigns), Description is the whole
/// entry's text verbatim.
/// </summary>
public sealed class NtfsCorruptionRecord
{
    public int Index { get; init; }
    public string Description { get; init; } = string.Empty;
}
