namespace TaskManagerPlus.Models;

/// <summary>Where a #895 bloatware inventory row came from - the three sources the item asks to
/// combine into one tiered list.</summary>
public enum BloatwareSource
{
    UninstallRegistry,
    AppxPackage,
    AppxProvisionedPackage,
}

/// <summary>
/// #895's own tiering heuristic - a SIMPLE keyword-matching classifier, explicitly not a perfect
/// one (the item's own text calls this out as an expected approximation). See
/// BloatwareInventoryService.Classify for the exact keyword rules per tier.
/// </summary>
public enum BloatwareTier
{
    Unclassified,
    OemUtility,
    OemUpdaterTelemetry,
    Trialware,
    StoreBloat,

    /// <summary>Visually distinguished (a different badge color) per the item's own text -
    /// "do not remove" is the whole point of this tier existing.</summary>
    DriverAdjacentDoNotRemove,
}

/// <summary>One row in the #895 preinstalled/OEM software inventory - combines the Uninstall
/// registry keys, Get-AppxPackage, and Get-AppxProvisionedPackage into one tiered list. See
/// Services/BloatwareInventoryService.</summary>
public sealed class BloatwareEntry
{
    public string Name { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public DateTime? InstallDate { get; init; }

    /// <summary>KB, from the Uninstall key's EstimatedSize value - null for AppX sources, which
    /// don't expose a comparable size without a separate, heavier per-package query.</summary>
    public long? EstimatedSizeKb { get; init; }

    public string UninstallString { get; init; } = string.Empty;
    public BloatwareSource Source { get; init; }
    public BloatwareTier Tier { get; init; }

    public string TierLabel => Tier switch
    {
        BloatwareTier.OemUtility => "OEM utility",
        BloatwareTier.OemUpdaterTelemetry => "OEM updater/telemetry",
        BloatwareTier.Trialware => "Trialware",
        BloatwareTier.StoreBloat => "Store bloat",
        BloatwareTier.DriverAdjacentDoNotRemove => "Driver-adjacent - do not remove",
        _ => "Unclassified",
    };

    public string SourceLabel => Source switch
    {
        BloatwareSource.AppxPackage => "Store app (installed)",
        BloatwareSource.AppxProvisionedPackage => "Store app (provisioned)",
        _ => "Uninstall registry",
    };
}
