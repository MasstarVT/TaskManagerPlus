using System.ComponentModel;
using System.Windows.Data;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the Memory tab. Thin composition over the shared PerformanceViewModel sampler -
/// see CpuViewModel's remarks for why this doesn't own its own timer. Also takes the shared
/// ProcessesViewModel (already polling independently, same as SummaryViewModel's "Top CPU
/// processes" card) purely to re-sort its existing collection for "Top memory consumers" -
/// no new process sampling here.
/// </summary>
public sealed class MemoryViewModel
{
    public PerformanceViewModel Performance { get; }

    /// <summary>All processes, live-sorted by memory descending, for the "Top memory
    /// consumers" card.</summary>
    public ICollectionView TopMemoryProcesses { get; }

    public MemoryViewModel(PerformanceViewModel performance, ProcessesViewModel processes)
    {
        Performance = performance;

        var view = new CollectionViewSource { Source = processes.Processes }.View;
        if (view is ICollectionViewLiveShaping liveShaping && liveShaping.CanChangeLiveSorting)
        {
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRow.MemoryBytes));
            liveShaping.IsLiveSorting = true;
        }
        view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.MemoryBytes), ListSortDirection.Descending));
        TopMemoryProcesses = view;
    }
}
