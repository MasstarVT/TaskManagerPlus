namespace TaskManagerPlus.Models;

/// <summary>#925: one row of the rule editor's "Test pack" result list - which rules would fire
/// against a previously-captured metric-bag snapshot, rather than the live system.</summary>
public sealed class RuleTestPreviewRow
{
    public string RuleId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public bool WouldFire { get; init; }
    public string? Error { get; init; }
}
