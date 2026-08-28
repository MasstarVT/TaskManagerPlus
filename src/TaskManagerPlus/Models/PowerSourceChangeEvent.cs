namespace TaskManagerPlus.Models;

/// <summary>#626: one Kernel-Power event 105 (AC/DC power-source transition) - see
/// EventLogService.ReadPowerSourceChangeEvents. Rapid flapping while a charger is plugged in
/// points at a failing barrel jack, a bad USB-C PD negotiation, or a bad cable, distinct from a
/// normal unplug/replug.</summary>
public sealed class PowerSourceChangeEvent
{
    public DateTime TimeCreated { get; init; }
    public string Message { get; init; } = string.Empty;
}
