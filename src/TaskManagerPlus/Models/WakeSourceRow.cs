namespace TaskManagerPlus.Models;

/// <summary>#655: one software wake cause - either a `powercfg /waketimers` active timer
/// (WakeTimerService) or a wake-enabled ("Wake the computer to run this task") scheduled task from
/// ScheduledTaskService.ListWakeEnabledAsync - unified into one table so both software wake sources
/// sit next to #653's hardware wake-armed device list.</summary>
public sealed class WakeSourceRow
{
    /// <summary>"Wake timer" or "Scheduled task".</summary>
    public string Kind { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;
}
