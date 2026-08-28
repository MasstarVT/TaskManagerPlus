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

    /// <summary>Round 12, #96: true when this is a recognized 12V/5V/3.3V rail reading more than
    /// ~5% off its nominal value - a simple threshold check EnergyThermalsViewModel stamps onto
    /// Voltage readings as it builds the Voltages collection (WithVoltageSpecCheck), the same
    /// "set by the ViewModel, not SensorMonitorService" shape SessionMin/SessionMax already use.
    /// Null (not just false) for any voltage sensor this app doesn't recognize as one of those
    /// three common rails (many boards report several oddly-named auxiliary rails LibreHardwareMonitorLib
    /// doesn't standardize any more than it standardizes CPU/battery sensor names) - the view
    /// only tints a row red when this is explicitly true, never for null/false.</summary>
    public bool? IsVoltageOutOfSpec { get; init; }
}
