namespace TaskManagerPlus.Models;

/// <summary>#660: one Errors/Warnings/Information finding parsed out of a `powercfg /energy`
/// diagnostic report (the 60-second energy-efficiency trace) - see PowerEfficiencyService's
/// remarks for why this is a best-effort HTML scrape (the report's exact markup is not a
/// documented, versioned contract).</summary>
public sealed class PowerEfficiencyFinding
{
    /// <summary>"Error", "Warning", or "Information" - mirrors the report's own three severity
    /// buckets.</summary>
    public string Severity { get; init; } = string.Empty;

    /// <summary>The finding's own short title line, e.g. "USB Suspend:The USB device is not
    /// enabled for selective suspend."</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Whatever additional detail rows (device instance ID, timer resolution, driver
    /// name, ...) the report attached to this finding, joined into one readable block. Empty when
    /// the finding had no further detail beyond its title.</summary>
    public string Detail { get; init; } = string.Empty;
}
