namespace TaskManagerPlus.Models;

/// <summary>
/// One Microsoft-Windows-Kernel-Processor-Power event 37 ("the speed of processor N is being
/// limited by system firmware") or its event 38 recovery counterpart (#602) - the one
/// authoritative, non-heuristic statement Windows itself makes about firmware-side CPU throttling,
/// unlike every other throttle signal in this app (which are all pattern-matches on otherwise
/// ambiguous temperature/clock/power data - see CpuViewModel.IsThrottling's remarks).
/// </summary>
public sealed class FirmwareThrottleEvent
{
    public DateTime TimeCreated { get; init; }

    /// <summary>False for event 37 (limit started), true for event 38 (limit lifted).</summary>
    public bool IsRecovery { get; init; }

    public string Message { get; init; } = string.Empty;
}
