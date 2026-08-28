namespace TaskManagerPlus.Models;

/// <summary>
/// #175: one matched line out of CBS.log's `[SR]` (System Resource repair - the sfc /scannow
/// engine) block. CBS.log has no stable, versioned schema for these lines (same caveat
/// EtwTraceService's own remarks make about logman/tracerpt text output), so this is a raw matched
/// line plus a coarse category tag, never a fully-structured parse.
/// </summary>
public sealed class CbsSrLine
{
    public DateTime? Timestamp { get; set; }
    public string Text { get; set; } = "";

    /// <summary>True for the specific "Cannot repair member file" line shape - drives the row's
    /// highlight in the UI, since that's the one line shape that means "sfc found damage it could
    /// not fix."</summary>
    public bool IsUnrepairable { get; set; }
}

/// <summary>
/// #175: the result of scanning CBS.log (or an expanded CbsPersist_*.log archive, #176) for the
/// lines that matter rather than surfacing the whole file - `[SR]` lines, "Cannot repair member
/// file" lines, and matched 0xNNNNNNNN error codes. <see cref="Exists"/>/<see cref="ErrorMessage"/>
/// follow this app's "degrade to Unknown/hidden, never fabricate" rule: a missing or unreadable
/// CBS.log (permission-denied, locked by TrustedInstaller in a way even FileShare.ReadWrite can't
/// get past) is reported as such, not silently shown as "no problems found."
/// </summary>
public sealed class CbsLogSummary
{
    public bool Exists { get; set; }
    public string LogPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? ErrorMessage { get; set; }

    public int TotalLinesScanned { get; set; }

    /// <summary>Capped list of `[SR]` lines actually matched (see ServicingLogService's own cap) -
    /// a 100+ MB CBS.log can have many thousands of `[SR]` lines, so this is deliberately bounded
    /// rather than a full dump; <see cref="Truncated"/> tells the UI whether the cap was hit.</summary>
    public List<CbsSrLine> SrLines { get; set; } = new();
    public bool Truncated { get; set; }

    /// <summary>Every distinct "Cannot repair member file" target path found, parsed out of the
    /// matching `[SR]` lines - the actual "what's still broken" answer #175 is after.</summary>
    public List<string> CannotRepairFiles { get; set; } = new();

    /// <summary>Distinct 0xNNNNNNNN codes seen anywhere in the matched `[SR]`/error lines, in
    /// first-seen order - resolved to plain text on demand by the ViewModel via
    /// StatusCodeResolverService (#124), the same reuse #177's DISM parser and #124 itself already
    /// establish, rather than a second decoder.</summary>
    public List<string> ErrorCodes { get; set; } = new();
}

/// <summary>
/// #176: a plain-English summary of the CBS `[SR]` block - "N files scanned, M corrupt, K
/// repaired, list of unrepairable files." CBS.log does not always log an explicit "N files
/// scanned" total (that count is only shown on the sfc.exe console itself, not written to the
/// log), so <see cref="FilesScanned"/> is nullable and left null (shown as "Unknown" in the UI)
/// rather than guessed when no scan-count line is found - everything else here is a real count of
/// matched `[SR]` lines, not an estimate.
/// </summary>
public sealed class SfcResultSummary
{
    public bool Found { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Which file(s) this summary was built from - the live CBS.log, and/or one expanded
    /// CbsPersist_*.log archive if the live log had no `[SR]` activity (see
    /// ServicingLogService.SummarizeSfcResultAsync's remarks on that fallback).</summary>
    public List<string> SourceLogs { get; set; } = new();

    public int? FilesScanned { get; set; }
    public int CorruptCount { get; set; }
    public int RepairedCount { get; set; }
    public List<string> UnrepairableFiles { get; set; } = new();

    public DateTime? LastSrActivityUtc { get; set; }
}

/// <summary>#177: one Error/Warning line from the most recent DISM.log session, with the raw
/// operation text and any HRESULT found in it - decoded via #124's StatusCodeResolverService (the
/// ViewModel resolves <see cref="HResultCode"/> on demand, same as #124 already does for event
/// messages), not a second decoder.</summary>
public sealed class DismLogEntry
{
    public DateTime? Timestamp { get; set; }
    public string Level { get; set; } = "";
    public string Operation { get; set; } = "";
    public string? HResultCode { get; set; }
    public string? HResultMeaning { get; set; }
    public string RawLine { get; set; } = "";
}

/// <summary>#177: the parsed result of DISM.log's most recent session (the contiguous run of
/// lines at the end of the file, bounded by a timestamp gap - see
/// ServicingLogService.ParseDismLogAsync's remarks) - only the Error/Warning lines from that
/// session, not the whole log.</summary>
public sealed class DismLogSummary
{
    public bool Exists { get; set; }
    public string LogPath { get; set; } = "";
    public string? ErrorMessage { get; set; }

