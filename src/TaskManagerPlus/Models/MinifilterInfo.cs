namespace TaskManagerPlus.Models;

/// <summary>
/// #493: one row from `fltmc filters` - a registered file-system minifilter, its altitude (the
/// number that determines its position in the I/O stack relative to every other minifilter),
/// frame, and how many volumes it currently has an instance attached to. #494 layers a category
/// classification on top, based on Microsoft's documented minifilter altitude range guidance (see
/// "Load Order Groups and Altitudes for Minifilter Drivers") - a best-effort bucketing by numeric
/// range, not an authoritative per-vendor lookup, so a filter sitting in the anti-virus range isn't
/// guaranteed to actually be anti-virus software (and vice versa) - see MinifilterService's remarks.
/// </summary>
public sealed class MinifilterEntry
{
    public string Name { get; init; } = string.Empty;
    public string AltitudeText { get; init; } = string.Empty;
    public double? AltitudeValue { get; init; }
    public string Frame { get; init; } = string.Empty;
    public int InstanceCount { get; init; }

    /// <summary>#496-style "attached volumes" list, filled in from `fltmc instances` (matched back
    /// to this filter by name) - e.g. ["C:", "D:"]. Empty when the filter has no instances anywhere
    /// (registered but not currently attached to any volume).</summary>
    public List<string> AttachedVolumes { get; init; } = new();

    public MinifilterCategory Category { get; init; } = MinifilterCategory.Other;

    public string CategoryText => Category switch
    {
        MinifilterCategory.AntiVirus => "Anti-virus range",
        MinifilterCategory.ActivityMonitor => "Activity-monitor range",
        MinifilterCategory.Encryption => "Encryption range",
        _ => "Other",
    };
}

/// <summary>#494: coarse altitude-range bucket - see MinifilterService.ClassifyAltitude for the
/// documented ranges each bucket is drawn from. "Quick flag, not a verdict": altitude ranges are a
/// Microsoft convention third-party vendors are expected to follow when requesting an altitude, not
/// something Windows enforces, so an unusual vendor could in principle sit anywhere.</summary>
public enum MinifilterCategory
{
    Other,
    AntiVirus,
    ActivityMonitor,
    Encryption,
}
