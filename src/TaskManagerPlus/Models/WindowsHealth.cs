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
