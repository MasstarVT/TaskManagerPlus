namespace TaskManagerPlus.Models;

/// <summary>#430: one configured page file, joining Win32_PageFileSetting (initial/maximum size,
/// as configured) with Win32_PageFileUsage (current allocated size, peak usage) by Name (volume +
/// file path) - see PageFileConfigurationService.Query. "System-managed" means Windows chose the
/// initial/maximum size itself for this file (InitialSize and MaximumSize both report 0, the
/// documented WMI convention for that state), not that no page file exists at all.</summary>
public sealed class PageFileConfigInfo
{
    public string Volume { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public double InitialSizeMb { get; init; }
    public double MaximumSizeMb { get; init; }
    public double CurrentSizeMb { get; init; }
    public double PeakUsageMb { get; init; }
    public bool IsSystemManaged { get; init; }

    /// <summary>#431: the classic "fixed page file, capped below what it actually needed to grow
    /// to" misconfiguration - see PageFileConfigurationService's remarks for exactly how this is
    /// flagged. Quick flag, not a verdict: a one-time historical peak doesn't necessarily mean the
    /// cap is causing ongoing problems right now.</summary>
    public bool IsCappedBelowPeakUsage { get; init; }
}

/// <summary>#430: whole-system page file configuration snapshot - "no page file configured" is a
/// real, valid state (Files.Count == 0), not an error.</summary>
public sealed class PageFileConfigSnapshot
{
    public List<PageFileConfigInfo> Files { get; init; } = new();

    /// <summary>Win32_ComputerSystem.AutomaticManagedPagefile - true when Windows is fully
    /// managing page file placement/sizing itself rather than following any per-file settings
    /// above (the common default). Null when the read itself failed.</summary>
    public bool? IsAutomaticallyManaged { get; init; }
}
