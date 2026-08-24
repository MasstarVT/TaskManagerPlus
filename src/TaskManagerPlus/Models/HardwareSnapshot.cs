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

    public double DiskActivePercent { get; init; }
    public double DiskReadBytesPerSec { get; init; }
    public double DiskWriteBytesPerSec { get; init; }

    public double NetworkReceiveBytesPerSec { get; init; }
    public double NetworkSendBytesPerSec { get; init; }

    public int ProcessCount { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public TimeSpan Uptime { get; init; }
}
