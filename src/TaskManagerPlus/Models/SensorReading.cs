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

    /// <summary>#620: signed percent deviation from nominal (e.g. -6.2 for "6.2% low") for a
    /// recognized 12V/5V/3.3V rail - null for any unrecognized rail, same "null means not
    /// checked" rule IsVoltageOutOfSpec already follows. Set alongside IsVoltageOutOfSpec by
    /// EnergyThermalsViewModel.WithVoltageSpecCheck.</summary>
    public float? VoltageDeviationPercent { get; init; }

    /// <summary>#620: count of times this rail has transitioned into an out-of-spec state within
    /// the trailing hour (edge-triggered - a rail that's been continuously out of spec for an hour
    /// counts once, not once per tick) - so one startup glitch doesn't read the same as continuous
    /// instability. Null for an unrecognized rail.</summary>
    public double? VoltageExcursionsPerHour { get; init; }

    /// <summary>#620: the row's full verdict text - "6% low, 14 excursions/hr" for a recognized,
    /// out-of-spec rail; "in spec (±5%)" for a recognized, healthy rail; the bare hardware name for
    /// an unrecognized rail (same text VfdMeter's SubText showed before this round). Set by
    /// EnergyThermalsViewModel so the view can bind one property instead of assembling this string
    /// itself. Explicitly caveated in the UI (not per-row - see the Voltages section header) that
    /// Super I/O rail readings are uncalibrated on many boards.</summary>
    public string RailVerdictText { get; init; } = string.Empty;

    /// <summary>#615: per-fan-channel status ("OK"/"Slow"/"Stopped"/"Not reporting"), computed by
    /// comparing every fan channel reported at the same tick instead of relying on a single
    /// global dead-fan flag - a chassis fan pinned well below its siblings under identical load
    /// is disconnected or dead even though it isn't reading exactly 0. Null for any non-fan
    /// reading.</summary>
    public string? FanStatus { get; init; }

    /// <summary>#614: the highest RPM ever recorded for this fan identifier across every session
    /// (FanMaxRpmService) - compared against this session's own max to flag a bearing wearing out
    /// even though the fan still spins and still ramps. Null for a non-fan reading, or a fan with
    /// no prior session's high-water mark recorded yet.</summary>
    public float? HistoricalMaxRpm { get; init; }

    /// <summary>#614: true when this session's max RPM for this fan is 20%+ below
    /// HistoricalMaxRpm - see the field's remarks. Null (not false) for any non-fan reading.</summary>
    public bool? StepLossDetected { get; init; }

    /// <summary>#616: true when this fan channel's name matched a pump/AIO hint ("Pump", "AIO",
    /// "W_PUMP") - held to a different rule (near-constant RPM expected) than case fans, and
    /// excluded from the #615 sibling-RPM "Slow" comparison since a pump's absolute RPM isn't
    /// meaningfully comparable to a case fan's.</summary>
    public bool IsPumpChannel { get; init; }
}
