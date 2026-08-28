namespace TaskManagerPlus.Models;

/// <summary>
/// #408: one bucket of a process's address-space walk (private committed / image / mapped file /
/// reserved-not-committed / free), grouped by the same MEM_PRIVATE/MEM_IMAGE/MEM_MAPPED type and
/// MEM_COMMIT/MEM_RESERVE/MEM_FREE state VirtualQueryEx reports per region - see
/// AddressSpaceInspectionService.
/// </summary>
public sealed class AddressSpaceBucket
{
    public string Category { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public int RegionCount { get; set; }
}

/// <summary>
/// #408: the result of a single-process address-space walk - a bucketed breakdown plus the
/// largest contiguous free block, which is what actually distinguishes "leaking heap" (private
/// committed keeps climbing) from "reserving address space" (large MEM_RESERVE regions that
/// never commit) from "fragmented address space" (free bytes exist in total but no single block
/// is large enough for a big allocation).
/// </summary>
public sealed class AddressSpaceSummary
{
    public int Pid { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public List<AddressSpaceBucket> Buckets { get; set; } = new();
    public long LargestFreeBlockBytes { get; set; }
    public long TotalRegionsScanned { get; set; }
    public bool WasCapped { get; set; }
    public string? Error { get; set; }
}
