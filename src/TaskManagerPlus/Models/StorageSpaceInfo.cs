namespace TaskManagerPlus.Models;

/// <summary>One Storage Spaces virtual disk (#85) - health rollup for a software-RAID-style pool,
/// if the system has one configured at all (most desktops/laptops don't - see
/// StorageSpacesService's remarks for why this whole feature degrades to "not shown" rather than
/// an error on a system with no Storage Spaces pools).</summary>
public sealed class StorageSpaceInfo
{
    public string PoolName { get; init; } = string.Empty;
    public string VirtualDiskName { get; init; } = string.Empty;
    public string HealthStatus { get; init; } = "Unknown";
    public string OperationalStatus { get; init; } = string.Empty;
    public string ResiliencySettingName { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public bool IsHealthWarning { get; init; }
}
