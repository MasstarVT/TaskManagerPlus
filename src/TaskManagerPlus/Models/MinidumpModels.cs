namespace TaskManagerPlus.Models;

/// <summary>Round 14, item 14: dump-type classification read from a kernel/complete dump's own
/// DUMP_HEADER64 - Windows only writes this distinction for the classic "PAGEDU64"-signature
/// format (MEMORY.DMP, and occasionally a Minidump-folder file depending on the CrashDumpEnabled
/// registry setting - see MinidumpHousekeepingService); a small triage dump (the modern default
/// "Minidump" format, MDMP signature) is always effectively a mini dump and is reported as such
/// without reading this field at all - see MinidumpParserService.ParseDumpFile's remarks on the
/// signature-based format dispatch this enum backs. Automatic/Active dump variants aren't
/// distinguishable from Kernel/Complete by reading the file's own header alone (they're
/// configured, not recorded, via the CrashDumpEnabled registry value - see
/// MinidumpHousekeepingService.ReadHousekeeping), so this app doesn't guess at them here.</summary>
public enum KernelDumpType
{
    Unknown,
    Mini,
    Kernel,
    Complete,
}

/// <summary>Round 14, item 16: one loaded module (driver or image) read from a minidump's
/// ModuleListStream - just enough (name, base address, size) to do item 17's address-range
/// blame and item 19's cross-dump intersection, not a full PE/version parse of every module
/// (that's item 18, done lazily only for whichever single module ends up blamed).</summary>
public sealed class DumpModuleRef
{
    public string Name { get; init; } = string.Empty;
    public ulong BaseAddress { get; init; }
    public uint Size { get; init; }
}

/// <summary>Round 14, item 18: FileVersionInfo + signature-check dossier for whichever driver
/// item 17's address-range blame points at - resolved by locating &lt;name&gt; under
/// %SystemRoot%\System32\drivers first, then System32 itself, since a blamed module isn't
/// always literally a .sys file. Null/"Unknown" fields when the file can't be found on this
/// machine at all (a driver that was since uninstalled, or one that never had a matching
/// on-disk file at either path this heuristic checks) - CLAUDE.md's "degrade to Unknown, never
/// fabricate."</summary>
public sealed class DriverDossier
{
    public string FileName { get; init; } = string.Empty;
    public string? ResolvedPath { get; init; }
    public string? CompanyName { get; init; }
    public string? ProductName { get; init; }
    public string? FileVersion { get; init; }
    public DateTime? FileDate { get; init; }
    public string SignatureStatus { get; init; } = "Unknown";
}

/// <summary>Round 14, items 13-19: everything read directly out of one dump file's own binary
/// header/stream directory, independent of (and a cross-check against) the existing event-log-
/// correlated MinidumpInfo/BugCheckRecord from round 13. Kept as a separate model rather than
/// folding into MinidumpInfo since this data comes from a completely different source
/// (BinaryReader over the .dmp file itself) with its own independent failure mode - a dump that
/// can't be parsed at all still shows up in the Minidumps list via the existing event-log path,
/// this side is just absent/ParseError-only for it.</summary>
public sealed class ParsedDumpInfo
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public long SizeBytes { get; init; }

    /// <summary>"PAGEDU64" (classic kernel/complete-dump header) or "MDMP" (minidump/triage-dump
    /// format) - see MinidumpParserService.ParseDumpFile's remarks on why this app auto-detects
    /// per file rather than assuming one universal layout for every *.dmp.</summary>
    public string Format { get; init; } = "Unknown";

    public KernelDumpType DumpType { get; init; } = KernelDumpType.Unknown;
    public string DumpTypeText { get; init; } = "Unknown";

    /// <summary>Item 15: the header's own signature/size/directory-bounds checks failed - a
    /// dump cut short by a reset before the write finished, or otherwise corrupt. Flagged inline
    /// as "incomplete - not analysable" rather than presented as if the (partially garbage)
    /// fields below were reliable; whatever could still be read before the check failed is kept
    /// on the record rather than discarded, per CLAUDE.md's "keep whatever's real" convention.</summary>
    public bool IsIncomplete { get; init; }
    public string? IncompleteReason { get; init; }

    /// <summary>OS major.minor (build) and machine architecture read from DUMP_HEADER64 - only
    /// populated for the classic format (item 13). A minidump's SystemInfoStream carries the
    /// same information but this app only records that the stream is present (see StreamKinds),
    /// not its individual fields.</summary>
    public string? OsVersion { get; init; }
    public string? MachineType { get; init; }

    /// <summary>Item 13/16: bugcheck code + up to 4 parameters read directly from the dump's own
    /// binary data (DUMP_HEADER64 for the classic format, or a kernel-mode minidump's
    /// MINIDUMP_EXCEPTION_STREAM ExceptionCode/ExceptionInformation for the triage format) -
    /// independent of EventLogService's event-log-correlated MinidumpInfo.BugcheckCode/
    /// BugCheckRecord.</summary>
    public string? BugcheckCode { get; init; }
    public string[] BugcheckParameters { get; init; } = Array.Empty<string>();

    /// <summary>Item 16: which well-known stream types this dump's directory actually contains -
    /// shown as a plain "Contents: ModuleList, SystemInfo, Exception" detail line. Always empty
    /// for the classic DUMP_HEADER64 format, which has no stream directory at all.</summary>
    public List<string> StreamKinds { get; init; } = new();

    public List<DumpModuleRef> Modules { get; init; } = new();

    /// <summary>Item 17: the module whose [BaseAddress, BaseAddress+Size) range contains one of
    /// BugcheckParameters (or the minidump exception record's own fault address), if any -
    /// "quick flag, not a verdict" per CLAUDE.md: plain address-range matching against whichever
    /// bugcheck parameter happens to look like a pointer, not a symbolised stack walk. Several
    /// bugcheck codes don't even use their parameters as addresses at all, so a match here is a
    /// lead worth checking, never a diagnosis. Null when there's no module list (always true for
    /// the classic DUMP_HEADER64 format - it has no PsLoadedModuleList this app can read
    /// offline), no plausible address among the parameters, or no module's range contains one.</summary>
    public string? BlamedModule { get; init; }
    public DriverDossier? BlamedModuleDossier { get; init; }

    public string? ParseError { get; init; }
}