    public DateTime? SessionStartUtc { get; set; }
    public DateTime? SessionEndUtc { get; set; }
    public int LinesScannedInSession { get; set; }

    public List<DismLogEntry> Entries { get; set; } = new();
}

/// <summary>#178: the rollback reason (from setuperr.log) and last operation attempted before
/// failure (the tail of setupact.log) for a Windows upgrade/setup failure - read from
/// %WinDir%\Panther (the normal post-upgrade location) or, when present,
/// C:\$WINDOWS.~BT\Sources\Panther (left behind only by a *failed* in-place upgrade that rolled
/// back to the previous OS - see ServicingLogService's remarks on why both are checked).
/// setupact.log/setuperr.log have no documented stable line grammar, so this is a bounded set of
/// matched/tail lines, not a structured parse - "quick flag, not a verdict" on what actually went
/// wrong.</summary>
public sealed class SetupFailureAnalysis
{
    public bool Found { get; set; }
    public string? ErrorMessage { get; set; }
    public string SourceFolder { get; set; } = "";
    public bool IsFromFailedUpgradeLeftovers { get; set; }

    public List<string> RollbackReasonLines { get; set; } = new();

    /// <summary>The last several non-blank lines of setupact.log's tail (this file can be
    /// enormous - hundreds of MB on a busy upgrade - so this is a bounded tail read, never a full
    /// read; see ServicingLogService.ReadTail), shown as "the last operation attempted before
    /// failure."</summary>
    public List<string> LastOperationLines { get; set; } = new();

    public DateTime? SetupErrLastWriteUtc { get; set; }
    public DateTime? SetupActLastWriteUtc { get; set; }
}

/// <summary>#179: one failure line found in the text log `Get-WindowsUpdateLog` produces from the
/// binary/ETL Windows Update trace (%WinDir%\Logs\WindowsUpdate\*.etl, undecodable directly since
/// Windows 10 moved off plain-text WU logging) - a matched "FAILED"/error line, not a full parse
/// of WU's own internal log grammar (undocumented and has drifted across Windows releases).</summary>
public sealed class WindowsUpdateLogFailureLine
{
    public string Text { get; set; } = "";
}

/// <summary>#179: the result of running `Get-WindowsUpdateLog -LogPath &lt;temp&gt;` and scanning
/// its output for failures. The decoded text log itself is left on disk at
/// <see cref="LogFilePath"/> (in this app's own settings folder) so a user can open the full file
/// if the matched-failures list here isn't enough context.</summary>
public sealed class WindowsUpdateLogResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? LogFilePath { get; set; }
    public TimeSpan Duration { get; set; }
    public List<WindowsUpdateLogFailureLine> Failures { get; set; } = new();
}

