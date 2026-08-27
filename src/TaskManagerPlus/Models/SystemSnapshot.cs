namespace TaskManagerPlus.Models;

/// <summary>A point-in-time capture of installed software / services / startup items (#93/#94 -
/// "record how my PC looks when healthy" doubles as the baseline for a later "what changed" diff,
/// since both suggestions boil down to the same capture-then-compare mechanism). Saved as plain
/// JSON so a baseline captured today can be compared against the live system weeks later.</summary>
public sealed class SystemSnapshot
{
    public DateTime CapturedAt { get; init; }
    public List<string> InstalledSoftware { get; init; } = new();
    public List<string> Services { get; init; } = new();
    public List<string> StartupItems { get; init; } = new();
}

/// <summary>Result of comparing a saved baseline snapshot against the system's current state.</summary>
public sealed class SnapshotDiff
{
    public DateTime BaselineCapturedAt { get; init; }
    public List<string> SoftwareAdded { get; init; } = new();
    public List<string> SoftwareRemoved { get; init; } = new();
    public List<string> ServicesAdded { get; init; } = new();
    public List<string> ServicesRemoved { get; init; } = new();
    public List<string> StartupAdded { get; init; } = new();
    public List<string> StartupRemoved { get; init; } = new();

    public bool HasChanges =>
        SoftwareAdded.Count > 0 || SoftwareRemoved.Count > 0 ||
        ServicesAdded.Count > 0 || ServicesRemoved.Count > 0 ||
        StartupAdded.Count > 0 || StartupRemoved.Count > 0;
}
