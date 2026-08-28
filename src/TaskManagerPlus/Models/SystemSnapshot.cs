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

    /// <summary>Round 7 #16: per-service StartType/logon-account config, captured alongside the
    /// plain name list above - a new, additive field, so a snapshot JSON file saved by an earlier
    /// round loads fine here too (System.Text.Json just leaves this list empty, the same
    /// "missing field degrades gracefully" shape ThemeService already relies on for theme.json).
    /// Used by the Services tab's config-drift check, distinct from the Summary tab's existing
    /// software/service/startup added-removed diff.</summary>
    public List<ServiceConfigSnapshot> ServiceConfigs { get; init; } = new();

    /// <summary>Round 12, #94: CPU package temperature at capture time, only when the system was
    /// genuinely idle (see SummaryViewModel.SaveSnapshot's idle-gating) - a rough thermal-paste-
    /// age proxy: idle temp creeping up build-to-build (with cooling/dust/room-temperature roughly
    /// unchanged) is a weak but real signal of degraded paste/pads, distinct from load temps which
    /// are much more sensitive to what happened to be running at capture time. Null when the
    /// system wasn't idle at capture (no misleading non-idle baseline saved) or no sensor was
    /// available - an older snapshot file with no such field also just leaves this null, the same
    /// missing-field-degrades-gracefully shape ServiceConfigs above already established.</summary>
    public double? IdleCpuTempC { get; init; }

    /// <summary>#486: driver inventory (#453's data) at capture time - one identity string per
    /// kernel driver ("ServiceName — InfName Version"), reusing the join DriverInventoryService.
    /// ListAsync already computes rather than re-deriving driver identity here. An older snapshot
    /// file with no such field just leaves this empty, the same missing-field-degrades-gracefully
    /// shape ServiceConfigs/IdleCpuTempC above already established.</summary>
    public List<string> DriverInventory { get; init; } = new();

    /// <summary>#486: driver store contents (#479's data) at capture time - one identity string
    /// per package ("PublishedName — OriginalName (Provider) Version").</summary>
    public List<string> DriverStorePackages { get; init; } = new();
}

/// <summary>One service's StartType + logon account at the moment a baseline was captured (Round 7 #16).</summary>
public sealed class ServiceConfigSnapshot
{
    public string ServiceName { get; init; } = string.Empty;
    public string StartType { get; init; } = string.Empty;
    public string LogOnAs { get; init; } = string.Empty;
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

    /// <summary>#486: driver inventory (#453) and driver store (#479) added/removed - the same
    /// added/removed shape as every other list above, diffed the same way (SnapshotService.Diff's
    /// shared DiffSet helper).</summary>
    public List<string> DriversAdded { get; init; } = new();
    public List<string> DriversRemoved { get; init; } = new();
    public List<string> DriverStorePackagesAdded { get; init; } = new();
    public List<string> DriverStorePackagesRemoved { get; init; } = new();

    public bool HasChanges =>
        SoftwareAdded.Count > 0 || SoftwareRemoved.Count > 0 ||
        ServicesAdded.Count > 0 || ServicesRemoved.Count > 0 ||
        StartupAdded.Count > 0 || StartupRemoved.Count > 0 ||
        DriversAdded.Count > 0 || DriversRemoved.Count > 0 ||
        DriverStorePackagesAdded.Count > 0 || DriverStorePackagesRemoved.Count > 0;
}
