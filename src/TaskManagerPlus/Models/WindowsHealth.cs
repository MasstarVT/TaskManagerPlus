namespace TaskManagerPlus.Models;

/// <summary>#769/#771: one Windows Update Client install/download/failure event, read from
/// Microsoft-Windows-WindowsUpdateClient/Operational (event IDs 19/20/21/25/31/43/44) - a much
/// fuller history than Win32_QuickFixEngineering's own hotfix-only view (see
/// SystemSpecsService.ReadRecentHotfixes' remarks: QFE only sees CBS-installed hotfixes and misses
/// driver/definition/feature updates entirely). See WindowsUpdateHistoryService.ReadUpdateClientHistory.
/// ErrorDescription/ServicingCorrelationText are filled in afterward (not at construction) - the
/// first by an async WindowsUpdateErrorCatalog lookup per unique code (#772), the second by
/// correlating against the Setup channel's own failure events (#771) - both cheap, pure
/// post-processing over the same event list, not a second event-log query.</summary>
public sealed class WindowsUpdateEvent
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }

    /// <summary>Plain-language label for EventId - see WindowsUpdateHistoryService.KindLabel.</summary>
    public string Kind { get; init; } = string.Empty;

    public bool IsFailure { get; init; }

    /// <summary>Update title/KB, best-effort extracted from the rendered message's tail (#8's
    /// ExtractFaultingModule approach for the same reason: not every one of these event templates
    /// has a documented indexed property for the title). Empty when extraction didn't match.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Hex HRESULT (e.g. "0x80073712"), only ever populated on a failure event.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>#772: plain-language cause+fix for ErrorCode, resolved asynchronously after the
    /// initial read via WindowsUpdateErrorCatalog.DescribeAsync - null until resolved, and stays
    /// null when ErrorCode itself is null.</summary>
    public string? ErrorDescription { get; set; }

    /// <summary>#771: the matching Setup-channel (Microsoft-Windows-Servicing) failure entry within
    /// a few minutes of this event, when one was found - null when nothing correlated (including
    /// every non-failure event, which is never matched against).</summary>
    public string? ServicingCorrelationText { get; set; }

    public string RawMessage { get; init; } = string.Empty;
}

