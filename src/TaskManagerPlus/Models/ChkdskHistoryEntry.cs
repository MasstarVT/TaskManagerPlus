namespace TaskManagerPlus.Models;

/// <summary>
/// #342: one row in the combined "filesystem check history" list - either an app-initiated run
/// persisted by #340/#341 (a ChkdskScanRecord), or a boot-time/online chkdsk report Windows itself
/// already logged on its own (Wininit event 1001, or the classic "Chkdsk" provider's online-run
/// events 26212/26213/26214/26226) - both answer "did chkdsk run and what happened", shown as one
/// time-ordered list with the Source column making clear which is which.
/// </summary>
public sealed class ChkdskHistoryEntry
{
    public DateTime Timestamp { get; init; }
    public string DriveLetter { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;

    /// <summary>"KB in bad sectors" parsed from the report text, where present - null (not 0) when
    /// the report didn't mention bad sectors at all, same "never fabricate a zero" convention
    /// BadSectorService.ReadLatestChkdskBadSectors already uses for the single-most-recent-report
    /// version of this same figure.</summary>
    public long? BadSectorsKb { get; init; }

    /// <summary>Best-effort single line from the report mentioning a correction/fix - null when no
    /// such line was found (most reports on a healthy volume have nothing to report here).</summary>
    public string? ErrorsFixedText { get; init; }

    public string Summary { get; init; } = string.Empty;
}
