namespace TaskManagerPlus.Models;

/// <summary>
/// Round 18, #369: parsed `fltmc filters` / `fltmc instances` output - see
/// MinifilterAuditService's remarks for the "quick flag, not a verdict" framing.
/// </summary>
public sealed class MinifilterAuditResult
{
    public bool Available { get; init; }
    public string UnavailableReason { get; init; } = string.Empty;
    public List<MinifilterDriverInfo> Filters { get; init; } = new();
    public List<MinifilterVolumeInfo> Volumes { get; init; } = new();

    public static MinifilterAuditResult Unavailable(string reason) => new() { Available = false, UnavailableReason = reason };
}

/// <summary>One filter driver from `fltmc filters`, cross-referenced with `fltmc instances` for the
/// list of volumes it's actually attached to. Altitude is kept as the raw string fltmc printed
/// (some drivers report a fractional altitude like "328010.005") rather than parsed to an int, so a
/// value that doesn't look like a plain integer is shown as-is instead of silently truncated.</summary>
public sealed class MinifilterDriverInfo
{
    public string Name { get; init; } = string.Empty;
    public string Altitude { get; init; } = string.Empty;
    public int InstanceCount { get; init; }
    public List<string> AttachedVolumes { get; init; } = new();
    public string AttachedVolumesText => AttachedVolumes.Count == 0 ? "(none)" : string.Join(", ", AttachedVolumes);
}

/// <summary>One volume's filter stack, from `fltmc instances` grouped by volume and sorted by
/// altitude (load order).</summary>
public sealed class MinifilterVolumeInfo
{
    public string VolumeName { get; init; } = string.Empty;
    public List<MinifilterInstanceInfo> Instances { get; init; } = new();
}

public sealed class MinifilterInstanceInfo
{
    public string FilterName { get; init; } = string.Empty;
    public string VolumeName { get; init; } = string.Empty;
    public string Altitude { get; init; } = string.Empty;
    public string InstanceName { get; init; } = string.Empty;
}
