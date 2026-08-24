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
    public ServiceStartMode StartType { get => _startType; set => SetProperty(ref _startType, value); }

    private string _description = string.Empty;
    public string Description { get => _description; set => SetProperty(ref _description, value); }

    private int _processId;
    public int ProcessId { get => _processId; set => SetProperty(ref _processId, value); }

    public bool CanStart => Status is ServiceControllerStatus.Stopped;
    public bool CanStop => Status is ServiceControllerStatus.Running && ServiceName is not ("RpcSs" or "RpcEptMapper" or "DcomLaunch");
}
