namespace TaskManagerPlus.Models;

/// <summary>
/// #938: one horizontal lane on the Timeline panel - each lane is one category of dated event,
/// aggregated from a different existing data source (see TimelineService's remarks for exactly
/// which). Declaration order here is also the top-to-bottom row order TimelineViewModel builds
/// Lanes in.
/// </summary>
public enum TimelineLane
{
    Crashes,
    ServiceFailures,
    WindowsUpdates,
    DriverInstalls,
    SoftwareInstalls,
    ThermalEvents,
    PerfSpikes,
    Notes,
}
