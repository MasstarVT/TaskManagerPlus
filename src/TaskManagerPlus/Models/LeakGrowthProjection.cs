namespace TaskManagerPlus.Models;

/// <summary>
/// #415: an uptime-normalized presentation of one image name's #402 private-bytes growth slope -
/// "MB per day" is a more intuitive unit than "MB per hour" for a trend meant to be read against
/// how long the system has been up, and the projected hours-to-commit-exhaustion turns the raw
/// slope into a "so what" figure. Built from ProcessHistoryService.GetTopGrowthSummaries - see
/// MemoryViewModel's remarks. Explicitly labelled a projection (a straight-line extrapolation of
/// the recent slope), not a prediction - the same "quick flag, not a verdict" tier as every other
/// heuristic in this app.
/// </summary>
public sealed class LeakGrowthProjection
{
    public string ImageName { get; set; } = string.Empty;
    public double GrowthMbPerDay { get; set; }
    public double RSquared { get; set; }

    /// <summary>Null when the growth rate is at/below zero (nothing to project) or the system's
    /// remaining commit headroom couldn't be read.</summary>
    public double? HoursToCommitExhaustion { get; set; }
}
