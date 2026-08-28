namespace TaskManagerPlus.Models;

/// <summary>
/// One measured cooldown event (#618) - seconds needed for CPU package temperature to fall 20°C
/// from its peak, measured from the moment load dropped from &gt;80% to &lt;10%. Persisted to
/// cooldown-history.json (CooldownHistoryService) and charted as a monthly average trend.
/// Cooldown slope is largely workload-independent (unlike absolute temperatures), so it degrades
/// measurably as thermal paste dries out or a heatsink clogs, without needing the same
/// load/power bucketing #611 uses for absolute temperatures.
/// </summary>
public sealed class CooldownEvent
{
    public DateTime RecordedAt { get; init; }
    public double PeakTempC { get; init; }
    public double CooldownSeconds { get; init; }
}
