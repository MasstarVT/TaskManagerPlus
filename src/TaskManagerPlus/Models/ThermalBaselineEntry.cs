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

    /// <summary>#619: room-ambient proxy recorded at the same idle-window close as
    /// MedianIdleTempC above - the lowest of the motherboard "System" sensor and any drive
    /// temperature reported at that moment (the sensors that track case/room temperature most
    /// closely, unlike a VRM or CPU reading which run hot even at idle). Null when no such
    /// sensor was reporting that day - the "normalize to ambient" toggle falls back to the raw
    /// MedianIdleTempC for any entry missing this, rather than fabricating one.</summary>
    public double? AmbientProxyC { get; init; }
}
