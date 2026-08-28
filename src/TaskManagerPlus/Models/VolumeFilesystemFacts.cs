namespace TaskManagerPlus.Models;

/// <summary>
/// #345: one fixed, lettered volume's filesystem-level facts, from a single MSFT_Volume query
/// (root\Microsoft\Windows\Storage) plus a physical-sector-size cross-reference via the same
/// MSFT_Volume -&gt; MSFT_Partition -&gt; MSFT_Disk -&gt; MSFT_PhysicalDisk associator chain
/// DiskFragmentationService.GetMediaType already uses for HDD/SSD detection (extended there with a
/// PhysicalSectorSize read alongside the existing MediaType one). This is the anchor the
/// #337/#338/#339/#343 NTFS-specific facts attach to (see StorageViewModel.VolumeFilesystemRow) -
/// a non-NTFS volume shows "N/A" for those rather than a guessed value.
/// </summary>
public sealed class VolumeFilesystemFacts
{
    public string DriveLetter { get; init; } = string.Empty;
    public string FileSystemLabel { get; init; } = string.Empty;

    /// <summary>MSFT_Volume.FileSystem - already a friendly string ("NTFS"/"ReFS"/"FAT32"/"CSVFS"),
    /// simpler and more reliable than decoding the parallel numeric FileSystemType enum ourselves.</summary>
    public string FileSystemName { get; init; } = "Unknown";
    public bool IsNtfs { get; init; }
    public bool IsRefs { get; init; }

    /// <summary>MSFT_Volume.HealthStatus - NOTE this is a different enum from
    /// MSFT_VirtualDisk.HealthStatus (StorageSpaceInfo's), which StorageSpacesService decodes
    /// separately: 0 Healthy, 1 Scan Needed, 2 Spot Fix Needed, 3 Full Repair Needed.</summary>
    public string HealthStatus { get; init; } = "Unknown";
    public string OperationalStatus { get; init; } = string.Empty;

    public uint? AllocationUnitSizeBytes { get; init; }
    public string DedupModeText { get; init; } = "Unknown";

    public uint? PhysicalSectorSizeBytes { get; init; }

    /// <summary>True when the cluster (allocation unit) size is smaller than the underlying
    /// device's physical sector size - a 512-byte cluster on a 4Kn/512e drive causes read-modify-
    /// write penalties on every write smaller than one physical sector.</summary>
    public bool ClusterSmallerThanPhysicalSector =>
        AllocationUnitSizeBytes.HasValue && PhysicalSectorSizeBytes.HasValue &&
        AllocationUnitSizeBytes.Value < PhysicalSectorSizeBytes.Value;

    /// <summary>ReFS integrity-stream state - "N/A" for every volume: non-ReFS volumes don't have
    /// the concept at all, and for ReFS itself, integrity-stream state is a per-file attribute (the
    /// moral equivalent of Get-FileIntegrity) rather than a single per-volume flag, so a per-volume
    /// card can't show a real on/off here without a separate per-file sweep - degrading to a stated
    /// "N/A" rather than guessing one file's state represents the whole volume.</summary>
    public string RefsIntegrityStreamsText { get; init; } = "N/A (not a ReFS volume)";
}
