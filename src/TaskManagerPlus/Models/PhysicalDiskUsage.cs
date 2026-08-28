using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>
/// Round 18, #362: live per-physical-disk usage, mirrored onto PerformanceViewModel.PhysicalDisks
/// each tick from HardwareSnapshot.PhysicalDisks - same "merge in place rather than rebuild" shape
/// PerformanceViewModel.Cores already uses for per-core CPU usage, so a bound DataGrid/ItemsControl
/// doesn't flicker every tick.
/// </summary>
public sealed class PhysicalDiskUsage : ObservableObject
{
    public string InstanceName { get; init; } = string.Empty;

    private double _activePercent;
    public double ActivePercent { get => _activePercent; set => SetProperty(ref _activePercent, value); }

    private double _idlePercent;
    public double IdlePercent { get => _idlePercent; set => SetProperty(ref _idlePercent, value); }

    private bool _idleTimeAvailable;
    public bool IdleTimeAvailable { get => _idleTimeAvailable; set => SetProperty(ref _idleTimeAvailable, value); }

    private double _utilizationPercent;
    public double UtilizationPercent { get => _utilizationPercent; set => SetProperty(ref _utilizationPercent, value); }

    private double _readBytesPerSec;
    public double ReadBytesPerSec { get => _readBytesPerSec; set => SetProperty(ref _readBytesPerSec, value); }

    private double _writeBytesPerSec;
    public double WriteBytesPerSec { get => _writeBytesPerSec; set => SetProperty(ref _writeBytesPerSec, value); }

    private double _queueLength;
    public double QueueLength { get => _queueLength; set => SetProperty(ref _queueLength, value); }

    private double _readLatencyMs;
    public double ReadLatencyMs { get => _readLatencyMs; set => SetProperty(ref _readLatencyMs, value); }

    private double _writeLatencyMs;
    public double WriteLatencyMs { get => _writeLatencyMs; set => SetProperty(ref _writeLatencyMs, value); }

    private double _transferLatencyMs;
    public double TransferLatencyMs { get => _transferLatencyMs; set => SetProperty(ref _transferLatencyMs, value); }
}
