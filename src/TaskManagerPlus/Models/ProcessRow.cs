using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>
/// One row in the Processes grid. Implements INotifyPropertyChanged so the
/// monitor service can update values in place each tick instead of rebuilding
/// (and flickering / losing selection on) the whole collection.
/// </summary>
public sealed class ProcessRow : ObservableObject
{
    public int Pid { get; init; }

    private string _name = string.Empty;
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private double _cpuPercent;
    public double CpuPercent { get => _cpuPercent; set => SetProperty(ref _cpuPercent, value); }

    private long _memoryBytes;
    public long MemoryBytes
    {
        get => _memoryBytes;
        set
        {
            if (SetProperty(ref _memoryBytes, value))
                OnPropertyChanged(nameof(MemoryMb));
        }
    }

    private double _diskBytesPerSec;
    public double DiskBytesPerSec { get => _diskBytesPerSec; set => SetProperty(ref _diskBytesPerSec, value); }

    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private string _user = string.Empty;
    public string User { get => _user; set => SetProperty(ref _user, value); }

    private int _threadCount;
    public int ThreadCount { get => _threadCount; set => SetProperty(ref _threadCount, value); }

    private DateTime? _startTime;
    public DateTime? StartTime { get => _startTime; set => SetProperty(ref _startTime, value); }

    private string? _filePath;
    public string? FilePath { get => _filePath; set => SetProperty(ref _filePath, value); }

    public double MemoryMb => MemoryBytes / 1024.0 / 1024.0;
}
