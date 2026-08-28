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
        set { if (SetProperty(ref _startType, value)) OnPropertyChanged(nameof(HasFailedToStart)); }
    }

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
    /// legitimately stopped most of the time and would otherwise dominate this list).</summary>
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

    /// <summary>#192: set after "Scan for crash loops & timeouts" runs - true when
    /// ServiceHealthEventService.ReadServiceCrashLoops flagged this service as having crashed and
    /// restarted repeatedly (SCM 7031/7034/7009 recurrence). Drives the row badge; a quick flag, not
    /// a verdict. False/empty until the scan has run.</summary>
    private bool _isCrashLooping;
    public bool IsCrashLooping { get => _isCrashLooping; set => SetProperty(ref _isCrashLooping, value); }

    private string _crashLoopSummaryText = string.Empty;
    public string CrashLoopSummaryText { get => _crashLoopSummaryText; set => SetProperty(ref _crashLoopSummaryText, value); }

    public bool CanStart => Status is ServiceControllerStatus.Stopped;
    public bool CanStop => Status is ServiceControllerStatus.Running && ServiceName is not ("RpcSs" or "RpcEptMapper" or "DcomLaunch");
}