/// <summary>#770: one row from `dism /online /get-packages /format:table` - every installed
/// servicing package with its current state. IsFlagged drives the "Install Pending"/"Uninstall
/// Pending"/"Failed" highlight - a quick flag, not a verdict (a package can sit in "Install
/// Pending" for a normal, still-in-progress reason).</summary>
public sealed class ServicingPackageInfo
{
    public string PackageIdentity { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;

    public bool IsFlagged =>
        State.Contains("Pending", StringComparison.OrdinalIgnoreCase) ||
        State.Contains("Failed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>#771: one event from the Setup channel (provider Microsoft-Windows-Servicing) - event 1
/// (initiating a change for a package), 2 (package changed to a new state), 3/4 (failure, with an
/// HRESULT). See WindowsUpdateHistoryService.ReadSetupChannelEvents.</summary>
public sealed class ServicingChannelEvent
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string PackageName { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string? ErrorCode { get; init; }
    public bool IsFailure { get; init; }

    public string EventLabel => EventId switch
    {
        1 => "Change initiated",
        2 => "State changed",
        3 or 4 => "Failure",
        _ => $"Event {EventId}",
    };
}

/// <summary>#773: best-effort analysis of a failed in-place upgrade's own logs
/// (C:\$WINDOWS.~BT\Sources\Panther\setuperr.log/setupact.log, or C:\Windows\Panther\* for an
/// upgrade that got far enough to commit before failing) - the "Host: "/"Rollback" markers and
/// MOUPG error lines those logs carry, plus SetupDiag.exe's own verdict when that tool is present
/// on the machine (its actual analysis engine is not reimplemented here - see
/// WindowsServicingService.RunSetupDiagAsync). LogsFound false means neither log location exists,
/// the common case on a machine that's never attempted (or has fully cleaned up after) a feature
/// update.</summary>
public sealed class FeatureUpdateFailureInfo
{
    public bool LogsFound { get; init; }
    public string SourceLogPath { get; init; } = string.Empty;
    public string? FailingPhase { get; init; }
    public string? RollbackReason { get; init; }
    public List<string> MoupgErrorLines { get; init; } = new();

    public bool SetupDiagAvailable { get; init; }
    public string? SetupDiagVerdict { get; init; }
}

/// <summary>#774: one reboot-pending indicator - turns SystemSpecsService.ReadRebootPending's
/// single bool into a detail list naming which specific indicator fired, with a plain-language
/// detail (e.g. the actual PendingFileRenameOperations file list, or the ActiveComputerName vs
/// ComputerName mismatch values) and, where available, that registry key's own last-write time.
/// See WindowsUpdatePolicyService.ReadRebootPendingDetail.</summary>
public sealed class RebootPendingIndicator
{
    public string Name { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public string Detail { get; init; } = string.Empty;
    public DateTime? LastWriteTime { get; init; }
}

/// <summary>#775: "why has this PC not had an update in 8 months" - pause/defer/policy state read
/// from HKLM\...\WindowsUpdate\UX\Settings and the HKLM\...\Policies\...\WindowsUpdate tree.
/// SummaryText is composed once, in WindowsUpdatePolicyService, from whichever of these fields are
/// actually set - a denied/missing key just leaves its field null/false, same as every other
/// registry read in this app.</summary>
public sealed class UpdatePolicySnapshot
{
    public DateTime? PauseUpdatesExpiryTime { get; init; }
    public DateTime? PauseFeatureUpdatesStart { get; init; }
    public DateTime? PauseFeatureUpdatesEnd { get; init; }
    public DateTime? PauseQualityUpdatesStart { get; init; }
    public DateTime? PauseQualityUpdatesEnd { get; init; }
    public string? ActiveHoursStart { get; init; }
    public string? ActiveHoursEnd { get; init; }

    /// <summary>Group Policy "Configure Automatic Updates" disabled entirely.</summary>
    public bool NoAutoUpdate { get; init; }
    public int? DeferFeatureUpdatesPeriodInDays { get; init; }
    public int? DeferQualityUpdatesPeriodInDays { get; init; }
    public string? TargetReleaseVersionInfo { get; init; }
    public string? WuServer { get; init; }
    public bool UseWuServer { get; init; }

    public bool IsPausedNow => PauseUpdatesExpiryTime is { } t && t > DateTime.Now;

    public string SummaryText { get; init; } = "No update pause, deferral, or WSUS policy found - nothing here is blocking updates.";
}

/// <summary>#776: on-demand plain-HTTP reachability result for a WSUS/WUfB server named by the
/// WUServer policy value - a GET/HEAD against the URL and nothing more (not a WSUS protocol
/// handshake), so this only ever tells "something answered" vs. "connection failed/timed out",
/// which is exactly what silently produces an 0x8024xxxx error on every client pointed at a dead
/// WUServer. See WindowsUpdatePolicyService.CheckWuServerReachabilityAsync.</summary>
public sealed class WuServerReachabilityResult
{
    public bool IsReachable { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public DateTime CheckedAt { get; init; }
}

/// <summary>#777: one entry of the update service-stack health check - start type/state for one of
/// the services Windows Update itself depends on (wuauserv, BITS, CryptSvc, msiserver,
/// TrustedInstaller, UsoSvc, WaaSMedicSvc, DoSvc). IsFlagged marks a service set to Disabled, which
/// silently breaks updates the same way a dead WSUS pointer does. See
/// ServiceControlService.ReadUpdateServiceStackHealth.</summary>
public sealed class UpdateServiceHealthEntry
{
    public string ServiceName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string StartTypeText { get; init; } = "Unknown";
    public string StatusText { get; init; } = "Unknown";
    public bool IsDisabled { get; init; }
    public bool IsMissing { get; init; }

    public bool IsFlagged => IsDisabled || IsMissing;
}

/// <summary>#779: measured sizes of the two update-related cache folders under SoftwareDistribution -
/// the download cache (wuauserv) and the Delivery Optimization cache (DoSvc). See
/// WindowsServicingService.ReadUpdateCacheSizes.</summary>
public sealed class UpdateCacheInfo
{
    public string DownloadCachePath { get; init; } = string.Empty;
    public long DownloadCacheSizeBytes { get; init; }
    public string DeliveryOptimizationCachePath { get; init; } = string.Empty;
    public long DeliveryOptimizationCacheSizeBytes { get; init; }
}

/// <summary>#780: one candidate for "uninstall a specific update" - either a KB-numbered hotfix from
/// Win32_QuickFixEngineering (removable via `wusa /uninstall /kb:&lt;n&gt;`) or a DISM servicing
/// package in the "Installed" state (removable via `dism /remove-package /packagename:&lt;name&gt;`).
/// See WindowsUpdateUninstallService.</summary>
public sealed class RemovableUpdateInfo
{
    /// <summary>The bare KB number (e.g. "5005565") for a QFE hotfix, or the full DISM package
    /// identity for a servicing package - whichever UninstallAsync needs to build its command line.</summary>
    public string Identifier { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public DateTime? InstalledOn { get; init; }
    public string Source { get; init; } = string.Empty;
    public bool IsDismPackage { get; init; }
}

/// <summary>#782: `dism /online /cleanup-image /analyzecomponentstore` output, parsed into
/// display-ready fields. Sizes are kept as DISM's own formatted text ("12.86 GB") rather than
/// re-parsed to bytes - this app doesn't control DISM's exact unit/locale formatting, so
/// re-parsing it risks silently showing a wrong number; the raw text is never wrong. RawOutput
/// backs the "show details" expander. See DismService.AnalyzeComponentStoreAsync.</summary>
public sealed class ComponentStoreAnalysis
{
    public bool Success { get; init; }
    public string? ErrorText { get; init; }
    public string? ActualSizeText { get; init; }
    public string? SharedWithWindowsText { get; init; }
    public string? BackupsAndDisabledFeaturesText { get; init; }
    public string? CacheAndTempDataText { get; init; }
    public string? DateOfLastCleanupText { get; init; }
    public int? ReclaimablePackageCount { get; init; }
    public bool? CleanupRecommended { get; init; }
    public string RawOutput { get; init; } = string.Empty;
}

/// <summary>#784: which of DISM's three health-check verbs a DismHealthScanResult came from -
/// CheckHealth (instant, reads a stored corruption flag), ScanHealth (a real scan, can take
/// minutes), RestoreHealth (attempts an actual repair, needs a repair source on failure).</summary>
public enum DismHealthOperation
{
    CheckHealth,
    ScanHealth,
    RestoreHealth,
}

/// <summary>#784: the result of one CheckHealth/ScanHealth/RestoreHealth run. NeedsRepairSource is
/// true specifically for RestoreHealth failing with 0x800f081f - see DismService.RunHealthScanAsync
/// and #785's RestoreHealth source picker. DismLogTail is only populated on failure (per this
/// item's spec: "tail of dism.log shown on failure").</summary>
public sealed class DismHealthScanResult
{
    public DismHealthOperation Operation { get; init; }
    public bool Success { get; init; }
    public int ExitCode { get; init; }

    /// <summary>true = "is repairable"/corruption found; false = "no corruption found"; null = the
    /// tool's own output didn't say either way in a form this app recognizes (CheckHealth also
    /// doesn't distinguish "not yet scanned" from these two, per DISM's own documented behavior).</summary>
    public bool? IsRepairable { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string RawOutput { get; init; } = string.Empty;
    public double DurationSeconds { get; init; }
    public bool NeedsRepairSource { get; init; }
    public string? ErrorCode { get; init; }
    public string? DismLogTail { get; init; }
}

/// <summary>#785: one image index from `dism /Get-WimInfo /WimFile:&lt;path&gt;` - lets the user
/// pick the edition that matches this PC before RestoreHealth retries against it. See
/// DismService.GetWimInfoAsync.</summary>
public sealed class WimImageInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
}

/// <summary>#786: one `sfc /scannow` run, with the CBS.log [SR]-line subset extracted rather than
/// just the one-line verdict - see SfcIntegrityService.RunScanAsync. ExtractedLogPath is where that
/// subset was written under AppPaths.SettingsDirectory (null if extraction itself failed - the
/// verdict/lists here still stand on their own since they're parsed before the write).</summary>
public sealed class SfcScanResult
{
    public bool Success { get; init; }
    public string VerdictText { get; init; } = string.Empty;
    public bool FoundViolations { get; init; }
    public bool AllRepaired { get; init; }
    public List<string> RepairedFiles { get; init; } = new();
    public List<string> UnrepairableEntries { get; init; } = new();
    public double DurationSeconds { get; init; }
    public string? ExtractedLogPath { get; init; }
    public string RawOutputTail { get; init; } = string.Empty;
}

/// <summary>#787: one persisted entry in integrity-history.json (AppPaths.SettingsDirectory) -
/// covers both SFC and every DISM health-scan verb, so the timeline under the scan buttons can show
/// "SFC", "DISM CheckHealth", "DISM ScanHealth", "DISM RestoreHealth" runs side by side. See
/// SfcIntegrityService.LoadHistory/AppendAndSave/CompareToPreviousRun.</summary>
public sealed class IntegrityHistoryEntry
{
    public DateTime Timestamp { get; init; }
    public string ScanType { get; init; } = string.Empty;
    public string Verdict { get; init; } = string.Empty;
    public double DurationSeconds { get; init; }
    public bool Success { get; init; }
    public List<string> UnrepairableFiles { get; init; } = new();

    public string DurationText => DurationSeconds < 60
        ? $"{DurationSeconds:0}s"
        : $"{(int)(DurationSeconds / 60)}m {DurationSeconds % 60:0}s";
}

/// <summary>#788: the in-place repair-install escape hatch checklist, shown after DISM
/// /RestoreHealth fails or SFC reports unrepairable files on two scans in a row.
/// Edition/Build/DisplayVersion/Language/Architecture are read straight from the registry (see
/// SfcIntegrityService.ReadMatchingImageSpec) so the card names the exact ISO to go find, rather
/// than leaving the user to guess which one matches this PC. Guidance only - this app never
/// downloads or launches an OS installer itself.</summary>
public sealed class RepairInstallGuidance
{
    public string TriggerReason { get; init; } = string.Empty;
    public string EditionText { get; init; } = "Unknown";
    public string BuildText { get; init; } = "Unknown";
    public string DisplayVersionText { get; init; } = "Unknown";
    public string LanguageText { get; init; } = "Unknown";
    public string ArchitectureText { get; init; } = "Unknown";
    public List<string> ChecklistItems { get; init; } = new();
    public string SetupCommandLine { get; init; } = "setup.exe /auto upgrade";
}

/// <summary>#789: one restore point from the SystemRestore WMI class (root\default, not the usual
/// root\cimv2 every other WMI read in this app uses). See SystemRestoreService.ReadSnapshotAsync.</summary>
public sealed class RestorePointInfo
{
    public uint SequenceNumber { get; init; }
    public string Description { get; init; } = string.Empty;
    public string RestorePointTypeText { get; init; } = string.Empty;
    public DateTime? CreationTime { get; init; }
}

/// <summary>#789: "System Protection is on for this volume" inferred from whether vssadmin reports
/// a shadow-storage association for it - a quick flag, not a verdict (see
/// SystemRestoreService.BuildVolumeProtection's remarks for why there's no simpler documented
/// signal). ProtectionLooksOn is true when a shadow-storage association was found; null (not
/// false) otherwise, since "no association yet" isn't proof protection itself is off.</summary>
public sealed class VolumeProtectionStatus
{
    public string Volume { get; init; } = string.Empty;
    public bool? ProtectionLooksOn { get; init; }
    public string Detail { get; init; } = string.Empty;
}

/// <summary>#789: one volume's shadow-copy storage allocation from `vssadmin list shadowstorage` -
/// kept as vssadmin's own formatted text per volume (Used/Allocated/Maximum), the same
/// "don't re-parse a unit this app doesn't control the format of" tradeoff ComponentStoreAnalysis
/// takes for DISM's sizes above.</summary>
public sealed class ShadowStorageVolumeInfo
{
    public string Volume { get; init; } = string.Empty;
    public string UsedText { get; init; } = string.Empty;
    public string AllocatedText { get; init; } = string.Empty;
    public string MaxText { get; init; } = string.Empty;
}

/// <summary>#789: the full Recovery-section snapshot - restore points, per-volume protection
/// inference, shadow-storage allocation, and the documented automatic-restore-point-frequency
/// policy value. SystemRestoreAvailable is false only when the SystemRestore WMI class itself
/// couldn't be reached (System Restore not installed at all, e.g. some Server SKUs) - distinguished
/// from "installed but zero points" (HasNoRestorePointsAtAll) so the UI shows the right message.</summary>
public sealed class SystemRestoreSnapshot
{
    public bool SystemRestoreAvailable { get; init; } = true;
    public List<RestorePointInfo> RestorePoints { get; init; } = new();
    public List<VolumeProtectionStatus> VolumeProtection { get; init; } = new();
    public List<ShadowStorageVolumeInfo> ShadowStorage { get; init; } = new();
    public int? AutomaticFrequencyMinutes { get; init; }
    public string? ErrorText { get; init; }

    /// <summary>#789: "System Protection is off, so you have no restore points at all" flag.</summary>
    public bool HasNoRestorePointsAtAll => SystemRestoreAvailable && RestorePoints.Count == 0;
}
