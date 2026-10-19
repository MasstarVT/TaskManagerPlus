namespace TaskManagerPlus.Models;

/// <summary>
/// One ACPI thermal zone sampled from Windows' built-in "Thermal Zone Information" perf-counter
/// category (#601) - a genuinely second, driver-free throttle source, independent of
/// LibreHardwareMonitorLib/SensorMonitorService: this is the same ACPI-sourced data Windows itself
/// acts on, so it still works when SensorMonitorService.IsAvailable is false. See
/// ThermalZoneService's remarks for exactly which counters back each field and why several are
/// nullable (not every zone/BIOS exposes every counter).
/// </summary>
public sealed class ThermalZoneReading
{
    /// <summary>The perf-counter instance name Windows assigns the zone (e.g. "ACPI\\ThermalZone\\TZ00_0") -
    /// not a friendly name; Windows doesn't expose one.</summary>
    public string ZoneName { get; init; } = string.Empty;

    /// <summary>From the "Temperature" counter (whole Kelvin, converted to °C) - null when this
    /// particular zone/BIOS doesn't populate it (reads 0).</summary>
    public double? TemperatureC { get; init; }

    /// <summary>From the "High Precision Temperature" counter, when present - finer-grained than
    /// "Temperature" on hardware that supports it.</summary>
    public double? HighPrecisionTemperatureC { get; init; }

    /// <summary>How much this zone is currently telling Windows to throttle, 0-100 - derived as
    /// 100 minus "% Passive Limit" (there is no "Throttle Percentage" counter). Null when the
    /// zone doesn't expose a passive limit.</summary>
    public double? ThrottlePercent { get; init; }

    /// <summary>From the "% Passive Limit" counter - the passive-cooling processor-frequency cap
    /// ACPI is currently requesting for this zone, as a percent of full speed. Null when the zone
    /// doesn't expose it or reports a raw 0 (unpopulated) or anything outside 1-100.</summary>
    public double? PassiveLimitPercent { get; init; }
}