/// <summary>#180: one row of the combined "update history with reasons" table - merges
/// Microsoft-Windows-WindowsUpdateClient/Operational events 19 (success)/20 (failure), the Setup
/// channel's events 1-4, and Win32_QuickFixEngineering (WMI) into one shape so the ViewModel can
/// bind one sorted list instead of three. <see cref="Source"/> tells the UI (and the reader) which
/// of the three this row actually came from, since each has a different reliability/detail level -
/// QFE rows in particular are inventory only (no failure information is available from QFE by
/// definition; only installed hotfixes appear there at all).</summary>
public sealed class UpdateHistoryEntry
{
    public DateTime? TimeCreated { get; set; }
    public string Source { get; set; } = ""; // "Windows Update Client", "Setup", "QFE inventory"
    public bool? Success { get; set; }
    public string? KbArticle { get; set; }
    public string? ResultCode { get; set; }

    /// <summary>Plain-English meaning of <see cref="ResultCode"/>, if any - see
    /// ServicingLogService's remarks on the small hand-written table of well-known servicing codes
    /// (#180) this is resolved from first, falling back to #124's general StatusCodeResolverService
    /// for anything not in that table.</summary>
    public string? ResultCodeMeaning { get; set; }

    public string Description { get; set; } = "";
}

/// <summary>#181: the pending-reboot/pending-servicing signal check - each signal is an
/// independent registry read that degrades to "not present" (never an error) when its key/value
/// doesn't exist on this Windows build, per the item's own "check the CBS registry key structure,
/// degrade cleanly if a given signal's key doesn't exist" instruction. This is a "quick flag, not
/// a verdict": Windows has no documented, authoritative "you are stuck mid-update" signal, so
/// <see cref="LooksStuck"/> is a heuristic (packages genuinely queued for next-boot processing),
/// worded as worth investigating rather than a diagnosis.</summary>
public sealed class PendingServicingStatus
{
    public bool CbsRebootPending { get; set; }
    public bool CbsRebootInProgress { get; set; }
    public bool CbsSessionsPending { get; set; }
    public int CbsPackagesPendingCount { get; set; }
    public bool WindowsUpdateRebootRequired { get; set; }
    public bool PendingFileRenameOperations { get; set; }

    public DateTime CheckedAtUtc { get; set; }

    public bool AnySignalActive => CbsRebootPending || CbsRebootInProgress || CbsSessionsPending
        || CbsPackagesPendingCount > 0 || WindowsUpdateRebootRequired || PendingFileRenameOperations;

    /// <summary>True only when packages are actually queued under CBS's PackagesPending key - the
    /// one signal here that specifically means "servicing has work queued for the next boot," as
    /// opposed to a plain "a reboot is pending" flag (which is normal after almost any update and
    /// resolves itself on the next restart).</summary>
    public bool LooksStuck => CbsPackagesPendingCount > 0;
}

/// <summary>A decoded status/error code pairing, used by the servicing-logs panel's on-demand
/// "decode these codes" actions (#175's CBS.log error-code list, #177's per-DISM-entry HRESULT) -
/// built as each code is resolved via #124's StatusCodeResolverService (one call per distinct
/// code, cached there), never fabricated. <see cref="Meaning"/> stays null (shown as "Unknown" in
/// the UI) when the resolver couldn't decode the code, same as every other resolver-backed field
/// in this app.</summary>
public sealed class ResolvedCode
{
    public string Code { get; set; } = "";
    public string? Meaning { get; set; }
}

/// <summary>#183: a small stat line for the servicing-logs panel - the size of
/// %WinDir%\Logs\CBS and how many CbsPersist_*.log archives it holds. Not a health "verdict" on
/// its own, just the raw numbers plus a reveal-in-Explorer button (reusing
/// EtwTraceService.RevealInExplorer, #159's helper).</summary>
public sealed class CbsLogHealth
{
    public bool Exists { get; set; }
    public string FolderPath { get; set; } = "";
    public long FolderSizeBytes { get; set; }
    public int CbsPersistArchiveCount { get; set; }
    public string? ErrorMessage { get; set; }
}
