namespace TaskManagerPlus.Models;

/// <summary>#329/#336: one event from Windows' own disk-diagnosis/bad-block/retry sources -
/// Microsoft-Windows-DiskDiagnosisDataCollector's Operational channel, or the System log's classic
/// "Disk" source (event 52 predicted failure, event 7 bad block, event 153 I/O retried). A later
/// chunk (#370) folds this into a unified storage event timeline; for now this is its own simple,
/// time-ordered list - see DiskDiagnosisEventService.</summary>
public sealed class DiskDiagnosisEvent
{
    public DateTime TimeCreated { get; init; }
    public string Source { get; init; } = string.Empty;
    public int EventId { get; init; }
    public string Message { get; init; } = string.Empty;
}
