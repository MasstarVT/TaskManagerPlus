namespace TaskManagerPlus.Models;

/// <summary>
/// Round 18, #362: one physical disk's per-instance "PhysicalDisk" counters, read alongside (not
/// instead of) the aggregate "_Total" fields on HardwareSnapshot - so a single slow drive isn't
/// averaged away by other fast ones the way it is when only "_Total" is read. InstanceName is the
/// raw PerformanceCounter instance string Windows reports (e.g. "0 C:", "1 D: E:" for a disk with
/// two partitions, or just "0" when no drive letter is assigned to that physical disk).
/// </summary>
public sealed class PhysicalDiskSample
{
    public string InstanceName { get; init; } = string.Empty;

    /// <summary>"% Disk Time" - the classic busy-time counter, which can read above 100% under a
    /// deep queue (see HardwareSnapshot.DiskActivePercent's remarks). Kept alongside
    /// UtilizationPercent below so a pinned-at-100% reading can be sanity-checked (#365).</summary>
    public double ActivePercent { get; init; }

    /// <summary>Whether "% Idle Time" was available for this instance - false degrades
    /// UtilizationPercent to ActivePercent rather than fabricating a number.</summary>
    public bool IdleTimeAvailable { get; init; }
    public double IdlePercent { get; init; }

    /// <summary>100 - IdlePercent (#365) - Task Manager's own "Active time" definition, which
    /// doesn't saturate above 100% the way "% Disk Time" can.</summary>
    public double UtilizationPercent { get; init; }

    public double ReadBytesPerSec { get; init; }
    public double WriteBytesPerSec { get; init; }

    /// <summary>"Avg. Disk Queue Length" - requests waiting, not just active.</summary>
    public double QueueLength { get; init; }

    /// <summary>Per-I/O latency in milliseconds ("Avg. Disk sec/Read|Write|Transfer" * 1000).</summary>
    public double ReadLatencyMs { get; init; }
    public double WriteLatencyMs { get; init; }

    /// <summary>"Avg. Disk sec/Transfer" - combined read+write per-I/O latency, used by the #363
    /// rolling-window histogram and the #364 stall detector.</summary>
    public double TransferLatencyMs { get; init; }
}
