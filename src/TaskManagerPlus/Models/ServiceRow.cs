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

    public bool CanStart => Status is ServiceControllerStatus.Stopped;
    public bool CanStop => Status is ServiceControllerStatus.Running && ServiceName is not ("RpcSs" or "RpcEptMapper" or "DcomLaunch");
}
