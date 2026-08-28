namespace TaskManagerPlus.Models;

/// <summary>
/// #635: one on-demand "steady-state silicon behavior" snapshot, captured by the CPU tab's
/// "Snapshot current behavior" button and persisted to silicon-snapshots.json
/// (SiliconSnapshotService) - lets the user compare before and after a BIOS update, a repaste, or
/// an undervolt/overclock change instead of trying to remember what the numbers used to be. Any
/// field the app couldn't read at capture time is left null rather than fabricated, same as every
/// other reading in this app.
/// </summary>
public sealed class SiliconSnapshot
{
    public DateTime Timestamp { get; init; }

    /// <summary>Optional short user-entered note (e.g. "before repaste") - empty when not given.</summary>
    public string Label { get; init; } = string.Empty;

    public double? ClockGhz { get; init; }
    public double? VcoreV { get; init; }
    public double? PackagePowerW { get; init; }
    public double? TempC { get; init; }

    /// <summary>Percent of this session's dwell time (up to the moment of capture) spent in any
    /// classified throttle state (Thermal/Power/Firmware/Core-parked) - see
    /// CpuViewModel.CurrentThrottleClass's remarks.</summary>
    public double? ThrottlePercent { get; init; }
}
