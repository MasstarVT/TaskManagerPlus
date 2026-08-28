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

    // #78: per-core parking status. Same "Processor Information" instances as the usage counters
    // above, just a different counter name - kept as a separate array (rather than folded into
    // one struct) since it's allowed to fail independently: older Windows versions don't expose
    // "Parking Status" at all, in which case this stays empty and every core reports "unparked".
    private readonly PerformanceCounter[] _cpuParkingCounters;
    private readonly PerformanceCounter _cpuTotalPerformanceCounter;
    private readonly PerformanceCounter _diskTimeCounter;
    private readonly PerformanceCounter _diskReadCounter;
    private readonly PerformanceCounter _diskWriteCounter;
    private readonly PerformanceCounter _diskQueueLengthCounter;
    private readonly PerformanceCounter _diskReadLatencyCounter;
    private readonly PerformanceCounter _diskWriteLatencyCounter;

    // Round 18, #365/#366: "% Disk Time" is a busy-time counter that saturates and can read above
    // 100% under a deep queue - "% Idle Time" doesn't, so 100 - idle is what Task Manager's own
    // "Active time" actually shows. Read/write time split and Disk Transfers/sec (for deriving
    // average bytes/transfer) are the same idea as the existing _Total counters above, just two/
    // three more instantaneous gauges - wrapped as TryCreateCounter (not required) since, like the
    // C-state tiers below, not every counter is guaranteed present on every Windows/driver combo.
    private readonly PerformanceCounter? _diskIdleTimeCounter;
    private readonly PerformanceCounter? _diskReadTimePercentCounter;
    private readonly PerformanceCounter? _diskWriteTimePercentCounter;
    private readonly PerformanceCounter? _diskTransfersCounter;

    // Round 18, #362: one counter set per PhysicalDisk instance (not just "_Total" above), built
    // via the exact same GetInstanceNames()-filtered-to-real-instances pattern the per-core CPU
    // counters below use - so a single slow drive isn't averaged away by others when queue length/
    // latency is read only from "_Total". See the nested PhysicalDiskCounterSet class below.
    private readonly PhysicalDiskCounterSet[] _perDiskCounters;

    private readonly PerformanceCounter _handleCountCounter;
    private readonly PerformanceCounter _threadCountCounter;
    private readonly PerformanceCounter _processCountCounter;
    private readonly PerformanceCounter _committedBytesCounter;
    private readonly PerformanceCounter _commitLimitCounter;
    private readonly PerformanceCounter _cacheBytesCounter;
    private readonly PerformanceCounter _pageFileUsageCounter;

    // CPU diagnostics (#13/#14/#15): interrupt/DPC time use the classic "Processor" category
    // (not "Processor Information") since that's the one guaranteed to exist with a plain
    // "_Total" instance on every SKU; context switches and queue length have no instance at all.
    private readonly PerformanceCounter _cpuInterruptCounter;
    private readonly PerformanceCounter _cpuDpcCounter;
    private readonly PerformanceCounter _contextSwitchesCounter;
    private readonly PerformanceCounter _cpuQueueLengthCounter;

    // Memory diagnostics (#20/#22/#24): page fault rates, the standby (reclaimable) list, and
    // kernel pool usage - all instantaneous/rate PerfCounters like the ones above, no new
    // dependency. Standby list is split across three priority-tier counters that get summed.
    private readonly PerformanceCounter _pageFaultsCounter;
    private readonly PerformanceCounter _hardFaultsCounter;
    private readonly PerformanceCounter _standbyCoreCounter;
    private readonly PerformanceCounter _standbyNormalCounter;
    private readonly PerformanceCounter _standbyReserveCounter;
    private readonly PerformanceCounter _poolNonpagedCounter;
    private readonly PerformanceCounter _poolPagedCounter;

    // Network diagnostic (#32): TCP retransmit rate. Wrapped separately since the "TCPv4"
    // category can legitimately be absent on an unusual network stack config - null means
    // "not available", and Sample() just reports 0 rather than throwing.
    private readonly PerformanceCounter? _tcpRetransmitsCounter;

    // C-state residency (#83): "% C1/C2/C3 Time" on "Processor Information"\_Total - a power-
    // related slowdown signal distinct from thermal throttling (a CPU stuck mostly in a deep
    // C-state under light load is idling for power savings, not being held back). Each wrapped
    // independently - not every CPU/chipset generation reports all three tiers (some modern
    // platforms only ever populate C1), so a missing one just reports 0 rather than failing the
    // whole group.
    private readonly PerformanceCounter? _cIdleTimeCounter;
    private readonly PerformanceCounter? _c1TimeCounter;
    private readonly PerformanceCounter? _c2TimeCounter;
    private readonly PerformanceCounter? _c3TimeCounter;

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

        _cpuTotalCounter = new PerformanceCounter("Processor Information", "% Processor Time", "_Total", readOnly: true);
        _cpuTotalPerformanceCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", readOnly: true);

        // Instance names in this category are "<NUMA node>,<core>" (e.g. "0,0", "0,1", ...,
        // "0,_Total" for a per-node aggregate). Two bugs to avoid here: (1) filtering only the
        // exact string "_Total" misses per-node aggregates like "0,_Total", which would otherwise
        // be counted as a bogus extra "core"; (2) sorting the instance names as strings puts
        // "0,10" before "0,2" (lexicographic, not numeric). Both matter beyond cosmetics: CPU
        // topology (CpuTopologyService) indexes cores by the OS's actual logical-processor
        // number, so CpuPerCorePercent[i] needs to line up 1:1 with that same index - sort
        // numerically by (node, core) instead.
        var coreInstances = new PerformanceCounterCategory("Processor Information")
            .GetInstanceNames()
            .Where(n => n != "_Total" && !n.EndsWith(",_Total", StringComparison.OrdinalIgnoreCase))
            .Select(n => (Name: n, Key: ParseNumaCoreKey(n)))
            .OrderBy(x => x.Key.NumaNode)
            .ThenBy(x => x.Key.Core)
            .Select(x => x.Name)
            .ToArray();
        _cpuCoreUsageCounters = coreInstances
            .Select(name => new PerformanceCounter("Processor Information", "% Processor Time", name, readOnly: true))
            .ToArray();

        // #78: best-effort - wrapped as a whole rather than per-counter, since if "Parking Status"
        // doesn't exist on this Windows version it won't exist for any instance either.
        try
        {
            _cpuParkingCounters = coreInstances
                .Select(name => new PerformanceCounter("Processor Information", "Parking Status", name, readOnly: true))
                .ToArray();
        }
        catch
        {
            _cpuParkingCounters = Array.Empty<PerformanceCounter>();
        }

        _diskTimeCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", readOnly: true);
        _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", readOnly: true);
        _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", readOnly: true);
        // "Avg. Disk Queue Length" is a classic bottleneck signal (requests waiting, not just
        // active) and "Avg. Disk sec/Read|Write" is per-I/O latency in seconds - both instantaneous
        // gauges like the Memory ones above, not rates, so they need priming too (below).
        _diskQueueLengthCounter = new PerformanceCounter("PhysicalDisk", "Avg. Disk Queue Length", "_Total", readOnly: true);
        _diskReadLatencyCounter = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Read", "_Total", readOnly: true);
        _diskWriteLatencyCounter = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Write", "_Total", readOnly: true);

        // #365/#366
        _diskIdleTimeCounter = TryCreateCounter("PhysicalDisk", "% Idle Time", "_Total");
        _diskReadTimePercentCounter = TryCreateCounter("PhysicalDisk", "% Disk Read Time", "_Total");
        _diskWriteTimePercentCounter = TryCreateCounter("PhysicalDisk", "% Disk Write Time", "_Total");
        _diskTransfersCounter = TryCreateCounter("PhysicalDisk", "Disk Transfers/sec", "_Total");

        // #362: instance names in this category look like "0 C:" (disk index, then every drive
        // letter that disk hosts) or just "0" when no drive letter is assigned - sort numerically
        // on the leading index rather than lexicographically, same reasoning ParseNumaCoreKey below
        // documents for the per-core CPU counters ("0,10" before "0,2" would otherwise be wrong).
        var diskInstances = new PerformanceCounterCategory("PhysicalDisk")
            .GetInstanceNames()
            .Where(n => n != "_Total")
            .OrderBy(ParsePhysicalDiskIndex)
            .ToArray();
        _perDiskCounters = diskInstances
            .Select(TryCreatePhysicalDiskCounterSet)
            .Where(set => set is not null)
            .Select(set => set!)
            .ToArray();

        _handleCountCounter = new PerformanceCounter("Process", "Handle Count", "_Total", readOnly: true);
        _threadCountCounter = new PerformanceCounter("Process", "Thread Count", "_Total", readOnly: true);
        _processCountCounter = new PerformanceCounter("System", "Processes", readOnly: true);

        // Instantaneous gauges (not rates), so no priming needed below like the disk/CPU
        // counters get. Used for the Memory tab's Committed/Cached breakdown - see CLAUDE.md's
        // Memory deep-dive notes for why these are the Windows-native categories used instead of
        // macOS-style "wired"/"compressed" labels.
        _committedBytesCounter = new PerformanceCounter("Memory", "Committed Bytes", readOnly: true);
        _commitLimitCounter = new PerformanceCounter("Memory", "Commit Limit", readOnly: true);
        _cacheBytesCounter = new PerformanceCounter("Memory", "Cache Bytes", readOnly: true);
        // "Paging File\% Usage\_Total" is Windows' own live figure for how full the page file
        // currently is - paired with the WMI-read total size below to get an actual MB-used value,
        // the same "WMI for the rarely-changing total, PerformanceCounter for the live rate/percent"
        // split ReadCpuInfoFromWmi + "% Processor Performance" already uses for clock speed.
        _pageFileUsageCounter = new PerformanceCounter("Paging File", "% Usage", "_Total", readOnly: true);
        _pageFileTotalMb = ReadPageFileTotalMb();

        _cpuInterruptCounter = new PerformanceCounter("Processor", "% Interrupt Time", "_Total", readOnly: true);
        _cpuDpcCounter = new PerformanceCounter("Processor", "% DPC Time", "_Total", readOnly: true);
        _contextSwitchesCounter = new PerformanceCounter("System", "Context Switches/sec", readOnly: true);
        _cpuQueueLengthCounter = new PerformanceCounter("System", "Processor Queue Length", readOnly: true);

        _pageFaultsCounter = new PerformanceCounter("Memory", "Page Faults/sec", readOnly: true);
        _hardFaultsCounter = new PerformanceCounter("Memory", "Pages/sec", readOnly: true);
        _standbyCoreCounter = new PerformanceCounter("Memory", "Standby Cache Core Bytes", readOnly: true);
        _standbyNormalCounter = new PerformanceCounter("Memory", "Standby Cache Normal Priority Bytes", readOnly: true);
        _standbyReserveCounter = new PerformanceCounter("Memory", "Standby Cache Reserve Bytes", readOnly: true);
        _poolNonpagedCounter = new PerformanceCounter("Memory", "Pool Nonpaged Bytes", readOnly: true);
        _poolPagedCounter = new PerformanceCounter("Memory", "Pool Paged Bytes", readOnly: true);

        try
        {
            _tcpRetransmitsCounter = new PerformanceCounter("TCPv4", "Segments Retransmitted/sec", readOnly: true);
            _ = _tcpRetransmitsCounter.NextValue();
        }
        catch
        {
            // "TCPv4" category can be missing on an unusual network stack config - degrade to
            // "not available" (Sample() reports 0) rather than failing the whole service.
            _tcpRetransmitsCounter = null;
        }

        _cIdleTimeCounter = TryCreateCounter("Processor Information", "% Idle Time", "_Total");
        _c1TimeCounter = TryCreateCounter("Processor Information", "% C1 Time", "_Total");
        _c2TimeCounter = TryCreateCounter("Processor Information", "% C2 Time", "_Total");
        _c3TimeCounter = TryCreateCounter("Processor Information", "% C3 Time", "_Total");

        // Rate counters return 0 on their first read; prime them now so the
        // very first UI sample isn't a meaningless zero.
        _ = _cpuTotalCounter.NextValue();
        _ = _cpuTotalPerformanceCounter.NextValue();
        foreach (var c in _cpuCoreUsageCounters) _ = c.NextValue();
        _ = _diskTimeCounter.NextValue();
        _ = _diskReadCounter.NextValue();
        _ = _diskWriteCounter.NextValue();
        _ = _diskQueueLengthCounter.NextValue();
        _ = _diskReadLatencyCounter.NextValue();
        _ = _diskWriteLatencyCounter.NextValue();
        _ = _diskIdleTimeCounter?.NextValue();
        _ = _diskReadTimePercentCounter?.NextValue();
        _ = _diskWriteTimePercentCounter?.NextValue();
        _ = _diskTransfersCounter?.NextValue();
        _ = _pageFileUsageCounter.NextValue();
        _ = _cpuInterruptCounter.NextValue();
        _ = _cpuDpcCounter.NextValue();
        _ = _contextSwitchesCounter.NextValue();
        _ = _pageFaultsCounter.NextValue();
        _ = _hardFaultsCounter.NextValue();
        _ = _cIdleTimeCounter?.NextValue();
        _ = _c1TimeCounter?.NextValue();
        _ = _c2TimeCounter?.NextValue();
        _ = _c3TimeCounter?.NextValue();

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

        // #78: "Parking Status" reports a small integer (0 = unparked); any nonzero value means
        // the scheduler has parked that logical core to save power - a common, otherwise invisible
        // reason "only half my CPU seems to be doing anything" under light load.
        var coreParked = new bool[_cpuParkingCounters.Length];
        for (int i = 0; i < _cpuParkingCounters.Length; i++)
        {
            try { coreParked[i] = _cpuParkingCounters[i].NextValue() != 0; }
            catch { coreParked[i] = false; }
        }

        double diskPercent = Clamp(_diskTimeCounter.NextValue());
        double diskRead = _diskReadCounter.NextValue();
        double diskWrite = _diskWriteCounter.NextValue();
        double diskQueueLength = Math.Max(0, _diskQueueLengthCounter.NextValue());
        // Avg. Disk sec/Read|Write reports in seconds; ms is the unit anyone diagnosing
        // "is my disk slow" actually thinks in.
        double diskReadLatencyMs = Math.Max(0, _diskReadLatencyCounter.NextValue() * 1000.0);
        double diskWriteLatencyMs = Math.Max(0, _diskWriteLatencyCounter.NextValue() * 1000.0);

        // #365: corrected utilization - 100 minus idle time, rather than the saturating "% Disk
        // Time" above. Falls back to diskPercent (not a fabricated 0/100) when the counter isn't
        // available on this system, same "degrade to the best signal actually present" approach
        // the C-state tiers below use.
        bool diskIdleTimeAvailable = _diskIdleTimeCounter is not null;
        double diskIdlePercent = diskIdleTimeAvailable ? Clamp(_diskIdleTimeCounter!.NextValue()) : 0;
        double diskUtilizationPercent = diskIdleTimeAvailable ? Math.Clamp(100.0 - diskIdlePercent, 0, 100) : diskPercent;

        // #366
        double diskReadTimePercent = _diskReadTimePercentCounter is null ? 0 : Clamp(_diskReadTimePercentCounter.NextValue());
        double diskWriteTimePercent = _diskWriteTimePercentCounter is null ? 0 : Clamp(_diskWriteTimePercentCounter.NextValue());
        double diskTransfersPerSec = _diskTransfersCounter is null ? 0 : Math.Max(0, _diskTransfersCounter.NextValue());

        // #362: per-instance sweep, alongside (not instead of) the _Total figures above.
        var physicalDisks = new PhysicalDiskSample[_perDiskCounters.Length];
        for (int i = 0; i < _perDiskCounters.Length; i++)
            physicalDisks[i] = _perDiskCounters[i].Sample();

        var (bytesReceived, bytesSent) = ReadTotalNetworkBytes();
        var (netInErrors, netInDiscards, netOutErrors, netOutDiscards) = ReadNetworkErrorCounters();
        var elapsedSec = Math.Max(0.001, (now - _lastNetSampleUtc).TotalSeconds);
        double netRecvRate = Math.Max(0, (bytesReceived - _lastBytesReceived) / elapsedSec);
        double netSendRate = Math.Max(0, (bytesSent - _lastBytesSent) / elapsedSec);
        _lastBytesReceived = bytesReceived;
        _lastBytesSent = bytesSent;
        _lastNetSampleUtc = now;

        GetMemoryStatus(out long totalBytes, out long availBytes);

        double totalFaultsPerSec = Math.Max(0, _pageFaultsCounter.NextValue());
        double hardFaultsPerSec = Math.Max(0, _hardFaultsCounter.NextValue());
        long standbyBytes = (long)_standbyCoreCounter.NextValue() + (long)_standbyNormalCounter.NextValue() + (long)_standbyReserveCounter.NextValue();

        return new HardwareSnapshot
        {
            CpuTotalPercent = Math.Round(cpuTotal, 1),
            CpuPerCorePercent = perCore.Select(v => Math.Round(v, 1)).ToArray(),
            CoreParkedFlags = coreParked,
            CpuCurrentClockGhz = currentClockGhz <= 0 ? _cpuBaseClockGhz : currentClockGhz,
            CpuBaseClockGhz = _cpuBaseClockGhz,
            CpuMaxClockGhz = _cpuBaseClockGhz,
            CpuName = _cpuName,
            LogicalProcessors = _logicalProcessors,
            PhysicalCores = _physicalCores,
            CpuInterruptPercent = Math.Round(Clamp(_cpuInterruptCounter.NextValue()), 2),
            CpuDpcPercent = Math.Round(Clamp(_cpuDpcCounter.NextValue()), 2),
            ContextSwitchesPerSec = Math.Round(Math.Max(0, _contextSwitchesCounter.NextValue()), 0),
            CpuQueueLength = Math.Round(Math.Max(0, _cpuQueueLengthCounter.NextValue()), 0),

            CStatesAvailable = _cIdleTimeCounter is not null,
            CpuIdlePercent = _cIdleTimeCounter is null ? 0 : Math.Round(Clamp(_cIdleTimeCounter.NextValue()), 1),
            CpuC1Percent = _c1TimeCounter is null ? 0 : Math.Round(Clamp(_c1TimeCounter.NextValue()), 1),
            CpuC2Percent = _c2TimeCounter is null ? 0 : Math.Round(Clamp(_c2TimeCounter.NextValue()), 1),
            CpuC3Percent = _c3TimeCounter is null ? 0 : Math.Round(Clamp(_c3TimeCounter.NextValue()), 1),

            RamTotalBytes = totalBytes,
            RamUsedBytes = totalBytes - availBytes,
            RamAvailableBytes = availBytes,
            CommittedBytes = (long)_committedBytesCounter.NextValue(),
            CommitLimitBytes = (long)_commitLimitCounter.NextValue(),
            CacheBytes = (long)_cacheBytesCounter.NextValue(),
            PageFileTotalBytes = _pageFileTotalMb * 1024L * 1024L,
            PageFileUsedBytes = (long)(_pageFileTotalMb * 1024L * 1024L * (Clamp(_pageFileUsageCounter.NextValue()) / 100.0)),
            PageFaultsPerSec = Math.Round(totalFaultsPerSec, 0),
            HardFaultsPerSec = Math.Round(hardFaultsPerSec, 0),
            StandbyListBytes = Math.Max(0, standbyBytes),
            PoolNonpagedBytes = (long)_poolNonpagedCounter.NextValue(),
            PoolPagedBytes = (long)_poolPagedCounter.NextValue(),

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
            TcpRetransmitsPerSec = _tcpRetransmitsCounter is null ? 0 : Math.Round(Math.Max(0, _tcpRetransmitsCounter.NextValue()), 1),

            ProcessCount = (int)_processCountCounter.NextValue(),
            ThreadCount = (int)_threadCountCounter.NextValue(),
            HandleCount = (int)_handleCountCounter.NextValue(),
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        };
    }

    private static double Clamp(double v) => Math.Max(0, Math.Min(100, v));

    /// <summary>Best-effort counter construction - some counter/instance combinations
    /// (C-state tiers above being the main case in this app) legitimately don't exist on every
    /// Windows/CPU generation, so a failure here just means "not available" rather than crashing
    /// the whole service.</summary>
    private static PerformanceCounter? TryCreateCounter(string category, string name, string instance)
    {
        try
        {
            return new PerformanceCounter(category, name, instance, readOnly: true);
        }
        catch
        {
            return null;
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

    /// <summary>Best-effort per-disk counter-set construction - mirrors TryCreateCounter's
    /// "degrade to not available rather than crash the whole service" approach, but for a whole
    /// PhysicalDiskCounterSet at once: if this particular instance's counters fail to construct
    /// (e.g. a race between GetInstanceNames() and the disk being removed), that one disk is
    /// skipped rather than losing per-disk data for every other drive too.</summary>
    private static PhysicalDiskCounterSet? TryCreatePhysicalDiskCounterSet(string instanceName)
    {
        try
        {
            return new PhysicalDiskCounterSet(instanceName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>#362: one PhysicalDisk instance's counter set, mirroring the "_Total" fields above
    /// but per physical drive - so one slow drive isn't averaged away by others when queue length/
    /// latency is read only from "_Total". "% Idle Time" is wrapped as optional (TryCreateCounter)
    /// like the class-level _diskIdleTimeCounter above, since it's not guaranteed present on every
    /// Windows/driver combination.</summary>
    private sealed class PhysicalDiskCounterSet : IDisposable
    {
        public string InstanceName { get; }

        private readonly PerformanceCounter _time;
        private readonly PerformanceCounter? _idleTime;
        private readonly PerformanceCounter _read;
        private readonly PerformanceCounter _write;
        private readonly PerformanceCounter _queueLength;
        private readonly PerformanceCounter _readLatency;
        private readonly PerformanceCounter _writeLatency;
        private readonly PerformanceCounter _transferLatency;

        public PhysicalDiskCounterSet(string instanceName)
        {
            InstanceName = instanceName;
            _time = new PerformanceCounter("PhysicalDisk", "% Disk Time", instanceName, readOnly: true);
            _idleTime = TryCreateCounter("PhysicalDisk", "% Idle Time", instanceName);
            _read = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", instanceName, readOnly: true);
            _write = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instanceName, readOnly: true);
            _queueLength = new PerformanceCounter("PhysicalDisk", "Avg. Disk Queue Length", instanceName, readOnly: true);
            _readLatency = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Read", instanceName, readOnly: true);
            _writeLatency = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Write", instanceName, readOnly: true);
            _transferLatency = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Transfer", instanceName, readOnly: true);

            // Rate/gauge counters return 0 on their first read - prime them now, same as the
            // class-level counters in HardwareMonitorService's own constructor.
            _ = _time.NextValue();
            _ = _idleTime?.NextValue();
            _ = _read.NextValue();
            _ = _write.NextValue();
            _ = _queueLength.NextValue();
            _ = _readLatency.NextValue();
            _ = _writeLatency.NextValue();
            _ = _transferLatency.NextValue();
        }

        public PhysicalDiskSample Sample()
        {
            bool idleAvailable = _idleTime is not null;
            double active = Clamp(_time.NextValue());
            double idle = idleAvailable ? Clamp(_idleTime!.NextValue()) : 0;
            double utilization = idleAvailable ? Math.Clamp(100.0 - idle, 0, 100) : active;

            return new PhysicalDiskSample
            {
                InstanceName = InstanceName,
                ActivePercent = Math.Round(active, 1),
                IdleTimeAvailable = idleAvailable,
                IdlePercent = Math.Round(idle, 1),
                UtilizationPercent = Math.Round(utilization, 1),
                ReadBytesPerSec = Math.Max(0, _read.NextValue()),
                WriteBytesPerSec = Math.Max(0, _write.NextValue()),
                QueueLength = Math.Round(Math.Max(0, _queueLength.NextValue()), 2),
                ReadLatencyMs = Math.Round(Math.Max(0, _readLatency.NextValue() * 1000.0), 2),
                WriteLatencyMs = Math.Round(Math.Max(0, _writeLatency.NextValue() * 1000.0), 2),
                TransferLatencyMs = Math.Round(Math.Max(0, _transferLatency.NextValue() * 1000.0), 2),
            };
        }

        public void Dispose()
        {
            _time.Dispose();
            _idleTime?.Dispose();
            _read.Dispose();
            _write.Dispose();
            _queueLength.Dispose();
            _readLatency.Dispose();
            _writeLatency.Dispose();
            _transferLatency.Dispose();
        }
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
        foreach (var c in _cpuParkingCounters) c.Dispose();
        _diskTimeCounter.Dispose();
        _diskReadCounter.Dispose();
        _diskWriteCounter.Dispose();
        _diskQueueLengthCounter.Dispose();
        _diskReadLatencyCounter.Dispose();
        _diskWriteLatencyCounter.Dispose();
        _diskIdleTimeCounter?.Dispose();
        _diskReadTimePercentCounter?.Dispose();
        _diskWriteTimePercentCounter?.Dispose();
        _diskTransfersCounter?.Dispose();
        foreach (var d in _perDiskCounters) d.Dispose();
        _handleCountCounter.Dispose();
        _threadCountCounter.Dispose();
        _processCountCounter.Dispose();
        _committedBytesCounter.Dispose();
        _commitLimitCounter.Dispose();
        _cacheBytesCounter.Dispose();
        _pageFileUsageCounter.Dispose();
        _cpuInterruptCounter.Dispose();
        _cpuDpcCounter.Dispose();
        _contextSwitchesCounter.Dispose();
        _cpuQueueLengthCounter.Dispose();
        _pageFaultsCounter.Dispose();
        _hardFaultsCounter.Dispose();
        _standbyCoreCounter.Dispose();
        _standbyNormalCounter.Dispose();
        _standbyReserveCounter.Dispose();
        _poolNonpagedCounter.Dispose();
        _poolPagedCounter.Dispose();
        _tcpRetransmitsCounter?.Dispose();
        _cIdleTimeCounter?.Dispose();
        _c1TimeCounter?.Dispose();
        _c2TimeCounter?.Dispose();
        _c3TimeCounter?.Dispose();
    }
}
