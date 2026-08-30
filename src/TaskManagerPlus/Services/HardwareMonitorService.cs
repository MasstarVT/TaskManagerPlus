using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Reads system-wide performance data: CPU usage/clock speed (overall and per
/// core), RAM, disk activity and network throughput. Each performance counter
/// CATEGORY is read exactly once per tick via CategorySampler (see its remarks -
/// this class used to hold ~130 PerformanceCounter objects whose per-tick
/// NextValue() calls each re-read their whole category, costing ~70ms of every
/// one-second tick); individual values come out of those per-tick snapshots.
/// </summary>
public sealed class HardwareMonitorService : IDisposable
{
    // One sampler per category this class reads. "Processor Information" carries total + per-core
    // usage, requested/delivered clock, parking and C-states; the classic "Processor" category is
    // still used for interrupt/DPC time since that's the one guaranteed to exist with a plain
    // "_Total" instance on every SKU. "Process"/"System" cover the _Total handle/thread/process
    // counts, "LogicalDisk" only the page-file volume's queue length (#432).
    private readonly CategorySampler _processorInfo = new("Processor Information");
    private readonly CategorySampler _processor = new("Processor");
    private readonly CategorySampler _physicalDisk = new("PhysicalDisk");
    private readonly CategorySampler _memory = new("Memory");
    private readonly CategorySampler _system = new("System");
    private readonly CategorySampler _process = new("Process");
    private readonly CategorySampler _pagingFile = new("Paging File");
    private readonly CategorySampler _tcp = new("TCPv4");
    private readonly CategorySampler _logicalDisk = new("LogicalDisk");

    // Instance-name orderings, fixed at construction: consumers index CpuPerCorePercent by the
    // OS's logical-processor number (see the ctor's sorting remarks), and #362's per-disk list
    // stays in disk-index order.
    private readonly string[] _coreInstanceNames;
    private readonly string[] _diskInstanceNames;

    // Availability flags, decided once at construction from what the categories actually expose -
    // the replacement for the old "TryCreateCounter returned null" checks. A missing counter
    // (e.g. "Parking Status" on older Windows, C-state tiers on some platforms) degrades exactly
    // as before: empty per-core arrays, 0 values, or the documented fallback.
    private readonly bool _parkingAvailable;
    private readonly bool _perCorePerformanceAvailable;
    private readonly bool _perCoreMaxFreqAvailable;
    private readonly bool _cStatesAvailable;
    private readonly string? _pageFileVolumeInstance;

    // #423: installed physical RAM (Win32_PhysicalMemory, summed) vs. GlobalMemoryStatusEx's own
    // total - the gap is platform/firmware-reserved memory Windows never sees. Read once via WMI,
    // same "WMI for the rarely-changing total" tradeoff _pageFileTotalMb already makes.
    private readonly long _installedRamBytes;

    private readonly string _cpuName;
    private readonly double _cpuBaseClockGhz;
    private readonly int _logicalProcessors;
    private readonly int _physicalCores;

    // Total page file size (MB), read once via WMI - it only changes if the page file is
    // resized (rare, needs a reboot), unlike the counter above which is genuinely live.
    private readonly long _pageFileTotalMb;

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
        _pageFileTotalMb = ReadPageFileTotalMb();
        _installedRamBytes = ReadInstalledRamBytes();

        // Prime every sampler once: an unprimed rate's first-ever read returns 0 (see
        // CategorySampler's remarks), so this plays the role the old per-counter priming
        // NextValue() loop did - the very first UI Sample() gets real values. (Round 18, #1029:
        // CategorySampler.Tick() now folds this priming snapshot into its previous-sample table,
        // which is what actually makes that true - it used to be silently discarded.)
        _processorInfo.Tick();
        _processor.Tick();
        _physicalDisk.Tick();
        _memory.Tick();
        _system.Tick();
        _process.Tick();
        _pagingFile.Tick();
        _tcp.Tick();

        // Instance names in "Processor Information" are "<NUMA node>,<core>" (e.g. "0,0", "0,1",
        // ..., "0,_Total" for a per-node aggregate). Two bugs to avoid here: (1) filtering only
        // the exact string "_Total" misses per-node aggregates like "0,_Total", which would
        // otherwise be counted as a bogus extra "core"; (2) sorting the instance names as strings
        // puts "0,10" before "0,2" (lexicographic, not numeric). Both matter beyond cosmetics:
        // CPU topology (CpuTopologyService) indexes cores by the OS's actual logical-processor
        // number, so CpuPerCorePercent[i] needs to line up 1:1 with that same index - sort
        // numerically by (node, core) instead.
        _coreInstanceNames = _processorInfo.InstanceNames("% Processor Time")
            .Where(n => n != "_Total" && !n.EndsWith(",_Total", StringComparison.OrdinalIgnoreCase))
            .Select(n => (Name: n, Key: ParseNumaCoreKey(n)))
            .OrderBy(x => x.Key.NumaNode)
            .ThenBy(x => x.Key.Core)
            .Select(x => x.Name)
            .ToArray();

        // #362: instance names in "PhysicalDisk" look like "0 C:" (disk index, then every drive
        // letter that disk hosts) or just "0" when no drive letter is assigned - sort numerically
        // on the leading index, same reasoning as ParseNumaCoreKey above.
        _diskInstanceNames = _physicalDisk.InstanceNames("% Disk Time")
            .Where(n => n != "_Total")
            .OrderBy(ParsePhysicalDiskIndex)
            .ToArray();

        // #78/#630/#83: per-core parking, requested/delivered clock, and the C-state tiers are
        // not exposed on every Windows/CPU generation - probe once and degrade exactly as the old
        // TryCreateCounter checks did (empty arrays / 0 values).
        _parkingAvailable = _processorInfo.HasCounter("Parking Status");
        _perCorePerformanceAvailable = _processorInfo.HasCounter("% Processor Performance");
        _perCoreMaxFreqAvailable = _processorInfo.HasCounter("% of Maximum Frequency");
        _cStatesAvailable = _processorInfo.HasCounter("% Idle Time");

        // #432: the page file's own drive letter, best-effort - only used to target the
        // "LogicalDisk\Avg. Disk Queue Length" instance for that specific volume rather than the
        // system-wide "PhysicalDisk" total, which can span drives the page file isn't even on.
        string? pageFileDrive = ReadPageFileDriveLetter();
        _pageFileVolumeInstance = pageFileDrive is null ? null : pageFileDrive + ":";
        if (_pageFileVolumeInstance is not null) _logicalDisk.Tick();

        (_lastBytesReceived, _lastBytesSent) = ReadTotalNetworkBytes();
        _lastNetSampleUtc = DateTime.UtcNow;
    }

    public HardwareSnapshot Sample()
    {
        var now = DateTime.UtcNow;

        // One category read each per tick - every Value() below comes out of these snapshots.
        _processorInfo.Tick();
        _processor.Tick();
        _physicalDisk.Tick();
        _memory.Tick();
        _system.Tick();
        _process.Tick();
        _pagingFile.Tick();
        _tcp.Tick();
        if (_pageFileVolumeInstance is not null) _logicalDisk.Tick();

        double cpuTotal = Clamp(_processorInfo.Value("% Processor Time", "_Total"));
        double perf = _processorInfo.Value("% Processor Performance", "_Total");
        double currentClockGhz = Math.Round(_cpuBaseClockGhz * (perf / 100.0), 2);

        var perCore = new double[_coreInstanceNames.Length];
        for (int i = 0; i < _coreInstanceNames.Length; i++)
            perCore[i] = Clamp(_processorInfo.Value("% Processor Time", _coreInstanceNames[i]));

        // #78: "Parking Status" reports a small integer (0 = unparked); any nonzero value means
        // the scheduler has parked that logical core to save power - a common, otherwise invisible
        // reason "only half my CPU seems to be doing anything" under light load.
        var coreParked = new bool[_parkingAvailable ? _coreInstanceNames.Length : 0];
        for (int i = 0; i < coreParked.Length; i++)
            coreParked[i] = _processorInfo.Value("Parking Status", _coreInstanceNames[i]) != 0;

        // #630: per-core requested (% Processor Performance) vs. delivered (% of Maximum
        // Frequency) clock, as percents of rated max - see the availability fields' remarks.
        var perCoreRequested = new double[_perCorePerformanceAvailable ? _coreInstanceNames.Length : 0];
        for (int i = 0; i < perCoreRequested.Length; i++)
            perCoreRequested[i] = Math.Max(0, _processorInfo.Value("% Processor Performance", _coreInstanceNames[i]));
        var perCoreDelivered = new double[_perCoreMaxFreqAvailable ? _coreInstanceNames.Length : 0];
        for (int i = 0; i < perCoreDelivered.Length; i++)
            perCoreDelivered[i] = Math.Max(0, _processorInfo.Value("% of Maximum Frequency", _coreInstanceNames[i]));

        double diskPercent = Clamp(_physicalDisk.Value("% Disk Time", "_Total"));
        double diskRead = _physicalDisk.Value("Disk Read Bytes/sec", "_Total");
        double diskWrite = _physicalDisk.Value("Disk Write Bytes/sec", "_Total");
        double diskQueueLength = Math.Max(0, _physicalDisk.Value("Avg. Disk Queue Length", "_Total"));
        // Avg. Disk sec/Read|Write reports in seconds; ms is the unit anyone diagnosing
        // "is my disk slow" actually thinks in.
        double diskReadLatencyMs = Math.Max(0, _physicalDisk.Value("Avg. Disk sec/Read", "_Total") * 1000.0);
        double diskWriteLatencyMs = Math.Max(0, _physicalDisk.Value("Avg. Disk sec/Write", "_Total") * 1000.0);

        // #365: corrected utilization - 100 minus idle time, rather than the saturating "% Disk
        // Time" above. Falls back to diskPercent (not a fabricated 0/100) when the counter isn't
        // available on this system, same "degrade to the best signal actually present" approach
        // the C-state tiers below use.
        bool diskIdleTimeAvailable = _physicalDisk.HasCounter("% Idle Time");
        double diskIdlePercent = diskIdleTimeAvailable ? Clamp(_physicalDisk.Value("% Idle Time", "_Total")) : 0;
        double diskUtilizationPercent = diskIdleTimeAvailable ? Math.Clamp(100.0 - diskIdlePercent, 0, 100) : diskPercent;

        // #366
        double diskReadTimePercent = Clamp(_physicalDisk.Value("% Disk Read Time", "_Total"));
        double diskWriteTimePercent = Clamp(_physicalDisk.Value("% Disk Write Time", "_Total"));
        double diskTransfersPerSec = Math.Max(0, _physicalDisk.Value("Disk Transfers/sec", "_Total"));

        // #362: per-instance sweep, alongside (not instead of) the _Total figures above.
        var physicalDisks = new PhysicalDiskSample[_diskInstanceNames.Length];
        for (int i = 0; i < _diskInstanceNames.Length; i++)
            physicalDisks[i] = SamplePhysicalDisk(_diskInstanceNames[i], diskIdleTimeAvailable);

        var (bytesReceived, bytesSent) = ReadTotalNetworkBytes();
        var (netInErrors, netInDiscards, netOutErrors, netOutDiscards) = ReadNetworkErrorCounters();
        var elapsedSec = Math.Max(0.001, (now - _lastNetSampleUtc).TotalSeconds);
        double netRecvRate = Math.Max(0, (bytesReceived - _lastBytesReceived) / elapsedSec);
        double netSendRate = Math.Max(0, (bytesSent - _lastBytesSent) / elapsedSec);
        _lastBytesReceived = bytesReceived;
        _lastBytesSent = bytesSent;
        _lastNetSampleUtc = now;

        GetMemoryStatus(out long totalBytes, out long availBytes);

        double totalFaultsPerSec = Math.Max(0, _memory.Value("Page Faults/sec"));
        double hardFaultsPerSec = Math.Max(0, _memory.Value("Pages/sec"));
        long standbyCoreBytes = (long)_memory.Value("Standby Cache Core Bytes");
        long standbyNormalBytes = (long)_memory.Value("Standby Cache Normal Priority Bytes");
        long standbyReserveBytes = (long)_memory.Value("Standby Cache Reserve Bytes");
        long standbyBytes = standbyCoreBytes + standbyNormalBytes + standbyReserveBytes;

        return new HardwareSnapshot
        {
            CpuTotalPercent = Math.Round(cpuTotal, 1),
            CpuPerCorePercent = perCore.Select(v => Math.Round(v, 1)).ToArray(),
            CoreParkedFlags = coreParked,
            CpuPerCoreRequestedPercent = perCoreRequested.Select(v => Math.Round(v, 1)).ToArray(),
            CpuPerCoreDeliveredPercent = perCoreDelivered.Select(v => Math.Round(v, 1)).ToArray(),
            CpuCurrentClockGhz = currentClockGhz <= 0 ? _cpuBaseClockGhz : currentClockGhz,
            CpuBaseClockGhz = _cpuBaseClockGhz,
            CpuMaxClockGhz = _cpuBaseClockGhz,
            CpuName = _cpuName,
            LogicalProcessors = _logicalProcessors,
            PhysicalCores = _physicalCores,
            CpuInterruptPercent = Math.Round(Clamp(_processor.Value("% Interrupt Time", "_Total")), 2),
            CpuDpcPercent = Math.Round(Clamp(_processor.Value("% DPC Time", "_Total")), 2),
            ContextSwitchesPerSec = Math.Round(Math.Max(0, _system.Value("Context Switches/sec")), 0),
            CpuQueueLength = Math.Round(Math.Max(0, _system.Value("Processor Queue Length")), 0),

            CStatesAvailable = _cStatesAvailable,
            CpuIdlePercent = !_cStatesAvailable ? 0 : Math.Round(Clamp(_processorInfo.Value("% Idle Time", "_Total")), 1),
            CpuC1Percent = Math.Round(Clamp(_processorInfo.Value("% C1 Time", "_Total")), 1),
            CpuC2Percent = Math.Round(Clamp(_processorInfo.Value("% C2 Time", "_Total")), 1),
            CpuC3Percent = Math.Round(Clamp(_processorInfo.Value("% C3 Time", "_Total")), 1),

            RamTotalBytes = totalBytes,
            RamUsedBytes = totalBytes - availBytes,
            RamAvailableBytes = availBytes,
            CommittedBytes = (long)_memory.Value("Committed Bytes"),
            CommitLimitBytes = (long)_memory.Value("Commit Limit"),
            CacheBytes = (long)_memory.Value("Cache Bytes"),
            PageFileTotalBytes = _pageFileTotalMb * 1024L * 1024L,
            PageFileUsedBytes = (long)(_pageFileTotalMb * 1024L * 1024L * (Clamp(_pagingFile.Value("% Usage", "_Total")) / 100.0)),
            PageFaultsPerSec = Math.Round(totalFaultsPerSec, 0),
            HardFaultsPerSec = Math.Round(hardFaultsPerSec, 0),
            StandbyListBytes = Math.Max(0, standbyBytes),
            StandbyCoreBytes = Math.Max(0, standbyCoreBytes),
            StandbyNormalBytes = Math.Max(0, standbyNormalBytes),
            StandbyReserveBytes = Math.Max(0, standbyReserveBytes),
            PagesInputPerSec = Math.Round(Math.Max(0, _memory.Value("Pages Input/sec")), 0),
            PagesOutputPerSec = Math.Round(Math.Max(0, _memory.Value("Pages Output/sec")), 0),
            PageFileVolumeQueueLength = _pageFileVolumeInstance is null ? null : Math.Round(Math.Max(0, _logicalDisk.Value("Avg. Disk Queue Length", _pageFileVolumeInstance)), 2),
            SystemCacheResidentBytes = (long)Math.Max(0, _memory.Value("System Cache Resident Bytes")),
            PoolNonpagedBytes = (long)_memory.Value("Pool Nonpaged Bytes"),
            PoolPagedBytes = (long)_memory.Value("Pool Paged Bytes"),
            PoolNonpagedAllocs = (long)Math.Max(0, _memory.Value("Pool Nonpaged Allocs")),
            SystemDriverResidentBytes = (long)Math.Max(0, _memory.Value("System Driver Resident Bytes")),
            SystemDriverTotalBytes = (long)Math.Max(0, _memory.Value("System Driver Total Bytes")),
            SystemCodeResidentBytes = (long)Math.Max(0, _memory.Value("System Code Resident Bytes")),
            ModifiedListBytes = (long)Math.Max(0, _memory.Value("Modified Page List Bytes")),
            HardwareReservedBytes = Math.Max(0, _installedRamBytes - totalBytes),

            DiskActivePercent = Math.Round(diskPercent, 1),
            DiskReadBytesPerSec = diskRead,
            DiskWriteBytesPerSec = diskWrite,
            DiskQueueLength = Math.Round(diskQueueLength, 2),
            DiskReadLatencyMs = Math.Round(diskReadLatencyMs, 1),
            DiskWriteLatencyMs = Math.Round(diskWriteLatencyMs, 1),

            DiskIdleTimeAvailable = diskIdleTimeAvailable,
            DiskIdlePercent = Math.Round(diskIdlePercent, 1),
            DiskUtilizationPercent = Math.Round(diskUtilizationPercent, 1),
            DiskReadTimePercent = Math.Round(diskReadTimePercent, 1),
            DiskWriteTimePercent = Math.Round(diskWriteTimePercent, 1),
            DiskTransfersPerSec = Math.Round(diskTransfersPerSec, 1),
            PhysicalDisks = physicalDisks,

            NetworkReceiveBytesPerSec = netRecvRate,
            NetworkSendBytesPerSec = netSendRate,
            NetworkInErrors = netInErrors,
            NetworkInDiscards = netInDiscards,
            NetworkOutErrors = netOutErrors,
            NetworkOutDiscards = netOutDiscards,
            TcpRetransmitsPerSec = Math.Round(Math.Max(0, _tcp.Value("Segments Retransmitted/sec")), 1),

            ProcessCount = (int)_system.Value("Processes"),
            ThreadCount = (int)_process.Value("Thread Count", "_Total"),
            HandleCount = (int)_process.Value("Handle Count", "_Total"),
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        };
    }

    /// <summary>#362: one PhysicalDisk instance's figures, mirroring the "_Total" fields above but
    /// per physical drive - so one slow drive isn't averaged away by others when queue length/
    /// latency is read only from "_Total". Reads out of the same per-tick _physicalDisk snapshot
    /// the _Total figures use (the old per-instance PerformanceCounter sets are exactly what this
    /// class's rewrite removed - see the class remarks).</summary>
    private PhysicalDiskSample SamplePhysicalDisk(string instanceName, bool idleAvailable)
    {
        double active = Clamp(_physicalDisk.Value("% Disk Time", instanceName));
        double idle = idleAvailable ? Clamp(_physicalDisk.Value("% Idle Time", instanceName)) : 0;
        double utilization = idleAvailable ? Math.Clamp(100.0 - idle, 0, 100) : active;

        return new PhysicalDiskSample
        {
            InstanceName = instanceName,
            ActivePercent = Math.Round(active, 1),
            IdleTimeAvailable = idleAvailable,
            IdlePercent = Math.Round(idle, 1),
            UtilizationPercent = Math.Round(utilization, 1),
            ReadBytesPerSec = Math.Max(0, _physicalDisk.Value("Disk Read Bytes/sec", instanceName)),
            WriteBytesPerSec = Math.Max(0, _physicalDisk.Value("Disk Write Bytes/sec", instanceName)),
            QueueLength = Math.Round(Math.Max(0, _physicalDisk.Value("Avg. Disk Queue Length", instanceName)), 2),
            ReadLatencyMs = Math.Round(Math.Max(0, _physicalDisk.Value("Avg. Disk sec/Read", instanceName) * 1000.0), 2),
            WriteLatencyMs = Math.Round(Math.Max(0, _physicalDisk.Value("Avg. Disk sec/Write", instanceName) * 1000.0), 2),
            TransferLatencyMs = Math.Round(Math.Max(0, _physicalDisk.Value("Avg. Disk sec/Transfer", instanceName) * 1000.0), 2),
        };
    }

    private static double Clamp(double v) => Math.Max(0, Math.Min(100, v));

    /// <summary>#423: sums Win32_PhysicalMemory.Capacity across every installed memory module -
    /// the platform's own record of how much RAM is physically installed, independent of how much
    /// GlobalMemoryStatusEx says the OS can actually see.</summary>
    private static long ReadInstalledRamBytes()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory");
            long total = 0;
            foreach (ManagementObject mo in searcher.Get())
                total += Convert.ToInt64(mo["Capacity"] ?? 0L);
            return total;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Parses a "Processor Information" instance name ("&lt;node&gt;,&lt;core&gt;") into
    /// a sortable (node, core) key, defaulting to (0, 0) for any unexpected format.</summary>
    private static (int NumaNode, int Core) ParseNumaCoreKey(string instanceName)
    {
        var parts = instanceName.Split(',');
        if (parts.Length == 2 && int.TryParse(parts[0], out int node) && int.TryParse(parts[1], out int core))
            return (node, core);
        return (0, 0);
    }

    /// <summary>#362: "PhysicalDisk" instance names look like "0 C:" or "1 D: E:" (disk index, then
    /// every drive letter that disk hosts) - parses the leading integer for a numeric sort, same
    /// reasoning ParseNumaCoreKey above documents for the per-core CPU counters. Unrecognized
    /// formats sort last rather than throwing.</summary>
    private static int ParsePhysicalDiskIndex(string instanceName)
    {
        var head = instanceName.Split(' ')[0];
        return int.TryParse(head, out int index) ? index : int.MaxValue;
    }

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

    /// <summary>
    /// Sums CRC/framing errors and dropped packets across every active adapter, since NIC/driver
    /// boot (these are cumulative counters, not rates - a nonzero total after any meaningful
    /// uptime already flags a problem, so there's no need to compute a per-second delta the way
    /// throughput does). A failing NIC or a bad cable shows up here well before it's obvious from
    /// throughput graphs alone.
    /// </summary>
    private static (long InErrors, long InDiscards, long OutErrors, long OutDiscards) ReadNetworkErrorCounters()
    {
        long inErrors = 0, inDiscards = 0, outErrors = 0, outDiscards = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var stats = ni.GetIPStatistics();
            inErrors += stats.IncomingPacketsWithErrors;
            inDiscards += stats.IncomingPacketsDiscarded;
            outErrors += stats.OutgoingPacketsWithErrors;
            outDiscards += stats.OutgoingPacketsDiscarded;
        }
        return (inErrors, inDiscards, outErrors, outDiscards);
    }

    /// <summary>Sums Win32_PageFileUsage.AllocatedBaseSize (MB) across every page file - almost
    /// always just one (C:\pagefile.sys), but a system can have more than one configured across
    /// multiple drives.</summary>
    private static long ReadPageFileTotalMb()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT AllocatedBaseSize FROM Win32_PageFileUsage");
            long total = 0;
            foreach (ManagementObject mo in searcher.Get())
                total += Convert.ToInt64(mo["AllocatedBaseSize"] ?? 0L);
            return total;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>#432: drive letter (no colon) hosting the first configured page file, or null if
    /// none is configured - a small, separate WMI read from ReadPageFileTotalMb above since that
    /// one only needs the size, not the path.</summary>
    private static string? ReadPageFileDriveLetter()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PageFileUsage");
            foreach (ManagementObject mo in searcher.Get())
            {
                string path = (mo["Name"] as string ?? string.Empty).Trim();
                if (path.Length >= 2 && path[1] == ':') return path[0].ToString();
            }
        }
        catch
        {
            // fall through
        }
        return null;
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

    /// <summary>Nothing to release since the CategorySampler rewrite - kept because
    /// PerformanceViewModel still disposes this service on shutdown.</summary>
    public void Dispose() { }
}
