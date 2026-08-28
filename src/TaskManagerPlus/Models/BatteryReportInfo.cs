namespace TaskManagerPlus.Models;

/// <summary>
/// #641/#643: battery identity + capacity figures, read either from `powercfg /batteryreport
/// /xml` (BatteryReportService.RunPowercfgReportAsync - far richer, includes the report's own
/// capacity-history and recent-usage tables below) or, when that's blocked/missing/unparseable, a
/// root\wmi + Win32_PortableBattery WMI fallback (BatteryReportService.ReadWmiFallback) that
/// covers the identity/capacity fields alone. <see cref="Source"/> records which path actually
/// produced this so the UI can say so. Any field neither source reported is left null/empty
/// ("Unknown") rather than guessed, per this project's degrade-never-fabricate convention.
/// </summary>
public sealed class BatteryReportInfo
{
    /// <summary>"powercfg /batteryreport" or "WMI fallback" - shown in the panel so a reading
    /// missing its history tables (the WMI path never has one) doesn't read as a bug.</summary>
    public string Source { get; init; } = string.Empty;

    public string Manufacturer { get; init; } = string.Empty;
    public string Chemistry { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;

    public double? DesignCapacityMwh { get; init; }
    public double? FullChargeCapacityMwh { get; init; }
    public int? CycleCount { get; init; }
    public DateTime? ReportGeneratedAt { get; init; }

    /// <summary>Full-charge / design capacity as a percent - null whenever either figure is
    /// unknown, never a guessed/fabricated percentage.</summary>
    public double? HealthPercent => DesignCapacityMwh is > 0 && FullChargeCapacityMwh is { } fcc
        ? Math.Round(fcc / DesignCapacityMwh.Value * 100.0, 1)
        : null;

    /// <summary>#641/#642: one full-charge-capacity reading per report period - the source data
    /// for the capacity-fade chart. Empty whenever this report came from the WMI fallback path
    /// (WMI has no equivalent history table).</summary>
    public List<BatteryCapacityHistoryEntry> CapacityHistory { get; init; } = new();

    /// <summary>#641: the report's own recent-usage table (active/standby runtime, AC vs.
    /// battery, per period). Empty on the WMI fallback path, same as CapacityHistory above.</summary>
    public List<BatteryUsageHistoryEntry> RecentUsage { get; init; } = new();
}

/// <summary>#641/#642: one row of the battery report's capacity-history table (typically one
/// calendar day) - charted as a line by EnergyThermalsViewModel so capacity wear reads as a
/// slope over time rather than a single point-in-time percentage.</summary>
public sealed class BatteryCapacityHistoryEntry
{
    public DateTime PeriodStart { get; init; }
    public double? FullChargeCapacityMwh { get; init; }
    public double? DesignCapacityMwh { get; init; }
}

/// <summary>#641: one row of the battery report's recent-usage table.</summary>
public sealed class BatteryUsageHistoryEntry
{
    public DateTime PeriodStart { get; init; }

    /// <summary>"Active" / "Connected standby", as reported by the tool - shown verbatim rather
    /// than mapped to an enum, since this is exactly the same "not a documented/versioned
    /// contract" format every other powercfg text/XML parse in this app already treats this way.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>"AC" / "Battery", as reported by the tool.</summary>
    public string PowerSource { get; init; } = string.Empty;

    public double? CapacityRemainingMwh { get; init; }
}
