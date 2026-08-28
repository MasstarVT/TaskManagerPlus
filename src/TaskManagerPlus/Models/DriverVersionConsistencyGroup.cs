namespace TaskManagerPlus.Models;

/// <summary>#466: one device bound to a hardware-ID group flagged by
/// DriverVersionConsistencyService - see DriverVersionConsistencyGroup's remarks.</summary>
public sealed class DriverVersionConsistencyDevice
{
    public string DeviceName { get; init; } = string.Empty;
    public string DriverVersion { get; init; } = string.Empty;
    public DateTime? DriverDate { get; init; }
}

/// <summary>
/// #466: a group of two-or-more devices that share the same first hardware ID (the strongest
/// available "these are identical hardware" signal short of a full hardware-ID list comparison -
/// the same first-HardwareID convention DriverInventoryService.ComputeMatchQuality already reads
/// per device) but are bound to two or more distinct DriverVersion values. Only genuinely
/// inconsistent groups are produced by DriverVersionConsistencyService - a group of identical
/// devices all running the same driver version isn't a finding worth showing.
/// </summary>
public sealed class DriverVersionConsistencyGroup
{
    public string HardwareId { get; init; } = string.Empty;
    public List<DriverVersionConsistencyDevice> Devices { get; init; } = new();

    public string DistinctVersionsText => string.Join(", ",
        Devices.Select(d => string.IsNullOrEmpty(d.DriverVersion) ? "(unknown version)" : d.DriverVersion)
               .Distinct(StringComparer.OrdinalIgnoreCase));
}
