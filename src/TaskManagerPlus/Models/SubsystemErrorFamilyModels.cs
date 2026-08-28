namespace TaskManagerPlus.Models;

/// <summary>
/// #195-196: models backing SubsystemErrorFamilyService - the Perflib counter-corruption card and
/// the "assorted subsystem error families" rollup card (Schannel/ESENT/GroupPolicy/Time-Service/
/// DNS Client/Tcpip). Same "one card per family, degrade to empty, never fabricate" shape
/// KernelEventFamilyModels already established for the prior chunk's storage/WHEA/driver cards.
/// </summary>

/// <summary>#195: one Perflib 1008/1010/1017/1023/2004 event - a performance counter DLL failed to
/// load or returned bad data for the named counter provider. Directly relevant to this app itself:
/// a broken counter provider also breaks this app's own PerformanceCounter reads.</summary>
public sealed class PerflibFailureEvent
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }

    /// <summary>Best-effort counter-provider/service name, extracted from the event's own inserted
    /// properties - null when nothing recognizable was found.</summary>
    public string? CounterProviderName { get; init; }

    public string Description { get; init; } = string.Empty;
}

/// <summary>#195: the Perflib card's full result - every failure event plus the distinct set of
/// counter providers they named, so the card can lead with "which providers are broken" rather than
/// a flat event list.</summary>
public sealed class PerflibFailureSummary
{
    public List<PerflibFailureEvent> Failures { get; init; } = new();
    public List<string> AffectedProviders { get; init; } = new();
}

/// <summary>#196: one rolled-up subsystem family (Schannel/ESENT/GroupPolicy/Time-Service/DNS
/// Client/Tcpip) - a per-family count/last-seen summary plus its individual hits for drill-down.</summary>
public sealed class SubsystemFamilyGroup
{
    public string FamilyName { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public int TotalCount { get; init; }
    public DateTime? LastSeen { get; init; }
    public List<SubsystemFamilyHit> Hits { get; init; } = new();
}

/// <summary>One (provider, eventId) event within a SubsystemFamilyGroup above.</summary>
public sealed class SubsystemFamilyHit
{
    public DateTime TimeCreated { get; init; }
    public string Provider { get; init; } = string.Empty;
    public int EventId { get; init; }
    public string Description { get; init; } = string.Empty;
}
