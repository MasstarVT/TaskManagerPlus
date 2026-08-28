using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>#386: one physical disk backing a Storage Spaces pool - MSFT_PhysicalDisk, associated
/// to the pool through MSFT_StoragePoolToPhysicalDisk. Nested under every virtual disk row that
/// shares the same pool (StorageSpaceInfo.MemberDisks) - see StorageSpacesService.ReadPoolMembers
/// for the associator chain and enum decoding.</summary>
public sealed class PoolMemberDiskInfo
{
    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>MSFT_PhysicalDisk.Usage: Auto-Select, Manual-Select, Hot Spare, Retired, Journal.</summary>
    public string UsageText { get; init; } = "Unknown";

    /// <summary>MSFT_PhysicalDisk.OperationalStatus, joined - e.g. "OK", "Lost Communication",
    /// "Failed Media". Empty array reads as "Unknown".</summary>
    public string OperationalStatusText { get; init; } = string.Empty;

    /// <summary>True when HealthStatus isn't Healthy(0) - drives the row's warning color in the UI.</summary>
    public bool IsUnhealthy { get; init; }

    public long SizeBytes { get; init; }
    public long AllocatedSizeBytes { get; init; }

    public string SizeText => Formatting.FormatBytes(SizeBytes);
    public string AllocatedText => Formatting.FormatBytes(AllocatedSizeBytes);
}
