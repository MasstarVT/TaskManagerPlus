using System.Globalization;

namespace TaskManagerPlus.Models;

/// <summary>#936: one row of the Health Check card's "Recently resolved" section - a rule that
/// was firing in a recent RefreshHealthIssues pass but isn't in the current one, so "it cleared
/// up" is visible without digging through findings-history.jsonl by hand.</summary>
public sealed class ResolvedFinding
{
    public string RuleId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public DateTime ResolvedAtUtc { get; init; }

    /// <summary>Pre-formatted in local time for direct binding - avoids adding a one-off
    /// DateTime-to-string converter just for this one field.</summary>
    public string ResolvedAtLocalText => ResolvedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
