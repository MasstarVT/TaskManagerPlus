namespace TaskManagerPlus.Models;

/// <summary>
/// #644: one app's aggregated share of the SRUM (System Resource Usage Monitor) energy-estimation
/// data over the lookback window BatteryDrainAttributionService scans - "what killed my battery
/// overnight" in a way the live drain-rate readout on its own can't answer, since that only ever
/// shows the current instantaneous total. <see cref="EnergyEstimate"/> is deliberately unitless
/// ("informational only" - see BatteryDrainAttributionService's remarks on why `powercfg
/// /srumutil`'s dump format is undocumented and parsed adaptively): useful for ranking apps
/// against each other over the same window, not as a calibrated watt-hour figure.
/// </summary>
public sealed class BatteryDrainAttributionRow
{
    public string AppName { get; init; } = string.Empty;

    /// <summary>Relative energy-estimate total for this app over the scanned window - see the
    /// class remarks for why this is a ranking signal, not a calibrated Wh reading.</summary>
    public double EnergyEstimate { get; init; }

    /// <summary>How many SRUM rows this total was aggregated from - a rough confidence signal
    /// (an app seen in one row is a weaker read than one seen across dozens).</summary>
    public int SampleCount { get; init; }

    public DateTime? LastSeen { get; init; }
}
