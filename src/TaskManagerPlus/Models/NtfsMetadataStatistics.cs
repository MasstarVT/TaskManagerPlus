namespace TaskManagerPlus.Models;

/// <summary>
/// #349: one `fsutil fsinfo statistics &lt;vol&gt;` read's cumulative NTFS metadata-operation
/// counters (session-scoped - these reset on remount, same as MSFT_StorageReliabilityCounter's
/// counters). StorageViewModel keeps the previous sample plus its timestamp and derives per-second
/// deltas from two samples taken a tick apart, rather than this model trying to carry a rate itself.
/// </summary>
public sealed class NtfsMetadataStatistics
{
    public bool Available { get; init; }
    public string UnavailableReason { get; init; } = string.Empty;

    public long MftReads { get; init; }
    public long MftWrites { get; init; }
    public long MetaDataReads { get; init; }
    public long MetaDataWrites { get; init; }
    public long LogFileWrites { get; init; }
}
