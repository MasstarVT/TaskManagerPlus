namespace TaskManagerPlus.Models;

/// <summary>One hit from the cross-tab search (#100) - "find anything mentioning 'nvidia'" across
/// Processes, Services, Startup, drivers, installed software, and USB devices at once.</summary>
public sealed class SearchResult
{
    public string Category { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}
