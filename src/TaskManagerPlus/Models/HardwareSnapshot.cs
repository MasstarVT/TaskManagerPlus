namespace TaskManagerPlus.Models;

/// <summary>Point-in-time reading of all the system metrics the Performance tab shows.</summary>
public sealed class HardwareSnapshot
{
    public double CpuTotalPercent { get; init; }
    public double[] CpuPerCorePercent { get; init; } = Array.Empty<double>();

    /// <summary>Per-core parking status (#78, "Processor Information\Parking Status" - 0 =
    /// unparked, nonzero = parked), lined up 1:1 with CpuPerCorePercent. Empty when the counter
    /// isn't available (older Windows versions don't expose core parking at all) - callers should
    /// treat that the same as "no cores parked" rather than an error.</summary>
    public bool[] CoreParkedFlags { get; init; } = Array.Empty<bool>();
    public double CpuCurrentClockGhz { get; init; }
    public double CpuBaseClockGhz { get; init; }
    public double CpuMaxClockGhz { get; init; }
    public string CpuName { get; init; } = string.Empty;
    public int LogicalProcessors { get; init; }
    public int PhysicalCores { get; init; }

    /// <summary>% time spent servicing hardware interrupts / deferred procedure calls
    /// (Processor\% Interrupt Time|% DPC Time, "_Total") - a sustained spike here usually means
    /// a bad driver, not application load.</summary>
    public double CpuInterruptPercent { get; init; }
    public double CpuDpcPercent { get; init; }

    /// <summary>System\Context Switches/sec - a rate, helps spot thrashing/livelock.</summary>
    public double ContextSwitchesPerSec { get; init; }

    /// <summary>System\Processor Queue Length - threads waiting for a CPU right now (not
    /// running ones), an over-subscription indicator independent of the vs busy percent.</summary>
    public double CpuQueueLength { get; init; }

    /// <summary>C-state residency (#83, "Processor Information\% Idle|C1|C2|C3 Time", "_Total") -
    /// distinguishes a CPU idling deeply for power savings from one held back by thermal/power
    /// throttling. False when the counters aren't exposed at all on this Windows/CPU generation -
    /// callers should hide the section rather than showing all-zero bars in that case.</summary>
    public bool CStatesAvailable { get; init; }
    public double CpuIdlePercent { get; init; }
    public double CpuC1Percent { get; init; }
    public double CpuC2Percent { get; init; }
    public double CpuC3Percent { get; init; }

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

    /// <summary>Total configured page file size across all page files (Win32_PageFileUsage,
    /// read once - only changes if the page file is resized).</summary>
    public long PageFileTotalBytes { get; init; }

    /// <summary>Current page file usage, derived from Paging File\% Usage\_Total times the total
    /// above. A page file that's nearly full is a real memory-pressure signal independent of
    /// physical RAM usage.</summary>
    public long PageFileUsedBytes { get; init; }

    /// <summary>Memory\Page Faults/sec (total, soft+hard) and Memory\Pages/sec (hard-fault
    /// proxy - pages actually read/written to disk). A hard-fault rate spiking usually means
    /// low RAM / heavy paging, unlike a soft fault which is cheap (already-resident memory).</summary>
    public double PageFaultsPerSec { get; init; }
    public double HardFaultsPerSec { get; init; }

    /// <summary>Reclaimable file-cache pages (Standby Cache Core/Normal Priority/Reserve Bytes,
    /// summed) - the biggest source of "why does Windows show low free RAM when nothing is
    /// running": this memory looks used but is instantly reclaimable under pressure.</summary>
    public long StandbyListBytes { get; init; }

    /// <summary>#434: the three priority tiers that sum to StandbyListBytes above, read
    /// separately so the Memory tab can chart the composition instead of only the total - a
    /// standby list dominated by Reserve (lowest priority, first to be reclaimed) reads very
    /// differently from one dominated by Core (highest priority, reclaimed last).</summary>
    public long StandbyCoreBytes { get; init; }
    public long StandbyNormalBytes { get; init; }
    public long StandbyReserveBytes { get; init; }

    /// <summary>#432: Memory\Pages Input/sec and Memory\Pages Output/sec - the actual page-file
    /// read/write I/O rate (as opposed to HardFaultsPerSec above, which is "Memory\Pages/sec",
    /// a slightly different combined figure). Output/sec doubles as the modified-page-writer's
    /// flush rate for #436, since Windows exposes no separate "writer flush rate" counter.</summary>
    public double PagesInputPerSec { get; init; }
    public double PagesOutputPerSec { get; init; }

    /// <summary>#432: "LogicalDisk\Avg. Disk Queue Length" for the specific volume hosting the
    /// (first configured) page file - null when there's no page file, the drive letter couldn't be
    /// resolved, or the counter instance isn't available, in which case the thrashing detector
    /// below just weighs its other two signals instead of treating "unknown" as "zero".</summary>
    public double? PageFileVolumeQueueLength { get; init; }

