namespace TaskManagerPlus.Models;

/// <summary>#652: one outstanding `powercfg /requests` power-request entry - a process, service, or
/// driver currently holding a DISPLAY/SYSTEM/AWAYMODE/EXECUTION/PERFBOOST power request open, which
/// is the direct, real-time answer to "why won't my PC sleep right now." Refreshed on demand only
/// (a real subprocess call each time) - see EnergyThermalsViewModel.LoadPowerRequestsCommand.</summary>
public sealed class PowerRequestEntry
{
    /// <summary>DISPLAY, SYSTEM, AWAYMODE, EXECUTION, PERFBOOST, or ACTIVELOCKSCREEN - whichever
    /// category header this entry was listed under.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>PROCESS, SERVICE, or DRIVER, as tagged by powercfg itself.</summary>
    public string SourceType { get; init; } = string.Empty;

    public string SourceName { get; init; } = string.Empty;

    /// <summary>The reason text powercfg reports for the request, when the source supplied one.</summary>
    public string Reason { get; init; } = string.Empty;
}
