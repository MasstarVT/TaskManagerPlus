using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

public enum StartupSource
{
    RegistryRunHkcu,
    RegistryRunHklm,
    RegistryRunHklmWow6432,
    StartupFolderUser,
    StartupFolderAllUsers,

    // #742: full autorun location sweep - RunOnce/RunOnceEx/RunServices/RunServicesOnce and their
    // Wow6432Node (32-bit-on-64-bit) variants, plus the two policy-enforced Run locations. None of
    // these have a StartupApproved equivalent, so the grid's enable/disable toggle is disabled for
    // every one of them - see StartupItem.SupportsToggle and StartupManagerService.SetEnabled.
    RegistryRunOnceHkcu,
    RegistryRunOnceHklm,
    RegistryRunOnceHklmWow6432,
    RegistryRunOnceExHklm,
    RegistryRunOnceExHklmWow6432,
    RegistryRunServicesHkcu,
    RegistryRunServicesHklm,
    RegistryRunServicesHklmWow6432,
    RegistryRunServicesOnceHkcu,
    RegistryRunServicesOnceHklm,
    RegistryRunServicesOnceHklmWow6432,
    PolicyRunHklm,
    PolicyRunHkcu,

    // #747: boot/logon-triggered scheduled tasks folded into this same grid as first-class rows -
    // see ScheduledTaskService.ListBootAndLogonTriggeredAsync. Toggling these calls back into
    // ScheduledTaskService.SetEnabledAsync (schtasks /change) rather than a StartupApproved flag.
    ScheduledTaskTrigger,
}

public sealed class StartupItem : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public StartupSource Source { get; init; }
    public string SourceDescription => Source switch
    {
        StartupSource.RegistryRunHkcu => "Registry (current user)",
        StartupSource.RegistryRunHklm => "Registry (all users)",
        StartupSource.RegistryRunHklmWow6432 => "Registry (all users, 32-bit)",
        StartupSource.StartupFolderUser => "Startup folder (current user)",
        StartupSource.StartupFolderAllUsers => "Startup folder (all users)",

        StartupSource.RegistryRunOnceHkcu => "RunOnce (current user)",
        StartupSource.RegistryRunOnceHklm => "RunOnce (all users)",
        StartupSource.RegistryRunOnceHklmWow6432 => "RunOnce (all users, 32-bit)",
        StartupSource.RegistryRunOnceExHklm => "RunOnceEx (all users)",
        StartupSource.RegistryRunOnceExHklmWow6432 => "RunOnceEx (all users, 32-bit)",
        StartupSource.RegistryRunServicesHkcu => "RunServices (current user, legacy)",
        StartupSource.RegistryRunServicesHklm => "RunServices (all users, legacy)",
        StartupSource.RegistryRunServicesHklmWow6432 => "RunServices (all users, 32-bit, legacy)",
        StartupSource.RegistryRunServicesOnceHkcu => "RunServicesOnce (current user, legacy)",
        StartupSource.RegistryRunServicesOnceHklm => "RunServicesOnce (all users, legacy)",
        StartupSource.RegistryRunServicesOnceHklmWow6432 => "RunServicesOnce (all users, 32-bit, legacy)",
        StartupSource.PolicyRunHklm => "Policy-enforced Run (all users)",
        StartupSource.PolicyRunHkcu => "Policy-enforced Run (current user)",

        StartupSource.ScheduledTaskTrigger => "Scheduled task (at boot / at logon)",
        _ => "Unknown",
    };

    /// <summary>#742: true only for the original Run-keys/Startup-folders sources (which map onto
    /// a real ...\Explorer\StartupApproved flag Explorer itself checks) and #747's merged
    /// scheduled-task rows (toggled via schtasks /change instead). Every other autorun location
    /// this app now inventories (RunOnce, RunOnceEx, RunServices/RunServicesOnce, the policy Run
    /// keys) has no approval-flag equivalent, so the grid's toggle is disabled there rather than
    /// faking a disable that wouldn't actually stop anything - per this app's "match what Explorer/
    /// Task Manager does" startup-toggle convention.</summary>
    public bool SupportsToggle => Source is StartupSource.RegistryRunHkcu or StartupSource.RegistryRunHklm
        or StartupSource.RegistryRunHklmWow6432 or StartupSource.StartupFolderUser or StartupSource.StartupFolderAllUsers
        or StartupSource.ScheduledTaskTrigger;

    private bool _isEnabled;
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }

    // #91: measured (not estimated) delay between boot and this item's process actually starting,
    // for whichever items are still running - see StartupDelayService's remarks.
    private string _measuredDelayText = "Checking...";
    public string MeasuredDelayText { get => _measuredDelayText; set => SetProperty(ref _measuredDelayText, value); }

    /// <summary>Round 8 #22: combined startup-impact score ("Low impact"/"Medium impact"/
    /// "High impact"), blending the measured delay above with a quick CPU/memory footprint sample
    /// of the matched running process - see StartupDelayService.ScoreImpact.</summary>
    private string _impactText = "Checking...";
    public string ImpactText { get => _impactText; set => SetProperty(ref _impactText, value); }

    private string _impactDetailText = string.Empty;
    public string ImpactDetailText { get => _impactDetailText; set => SetProperty(ref _impactDetailText, value); }

    /// <summary>Round 8 #18: "Signed"/"Unsigned"/"Unknown" for the item's target executable -
    /// reuses SignatureCheckService (extracted from ProcessMonitorService's Round 2 per-file-path
    /// cache) rather than duplicating the check.</summary>
    private string _signatureStatus = "Checking...";
    public string SignatureStatus { get => _signatureStatus; set => SetProperty(ref _signatureStatus, value); }

    /// <summary>Round 8 #21: file size and last-modified time of the target executable, read by
    /// StartupManagerService.Sample() - null for a file that can't be resolved or no longer exists.</summary>
    public long? FileSizeBytes { get; init; }
    public DateTime? LastModifiedUtc { get; init; }

    // #748: persisted per-item startup cost history (startup-history.json, keyed by Name) - a
    // median across this item's retained samples plus a sparkline, shown alongside (not instead
    // of) the single-scan MeasuredDelayText above, since that one number is volatile scan to scan.
    // MedianDelayText/DelayTrendFlag are null until at least one persisted sample exists for this
    // item's name; SparklinePointsText stays empty (and the grid draws nothing) below two samples.
    private string? _medianDelayText;
    public string? MedianDelayText { get => _medianDelayText; set => SetProperty(ref _medianDelayText, value); }

    /// <summary>Ready-to-bind "x,y x,y ..." point-collection text for a Polyline sparkline - WPF's
    /// built-in PointCollectionConverter parses this straight off a string binding with no value
    /// converter needed (the same trick this app's Geometry/Data string bindings already lean on).
    /// See StartupHistoryService.BuildSparkline for how it's built.</summary>
    private string _sparklinePointsText = string.Empty;
    public string SparklinePointsText { get => _sparklinePointsText; set => SetProperty(ref _sparklinePointsText, value); }

    /// <summary>"Grown from 1.2s to 14s over your last 20 boots" - null when the retained history
    /// doesn't show that shape (either not enough samples yet, or no real growth trend). Quick
    /// flag, not a verdict - see StartupHistoryService.BuildStats.</summary>
    private string? _delayTrendFlag;
    public string? DelayTrendFlag { get => _delayTrendFlag; set => SetProperty(ref _delayTrendFlag, value); }
}
