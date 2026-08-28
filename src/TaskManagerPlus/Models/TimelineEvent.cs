namespace TaskManagerPlus.Models;

/// <summary>
/// #938: one dated marker on the Timeline panel, from any lane/source. A plain DTO (not an
/// ObservableObject) - the whole set is rebuilt on each "Load timeline" pass rather than mutated
/// in place, since none of these sources are individually re-polled on a timer (see
/// TimelineService's remarks - this is an on-demand aggregation, not a new poller).
/// </summary>
public sealed class TimelineEvent
{
    public required TimelineLane Lane { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = string.Empty;

    /// <summary>Where this marker came from (a WMI class, an event-log provider/ID, a file path,
    /// ...) - shown in the detail table/export so a finding can be traced back to its real source,
    /// the same "cite the source" pattern this app's other diagnostic evidence lines already use.</summary>
    public required string Source { get; init; }

    /// <summary>True for a marker that represents something going wrong (a failed update, a
    /// service crash, a crash/hang/hardware-failure reliability record, a detected perf spike, an
    /// over-threshold thermal transition) - drives the red/green marker distinction #940 asks for
    /// specifically for Windows Update, generalized here to every lane that can fail.</summary>
    public bool IsFailure { get; init; }

    public string LaneDisplayName => Lane switch
    {
        TimelineLane.Crashes => "Crashes",
        TimelineLane.ServiceFailures => "Service failures",
        TimelineLane.WindowsUpdates => "Windows Updates",
        TimelineLane.DriverInstalls => "Driver installs",
        TimelineLane.SoftwareInstalls => "Software installs",
        TimelineLane.ThermalEvents => "Thermal events",
        TimelineLane.PerfSpikes => "Perf spikes",
        TimelineLane.Notes => "Notes",
        _ => Lane.ToString(),
    };
}
