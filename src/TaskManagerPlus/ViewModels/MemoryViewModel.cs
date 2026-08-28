using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the Memory tab. Thin composition over the shared PerformanceViewModel sampler -
/// see CpuViewModel's remarks for why this doesn't own its own timer. Also takes the shared
/// ProcessesViewModel (already polling independently, same as SummaryViewModel's "Top CPU
/// processes" card) purely to re-sort its existing collection for "Top memory consumers" -
/// no new process sampling here. Now an ObservableObject (was a plain composition class) since
/// #411/#412/#415 need bindable command/status state, not just static-at-construction views.
/// </summary>
public sealed class MemoryViewModel : ObservableObject, IDisposable
{
    private readonly ProcessHistoryService _processHistory;
    private readonly ICollectionView _topMemoryView;
    private readonly DispatcherTimer _leakGrowthTimer;

    public PerformanceViewModel Performance { get; }

    /// <summary>All processes, live-sorted by memory descending (or, when
    /// SortTopMemoryByPrivateWorkingSet is on, by #412 private working set descending instead) -
    /// for the "Top memory consumers" card.</summary>
    public ICollectionView TopMemoryProcesses => _topMemoryView;

    /// <summary>#412: toggles TopMemoryProcesses' sort key between total working set (the
    /// original figure - includes pages shared with other processes) and private working set
    /// (resident, non-shared memory only) - so a shared-DLL-heavy process isn't misread as a
    /// memory hog just because its total working set looks large.</summary>
    private bool _sortTopMemoryByPrivateWorkingSet;
    public bool SortTopMemoryByPrivateWorkingSet
    {
        get => _sortTopMemoryByPrivateWorkingSet;
        set
        {
            if (SetProperty(ref _sortTopMemoryByPrivateWorkingSet, value))
                ApplyTopMemorySort();
        }
    }

    /// <summary>Round 8 #32: all processes, live-sorted by nonpaged-pool usage descending (paged
    /// pool is shown alongside each row rather than as a second sort key) for the "Top
    /// kernel-pool consumers" card - same "re-sort the already-polling collection" pattern as
    /// TopMemoryProcesses above, no new process sampling.</summary>
    public ICollectionView TopPoolProcesses { get; }

    /// <summary>#413: all processes, live-sorted by private bytes descending - a process's
    /// contribution to the system commit charge, which is a separate failure mode from working-
    /// set/physical-RAM exhaustion (TopMemoryProcesses above). A process near the top of this
    /// list but not the memory list is reserving/committing address space it isn't necessarily
    /// keeping resident.</summary>
    public ICollectionView TopCommitProcesses { get; }

    /// <summary>#403: live-filtered view of the already-polling Processes collection, showing
    /// only rows currently flagged IsHandleLeakSuspect - the "Leak watch" card.</summary>
    public ICollectionView HandleLeakWatch { get; }

    /// <summary>#404: live-filtered view of processes currently at/above 80% of their GDI or
    /// USER object quota - see ProcessMonitorService.Sample/GdiQuotaService.</summary>
    public ICollectionView GdiUserQuotaWatch { get; }

    /// <summary>#409: live-filtered view of processes whose private bytes are running well ahead
    /// of their working set - see ProcessRow.IsWorkingSetDivergent's remarks for the threshold.</summary>
    public ICollectionView WorkingSetDivergenceWatch { get; }

    /// <summary>#406: pinned leak-watch list (right-click a process in the Processes tab -
    /// "Watch for leaks") - one independently-sampled glow+gradient chart per watched process,
    /// rendered by the Memory tab's "Leak watch list" panel.</summary>
    public LeakWatchViewModel LeakWatch { get; }

    /// <summary>#411: results of the most recent "Scan shared memory" pass - empty until
    /// ScanSharedMemoryCommand runs, see SharedMemoryInspectionService's remarks for why this is
    /// strictly button-triggered rather than a tick.</summary>
    public ObservableCollection<SharedMemorySection> SharedMemorySections { get; } = new();

