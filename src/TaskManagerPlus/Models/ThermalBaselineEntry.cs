namespace TaskManagerPlus.Models;

/// <summary>
/// One day's median CPU package temperature during a genuinely idle window (CPU &lt; 5% for 60s,
/// #609) - persisted to thermal-baseline.json (ThermalBaselineService), one entry per calendar
/// day. A rising idle floor at unchanged ambient is a cleaner dust/pump-failure signal than load
/// temps, since it isn't confounded by how hard the machine happened to be working that day.
/// </summary>
public sealed class ThermalBaselineEntry
{
    public DateTime Date { get; init; }
    public double MedianIdleTempC { get; init; }
}
