namespace TaskManagerPlus.Models;

/// <summary>
/// #184-190: "kernel, storage and driver event families" - Stability-tab cards that each query one
/// specific, named event-ID family (rather than the broad Level=1|2 sweep EventLogService.ReadLog
/// already does) and present the result as a focused, purpose-built card instead of a handful of
/// rows buried in the flat "Recent critical / error events" grid. Every type here is built by
/// KernelEventFamilyService, which reuses EventLogExplorerService.ReadPage for the actual log reads
/// (see that service's remarks) rather than adding a new event-log reading path.
/// </summary>

/// <summary>#184: one physical disk's worth of the storage-fault event family (disk 7/11/51/153,
/// Microsoft-Windows-Ntfs 55/98/137/140, storahci/stornvme/iaStorA 129, volmgr 46), grouped by the
/// \Device\HarddiskN\DRn path parsed out of each event's own message text, then joined against
/// Win32_DiskDrive (via SystemSpecsService.ListDisksForSmart) and a disk-index -> drive-letter WMI
/// association KernelEventFamilyService builds itself for a friendly model/letter - "which physical
/// disk is throwing errors" instead of a flat count. DiskIndex is -1 (FriendlyName "Unknown disk")
/// when no event in the group's message carried a parseable device path - grouped together rather
/// than dropped, since the events themselves are still real (CLAUDE.md's "degrade, never
/// fabricate").</summary>
public sealed class StorageErrorDiskGroup
{
    public int DiskIndex { get; init; } = -1;
    public string FriendlyName { get; init; } = "Unknown disk";
    public int TotalCount { get; init; }
    public DateTime LastSeen { get; init; }
    public List<StorageErrorHit> Hits { get; init; } = new();
}

/// <summary>One (provider, eventId) family member's count/last-seen within one disk's group
/// above.</summary>
public sealed class StorageErrorHit
{
    public string Provider { get; init; } = string.Empty;
    public int EventId { get; init; }
    public int Count { get; init; }
    public DateTime LastSeen { get; init; }
    public string Description { get; init; } = string.Empty;
}

/// <summary>#185: one volume's shadow-copy storage allocation, parsed from `vssadmin list
/// shadowstorage` text output (see KernelEventFamilyService.ParseShadowStorageOutput) - Windows
/// exposes no WMI class for this, so shelling out and parsing text is the only option, the same
/// "known tool, parsed text" convention CLAUDE.md documents for vssadmin.exe elsewhere in this
/// app.</summary>
public sealed class ShadowStorageVolumeInfo
{
    public string ForVolume { get; init; } = string.Empty;
    public string StorageVolume { get; init; } = string.Empty;
    public string UsedSpace { get; init; } = string.Empty;
    public string AllocatedSpace { get; init; } = string.Empty;
    public string MaximumSpace { get; init; } = string.Empty;
}

/// <summary>#185: one volsnap (System log) or classic "VSS" source (Application log) shadow-copy
/// deletion/failure event - silently deleted shadow copies mean System Restore and File History are
/// quietly not working, so these are surfaced explicitly rather than left buried in the general
/// event list.</summary>
public sealed class ShadowCopyEventInfo
{
    public DateTime TimeCreated { get; init; }
    public string Provider { get; init; } = string.Empty;
    public int EventId { get; init; }
    public string Description { get; init; } = string.Empty;
}

/// <summary>#185: the Stability tab's "Backup and restore points" card - shadow-copy family events
/// plus vssadmin's own current storage-allocation snapshot, bundled together since both answer the
/// same underlying "is System Restore actually working" question. VssAdminError is non-null only
/// when vssadmin.exe itself couldn't be run/parsed - the event list above still shows independently
/// of whether vssadmin succeeded.</summary>
public sealed class ShadowCopyStatus
{
    public List<ShadowCopyEventInfo> Events { get; init; } = new();
    public List<ShadowStorageVolumeInfo> StorageVolumes { get; init; } = new();
    public string? VssAdminError { get; init; }
}

