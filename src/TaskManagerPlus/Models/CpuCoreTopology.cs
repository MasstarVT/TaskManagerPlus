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

    /// <summary>Round 8 #26: index into the physical-core group this logical processor belongs to
    /// - every RelationProcessorCore entry read from GetLogicalProcessorInformationEx represents
    /// exactly one physical core, and its GroupMask lists the logical processors that are SMT/
    /// Hyper-Threading siblings sharing it. Two logical cores with the same PhysicalCoreGroup are
    /// siblings on one physical core; -1 when topology couldn't be determined (the Flat fallback).</summary>
    public int PhysicalCoreGroup { get; init; } = -1;
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

    /// <summary>Round 8 #26: true when at least one physical core hosts more than one logical
    /// processor (SMT/Hyper-Threading enabled) - false, including on interop failure, means every
    /// logical core is its own physical core and sibling pairing isn't meaningful.</summary>
    public bool HasSmt { get; init; }

    /// <summary>Fallback used when the topology query isn't available or fails: every core on
    /// NUMA node 0, no P/E distinction, no SMT sibling pairing.</summary>
    public static CpuTopologySnapshot Flat(int logicalProcessorCount)
    {
        var cores = new List<CoreTopologyInfo>(Math.Max(0, logicalProcessorCount));
        for (int i = 0; i < logicalProcessorCount; i++)
            cores.Add(new CoreTopologyInfo { LogicalIndex = i, NumaNode = 0, EfficiencyClass = 0, IsPCore = true, PhysicalCoreGroup = i });

        return new CpuTopologySnapshot { Cores = cores, HasHybridTopology = false, HasMultipleNumaNodes = false, HasSmt = false };
    }
}
