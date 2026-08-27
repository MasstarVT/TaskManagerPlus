using LibreHardwareMonitor.Hardware;

namespace TaskManagerPlus.Models;

/// <summary>
/// One sensor reading flattened out of LibreHardwareMonitorLib's hardware tree (see
/// Services/SensorMonitorService). Plain/immutable like the rest of Models/ - the Energy &amp;
/// Thermals tab's sensor lists are read-only display lists (no selection/scroll state to
/// preserve), so EnergyThermalsViewModel clears and rebuilds them each tick rather than
/// merging in place, the same simpler pattern SystemSpecsViewModel already uses for its
/// read-only memory/GPU/disk lists.
/// </summary>
public sealed class SensorReading
{
    public string HardwareName { get; init; } = string.Empty;
    public HardwareType HardwareType { get; init; }
    public string SensorName { get; init; } = string.Empty;
    public SensorType Type { get; init; }

    /// <summary>Null when this sensor has no current reading (hardware present but this
    /// particular value unsupported) - never fabricate a 0 for "no data".</summary>
    public float? Value { get; init; }

    public string Identifier { get; init; } = string.Empty;

    /// <summary>Lowest/highest value seen for this sensor since the app launched (#46) - lets a
    /// temperature tile answer "is 70°C normal for my CPU" without needing an external baseline.
    /// Null until at least one reading has been recorded; set by EnergyThermalsViewModel, not
    /// SensorMonitorService (which just reports the instantaneous hardware value).</summary>
    public float? SessionMin { get; init; }
    public float? SessionMax { get; init; }
}
