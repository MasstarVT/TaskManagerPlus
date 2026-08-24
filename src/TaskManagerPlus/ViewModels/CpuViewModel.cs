using System.ComponentModel;
using System.Windows.Data;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.ViewModels;

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

    /// <summary>Performance.Cores grouped by NUMA node, for the per-core grid's optional
    /// "NUMA Node N" section headers.</summary>
    public ICollectionView CoresByNumaNode { get; }

    public CpuViewModel(PerformanceViewModel performance)
    {
        Performance = performance;

        var view = new CollectionViewSource { Source = performance.Cores }.View;
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CoreUsage.NumaNode)));
        view.SortDescriptions.Add(new SortDescription(nameof(CoreUsage.NumaNode), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(CoreUsage.Index), ListSortDirection.Ascending));
        CoresByNumaNode = view;
    }
}
