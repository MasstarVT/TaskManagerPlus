namespace TaskManagerPlus.Models;

/// <summary>suggestions.md #999: one row on the read-only "Network activity" disclosure page - a
/// static table, not something computed at runtime (see NetworkActivityCatalogService's remarks).</summary>
public sealed class NetworkActivityEntry
{
    public string Name { get; init; } = string.Empty;
    public string Trigger { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public bool GatedByOfflineMode { get; init; }
}
