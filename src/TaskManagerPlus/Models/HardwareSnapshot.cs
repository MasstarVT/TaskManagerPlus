namespace TaskManagerPlus.Models;

/// <summary>Point-in-time reading of all the system metrics the Performance tab shows.</summary>
public sealed class HardwareSnapshot
{
    public double CpuTotalPercent { get; init; }
    public double[] CpuPerCorePercent { get; init; } = Array.Empty<double>();
    public double CpuCurrentClockGhz { get; init; }
    public double CpuBaseClockGhz { get; init; }
    public double CpuMaxClockGhz { get; init; }
    public string CpuName { get; init; } = string.Empty;
    public int LogicalProcessors { get; init; }
    public int PhysicalCores { get; init; }

    public long RamUsedBytes { get; init; }
    public long RamTotalBytes { get; init; }
    public double RamPercent => RamTotalBytes == 0 ? 0 : (double)RamUsedBytes / RamTotalBytes * 100.0;

    /// <summary>Physical memory free (GlobalMemoryStatusEx.ullAvailPhys).</summary>
    public long RamAvailableBytes { get; init; }

    /// <summary>Total virtual memory currently committed (Memory\Committed Bytes) - Windows'
    /// own overall memory-pressure figure, closest analogue to Task Manager's "Committed".</summary>
    public long CommittedBytes { get; init; }

    /// <summary>Current commit limit - physical RAM plus page file size (Memory\Commit Limit).</summary>
    public long CommitLimitBytes { get; init; }

    /// <summary>System file cache / standby memory (Memory\Cache Bytes).</summary>
    public long CacheBytes { get; init; }

    public double DiskActivePercent { get; init; }
    public double DiskReadBytesPerSec { get; init; }
    public double DiskWriteBytesPerSec { get; init; }

    /// <summary>Avg. Disk Queue Length (_Total) - requests waiting, not just active; a classic
    /// "is the disk actually the bottleneck" signal beyond raw throughput.</summary>
    public double DiskQueueLength { get; init; }

    /// <summary>Per-I/O read/write latency in milliseconds (Avg. Disk sec/Read|Write * 1000) -
    /// latency spikes flag a failing/overloaded drive better than throughput alone.</summary>
    public double DiskReadLatencyMs { get; init; }
    public double DiskWriteLatencyMs { get; init; }

    public double NetworkReceiveBytesPerSec { get; init; }
    public double NetworkSendBytesPerSec { get; init; }

    public int ProcessCount { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public TimeSpan Uptime { get; init; }
}
