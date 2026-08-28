namespace TaskManagerPlus.Models;

/// <summary>
/// One tick's aggregated sample for a given image name (#401). If several running processes
/// share the same name (e.g. several chrome.exe instances), the tick's values are summed across
/// all of them so the resulting time series reads as one coherent trend rather than several
/// interleaved, individually-noisy ones - the same "group by name" idea
/// ProcessMonitorService.ComputeDuplicateInstances already uses elsewhere in this app.
/// </summary>
public sealed class ProcessHistorySample
{
    public DateTime TimestampUtc { get; set; }
    public long WorkingSetBytes { get; set; }
    public long PrivateBytes { get; set; }
    public int HandleCount { get; set; }
    public int GdiHandleCount { get; set; }
    public int UserHandleCount { get; set; }
    public int ThreadCount { get; set; }
}

/// <summary>
/// Persisted ring-buffer history for one image name - the JSON unit ProcessHistoryService reads
/// from/writes to process-history.json (#401), so a trend survives this app being closed and
/// reopened even though the monitored process's own PID doesn't.
/// </summary>
public sealed class ProcessHistoryRecord
{
    public string ImageName { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
    public List<ProcessHistorySample> Samples { get; set; } = new();
}

/// <summary>
/// A least-squares summary of one image name's recorded history (#402/#403/#405) - what the
/// "Memory leak evidence" report section (#407) and the Processes grid's sortable slope/R²
/// columns are both built from. A magnitude and a confidence, not a verdict - see
/// ProcessHistoryService.Regress's remarks for the exact math and its limitations.
/// </summary>
public sealed class ProcessHistorySummary
{
    public string ImageName { get; set; } = string.Empty;
    public double PrivateBytesSlopeMbPerHour { get; set; }
    public double PrivateBytesRSquared { get; set; }
    public double HandleSlopePerHour { get; set; }
    public double HandleRSquared { get; set; }
    public double ThreadSlopePerHour { get; set; }
    public double ThreadRSquared { get; set; }
    public int SampleCount { get; set; }
    public DateTime FirstSampleUtc { get; set; }
    public DateTime LastSampleUtc { get; set; }
}
