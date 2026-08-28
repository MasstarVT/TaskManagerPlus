namespace TaskManagerPlus.Models;

/// <summary>
/// The highest RPM ever observed for one fan channel, across every session (#614) - persisted to
/// fan-max-rpm.json (FanMaxRpmService). A fan whose session ceiling has dropped 20%+ below its
/// historical maximum is a bearing wearing out, even though it still spins and still ramps.
/// </summary>
public sealed class FanMaxRpmEntry
{
    /// <summary>SensorReading.Identifier of the fan.</summary>
    public string Identifier { get; init; } = string.Empty;
    public double HistoricalMaxRpm { get; init; }
    public DateTime RecordedAt { get; init; }
}