/// <summary>#186: one day's Microsoft-Windows-WHEA-Logger 17/18/19/47 (corrected/uncorrected
/// hardware-error) count, plus the totals for the card header - deliberately the same Date+Count
/// shape as EventLogService's own DailyEventCount so the Stability view's existing small-column-
/// chart binding pattern applies unchanged. "Quick flag, not a verdict" - corrected errors (17/47)
/// are not fatal by themselves; only frequent recurrence is worth investigating.</summary>
public sealed class WheaErrorSummary
{
    public List<DailyEventCount> DailyCounts { get; init; } = new();
    public int TotalCount { get; init; }
    public DateTime? LastSeen { get; init; }
}

/// <summary>#187: one driver-load-failure event (Kernel-PnP 219/411/442, UserPnp 20001/20003), with
/// the driver/device name best-effort-extracted from the event's own inserted properties (the same
/// positional, best-effort convention EventLogService.ExtractBugcheckCode already uses for event
/// 41's property 0) and joined - by module name, case-insensitive, extension-agnostic - against
/// `driverquery /v /fo csv` output. DriverInfo is null when driverquery has nothing under that name
/// (a driver since uninstalled, or a name the event didn't actually carry), never a fabricated
/// match.</summary>
public sealed class DriverFailureEvent
{
    public DateTime TimeCreated { get; init; }
    public string Provider { get; init; } = string.Empty;
    public int EventId { get; init; }
    public string? DriverName { get; init; }
    public string Description { get; init; } = string.Empty;
    public InstalledDriverInfo? DriverInfo { get; init; }
}

/// <summary>#187: one row of `driverquery /v /fo csv` - only the fields that CSV actually reports.
/// There is no "driver version" column in driverquery's own output, so this never invents one;
/// LinkDate is the closest real proxy for "how old is this build" CLAUDE.md's "degrade, never
/// fabricate" rule allows.</summary>
public sealed class InstalledDriverInfo
{
    public string ModuleName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string StartMode { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string LinkDate { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
}

/// <summary>#188: one autochk/chkdsk run's outcome, parsed from Wininit event 1001's full boot-time
/// report text or a Chkdsk 26212/26214 event - see KernelEventFamilyService.BuildChkdskRun for the
/// best-effort text scan. BadSectorsFoundKb/CompletedCleanly come back null/false when the summary
/// text didn't match a recognized phrase (chkdsk's own wording has drifted across Windows versions)
/// rather than a guessed value - RawSummary always carries the real (truncated) text so nothing is
/// lost even when the structured fields come back empty.</summary>
public sealed class ChkdskRunInfo
{
    public DateTime TimeCreated { get; init; }
    public string Source { get; init; } = string.Empty; // "Wininit 1001" / "Chkdsk 26212" / "Chkdsk 26214"
    public string? Volume { get; init; }
    public long? BadSectorsFoundKb { get; init; }
    public bool CompletedCleanly { get; init; }
    public string RawSummary { get; init; } = string.Empty;
}

/// <summary>#189: the outcome of the most recent Windows Memory Diagnostic run
/// (Microsoft-Windows-MemoryDiagnostics-Results 1101/1201), or "never run" when no such event was
/// found at all within KernelEventFamilyService's deliberately long lookback for this one query - a
/// genuinely rare event, not a 30-day-window question like most of this tab's other cards.</summary>
public sealed class MemoryDiagnosticStatus
{
    public bool HasEverRun { get; init; }
    public DateTime? LastRunTime { get; init; }
    public string? Outcome { get; init; }
}

/// <summary>#190: one Kernel-Power 125/126/131 (device power-transition failure) or
/// Kernel-Processor-Power 37/55 (processor power/throttling) event - "power and throttling
/// incidents," a historical record card that complements (but deliberately doesn't merge with) the
/// existing live thermal-throttle/power-limit flag on the CPU/Energy &amp; Thermals tabs.</summary>
public sealed class PowerTransitionIncident
{
    public DateTime TimeCreated { get; init; }
    public string Provider { get; init; } = string.Empty;
    public int EventId { get; init; }
    public string? DeviceName { get; init; }
    public string Description { get; init; } = string.Empty;
}
