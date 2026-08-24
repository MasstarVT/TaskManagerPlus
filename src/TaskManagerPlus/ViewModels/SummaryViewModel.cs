using System.ComponentModel;
using System.Windows.Data;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the Summary dashboard page. Deliberately owns no timers or sampling of its own -
/// it's a thin composition over the Performance/Processes view-models MainViewModel already
/// polls, the same live data just re-presented as a mosaic of small widgets.
/// </summary>
public sealed class SummaryViewModel
{
    public PerformanceViewModel Performance { get; }
    public ProcessesViewModel Processes { get; }

    /// <summary>All processes, live-sorted by CPU% descending, for the "Top processes" card.</summary>
    public ICollectionView TopProcesses { get; }

    public SummaryViewModel(PerformanceViewModel performance, ProcessesViewModel processes)
    {
        Performance = performance;
        Processes = processes;

        var view = new CollectionViewSource { Source = processes.Processes }.View;
        if (view is ICollectionViewLiveShaping liveShaping && liveShaping.CanChangeLiveSorting)
        {
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRow.CpuPercent));
            liveShaping.IsLiveSorting = true;
        }
        view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.CpuPercent), ListSortDirection.Descending));
        TopProcesses = view;
    }
}
