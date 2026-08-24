using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Reads system-wide performance data: CPU usage/clock speed (overall and per
/// core), RAM, disk activity and network throughput. Performance counters are
/// created once and reused, since counters that measure a rate need two
/// samples to produce a meaningful value.
/// </summary>
public sealed class HardwareMonitorService : IDisposable
{
    private readonly PerformanceCounter _cpuTotalCounter;
    private readonly PerformanceCounter[] _cpuCoreUsageCounters;
    private readonly PerformanceCounter _cpuTotalPerformanceCounter;
    private readonly PerformanceCounter _diskTimeCounter;
    private readonly PerformanceCounter _diskReadCounter;
    private readonly PerformanceCounter _diskWriteCounter;
    private readonly PerformanceCounter _handleCountCounter;
    private readonly PerformanceCounter _threadCountCounter;
    private readonly PerformanceCounter _processCountCounter;

    private readonly string _cpuName;
    private readonly double _cpuBaseClockGhz;
    private readonly int _logicalProcessors;
    private readonly int _physicalCores;

    // Network counters use System.Net.NetworkInformation instead of perf counters:
    // interface instance names in the "Network Interface" perf category are unstable
    // across driver updates, whereas NetworkInterface.GetIPv4Statistics is not.
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTime _lastNetSampleUtc;

    public HardwareMonitorService()
    {
        _logicalProcessors = Environment.ProcessorCount;

        (_cpuName, _cpuBaseClockGhz, _physicalCores) = ReadCpuInfoFromWmi();

        _cpuTotalCounter = new PerformanceCounter("Processor Information", "% Processor Time", "_Total", readOnly: true);
        _cpuTotalPerformanceCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", readOnly: true);

        var coreInstances = new PerformanceCounterCategory("Processor Information")
            .GetInstanceNames()
            .Where(n => n != "_Total")
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _cpuCoreUsageCounters = coreInstances
            .Select(name => new PerformanceCounter("Processor Information", "% Processor Time", name, readOnly: true))
            .ToArray();

        _diskTimeCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", readOnly: true);
        _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", readOnly: true);
        _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", readOnly: true);

        _handleCountCounter = new PerformanceCounter("Process", "Handle Count", "_Total", readOnly: true);
        _threadCountCounter = new PerformanceCounter("Process", "Thread Count", "_Total", readOnly: true);
        _processCountCounter = new PerformanceCounter("System", "Processes", readOnly: true);

        // Rate counters return 0 on their first read; prime them now so the
        // very first UI sample isn't a meaningless zero.
        _ = _cpuTotalCounter.NextValue();
        _ = _cpuTotalPerformanceCounter.NextValue();
        foreach (var c in _cpuCoreUsageCounters) _ = c.NextValue();
        _ = _diskTimeCounter.NextValue();
        _ = _diskReadCounter.NextValue();
        _ = _diskWriteCounter.NextValue();

        (_lastBytesReceived, _lastBytesSent) = ReadTotalNetworkBytes();
        _lastNetSampleUtc = DateTime.UtcNow;
    }

    public HardwareSnapshot Sample()
    {
        var now = DateTime.UtcNow;

        double cpuTotal = Clamp(_cpuTotalCounter.NextValue());
        double perf = _cpuTotalPerformanceCounter.NextValue();
        double currentClockGhz = Math.Round(_cpuBaseClockGhz * (perf / 100.0), 2);

        var perCore = new double[_cpuCoreUsageCounters.Length];
        for (int i = 0; i < _cpuCoreUsageCounters.Length; i++)
            perCore[i] = Clamp(_cpuCoreUsageCounters[i].NextValue());

        double diskPercent = Clamp(_diskTimeCounter.NextValue());
        double diskRead = _diskReadCounter.NextValue();
        double diskWrite = _diskWriteCounter.NextValue();

        var (bytesReceived, bytesSent) = ReadTotalNetworkBytes();
        var elapsedSec = Math.Max(0.001, (now - _lastNetSampleUtc).TotalSeconds);
        double netRecvRate = Math.Max(0, (bytesReceived - _lastBytesReceived) / elapsedSec);
        double netSendRate = Math.Max(0, (bytesSent - _lastBytesSent) / elapsedSec);
        _lastBytesReceived = bytesReceived;
        _lastBytesSent = bytesSent;
        _lastNetSampleUtc = now;

        GetMemoryStatus(out long totalBytes, out long availBytes);

        return new HardwareSnapshot
        {
            CpuTotalPercent = Math.Round(cpuTotal, 1),
            CpuPerCorePercent = perCore.Select(v => Math.Round(v, 1)).ToArray(),
            CpuCurrentClockGhz = currentClockGhz <= 0 ? _cpuBaseClockGhz : currentClockGhz,
            CpuBaseClockGhz = _cpuBaseClockGhz,
            CpuMaxClockGhz = _cpuBaseClockGhz,
            CpuName = _cpuName,
            LogicalProcessors = _logicalProcessors,
            PhysicalCores = _physicalCores,

            RamTotalBytes = totalBytes,
            RamUsedBytes = totalBytes - availBytes,

            DiskActivePercent = Math.Round(diskPercent, 1),
            DiskReadBytesPerSec = diskRead,
            DiskWriteBytesPerSec = diskWrite,

            NetworkReceiveBytesPerSec = netRecvRate,
            NetworkSendBytesPerSec = netSendRate,

            ProcessCount = (int)_processCountCounter.NextValue(),
            ThreadCount = (int)_threadCountCounter.NextValue(),
            HandleCount = (int)_handleCountCounter.NextValue(),
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        };
    }

    private static double Clamp(double v) => Math.Max(0, Math.Min(100, v));

    private static (long Received, long Sent) ReadTotalNetworkBytes()
    {
        long received = 0, sent = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var stats = ni.GetIPStatistics();
            received += stats.BytesReceived;
            sent += stats.BytesSent;
        }
        return (received, sent);
    }

    private static (string Name, double BaseClockGhz, int PhysicalCores) ReadCpuInfoFromWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, MaxClockSpeed, NumberOfCores FROM Win32_Processor");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = (mo["Name"] as string ?? "Unknown CPU").Trim();
                double maxClockMhz = Convert.ToDouble(mo["MaxClockSpeed"] ?? 0.0);
                int cores = Convert.ToInt32(mo["NumberOfCores"] ?? Environment.ProcessorCount);
                return (name, Math.Round(maxClockMhz / 1000.0, 2), cores);
            }
        }
        catch
        {
            // fall through to defaults
        }
        return ("Unknown CPU", 0, Environment.ProcessorCount);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    private static void GetMemoryStatus(out long totalBytes, out long availBytes)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref status))
        {
            totalBytes = (long)status.ullTotalPhys;
            availBytes = (long)status.ullAvailPhys;
        }
        else
        {
            totalBytes = 0;
            availBytes = 0;
        }
    }

    public void Dispose()
    {
        _cpuTotalCounter.Dispose();
        _cpuTotalPerformanceCounter.Dispose();
        foreach (var c in _cpuCoreUsageCounters) c.Dispose();
        _diskTimeCounter.Dispose();
        _diskReadCounter.Dispose();
        _diskWriteCounter.Dispose();
        _handleCountCounter.Dispose();
        _threadCountCounter.Dispose();
        _processCountCounter.Dispose();
    }
}
