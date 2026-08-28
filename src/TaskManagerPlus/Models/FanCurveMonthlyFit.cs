namespace TaskManagerPlus.Models;

/// <summary>
/// One fan's least-squares-fitted RPM-vs-temperature line for one calendar month (#612) -
/// persisted to fan-curve-history.json (FanCurveHistoryService). EnergyThermalsViewModel
/// overlays the most recent *prior* month's fit, ghosted, behind the current fan-curve scatter
/// cloud: a curve shifted right (more RPM needed for the same temperature) points at a dusty or
/// clogged heatsink rather than a fan/sensor problem.
/// </summary>
public sealed class FanCurveMonthlyFit
{
    /// <summary>SensorReading.Identifier of the fan this fit was built from - fan identifiers
    /// aren't standardized across vendors, but are stable across sessions for a given board.</summary>
    public string FanIdentifier { get; init; } = string.Empty;

    public int Year { get; init; }
    public int Month { get; init; }

    /// <summary>Least-squares slope/intercept: Rpm = Slope * TempC + Intercept.</summary>
    public double Slope { get; init; }
    public double Intercept { get; init; }

    public int SampleCount { get; init; }

    /// <summary>Temperature range the fit was actually built over - the ghost overlay is drawn
    /// only across this domain rather than extrapolated across the current chart's full range.</summary>
    public double MinTempC { get; init; }
    public double MaxTempC { get; init; }
}
