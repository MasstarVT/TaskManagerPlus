namespace TaskManagerPlus.Models;

/// <summary>Static topology info for one logical CPU core (doesn't change at runtime).</summary>
public sealed class CoreTopologyInfo
{
    public int LogicalIndex { get; init; }
    public int NumaNode { get; init; }
    public byte EfficiencyClass { get; init; }

    /// <summary>True for performance cores on a hybrid CPU. Always true when the CPU isn't
    /// hybrid (see CpuTopologySnapshot.HasHybridTopology) - callers should hide the P/E
    /// distinction entirely rather than trust this flag in that case.</summary>
    public bool IsPCore { get; init; } = true;
}

/// <summary>Result of a one-time CpuTopologyService query: per-core NUMA node and
/// performance/efficiency-core classification.</summary>
public sealed class CpuTopologySnapshot
{
    public IReadOnlyList<CoreTopologyInfo> Cores { get; init; } = Array.Empty<CoreTopologyInfo>();

    /// <summary>True only when cores report genuinely different efficiency classes (Intel
    /// 12th-gen+ style hybrid). False - including on interop failure - means every core should
    /// be treated the same; IsPCore is not meaningful in that case.</summary>
    public bool HasHybridTopology { get; init; }

    public bool HasMultipleNumaNodes { get; init; }

    /// <summary>Fallback used when the topology query isn't available or fails: every core on
    /// NUMA node 0, no P/E distinction.</summary>
    public static CpuTopologySnapshot Flat(int logicalProcessorCount)
    {
        var cores = new List<CoreTopologyInfo>(Math.Max(0, logicalProcessorCount));
        for (int i = 0; i < logicalProcessorCount; i++)
            cores.Add(new CoreTopologyInfo { LogicalIndex = i, NumaNode = 0, EfficiencyClass = 0, IsPCore = true });

        return new CpuTopologySnapshot { Cores = cores, HasHybridTopology = false, HasMultipleNumaNodes = false };
    }
}
