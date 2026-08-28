namespace TaskManagerPlus.Models;

/// <summary>#352: one fixed volume's free-space low-water mark for one calendar day - the lowest
/// AvailableFreeSpace seen across every Storage-tab sampler tick that day, not just whatever the
/// value happened to be at the last tick. Using the day's minimum (rather than its last reading)
/// means a volume that briefly dipped low (a big download, a temp-file spike) and then recovered
/// still shows the dip in the trend, instead of the history silently smoothing it away.</summary>
public sealed class FreeSpaceDailyPoint
{
    public DateTime Date { get; set; }
    public long FreeBytes { get; set; }
    public long TotalBytes { get; set; }
}

/// <summary>Persisted shape of free-space-history.json - one daily-point list per drive letter
/// (e.g. "C:"). See FreeSpaceHistoryService.</summary>
public sealed class FreeSpaceHistoryStore
{
    public Dictionary<string, List<FreeSpaceDailyPoint>> ByDrive { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
