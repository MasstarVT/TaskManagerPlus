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

    /// <summary>Round 8 #32: all processes, live-sorted by nonpaged-pool usage descending (paged
    /// pool is shown alongside each row rather than as a second sort key) for the "Top
    /// kernel-pool consumers" card - same "re-sort the already-polling collection" pattern as
    /// TopMemoryProcesses above, no new process sampling.</summary>
    public ICollectionView TopPoolProcesses { get; }

    /// <summary>#403: live-filtered view of the already-polling Processes collection, showing
    /// only rows currently flagged IsHandleLeakSuspect - the "Leak watch" card.</summary>
    public ICollectionView HandleLeakWatch { get; }

    /// <summary>#404: live-filtered view of processes currently at/above 80% of their GDI or
    /// USER object quota - see ProcessMonitorService.Sample/GdiQuotaService.</summary>
    public ICollectionView GdiUserQuotaWatch { get; }

    /// <summary>#406: pinned leak-watch list (right-click a process in the Processes tab -
    /// "Watch for leaks") - one independently-sampled glow+gradient chart per watched process,
    /// rendered by the Memory tab's "Leak watch list" panel.</summary>
    public LeakWatchViewModel LeakWatch { get; }

    public MemoryViewModel(PerformanceViewModel performance, ProcessesViewModel processes, LeakWatchViewModel leakWatch)
    {
        Performance = performance;
        LeakWatch = leakWatch;

        var view = new CollectionViewSource { Source = processes.Processes }.View;
        if (view is ICollectionViewLiveShaping liveShaping && liveShaping.CanChangeLiveSorting)
        {
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRow.MemoryBytes));
            liveShaping.IsLiveSorting = true;
        }
        view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.MemoryBytes), ListSortDirection.Descending));
        TopMemoryProcesses = view;

        var poolView = new CollectionViewSource { Source = processes.Processes }.View;
        if (poolView is ICollectionViewLiveShaping poolLiveShaping && poolLiveShaping.CanChangeLiveSorting)
        {
            poolLiveShaping.LiveSortingProperties.Add(nameof(ProcessRow.NonpagedPoolBytes));
            poolLiveShaping.LiveSortingProperties.Add(nameof(ProcessRow.PagedPoolBytes));
            poolLiveShaping.IsLiveSorting = true;
        }
        poolView.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.NonpagedPoolBytes), ListSortDirection.Descending));
        TopPoolProcesses = poolView;

        var handleLeakView = new CollectionViewSource { Source = processes.Processes }.View;
        handleLeakView.Filter = o => o is ProcessRow r && r.IsHandleLeakSuspect;
        if (handleLeakView is ICollectionViewLiveShaping handleLeakLiveShaping && handleLeakLiveShaping.CanChangeLiveSorting)
        {
            handleLeakLiveShaping.LiveFilteringProperties.Add(nameof(ProcessRow.IsHandleLeakSuspect));
            handleLeakLiveShaping.IsLiveFiltering = true;
        }
        HandleLeakWatch = handleLeakView;

        var gdiUserView = new CollectionViewSource { Source = processes.Processes }.View;
        gdiUserView.Filter = o => o is ProcessRow r && (r.IsGdiQuotaWarning || r.IsUserQuotaWarning);
        if (gdiUserView is ICollectionViewLiveShaping gdiUserLiveShaping && gdiUserLiveShaping.CanChangeLiveSorting)
        {
            gdiUserLiveShaping.LiveFilteringProperties.Add(nameof(ProcessRow.IsGdiQuotaWarning));
            gdiUserLiveShaping.LiveFilteringProperties.Add(nameof(ProcessRow.IsUserQuotaWarning));
            gdiUserLiveShaping.IsLiveFiltering = true;
        }
        GdiUserQuotaWatch = gdiUserView;
    }
}
