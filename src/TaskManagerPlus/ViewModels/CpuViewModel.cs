using System.Collections.ObjectModel;
using System.Collections.Specialized;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.ViewModels;

/// <summary>One NUMA node's worth of cores, for the CPU tab's per-core grid.</summary>
public sealed class CoreGroup
{
    public int NumaNode { get; init; }
    public IReadOnlyList<CoreUsage> Cores { get; init; } = Array.Empty<CoreUsage>();
}

/// <summary>
/// Backs the CPU tab. Deliberately owns no timer or HardwareMonitorService of its own -
/// it's a thin composition over the single shared PerformanceViewModel sampler (same pattern
/// as SummaryViewModel), since CPU/Memory/Storage/Network all come from one
/// HardwareMonitorService.Sample() call per tick. Splitting each into its own sampler would
/// mean redundant PerformanceCounter instantiation for identical underlying data.
/// </summary>
public sealed class CpuViewModel
{
    public PerformanceViewModel Performance { get; }

    /// <summary>Pass-through: true only on genuinely hybrid CPUs. The view should hide the
    /// P-core/E-core color distinction entirely when this is false.</summary>
    public bool HasHybridTopology => Performance.HasHybridTopology;

    /// <summary>Pass-through: true only when the system has more than one NUMA node. The view
    /// should render a single flat core grid (no group headers) when this is false.</summary>
    public bool HasMultipleNumaNodes => Performance.HasMultipleNumaNodes;

    /// <summary>
    /// Performance.Cores grouped by NUMA node, each group rendered as its own nested
    /// WrapPanel-hosted ItemsControl in the view. Built manually here (rather than via
    /// ICollectionView.GroupDescriptions + ItemsControl.GroupStyle) - NUMA node is static per
    /// core, so this only needs to rebuild when the Cores collection itself is replaced
    /// (core-count change), not per tick, and a plain nested ItemsControl avoids depending on
    /// WPF's grouping/GroupStyle machinery correctly detecting an externally-grouped
    /// CollectionView, which turned out not to lay out as a wrapping grid in practice.
    /// </summary>
    public ObservableCollection<CoreGroup> CoreGroups { get; } = new();

    public CpuViewModel(PerformanceViewModel performance)
    {
        Performance = performance;
        performance.Cores.CollectionChanged += OnCoresCollectionChanged;
        RebuildGroups();
    }

    private void OnCoresCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Per-tick updates only mutate existing CoreUsage.Percent in place (no Add/Remove), so
        // this only fires on the initial populate or an actual core-count change - cheap to
        // rebuild fully rather than trying to patch the grouping incrementally.
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove
            or NotifyCollectionChangedAction.Reset)
        {
            RebuildGroups();
        }
    }

    private void RebuildGroups()
    {
        CoreGroups.Clear();
        foreach (var group in Performance.Cores.GroupBy(c => c.NumaNode).OrderBy(g => g.Key))
            CoreGroups.Add(new CoreGroup { NumaNode = group.Key, Cores = group.OrderBy(c => c.Index).ToList() });
    }
}
