namespace TaskManagerPlus.Models;

/// <summary>
/// #344: one event from the System log's "Ntfs" provider signalling on-disk corruption (55), an
/// unwritable transaction log (98), volume-resource exhaustion / a full transaction log (130/137), or
/// a volume that's no longer writable (140/142). VolumeText is a best-effort drive letter resolved
/// from the message's embedded NT device path (via QueryDosDevice) - "Unknown volume" when that
/// resolution fails, never a guess. A later chunk (per this chunk's brief, #370) folds this into
/// DiskDiagnosisEventService's unified timeline; for now this is its own simple, time-ordered list -
/// see NtfsCorruptionEventService.
/// </summary>
public sealed class NtfsCorruptionEvent
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string VolumeText { get; init; } = "Unknown volume";
    public string Message { get; init; } = string.Empty;
}
