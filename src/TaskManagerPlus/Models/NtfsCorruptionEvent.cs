namespace TaskManagerPlus.Models;

/// <summary>
/// #344: one NTFS corruption signal from the System log - legacy "Ntfs" 55 (corruption detected) /
/// 130 (repaired), or "Microsoft-Windows-Ntfs" 98 (volume needs repair; healthy reports are
/// skipped) / 140 (transaction-log flush failed); see NtfsCorruptionEventService. VolumeText is a best-effort drive letter resolved
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