    /// <summary>#437: Memory\System Cache Resident Bytes - the file-system-cache component of
    /// Cache Bytes above (Cache Bytes = System Cache Resident + System Driver Resident + System
    /// Code Resident), read directly rather than derived by subtraction since these three are
    /// independently-sampled counters that can disagree slightly at the margins.</summary>
    public long SystemCacheResidentBytes { get; init; }

    /// <summary>Kernel pool usage (Memory\Pool Nonpaged|Paged Bytes) - a slow, sustained climb
    /// here (rather than a stable baseline) usually points to a leaking driver, not an app.</summary>
    public long PoolNonpagedBytes { get; init; }
    public long PoolPagedBytes { get; init; }

    /// <summary>#422: Memory\Pool Nonpaged Allocs - the *count* of outstanding nonpaged pool
    /// allocations, alongside PoolNonpagedBytes' byte total above; a count climbing faster than
    /// the byte total points at many small leaked allocations rather than a few large ones.</summary>
    public long PoolNonpagedAllocs { get; init; }

    /// <summary>#422: Memory\System Driver Resident|Total Bytes - the RAM drivers hold that never
    /// shows up in any process's own working set. Resident is the subset currently paged in
    /// (actually occupying physical RAM right now); Total also counts the pageable portion that
    /// may currently be paged out - a large gap between the two is normal, not a leak signal on
    /// its own.</summary>
    public long SystemDriverResidentBytes { get; init; }
    public long SystemDriverTotalBytes { get; init; }

    /// <summary>#422: Memory\System Code Resident Bytes - the resident portion of the OS's own
    /// pageable kernel-mode code (as opposed to driver code above).</summary>
    public long SystemCodeResidentBytes { get; init; }

    /// <summary>#423: Memory\Modified Page List Bytes - pages that have been changed since being
    /// read from disk and are waiting to be written back before they can move to the standby
    /// list. A real, separate "where did my RAM go" category from the reclaimable-but-clean
    /// standby list above.</summary>
    public long ModifiedListBytes { get; init; }

    /// <summary>#423: installed physical RAM (summed Win32_PhysicalMemory.Capacity) minus
    /// GlobalMemoryStatusEx's own total - the chunk of RAM the platform (BIOS/UEFI, chipset,
    /// integrated GPU shared memory, etc.) reserves before Windows ever sees it. Read once via
    /// WMI, like ReadPageFileTotalMb below - it only changes with a hardware/firmware config
    /// change, not per tick.</summary>
    public long HardwareReservedBytes { get; init; }

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

    /// <summary>#365: whether "PhysicalDisk\% Idle Time\_Total" was available on this system -
    /// false means DiskUtilizationPercent below just mirrors DiskActivePercent (degraded, not
    /// fabricated) rather than a real 100-minus-idle figure.</summary>
    public bool DiskIdleTimeAvailable { get; init; }
    public double DiskIdlePercent { get; init; }

    /// <summary>#365: 100 - DiskIdlePercent - Task Manager's own "Active time" definition, which
    /// doesn't saturate above 100% the way DiskActivePercent ("% Disk Time") can under a deep
    /// queue. Shown alongside DiskActivePercent so a pinned-at-100% reading can be sanity-checked.</summary>
    public double DiskUtilizationPercent { get; init; }

    /// <summary>#366: "% Disk Read Time" / "% Disk Write Time" (_Total) - splits the aggregate
    /// active-time figure above into how much of it was reads vs. writes.</summary>
    public double DiskReadTimePercent { get; init; }
    public double DiskWriteTimePercent { get; init; }

    /// <summary>#366: "Disk Transfers/sec" (_Total) - paired with DiskReadBytesPerSec/
    /// DiskWriteBytesPerSec by the ViewModel to derive average bytes per transfer (I/O size).</summary>
    public double DiskTransfersPerSec { get; init; }

    /// <summary>#362: per-PhysicalDisk-instance readings, alongside (not instead of) the "_Total"
    /// aggregate fields above - so one slow drive isn't averaged away by others.</summary>
    public IReadOnlyList<PhysicalDiskSample> PhysicalDisks { get; init; } = Array.Empty<PhysicalDiskSample>();

    public double NetworkReceiveBytesPerSec { get; init; }
    public double NetworkSendBytesPerSec { get; init; }

    /// <summary>Cumulative CRC/framing error and dropped-packet counts across all active adapters
    /// since NIC/driver load - not per-second rates. A nonzero value flags a failing NIC or bad
    /// cable earlier than throughput graphs alone would.</summary>
    public long NetworkInErrors { get; init; }
    public long NetworkInDiscards { get; init; }
    public long NetworkOutErrors { get; init; }
    public long NetworkOutDiscards { get; init; }

    /// <summary>TCPv4\Segments Retransmitted/sec - a transport-level packet-loss signal distinct
    /// from the adapter-level CRC/discard counters above. 0 when the "TCPv4" perf category isn't
    /// available on this system, same as a genuinely healthy connection - not distinguishable
    /// from here, but never crashes the sampler either way.</summary>
    public double TcpRetransmitsPerSec { get; init; }

    public int ProcessCount { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public TimeSpan Uptime { get; init; }
}
