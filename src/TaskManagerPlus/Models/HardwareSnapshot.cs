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

    /// <summary>#630: per-core "% Processor Performance" (the OS-requested clock, as a percent of
    /// rated max, reflecting turbo multiplier over base) - lined up 1:1 with CpuPerCorePercent.
    /// Empty when the per-core counter instance array couldn't be created (best-effort, same as
    /// CoreParkedFlags above).</summary>
    public double[] CpuPerCoreRequestedPercent { get; init; } = Array.Empty<double>();

    /// <summary>#630: per-core "% of Maximum Frequency" (the silicon-delivered clock, accounting
    /// for any throttling, as a percent of rated max) - lined up 1:1 with CpuPerCorePercent. A
    /// persistent gap vs. CpuPerCoreRequestedPercent above means the OS is asking for more than
    /// the silicon is actually delivering. Empty when unavailable, same as CpuPerCoreRequestedPercent.</summary>
    public double[] CpuPerCoreDeliveredPercent { get; init; } = Array.Empty<double>();

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

    /// <summary>Kernel pool usage (Memory\Pool Nonpaged|Paged Bytes) - a slow, sustained climb
    /// here (rather than a stable baseline) usually points to a leaking driver, not an app.</summary>
    public long PoolNonpagedBytes { get; init; }
    public long PoolPagedBytes { get; init; }

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
