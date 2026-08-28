namespace TaskManagerPlus.Models;

/// <summary>
/// #350: one NTFS volume's on-disk geometry facts, from `fsutil fsinfo ntfsinfo &lt;vol&gt;` -
/// NTFS-only (the command itself is NTFS-specific), read once alongside this volume's other
/// per-row facts (see StorageViewModel.LoadRowNtfsDetailsAsync). Available=false on any read/parse
/// failure so the card shows "Unknown" rather than zeros.
/// </summary>
public sealed class NtfsGeometryFacts
{
    public bool Available { get; init; }
    public string UnavailableReason { get; init; } = string.Empty;

    public uint? BytesPerCluster { get; init; }
    public uint? BytesPerSector { get; init; }
    public uint? BytesPerPhysicalSector { get; init; }
    public ulong? MftStartLcn { get; init; }
    public ulong? MftZoneStart { get; init; }
    public ulong? MftZoneEnd { get; init; }
    public ulong? MftValidDataLengthBytes { get; init; }

    /// <summary>$LogFile size in bytes. `fsutil fsinfo ntfsinfo` does not document (or, on every
    /// build actually tested while building this feature) report this field at all - best-effort
    /// parsed if a recognizable "...Log File Size..." line is ever present, left null (shown as
    /// "Unknown") otherwise rather than guessed from LFS Version or any other unrelated field.</summary>
    public ulong? LogFileSizeBytes { get; init; }

    public string RawText { get; init; } = string.Empty;
}