    private bool _isScanningSharedMemory;
    public bool IsScanningSharedMemory { get => _isScanningSharedMemory; private set => SetProperty(ref _isScanningSharedMemory, value); }

    private string? _sharedMemoryStatusText;
    public string? SharedMemoryStatusText { get => _sharedMemoryStatusText; private set => SetProperty(ref _sharedMemoryStatusText, value); }

    public AsyncRelayCommand ScanSharedMemoryCommand { get; }

    /// <summary>#415: an uptime-normalized presentation of #402's per-image-name growth slopes -
    /// refreshed on a light timer (ProcessHistoryService.GetTopGrowthSummaries is pure in-memory
    /// regression over already-recorded samples, no I/O, so this doesn't need to be gated behind
    /// a button the way #411's system-wide handle scan does). Labelled a projection, not a
    /// prediction - see LeakGrowthProjection's remarks.</summary>
    public ObservableCollection<LeakGrowthProjection> LeakGrowthProjections { get; } = new();

    /// <summary>#415: system uptime shown alongside the per-process growth figures above, so a
    /// process that's grown 50 MB/day reads differently next to "up 2 hours" than next to "up 30
    /// days".</summary>
    public string SystemUptimeText => Performance.Uptime;

    private static readonly TimeSpan LeakGrowthRefreshInterval = TimeSpan.FromSeconds(15);

    // #415: below this, a slope that's technically positive is just noise, not worth projecting
    // an exhaustion estimate from - mirrors the "quick flag, not a verdict" floors used elsewhere
    // (ProcessHistoryService's own handle-leak/thread-runaway thresholds).
    private const double MinGrowthMbPerHourToProject = 1.0;
    private const double MinRSquaredToProject = 0.4;

