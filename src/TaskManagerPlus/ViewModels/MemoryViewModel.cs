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
    private readonly ProcessesViewModel _processes;
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

    // #416/#417/#418: "Kernel pool by tag" grid - raw NtQuerySystemInformation(SystemPoolTagInformation)
    // read (#416), joined with a cached #417 driver-attribution scan and the curated #418 embedded
    // dictionary. See PoolTagInspectionService's remarks for why the driver-attribution pass is a
    // second, separately-gated button rather than part of the same scan.
    public ObservableCollection<PoolTagRow> PoolTagRows { get; } = new();

    private bool _isScanningPoolTags;
    public bool IsScanningPoolTags { get => _isScanningPoolTags; private set => SetProperty(ref _isScanningPoolTags, value); }

    private string? _poolTagStatusText;
    public string? PoolTagStatusText { get => _poolTagStatusText; private set => SetProperty(ref _poolTagStatusText, value); }

    public AsyncRelayCommand ScanPoolTagsCommand { get; }

    private bool _isScanningDriverAttribution;
    public bool IsScanningDriverAttribution { get => _isScanningDriverAttribution; private set => SetProperty(ref _isScanningDriverAttribution, value); }

    private string? _driverAttributionStatusText;
    public string? DriverAttributionStatusText { get => _driverAttributionStatusText; private set => SetProperty(ref _driverAttributionStatusText, value); }

    public AsyncRelayCommand ScanDriverAttributionCommand { get; }

    // #419: "Compare pool snapshots" - captures the current #416 table as a baseline, then diffs
    // a fresh read against it on demand. Held purely in memory (no need to persist a baseline
    // across app restarts the way #417's slow attribution scan is worth caching).
    private PoolTagSnapshot? _poolSnapshotBaseline;

    public ObservableCollection<PoolTagGrowth> PoolTagGrowthRows { get; } = new();

    private string? _poolSnapshotStatusText;
    public string? PoolSnapshotStatusText { get => _poolSnapshotStatusText; private set => SetProperty(ref _poolSnapshotStatusText, value); }

    public RelayCommand CapturePoolSnapshotCommand { get; }
    public AsyncRelayCommand ComparePoolSnapshotsCommand { get; }

    // #424: loaded kernel module (driver) list - button-gated, see KernelModuleService's remarks.
    public ObservableCollection<KernelModuleRow> KernelModules { get; } = new();

    private bool _isScanningKernelModules;
    public bool IsScanningKernelModules { get => _isScanningKernelModules; private set => SetProperty(ref _isScanningKernelModules, value); }

    private string? _kernelModulesStatusText;
    public string? KernelModulesStatusText { get => _kernelModulesStatusText; private set => SetProperty(ref _kernelModulesStatusText, value); }

    public AsyncRelayCommand ScanKernelModulesCommand { get; }

    // #426: kernel object counts by type - button-gated, degrades to an empty (hidden) list if
    // the undocumented struct layout doesn't parse cleanly - see KernelObjectTypeService's remarks.
    public ObservableCollection<ObjectTypeCount> ObjectTypeCounts { get; } = new();

    private bool _isScanningObjectTypes;
    public bool IsScanningObjectTypes { get => _isScanningObjectTypes; private set => SetProperty(ref _isScanningObjectTypes, value); }

    private string? _objectTypesStatusText;
    public string? ObjectTypesStatusText { get => _objectTypesStatusText; private set => SetProperty(ref _objectTypesStatusText, value); }

    public AsyncRelayCommand ScanObjectTypesCommand { get; }

    // #423: "Where did my RAM go" reconciliation - sums categories already read elsewhere
    // (process private working sets, standby/modified lists, kernel pools, driver-locked pages,
    // hardware-reserved memory) and shows the unaccounted remainder explicitly. Pure aggregation/
    // presentation over already-available figures, recomputed once per Performance tick (see the
    // constructor's Performance.PropertyChanged subscription above) - no new sampling of its own.
    private double _reconciledProcessPrivateWsGb;
    public double ReconciledProcessPrivateWsGb { get => _reconciledProcessPrivateWsGb; private set => SetProperty(ref _reconciledProcessPrivateWsGb, value); }

    private double _reconciledKernelPoolsGb;
    public double ReconciledKernelPoolsGb { get => _reconciledKernelPoolsGb; private set => SetProperty(ref _reconciledKernelPoolsGb, value); }

    private double _reconciledAccountedGb;
    public double ReconciledAccountedGb { get => _reconciledAccountedGb; private set => SetProperty(ref _reconciledAccountedGb, value); }

    private double _reconciledRemainderGb;
    public double ReconciledRemainderGb { get => _reconciledRemainderGb; private set => SetProperty(ref _reconciledRemainderGb, value); }

    private double _reconciledRemainderPercent;
    public double ReconciledRemainderPercent { get => _reconciledRemainderPercent; private set => SetProperty(ref _reconciledRemainderPercent, value); }

    public MemoryViewModel(PerformanceViewModel performance, ProcessesViewModel processes, LeakWatchViewModel leakWatch, ProcessHistoryService processHistory)
    {
        Performance = performance;
        _processes = processes;
        LeakWatch = leakWatch;
        _processHistory = processHistory;

        _topMemoryView = new CollectionViewSource { Source = processes.Processes }.View;
        ApplyTopMemorySort();
        Performance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PerformanceViewModel.Uptime)) OnPropertyChanged(nameof(SystemUptimeText));
            // #423: ModifiedGb is set on every Performance tick (RefreshCoreAsync), so this is a
            // convenient "once per tick" hook to recompute the reconciliation below - same trick
            // SystemUptimeText's own subscription uses, just against a different always-updated property.
            if (e.PropertyName == nameof(PerformanceViewModel.ModifiedGb)) RefreshReconciliation();
        };

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

        // #416/#417/#419
        ScanPoolTagsCommand = new AsyncRelayCommand(ScanPoolTagsAsync, () => !IsScanningPoolTags);
        ScanDriverAttributionCommand = new AsyncRelayCommand(ScanDriverAttributionAsync, () => !IsScanningPoolTags && !IsScanningDriverAttribution && PoolTagRows.Count > 0);
        CapturePoolSnapshotCommand = new RelayCommand(CapturePoolSnapshot, () => PoolTagRows.Count > 0);
        ComparePoolSnapshotsCommand = new AsyncRelayCommand(ComparePoolSnapshotsAsync, () => !IsScanningPoolTags && _poolSnapshotBaseline is not null);

        // #424
        ScanKernelModulesCommand = new AsyncRelayCommand(ScanKernelModulesAsync, () => !IsScanningKernelModules);

        // #426
        ScanObjectTypesCommand = new AsyncRelayCommand(ScanObjectTypesAsync, () => !IsScanningObjectTypes);

        _leakGrowthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LeakGrowthRefreshInterval };
        _leakGrowthTimer.Tick += (_, _) => RefreshLeakGrowthProjections();
        _leakGrowthTimer.Start();
        RefreshLeakGrowthProjections();
        RefreshReconciliation();
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

    /// <summary>#416/#418: reads the raw per-tag pool table and joins in whatever driver
    /// attribution (#417) is already cached plus the curated #418 dictionary - fast (one syscall
    /// plus in-memory lookups), so unlike ScanDriverAttributionAsync below this can run on every
    /// click without a slow re-scan.</summary>
    private async Task ScanPoolTagsAsync()
    {
        IsScanningPoolTags = true;
        PoolTagStatusText = "Reading kernel pool tag table...";
        try
        {
            var rows = await Task.Run(PoolTagInspectionService.ReadPoolTags);
            var cache = PoolTagDriverCacheService.Load();

            foreach (var row in rows)
            {
                row.Description = PoolTagInspectionService.LookupDescription(row.Tag);
                row.LikelyDriver = cache.TagToDriver.TryGetValue(row.Tag, out var driver) ? driver : null;
            }

            PoolTagRows.Clear();
            foreach (var row in rows.OrderByDescending(r => r.TotalBytes))
                PoolTagRows.Add(row);

            PoolTagStatusText = PoolTagRows.Count == 0
                ? "Couldn't read the pool tag table on this Windows build."
                : $"{PoolTagRows.Count:N0} tags. \"Find owning drivers\" below fills in Likely driver (cached - slow the first time).";
        }
        finally
        {
            IsScanningPoolTags = false;
            // AsyncRelayCommand has no manual RaiseCanExecuteChanged (its CanExecuteChanged is
            // CommandManager.RequerySuggested, which WPF already re-queries on the next UI
            // event); RelayCommand does, so only CapturePoolSnapshotCommand needs the nudge.
            CapturePoolSnapshotCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>#417: the slow half of the pool tag grid - scans every driver file on disk for
    /// each not-yet-attributed tag's literal 4-byte string. Only scans tags PoolTagRows doesn't
    /// already have a cached answer for, and persists whatever it finds so a later "Scan pool
    /// tags" click doesn't need to re-run this.</summary>
    private async Task ScanDriverAttributionAsync()
    {
        var cache = PoolTagDriverCacheService.Load();
        var unresolvedTags = PoolTagRows
            .Where(r => !cache.TagToDriver.ContainsKey(r.Tag))
            .Select(r => r.Tag)
            .Distinct()
            .ToList();

        if (unresolvedTags.Count == 0)
        {
            DriverAttributionStatusText = "Every currently-listed tag already has a cached (or previously-scanned, unmatched) result.";
            return;
        }

        IsScanningDriverAttribution = true;
        DriverAttributionStatusText = $"Scanning driver files for {unresolvedTags.Count:N0} tag(s) - this can take up to a minute...";
        try
        {
            var modulePaths = _processes.Processes
                .Select(p => p.FilePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var found = await Task.Run(() => PoolTagInspectionService.ScanForDriverAttribution(unresolvedTags, modulePaths));

            foreach (var tag in unresolvedTags)
                cache.TagToDriver[tag] = found.TryGetValue(tag, out var driver) ? driver : "Unknown";
            cache.LastScanUtc = DateTime.UtcNow;
            PoolTagDriverCacheService.Save(cache);

            foreach (var row in PoolTagRows)
            {
                if (cache.TagToDriver.TryGetValue(row.Tag, out var driver))
                    row.LikelyDriver = driver;
            }
            // ObservableCollection<T> doesn't raise a change notification for in-place mutation of
            // an existing item's property - re-add in place so the DataGrid actually repaints.
            var snapshot = PoolTagRows.ToList();
            PoolTagRows.Clear();
            foreach (var row in snapshot) PoolTagRows.Add(row);

            DriverAttributionStatusText = $"Found a likely driver for {found.Count:N0} of {unresolvedTags.Count:N0} previously-unattributed tag(s).";
        }
        finally
        {
            IsScanningDriverAttribution = false;
        }
    }

    /// <summary>#419: stores the current #416 table as the comparison baseline.</summary>
    private void CapturePoolSnapshot()
    {
        _poolSnapshotBaseline = new PoolTagSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow,
            Tags = PoolTagRows.Select(r => new PoolTagRow
            {
                Tag = r.Tag,
                PagedAllocs = r.PagedAllocs,
                PagedFrees = r.PagedFrees,
                PagedBytes = r.PagedBytes,
                NonpagedAllocs = r.NonpagedAllocs,
                NonpagedFrees = r.NonpagedFrees,
                NonpagedBytes = r.NonpagedBytes,
                LikelyDriver = r.LikelyDriver,
                Description = r.Description,
            }).ToList(),
        };
        PoolTagGrowthRows.Clear();
        PoolSnapshotStatusText = $"Baseline captured at {_poolSnapshotBaseline.CapturedAtUtc.ToLocalTime():T} ({_poolSnapshotBaseline.Tags.Count:N0} tags). Wait, then click \"Compare to now\".";
    }

    /// <summary>#419: re-reads the pool tag table fresh and diffs it against the captured
    /// baseline - the tags that grew the most (by total bytes) are what actually identifies a
    /// leaking driver, as opposed to #416's flat point-in-time view.</summary>
    private async Task ComparePoolSnapshotsAsync()
    {
        if (_poolSnapshotBaseline is null) return;

        IsScanningPoolTags = true;
        PoolSnapshotStatusText = "Re-reading the pool tag table for comparison...";
        try
        {
            var current = await Task.Run(PoolTagInspectionService.ReadPoolTags);
            var baselineByTag = _poolSnapshotBaseline.Tags.ToDictionary(t => t.Tag, StringComparer.Ordinal);

            var growth = new List<PoolTagGrowth>();
            foreach (var now in current)
            {
                baselineByTag.TryGetValue(now.Tag, out var before);
                long pagedDelta = now.PagedBytes - (before?.PagedBytes ?? 0);
                long nonpagedDelta = now.NonpagedBytes - (before?.NonpagedBytes ?? 0);
                int allocDelta = now.OutstandingAllocs - (before?.OutstandingAllocs ?? 0);
                if (pagedDelta == 0 && nonpagedDelta == 0) continue;

                growth.Add(new PoolTagGrowth
                {
                    Tag = now.Tag,
                    LikelyDriver = now.LikelyDriver,
                    Description = now.Description ?? PoolTagInspectionService.LookupDescription(now.Tag),
                    PagedByteDelta = pagedDelta,
                    NonpagedByteDelta = nonpagedDelta,
                    OutstandingAllocDelta = allocDelta,
                });
            }

            PoolTagGrowthRows.Clear();
            foreach (var g in growth.OrderByDescending(g => g.TotalByteDelta).Take(30))
                PoolTagGrowthRows.Add(g);

            var elapsed = DateTime.UtcNow - _poolSnapshotBaseline.CapturedAtUtc;
            PoolSnapshotStatusText = $"Compared against the baseline from {elapsed.TotalMinutes:0.#} minute(s) ago. Showing tags with any change, largest growth first.";
        }
        finally
        {
            IsScanningPoolTags = false;
        }
    }

    /// <summary>#424: loaded kernel module (driver) inventory, sorted by image size descending -
    /// see KernelModuleService's remarks for the raw-enumeration/driverquery-fallback split.</summary>
    private async Task ScanKernelModulesAsync()
    {
        IsScanningKernelModules = true;
        KernelModulesStatusText = "Enumerating loaded kernel modules...";
        try
        {
            var modules = await KernelModuleService.ListAsync();
            KernelModules.Clear();
            foreach (var m in modules) KernelModules.Add(m);

            KernelModulesStatusText = KernelModules.Count == 0
                ? "Couldn't enumerate loaded kernel modules on this Windows build."
                : $"{KernelModules.Count:N0} loaded modules, sorted by image size.";
        }
        finally
        {
            IsScanningKernelModules = false;
        }
    }

    /// <summary>#426: kernel object counts by type - see KernelObjectTypeService's remarks for
    /// why this degrades to an empty (hidden) list rather than showing a partially-parsed or
    /// garbage result.</summary>
    private async Task ScanObjectTypesAsync()
    {
        IsScanningObjectTypes = true;
        ObjectTypesStatusText = "Reading kernel object type table...";
        try
        {
            var types = await Task.Run(KernelObjectTypeService.ReadObjectTypeCounts);
            ObjectTypeCounts.Clear();
            foreach (var t in types.OrderByDescending(t => t.IsNearHighWaterMark).ThenByDescending(t => t.TotalNumberOfObjects))
                ObjectTypeCounts.Add(t);

            ObjectTypesStatusText = ObjectTypeCounts.Count == 0
                ? "Couldn't read the object type table on this Windows build (undocumented layout) - nothing to show."
                : $"{ObjectTypeCounts.Count:N0} object types. {ObjectTypeCounts.Count(t => t.IsNearHighWaterMark):N0} near their own high-water mark.";
        }
        finally
        {
            IsScanningObjectTypes = false;
        }
    }

    /// <summary>#423: "where did my RAM go" - sums categories already read elsewhere and shows
    /// the unaccounted remainder explicitly, rather than just a flat total. Categories can overlap
    /// slightly at the margins (this is a reconciliation across independently-sampled counters,
    /// not a single atomic accounting pass), so a small remainder either direction is expected and
    /// not itself a sign of anything wrong - a large one is what's actually informative.</summary>
    private void RefreshReconciliation()
    {
        double processPrivateWsGb = _processes.Processes.Sum(p => p.PrivateWorkingSetBytes) / 1024.0 / 1024.0 / 1024.0;
        ReconciledProcessPrivateWsGb = processPrivateWsGb;
        ReconciledKernelPoolsGb = Performance.PoolNonpagedGb + Performance.PoolPagedGb;

        double accounted = processPrivateWsGb
            + Performance.StandbyGb
            + Performance.ModifiedGb
            + ReconciledKernelPoolsGb
            + Performance.SystemDriverResidentGb
            + Performance.HardwareReservedGb;
        ReconciledAccountedGb = accounted;

        double remainder = Performance.RamTotalGb - accounted;
        ReconciledRemainderGb = remainder;
        ReconciledRemainderPercent = Performance.RamTotalGb <= 0 ? 0 : Math.Clamp(remainder / Performance.RamTotalGb * 100.0, -100, 100);
    }

    public void Dispose() => _leakGrowthTimer.Stop();
}
