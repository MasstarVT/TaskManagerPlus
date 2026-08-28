using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>#385: one partition's alignment fact, from MSFT_Partition.Offset % 4096. Framed
/// factually - a misaligned partition costs read-modify-write overhead on 4Kn/512e media, not
/// framed as "your disk is broken".</summary>
public sealed class PartitionAlignmentInfo
{
    public int PartitionNumber { get; init; }
    public string DriveLetter { get; init; } = string.Empty;
    public long StartingOffsetBytes { get; init; }
    public long SizeBytes { get; init; }
    public bool IsAligned { get; init; }

    public string DriveLetterText => DriveLetter.Length > 0 ? $"{DriveLetter}:" : "(no letter)";
    public string SizeText => Formatting.FormatBytes(SizeBytes);
    public string AlignmentText => IsAligned
        ? $"Aligned (offset {StartingOffsetBytes:N0} bytes, divisible by 4096)"
        : $"Not aligned to 4K (offset {StartingOffsetBytes:N0} bytes) - costs read-modify-write overhead on 4Kn/512e media.";
}

/// <summary>#385: over-provisioning (unallocated tail space) and partition alignment for one disk -
/// its own "Layout" card, separate from the Controller card, loaded once at Storage-tab load
/// alongside the rest of the tab's one-time disk inventory. See DiskLayoutService.</summary>
public sealed class DiskLayoutInfo
{
    public int DiskIndex { get; init; }
    public string Model { get; init; } = string.Empty;
    public bool IsSsd { get; init; }
    public long DiskSizeBytes { get; init; }
    public long PartitionedBytes { get; init; }
    public long? LargestFreeExtentBytes { get; init; }
    public List<PartitionAlignmentInfo> Partitions { get; init; } = new();
    public bool Available { get; init; } = true;
    public string UnavailableReason { get; init; } = string.Empty;

    public bool AllAligned => Partitions.Count > 0 && Partitions.All(p => p.IsAligned);
    public int MisalignedCount => Partitions.Count(p => !p.IsAligned);

    public string MediaTypeText => IsSsd ? "SSD" : "HDD/Unknown";
    public string DiskSizeText => Formatting.FormatBytes(DiskSizeBytes);
    public string PartitionedText => Formatting.FormatBytes(PartitionedBytes);

    public string LargestFreeExtentText => LargestFreeExtentBytes is { } b ? Formatting.FormatBytes(b) : "Unknown";

    /// <summary>Informational summary framing #385's own brief: a real gap between disk size and
    /// partitioned size on an SSD reads as deliberate over-provisioning (a valid, common choice),
    /// not a problem - shown as a plain fact either way, never alarmed about.</summary>
    public string OverProvisioningSummaryText
    {
        get
        {
            if (DiskSizeBytes <= 0) return "Unknown disk size.";
            long unallocated = Math.Max(0, DiskSizeBytes - PartitionedBytes);
            double percent = DiskSizeBytes > 0 ? unallocated / (double)DiskSizeBytes * 100 : 0;
            string extent = LargestFreeExtentBytes is { } b ? $"; largest free extent {Formatting.FormatBytes(b)}" : string.Empty;
            string unallocatedText = unallocated == 0 ? "0 B" : Formatting.FormatBytes(unallocated);
            string baseText = $"{unallocatedText} unallocated ({percent:0.#}% of the disk){extent}.";
            return IsSsd && percent >= 3
                ? baseText + " On an SSD this can be deliberate over-provisioning (a valid, common choice to improve endurance/consistency) rather than a problem."
                : baseText;
        }
    }
}
