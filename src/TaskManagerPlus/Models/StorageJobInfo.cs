namespace TaskManagerPlus.Models;

/// <summary>#387: one in-flight Storage Spaces job (rebuild/repair/optimize) affecting a virtual
/// disk - MSFT_StorageJob, associated via MSFT_StorageJobToAffectedStorageObject. Elapsed time and
/// percent complete are shown side by side, un-interpreted, so a pool stuck "repairing" at a low
/// percent after a long elapsed time is something the reader can notice for themselves rather than
/// a fabricated "stalled" verdict this single snapshot read has no way to actually confirm (that
/// would need history across repeated reads, which this on-demand card doesn't keep). See
/// StorageSpacesService.ReadActiveJobs.</summary>
public sealed class StorageJobInfo
{
    public string Name { get; init; } = string.Empty;
    public double PercentComplete { get; init; }
    public string JobStateText { get; init; } = "Unknown";
    public string ElapsedTimeText { get; init; } = string.Empty;
    public string ErrorDescription { get; init; } = string.Empty;
}
