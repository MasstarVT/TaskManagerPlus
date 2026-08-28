using System.ServiceProcess;
using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

public sealed class ServiceRow : ObservableObject
{
    public string ServiceName { get; init; } = string.Empty;

    private string _displayName = string.Empty;
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }

    private ServiceControllerStatus _status;
    public ServiceControllerStatus Status { get => _status; set => SetProperty(ref _status, value); }

    private ServiceStartMode _startType;
    public ServiceStartMode StartType
    {
        get => _startType;
        set
        {
            if (SetProperty(ref _startType, value))
            {
                OnPropertyChanged(nameof(HasFailedToStart));
                OnPropertyChanged(nameof(StartTypeDisplay));
            }
        }
    }

    /// <summary>#755: registry DelayedAutostart flag under this service's own key, read alongside
    /// Description in ServiceControlService.Sample() - a single trivial per-service registry read,
    /// the same cost class Description already pays every tick. See AutoStartDelaySeconds and
    /// StartTypeDisplay.</summary>
    private bool _isDelayedAutoStart;
    public bool IsDelayedAutoStart
    {
        get => _isDelayedAutoStart;
        set { if (SetProperty(ref _isDelayedAutoStart, value)) OnPropertyChanged(nameof(StartTypeDisplay)); }
    }

    /// <summary>#755: the machine-wide HKLM\SYSTEM\CurrentControlSet\Control\AutoStartDelay value
    /// (Windows' documented default is 120 seconds when the value is absent) - the same for every
    /// service, but read alongside IsDelayedAutoStart so StartTypeDisplay doesn't need a second
    /// lookup.</summary>
    private int _autoStartDelaySeconds = 120;
    public int AutoStartDelaySeconds { get => _autoStartDelaySeconds; set => SetProperty(ref _autoStartDelaySeconds, value); }

    /// <summary>#755: what the Startup type column actually shows - "Automatic (Delayed Start)"
    /// with the machine's configured delay, the same distinction Explorer's own Services snap-in
    /// draws, instead of a bare "Automatic" that leaves a service legitimately still-stopped in the
    /// first couple of minutes after boot looking indistinguishable from one that never starts.</summary>
    public string StartTypeDisplay => StartType == ServiceStartMode.Automatic && IsDelayedAutoStart
        ? $"Automatic (Delayed Start, ~{AutoStartDelaySeconds}s)"
        : StartType.ToString();

    private string _description = string.Empty;
    public string Description { get => _description; set => SetProperty(ref _description, value); }

    private int _processId;
    public int ProcessId { get => _processId; set => SetProperty(ref _processId, value); }

    /// <summary>Win32_Service.ExitCode from its last start attempt. 0 means "no error reported" -
    /// true even for a service that's simply not running yet (delayed-auto-start, trigger-start,
    /// or a normal clean stop all report 0 too), so this is NOT the same as "currently running".
    /// See ServiceControlService for why StartType==Automatic + State!=Running alone is too noisy
    /// a signal to use for "failed to start".</summary>
    private uint _exitCode;
    public uint ExitCode
    {
        get => _exitCode;
        set { if (SetProperty(ref _exitCode, value)) OnPropertyChanged(nameof(HasFailedToStart)); }
    }

    /// <summary>True only when StartType is Automatic AND the last recorded start attempt
    /// reported a real Win32 error code - see ExitCode's remarks for why "Automatic but not
    /// currently running" alone isn't used (delayed-start and trigger-start services are
    /// legitimately stopped most of the time and would otherwise dominate this list). #755 adds
    /// direct data for the delayed-start half of that noise (IsDelayedAutoStart/
    /// AutoStartDelaySeconds, shown via StartTypeDisplay) rather than only inferring it indirectly -
    /// this flag's own condition stays as-is regardless, since ExitCode != 0 already means a real
    /// start attempt was made and failed even for a delayed-start service.</summary>
    public bool HasFailedToStart => StartType == ServiceStartMode.Automatic && ExitCode != 0;

    /// <summary>Other services this one depends on to start, and services that in turn depend on
    /// it (#37) - shown so a user understands the blast radius before stopping a service. Display
    /// names, resolved from ServiceController.ServicesDependedOn/DependentServices.</summary>
    private IReadOnlyList<string> _dependsOn = Array.Empty<string>();
    public IReadOnlyList<string> DependsOn { get => _dependsOn; set => SetProperty(ref _dependsOn, value); }

    private IReadOnlyList<string> _dependentServices = Array.Empty<string>();
    public IReadOnlyList<string> DependentServices { get => _dependentServices; set => SetProperty(ref _dependentServices, value); }

    /// <summary>Recovery/failure actions text (#71, e.g. "Restart the service" / "Run a program"
    /// after N failures), loaded on demand via ViewFailureActionsCommand - see
    /// ServiceControlService.ReadFailureActionsText. Empty until requested, same "expensive, so
    /// make it explicit" tradeoff as Processes' on-demand module list.</summary>
    private string _failureActionsText = string.Empty;
    public string FailureActionsText { get => _failureActionsText; set => SetProperty(ref _failureActionsText, value); }

    /// <summary>Round 7 #14: the account this service logs on as (Win32_Service.StartName -
    /// LocalSystem, a virtual per-service SID "NT SERVICE\...", a network service account, or a
    /// real domain/local user for a small minority of services). See
    /// ServiceControlService.ReadServiceAccounts.</summary>
    private string _logOnAs = string.Empty;
    public string LogOnAs
    {
        get => _logOnAs;
        set { if (SetProperty(ref _logOnAs, value)) OnPropertyChanged(nameof(IsNonStandardAccount)); }
    }

    private static readonly string[] StandardAccounts =
    {
        "LocalSystem", "NT AUTHORITY\\LocalService", "NT AUTHORITY\\NetworkService",
        "NT AUTHORITY\\LOCAL SERVICE", "NT AUTHORITY\\NETWORK SERVICE",
    };

    /// <summary>True when LogOnAs is neither empty (drivers) nor one of the four standard built-in
    /// service accounts - worth a second look when auditing services for something unexpected, the
    /// same "quick flag" spirit as Processes' IsHighPrivilege.</summary>
    public bool IsNonStandardAccount =>
        !string.IsNullOrEmpty(LogOnAs) &&
        !StandardAccounts.Contains(LogOnAs, StringComparer.OrdinalIgnoreCase) &&
        !LogOnAs.StartsWith("NT SERVICE\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Round 7 #13: an approximate measured start duration mined from Service Control
    /// Manager 7036 event-log entries, loaded on demand via ServicesViewModel.LoadStartDurationsAsync -
    /// see EventLogService.ReadServiceStartDurations for exactly what's measured and its
    /// limitations. Empty until requested.</summary>
    private string _startDurationText = string.Empty;
    public string StartDurationText { get => _startDurationText; set => SetProperty(ref _startDurationText, value); }

    /// <summary>Round 7 #16: set after comparing this row's StartType/LogOnAs against a saved
    /// baseline snapshot (ServicesViewModel.CheckConfigDriftAsync) - empty/false until a baseline
    /// has been loaded and compared.</summary>
    private bool _hasConfigDrift;
    public bool HasConfigDrift { get => _hasConfigDrift; set => SetProperty(ref _hasConfigDrift, value); }

    private string _configDriftText = string.Empty;
    public string ConfigDriftText { get => _configDriftText; set => SetProperty(ref _configDriftText, value); }

    /// <summary>Round 7 #15: true for a row sourced from ServiceController.GetDevices() (kernel/
    /// file-system drivers) rather than GetServices() - see ServiceControlService.SampleDrivers.
    /// Drivers report a much narrower set of the fields above (no dependencies, often no logon
    /// account), so the Services view hides those columns for driver rows.</summary>
    public bool IsDriver { get; init; }

    /// <summary>#749/#750/#751: SCM failure/crash history for this service within the lookback
    /// window, loaded on demand via ServicesViewModel.LoadFailureHistoryCommand - see
    /// EventLogService.ReadServiceFailureEvents. Empty until requested.</summary>
    private IReadOnlyList<ServiceScmEvent> _scmEvents = Array.Empty<ServiceScmEvent>();
    public IReadOnlyList<ServiceScmEvent> ScmEvents
    {
        get => _scmEvents;
        set { if (SetProperty(ref _scmEvents, value)) OnPropertyChanged(nameof(HasScmEvents)); }
    }

    public bool HasScmEvents => ScmEvents.Count > 0;

    /// <summary>#750: count of 7031/7034 ("terminated unexpectedly") events within the last 24
    /// hours - crossing CrashLoopThreshold flags the row as crash-looping.</summary>
    private int _crashLoopCount24H;
    public int CrashLoopCount24H
    {
        get => _crashLoopCount24H;
        set { if (SetProperty(ref _crashLoopCount24H, value)) OnPropertyChanged(nameof(IsCrashLooping)); }
    }

    private const int CrashLoopThreshold = 3;

    /// <summary>#750: "more than a handful" of unexpected terminations within 24h - a quick flag,
    /// not a verdict (a service with real recovery actions configured may legitimately restart
    /// itself often).</summary>
    public bool IsCrashLooping => CrashLoopCount24H > CrashLoopThreshold;

    /// <summary>#751: dependency-failure root-cause chain, walked from this service down through
    /// its static DependsOn graph using each hop's own #749 failure history - see
    /// ServicesViewModel.BuildRootCause. Empty until failure history has been loaded, or when no
    /// failure was found anywhere in the chain.</summary>
    private string _dependencyRootCauseText = string.Empty;
    public string DependencyRootCauseText { get => _dependencyRootCauseText; set => SetProperty(ref _dependencyRootCauseText, value); }

    /// <summary>#752: this service's DependOnService names a service that either doesn't exist or
    /// has Start=4 (Disabled) - either would keep this service from ever starting. See
    /// ServiceControlService.RunInventoryAudit. False until an audit has been run.</summary>
    private bool _hasBrokenDependency;
    public bool HasBrokenDependency { get => _hasBrokenDependency; set => SetProperty(ref _hasBrokenDependency, value); }

    private string _brokenDependencyText = string.Empty;
    public string BrokenDependencyText { get => _brokenDependencyText; set => SetProperty(ref _brokenDependencyText, value); }

    /// <summary>#753: ImagePath resolves to a binary that no longer exists on disk (following
    /// svchost -k/rundll32 through to their hosted Parameters\ServiceDll where applicable) - see
    /// ServiceControlService.RunInventoryAudit/DeleteAsync (the confirmed "sc delete" offered for
    /// this row once orphaned). False until an audit has been run.</summary>
    private bool _isOrphaned;
    public bool IsOrphaned { get => _isOrphaned; set => SetProperty(ref _isOrphaned, value); }

    private string _orphanedImagePath = string.Empty;
    public string OrphanedImagePath { get => _orphanedImagePath; set => SetProperty(ref _orphanedImagePath, value); }

    /// <summary>#754: ImagePath is unquoted and contains a space before its .exe boundary - the
    /// classic unquoted-service-path privilege-escalation pattern. UnquotedPathCorrected is the
    /// exact value to paste back into ImagePath to fix it. False until an audit has been run.</summary>
    private bool _hasUnquotedPath;
    public bool HasUnquotedPath { get => _hasUnquotedPath; set => SetProperty(ref _hasUnquotedPath, value); }

    private string _unquotedPathOriginal = string.Empty;
    public string UnquotedPathOriginal { get => _unquotedPathOriginal; set => SetProperty(ref _unquotedPathOriginal, value); }

    private string _unquotedPathCorrected = string.Empty;
    public string UnquotedPathCorrected { get => _unquotedPathCorrected; set => SetProperty(ref _unquotedPathCorrected, value); }

    /// <summary>#756: trigger-start conditions (`sc qtriggerinfo`), loaded on demand via
    /// ServicesViewModel.ViewTriggerInfoCommand - see ServiceControlService.ReadTriggerInfoTextAsync.
    /// Empty until requested, the same on-demand shape as FailureActionsText.</summary>
    private string _triggerInfoText = string.Empty;
    public string TriggerInfoText { get => _triggerInfoText; set => SetProperty(ref _triggerInfoText, value); }

    /// <summary>#758: set once, in ServiceControlService.Sample(), for a service on the small
    /// hard-coded core-plumbing denylist (see ServiceControlService.ProtectedCoreServiceNames) -
    /// this app declines to offer editable recovery-action configuration for these, the same
    /// "won't touch protected core services" line CanStop below already draws for
    /// Rpc/DCOM.</summary>
    public bool IsProtectedCore { get; init; }

    /// <summary>#763: set after ServicesViewModel.DiagnoseHangCommand runs for this row - true only
    /// when the service was found pending (START_PENDING/STOP_PENDING) with a checkpoint that isn't
    /// advancing. See ServiceControlService.DiagnoseHangAsync/HungServiceDiagnosis.</summary>
    private bool _isHung;
    public bool IsHung { get => _isHung; set => SetProperty(ref _isHung, value); }

    private string _hangDiagnosisText = string.Empty;
    public string HangDiagnosisText { get => _hangDiagnosisText; set => SetProperty(ref _hangDiagnosisText, value); }

    /// <summary>#192: set after "Scan for crash loops & timeouts" runs - true when
    /// ServiceHealthEventService.ReadServiceCrashLoops flagged this service as having crashed and
    /// restarted repeatedly (SCM 7031/7034/7009 recurrence). Drives the row badge; a quick flag, not
    /// a verdict. False/empty until the scan has run. Named distinctly from #750's read-only,
    /// computed IsCrashLooping above (a different, threshold-based detection already wired to its
    /// own UI) since both exist side by side.</summary>
    private bool _isCrashLoopingFlagged;
    public bool IsCrashLoopingFlagged { get => _isCrashLoopingFlagged; set => SetProperty(ref _isCrashLoopingFlagged, value); }

    private string _crashLoopSummaryText = string.Empty;
    public string CrashLoopSummaryText { get => _crashLoopSummaryText; set => SetProperty(ref _crashLoopSummaryText, value); }

    public bool CanStart => Status is ServiceControllerStatus.Stopped;
    public bool CanStop => Status is ServiceControllerStatus.Running && ServiceName is not ("RpcSs" or "RpcEptMapper" or "DcomLaunch");
}