/// <summary>Round 14, item 20: %SystemRoot%\MEMORY.DMP - the kernel/complete dump, never shown
/// on this tab before now. Presence/size/timestamp are a plain FileInfo read; Parsed reuses the
/// same binary header parse every Minidump-folder file already goes through.</summary>
public sealed class MemoryDumpInfo
{
    public bool Exists { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTime? Timestamp { get; init; }
    public ParsedDumpInfo? Parsed { get; init; }
}

/// <summary>Round 14, items 21/22: one file under %SystemRoot%\LiveKernelReports - a live kernel
/// dump taken by a watchdog (DPCWATCHDOG, USBHUB3, NDIS, PoW32kWatchdog, VIDEO_ENGINE_TIMEOUT,
/// ...) without a bluescreen. Category is the immediate subfolder name (the watchdog component
/// itself names its own subfolder). WerCode/WerDescription (item 22) come from a joined WER
/// report folder whose EventType is LiveKernelEvent, when one is found for this file.</summary>
public sealed class LiveKernelReportInfo
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTime Timestamp { get; init; }
    public string? WerCode { get; init; }
    public string? WerDescription { get; init; }
}

/// <summary>Round 14, item 19: one third-party driver present in every dump's module list on
/// this machine - the strongest cheap cross-dump signal available without a real debugger.
/// "Every dump" only counts dumps this app could actually read a non-empty module list from
/// (DumpCount always equals the number of such dumps, by construction of how this list is
/// built - see MinidumpParserService.FindCommonDrivers).</summary>
public sealed class CommonDriverRow
{
    public string Name { get; init; } = string.Empty;
    public int DumpCount { get; init; }
}

/// <summary>Round 14, item 24: parsed `cdb -z &lt;dump&gt; -c "!analyze -v; q"` output - see
/// DebuggerToolsService.RunAnalyzeAsync. Null fields when that section header wasn't found in
/// the output (a normal outcome for a dump missing a MODULE_NAME/FAILURE_BUCKET_ID section, not
/// necessarily a parse failure) rather than an empty string standing in for "not found."</summary>
public sealed class CdbAnalysisResult
{
    public DateTime AnalyzedAt { get; init; }
    public string? ModuleName { get; init; }
    public string? ImageName { get; init; }
    public string? FailureBucketId { get; init; }
    public string? ProcessName { get; init; }
    public string? StackText { get; init; }
    public string RawOutput { get; init; } = string.Empty;
    public string? Error { get; init; }
}

/// <summary>Round 14, item 23: cdb.exe/windbg.exe locations found by probing the usual install
/// spots - Windows Kits Debugging Tools (traditional WinDbg/SDK install) and the Microsoft Store
/// WinDbg Preview package. Both null means "show a hint about installing the Windows SDK
/// Debugging Tools feature" instead of a dead "Open in WinDbg"/"Analyse" button.</summary>
public sealed class DebuggerAvailability
{
    public string? CdbPath { get; init; }
    public string? WindbgPath { get; init; }
    public bool AnyFound => CdbPath is not null || WindbgPath is not null;
}

/// <summary>Round 14, item 26: %SystemRoot%\Minidump folder housekeeping stats - total size,
/// oldest/newest file, and the live HKLM\...\CrashControl\MinidumpsCount value (how many small
/// dumps Windows keeps before recycling old ones). Null MinidumpsCountRegistryValue means the
/// value isn't set (Windows then falls back to its own undocumented built-in default) rather
/// than a fabricated number.</summary>
public sealed class MinidumpHousekeepingInfo
{
    public int FileCount { get; init; }
    public long TotalSizeBytes { get; init; }
    public DateTime? OldestFile { get; init; }
    public DateTime? NewestFile { get; init; }
    public int? MinidumpsCountRegistryValue { get; init; }
}
