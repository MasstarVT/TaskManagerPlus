namespace TaskManagerPlus.Models;

public enum DriveHealthLevel { Healthy, Watch, Replace }

/// <summary>#328: one physical disk's combined verdict, from the predicted-failure flag, critical
/// SMART attributes, NVMe critical-warning bits, pending sectors, and Storage Spaces pool-membership
/// status - deliberately three-state and reason-listing rather than a made-up numeric score (a
/// "73/100 health score" implies a precision none of the underlying signals actually have). Reasons
/// are the plain-language facts that produced the verdict, so "Watch"/"Replace" is never just a
/// color with no explanation - see StorageViewModel.ComputeDriveHealthVerdict.</summary>
public sealed class DriveHealthVerdict
{
    public int Index { get; init; }
    public string Model { get; init; } = string.Empty;
    public DriveHealthLevel Level { get; set; } = DriveHealthLevel.Healthy;
    public List<string> Reasons { get; } = new();

    public string LevelText => Level switch
    {
        DriveHealthLevel.Healthy => "Healthy",
        DriveHealthLevel.Watch => "Watch",
        _ => "Replace",
    };

    public string ReasonsText => Reasons.Count == 0 ? "No adverse signals detected." : string.Join(" · ", Reasons);
}
