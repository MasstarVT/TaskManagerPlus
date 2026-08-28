namespace TaskManagerPlus.Models;

/// <summary>#764: one Microsoft-Windows-TaskScheduler/Operational event from the failure-family IDs
/// this domain's design note asks for (101 task start failed, 103 action start failed, 111
/// terminated due to timeout, 203 action failed with a return code, 322 not run - instance already
/// running, 332 not run - credential problem). TaskName is regex-extracted from the rendered
/// message's first quoted segment (every one of these templates quotes the task path) rather than
/// read from a documented indexed property, the same "the display name is embedded in the rendered
/// message" approach EventLogService.ExtractFaultingModule/ScmServiceNamePatterns already take for
/// events without a stable property layout across their whole ID set. See
/// EventLogService.ReadTaskFailureEvents.</summary>
public sealed class TaskSchedulerOperationalEvent
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string TaskName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    public string EventLabel => EventId switch
    {
        101 => "Task start failed",
        103 => "Action start failed",
        111 => "Terminated - exceeded its time limit",
        203 => "Action failed with a return code",
        322 => "Not run - an instance was already running",
        332 => "Not run - a credential problem",
        _ => $"Event {EventId}",
    };
}

/// <summary>#768: one completed task run's wall-clock duration, computed by pairing a start event
/// (100 - task started, 129 - action started) with its matching completion event (102 - task
/// completed, 201 - action completed) by the event log's own ActivityId correlation GUID - see
/// EventLogService.ReadTaskRunDurations.</summary>
public sealed class TaskRunDuration
{
    public string TaskName { get; init; } = string.Empty;
    public DateTime StartTime { get; init; }
    public double DurationMs { get; init; }
}
