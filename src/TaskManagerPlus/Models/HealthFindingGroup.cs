namespace TaskManagerPlus.Models;

/// <summary>#934: one row of the Health Check card's grouped finding list - either a single
/// standalone finding (no GroupKey, or the only finding currently holding that GroupKey) or a
/// collapsed parent row for every currently-firing finding sharing a non-null Rule.GroupKey (e.g.
/// several storage rules all firing about the same drive). SummaryViewModel rebuilds this list
/// fresh alongside the flat HealthIssues collection every RefreshHealthIssues pass - it's a pure
/// display grouping over that same data, not a second source of truth.</summary>
public sealed class HealthFindingGroup
{
    /// <summary>Null for a standalone finding with no GroupKey at all.</summary>
    public string? GroupKey { get; init; }

    public List<HealthIssue> Findings { get; init; } = new();

    public bool IsGroup => Findings.Count > 1;
    public bool IsSingle => !IsGroup;

    /// <summary>Representative finding for the standalone (IsSingle) case - the grouped case
    /// renders every entry in Findings instead, see SummaryView's finding-row DataTemplate.</summary>
    public HealthIssue? Representative => Findings.Count > 0 ? Findings[0] : null;

    public string HeaderText => $"{Findings.Count} findings about {GroupKey}";
}
