using System.Runtime.InteropServices;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// One-time query of static CPU topology (NUMA node + P-core/E-core classification per
/// logical processor) via the Win32 GetLogicalProcessorInformationEx API - there is no WMI
/// equivalent. This is the highest-risk interop in the app: the returned buffer is a packed
/// sequence of variable-length, unioned structs, so instead of trying to Marshal.PtrToStructure
/// an exact mirror of the (version-dependent) native layout, this reads the handful of fields it
/// needs directly at fixed byte offsets that are stable across SDK versions (verified against
/// both the Windows 7-era and current winnt.h layouts - see remarks on each offset below).
///
/// Topology never changes at runtime, so this is queried once (from PerformanceViewModel's
/// constructor) rather than per tick. Any failure - unsupported OS, unexpected layout, anything -
/// degrades to CpuTopologySnapshot.Flat rather than throwing, so a bad read here can never crash
/// the app or block startup (same philosophy as ThemeService's silent fallback).
/// </summary>
public static class CpuTopologyService
{
    // LOGICAL_PROCESSOR_RELATIONSHIP values we care about; others (RelationCache,
    // RelationProcessorPackage, RelationGroup, ...) are skipped.
    private const int RelationProcessorCore = 0;
    private const int RelationNumaNode = 1;
    private const int RelationAll = 0xffff;

    // Offsets into a SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX entry. Relationship (4 bytes) and
    // Size (4 bytes) are the common header; the union starts at offset 8.
    private const int OffsetRelationship = 0;
    private const int OffsetSize = 4;
    private const int OffsetProcessorEfficiencyClass = 9;  // PROCESSOR_RELATIONSHIP.EfficiencyClass
    private const int OffsetProcessorGroupCount = 30;       // PROCESSOR_RELATIONSHIP.GroupCount
    private const int OffsetProcessorGroupMask = 32;        // PROCESSOR_RELATIONSHIP.GroupMask[0]
    private const int OffsetNumaNodeNumber = 8;              // NUMA_NODE_RELATIONSHIP.NodeNumber
    private const int OffsetNumaGroupMask = 32;              // NUMA_NODE_RELATIONSHIP.GroupMask (same
                                                               // offset under both the pre-Win10
                                                               // Reserved[20] layout and the current
                                                               // Reserved[18]+GroupCount(WORD) layout,
                                                               // since both total 24 bytes from offset 8).

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    public static CpuTopologySnapshot Query()
    {
        try
        {
            return QueryCore();
        }
        catch
        {
            return CpuTopologySnapshot.Flat(Environment.ProcessorCount);
        }
    }

    private static CpuTopologySnapshot QueryCore()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationAll, IntPtr.Zero, ref length);
        if (length == 0)
            return CpuTopologySnapshot.Flat(Environment.ProcessorCount);

        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationAll, buffer, ref length))
                return CpuTopologySnapshot.Flat(Environment.ProcessorCount);

            var coreMasks = new List<(ulong Mask, byte EfficiencyClass)>();
            var numaMasks = new List<(ulong Mask, int NodeNumber)>();

            IntPtr entry = buffer;
            uint bytesRead = 0;
            while (bytesRead < length)
            {
                int relationship = Marshal.ReadInt32(entry, OffsetRelationship);
                int size = Marshal.ReadInt32(entry, OffsetSize);
                if (size <= 0) break; // malformed - bail out to the flat fallback below

                switch (relationship)
                {
                    case RelationProcessorCore:
                        byte efficiencyClass = Marshal.ReadByte(entry, OffsetProcessorEfficiencyClass);
                        short groupCount = Marshal.ReadInt16(entry, OffsetProcessorGroupCount);
                        // Only the first group is read - correct for every system with <=64
                        // logical processors (i.e. every consumer desktop/laptop this app
                        // targets); multi-group server systems would need each GroupMask[i].
                        if (groupCount >= 1)
                            coreMasks.Add((ReadMask(entry, OffsetProcessorGroupMask), efficiencyClass));
                        break;

                    case RelationNumaNode:
                        int nodeNumber = Marshal.ReadInt32(entry, OffsetNumaNodeNumber);
                        numaMasks.Add((ReadMask(entry, OffsetNumaGroupMask), nodeNumber));
                        break;
                }

                entry = IntPtr.Add(entry, size);
                bytesRead += (uint)size;
            }

            if (coreMasks.Count == 0)
                return CpuTopologySnapshot.Flat(Environment.ProcessorCount);

            int logicalCount = Environment.ProcessorCount;
            byte maxEfficiency = coreMasks.Max(c => c.EfficiencyClass);

            var cores = new List<CoreTopologyInfo>(logicalCount);
            bool anySmtGroup = false;
            for (int i = 0; i < logicalCount; i++)
            {
                // Bits beyond 63 (>64 logical processors) fall back to defaults below - see the
                // single-group note above.
                ulong bit = i < 64 ? 1UL << i : 0UL;

                byte efficiencyClass = 0;
                // #26: each coreMasks entry (index = physical-core group id) represents exactly
                // one physical core - a group whose mask sets more than one bit is an SMT/
                // Hyper-Threading pair sharing that one physical core.
                int physicalCoreGroup = i;
                for (int g = 0; g < coreMasks.Count; g++)
                {
                    if ((coreMasks[g].Mask & bit) == 0) continue;
                    efficiencyClass = coreMasks[g].EfficiencyClass;
                    physicalCoreGroup = g;
                    if (System.Numerics.BitOperations.PopCount(coreMasks[g].Mask) > 1) anySmtGroup = true;
                    break;
                }

                int numaNode = 0;
                foreach (var (mask, node) in numaMasks)
                {
                    if ((mask & bit) != 0) { numaNode = node; break; }
                }

                cores.Add(new CoreTopologyInfo
                {
                    LogicalIndex = i,
                    NumaNode = numaNode,
                    EfficiencyClass = efficiencyClass,
                    IsPCore = efficiencyClass >= maxEfficiency,
                    PhysicalCoreGroup = physicalCoreGroup,
                });
            }

            bool hybrid = coreMasks.Select(c => c.EfficiencyClass).Distinct().Count() > 1;
            bool multiNuma = numaMasks.Select(n => n.NodeNumber).Distinct().Count() > 1;

            return new CpuTopologySnapshot { Cores = cores, HasHybridTopology = hybrid, HasMultipleNumaNodes = multiNuma, HasSmt = anySmtGroup };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads a GROUP_AFFINITY.Mask (KAFFINITY, pointer-sized) at the given offset,
    /// reinterpreting all 64 bits as unsigned regardless of whether the top bit is set.</summary>
    private static ulong ReadMask(IntPtr entry, int offset) => unchecked((ulong)Marshal.ReadIntPtr(entry, offset).ToInt64());
}