    public MemoryViewModel(PerformanceViewModel performance, ProcessesViewModel processes, LeakWatchViewModel leakWatch, ProcessHistoryService processHistory)
    {
        Performance = performance;
        LeakWatch = leakWatch;
        _processHistory = processHistory;

        _topMemoryView = new CollectionViewSource { Source = processes.Processes }.View;
        ApplyTopMemorySort();
        Performance.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PerformanceViewModel.Uptime)) OnPropertyChanged(nameof(SystemUptimeText)); };

        var poolView = new CollectionViewSource { Source = processes.Processes }.View;
        if (poolView is ICollectionViewLiveShaping poolLiveShaping && poolLiveShaping.CanChangeLiveSorting)
        {
            poolLiveShaping.LiveSortingProperties.Add(nameof(ProcessRow.NonpagedPoolBytes));
            poolLiveShaping.LiveSortingProperties.Add(nameof(ProcessRow.PagedPoolBytes));
            poolLiveShaping.IsLiveSorting = true;
        }
        poolView.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.NonpagedPoolBytes), ListSortDirection.Descending));
        TopPoolProcesses = poolView;

        // #413: commit-charge ranking (private bytes), independent of the working-set ranking
        // above - see TopCommitProcesses' remarks for why these two can disagree.
        var commitView = new CollectionViewSource { Source = processes.Processes }.View;
        if (commitView is ICollectionViewLiveShaping commitLiveShaping && commitLiveShaping.CanChangeLiveSorting)
        {
            commitLiveShaping.LiveSortingProperties.Add(nameof(ProcessRow.PrivateBytes));
            commitLiveShaping.IsLiveSorting = true;
        }
        commitView.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.PrivateBytes), ListSortDirection.Descending));
        TopCommitProcesses = commitView;

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

        // #409
        var divergenceView = new CollectionViewSource { Source = processes.Processes }.View;
        divergenceView.Filter = o => o is ProcessRow r && r.IsWorkingSetDivergent;
        if (divergenceView is ICollectionViewLiveShaping divergenceLiveShaping && divergenceLiveShaping.CanChangeLiveSorting)
        {
            divergenceLiveShaping.LiveFilteringProperties.Add(nameof(ProcessRow.IsWorkingSetDivergent));
            divergenceLiveShaping.IsLiveFiltering = true;
        }
        WorkingSetDivergenceWatch = divergenceView;

        ScanSharedMemoryCommand = new AsyncRelayCommand(ScanSharedMemoryAsync, () => !IsScanningSharedMemory);

        _leakGrowthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LeakGrowthRefreshInterval };
        _leakGrowthTimer.Tick += (_, _) => RefreshLeakGrowthProjections();
        _leakGrowthTimer.Start();
        RefreshLeakGrowthProjections();
    }

    private void ApplyTopMemorySort()
    {
        string property = _sortTopMemoryByPrivateWorkingSet ? nameof(ProcessRow.PrivateWorkingSetBytes) : nameof(ProcessRow.MemoryBytes);

        if (_topMemoryView is ICollectionViewLiveShaping liveShaping && liveShaping.CanChangeLiveSorting)
        {
            liveShaping.LiveSortingProperties.Clear();
            liveShaping.LiveSortingProperties.Add(property);
            liveShaping.IsLiveSorting = true;
        }
        _topMemoryView.SortDescriptions.Clear();
        _topMemoryView.SortDescriptions.Add(new SortDescription(property, ListSortDirection.Descending));
    }

    /// <summary>#411: runs SharedMemoryInspectionService.Scan off the UI thread - see its remarks
    /// for why this is a heavier, capped, explicitly-triggered operation rather than anything on
    /// a tick.</summary>
    private async Task ScanSharedMemoryAsync()
    {
        IsScanningSharedMemory = true;
        SharedMemoryStatusText = "Scanning shared memory sections - this can take a few seconds...";
        try
        {
            var result = await Task.Run(SharedMemoryInspectionService.Scan);

            SharedMemorySections.Clear();
            foreach (var section in result.Sections)
                SharedMemorySections.Add(section);

            SharedMemoryStatusText = result.Error is not null
                ? $"Scan failed: {result.Error}"
                : result.Sections.Count == 0
                    ? "No named shared-memory sections found (or none could be resolved)."
                    : $"Found {result.Sections.Count:N0} named section(s)." + (result.WasCapped ? " Stopped early - the scan hit its handle cap." : "");
        }
        finally
        {
            IsScanningSharedMemory = false;
        }
    }

    /// <summary>#415: rebuilds LeakGrowthProjections from ProcessHistoryService's already-computed
    /// per-image-name regression - pure in-memory math over already-recorded samples, so this
    /// runs on a light timer rather than needing its own explicit button.</summary>
    private void RefreshLeakGrowthProjections()
    {
        double remainingCommitMb = Math.Max(0, Performance.CommitLimitGb - Performance.CommittedGb) * 1024.0;

        var summaries = _processHistory.GetTopGrowthSummaries(8)
            .Where(s => s.PrivateBytesSlopeMbPerHour >= MinGrowthMbPerHourToProject && s.PrivateBytesRSquared >= MinRSquaredToProject)
            .ToList();

        LeakGrowthProjections.Clear();
        foreach (var s in summaries)
        {
            double growthMbPerDay = s.PrivateBytesSlopeMbPerHour * 24.0;
            double? hoursToExhaustion = s.PrivateBytesSlopeMbPerHour > 0 && remainingCommitMb > 0
                ? remainingCommitMb / s.PrivateBytesSlopeMbPerHour
                : null;

            LeakGrowthProjections.Add(new LeakGrowthProjection
            {
                ImageName = s.ImageName,
                GrowthMbPerDay = Math.Round(growthMbPerDay, 1),
                RSquared = s.PrivateBytesRSquared,
                HoursToCommitExhaustion = hoursToExhaustion.HasValue ? Math.Round(hoursToExhaustion.Value, 1) : null,
            });
        }

        OnPropertyChanged(nameof(SystemUptimeText));
    }

    public void Dispose() => _leakGrowthTimer.Stop();
}
