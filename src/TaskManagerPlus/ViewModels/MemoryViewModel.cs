using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
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

    // #428: commit-limit exhaustion projection - a straight-line least-squares extrapolation of
    // Performance.CommittedHistory toward Performance.CommitLimitGb, hidden entirely unless the
    // trend is positive, reasonably confident (R² floor - the same "quick flag, not a verdict"
    // treatment #415's per-process leak growth projection above already uses), and there's
    // actually a limit left to reach.
    private const double MinCommitGrowthGbPerHourToProject = 0.05;
    private const double MinCommitTrendRSquared = 0.5;

    private double? _minutesToCommitLimitExhaustion;
    public double? MinutesToCommitLimitExhaustion { get => _minutesToCommitLimitExhaustion; private set => SetProperty(ref _minutesToCommitLimitExhaustion, value); }

    public bool ShowCommitLimitProjection => MinutesToCommitLimitExhaustion.HasValue;

    public string CommitLimitProjectionText => MinutesToCommitLimitExhaustion is { } minutes
        ? minutes < 60
            ? $"Projected: commit limit reached in ~{minutes:0} min at the current rate"
            : $"Projected: commit limit reached in ~{minutes / 60.0:0.0} hr at the current rate"
        : string.Empty;

    // #429: peak committed-memory high-water mark, persisted per boot session (a persisted "last
    // known boot time" compared against Environment.TickCount64 is the cheap boot-id proxy - see
    // MemoryHighWaterSettings' remarks) so a machine that briefly hit its limit hours ago still
    // shows the evidence after this app restarts, as long as the machine itself hasn't rebooted.
    private static readonly TimeSpan BootTimeTolerance = TimeSpan.FromMinutes(3);
    private readonly MemoryHighWaterSettings _highWater;

    private double _peakCommittedGb;
    public double PeakCommittedGb { get => _peakCommittedGb; private set => SetProperty(ref _peakCommittedGb, value); }

    private double _peakCommitLimitGbAtPeak;
    public double PeakCommitLimitGbAtPeak { get => _peakCommitLimitGbAtPeak; private set => SetProperty(ref _peakCommitLimitGbAtPeak, value); }

    private DateTime _peakTimestampUtc;

    public string PeakCommittedText => PeakCommittedGb <= 0
        ? "No peak recorded yet this boot"
        : $"Peak this boot: {PeakCommittedGb:0.0} GB of {PeakCommitLimitGbAtPeak:0.0} GB limit ({_peakTimestampUtc.ToLocalTime():t})";

    // #430/#431: per-page-file configuration + sizing advisories - see PageFileConfigurationService.
    public ObservableCollection<PageFileConfigInfo> PageFileConfigs { get; } = new();

    private bool? _pageFileAutomaticallyManaged;
    public bool? PageFileAutomaticallyManaged { get => _pageFileAutomaticallyManaged; private set => SetProperty(ref _pageFileAutomaticallyManaged, value); }

    public string PageFileManagementText => PageFileAutomaticallyManaged switch
    {
        true => "Windows is automatically managing page file size and placement.",
        false => "Page file size/placement is manually configured (automatic management is off).",
        null => "Couldn't determine whether page file management is automatic.",
    };

    private bool _isLoadingPageFileConfig;
    public bool IsLoadingPageFileConfig { get => _isLoadingPageFileConfig; private set => SetProperty(ref _isLoadingPageFileConfig, value); }

    private string? _pageFileConfigStatusText;
    public string? PageFileConfigStatusText { get => _pageFileConfigStatusText; private set => SetProperty(ref _pageFileConfigStatusText, value); }

    public AsyncRelayCommand RefreshPageFileConfigCommand { get; }

    /// <summary>#431: "quick flag, not a verdict" sizing advisories - see
    /// RefreshPageFileAdvisories for exactly what's flagged.</summary>
    public ObservableCollection<string> PageFileAdvisories { get; } = new();

    // #432: page-file thrashing detector - combines actual page-file I/O rate, how full the page
    // file is, and its volume's own disk queue length into one flag with a duration timer, rather
    // than trusting any single signal alone (a burst of page-file I/O right after launching a big
    // app is normal; sustained alongside a saturated page file and a backed-up disk queue is the
    // actual thrashing signature).
    private const double ThrashingPagingActivityThreshold = 500.0; // combined Pages Input/sec + Pages Output/sec
    private const double ThrashingPageFilePercentThreshold = 70.0;
    private const double ThrashingQueueLengthThreshold = 2.0;

    private DateTime? _thrashingStartUtc;

    private bool _isThrashing;
    public bool IsThrashing { get => _isThrashing; private set => SetProperty(ref _isThrashing, value); }

    private string _thrashingStatusText = "Not thrashing";
    public string ThrashingStatusText { get => _thrashingStatusText; private set => SetProperty(ref _thrashingStatusText, value); }

    // #433: "Find what's paging" - a 30-second per-process Page Faults/sec sampling pass, plus a
    // best-effort raw kernel-trace (.etl) capture running alongside it - see ScanPageFaultsAsync
    // and PageFaultTraceService's remarks for why the raw trace isn't parsed in-app.
    private static readonly TimeSpan PageFaultScanWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PageFaultSampleInterval = TimeSpan.FromSeconds(2);

    public ObservableCollection<PageFaultAttributionRow> PageFaultAttributionRows { get; } = new();

    private bool _isScanningPageFaults;
    public bool IsScanningPageFaults { get => _isScanningPageFaults; private set => SetProperty(ref _isScanningPageFaults, value); }

    private string? _pageFaultScanStatusText;
    public string? PageFaultScanStatusText { get => _pageFaultScanStatusText; private set => SetProperty(ref _pageFaultScanStatusText, value); }

    private string? _pageFaultTraceFilePath;
    public string? PageFaultTraceFilePath { get => _pageFaultTraceFilePath; private set => SetProperty(ref _pageFaultTraceFilePath, value); }

    public AsyncRelayCommand ScanPageFaultsCommand { get; }

    // #435: RAMMap-style memory list purge actions - see MemoryListPurgeService's remarks for the
    // underlying NtSetSystemInformation calls. Every command confirms via a warning dialog first
    // (the same MessageBox.Show(..., YesNo, Warning) pattern ProcessesViewModel.EndSelected uses
    // for ending a process) since these trade a temporary free-RAM gain for slower reads/rebuilding
    // afterwards - a real tradeoff, not a pure win, so it's never a one-click action.
    public AsyncRelayCommand PurgeStandbyListCommand { get; }
    public AsyncRelayCommand PurgeLowPriorityStandbyListCommand { get; }
    public AsyncRelayCommand FlushModifiedListCommand { get; }
    public AsyncRelayCommand EmptyWorkingSetsCommand { get; }

    private bool _isPurgingMemoryList;
    public bool IsPurgingMemoryList { get => _isPurgingMemoryList; private set => SetProperty(ref _isPurgingMemoryList, value); }

    private string? _memoryPurgeStatusText;
    public string? MemoryPurgeStatusText { get => _memoryPurgeStatusText; private set => SetProperty(ref _memoryPurgeStatusText, value); }

    // #437: metafile/system-cache runaway flag - a small rolling window (not a full history/chart)
    // is enough to tell "trending up" from "fluctuating around a stable baseline".
    private const double SystemCacheRunawayRamSharePercent = 40.0;
    private const double SystemCacheRunawayGrowthGb = 0.5;
    private const int SystemCacheRunawayWindowSamples = 20;
    private readonly Queue<double> _systemCacheSamples = new();

    private bool _isSystemCacheRunawayFlagged;
    public bool IsSystemCacheRunawayFlagged { get => _isSystemCacheRunawayFlagged; private set => SetProperty(ref _isSystemCacheRunawayFlagged, value); }

    // #438: MemCompression process working set as a compressed-store-size proxy - see
    // RefreshCompressionStats. Replaces the Memory tab's former flat explanatory note with real
    // numbers when the process is present, and falls back to that same note text otherwise.
    private double? _compressedStoreGb;
    public double? CompressedStoreGb { get => _compressedStoreGb; private set => SetProperty(ref _compressedStoreGb, value); }

    public bool HasCompressedStoreData => CompressedStoreGb.HasValue;

    // #452: quick in-app memory pattern test - allocates PatternTestSizeMb of unmanaged RAM and
    // runs a walking-pattern write/verify pass over it (MemoryPatternTestService). Explicitly a
    // far weaker check than a boot-time tool (#448's launcher): it can only touch pageable
    // user-mode memory the OS hands this one process, so it never runs unattended/automatically -
    // same confirm-first pattern as the #435 purge actions above, since a multi-gigabyte
    // allocation can itself put real memory pressure on the system while it runs.
    private CancellationTokenSource? _patternTestCts;

    private double _patternTestSizeMb = 512;
    public double PatternTestSizeMb { get => _patternTestSizeMb; set => SetProperty(ref _patternTestSizeMb, Math.Clamp(value, 16, 65536)); }

    private bool _isPatternTestRunning;
    public bool IsPatternTestRunning { get => _isPatternTestRunning; private set => SetProperty(ref _isPatternTestRunning, value); }

    private double _patternTestProgressPercent;
    public double PatternTestProgressPercent { get => _patternTestProgressPercent; private set => SetProperty(ref _patternTestProgressPercent, value); }

    private string? _patternTestStatusText;
    public string? PatternTestStatusText { get => _patternTestStatusText; private set => SetProperty(ref _patternTestStatusText, value); }

    private bool _patternTestFailed;
    public bool PatternTestFailed { get => _patternTestFailed; private set => SetProperty(ref _patternTestFailed, value); }

    public AsyncRelayCommand StartPatternTestCommand { get; }
    public RelayCommand AbortPatternTestCommand { get; }

    public MemoryViewModel(PerformanceViewModel performance, ProcessesViewModel processes, LeakWatchViewModel leakWatch, ProcessHistoryService processHistory)
    {
        Performance = performance;
        _processes = processes;
        LeakWatch = leakWatch;
        _processHistory = processHistory;

        _topMemoryView = new CollectionViewSource { Source = processes.Processes }.View;
        ApplyTopMemorySort();

        // #429: is the persisted peak from this same boot session, or a stale one from before the
        // machine's last reboot? Compared once here at startup; RefreshHighWaterMark keeps the
        // persisted boot time fresh on every save afterwards.
        var approxBootTimeUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var loadedHighWater = MemoryHighWaterService.Load();
        _highWater = loadedHighWater.LastKnownBootTimeUtc != default
            && Math.Abs((loadedHighWater.LastKnownBootTimeUtc - approxBootTimeUtc).TotalMinutes) <= BootTimeTolerance.TotalMinutes
                ? loadedHighWater
                : new MemoryHighWaterSettings { LastKnownBootTimeUtc = approxBootTimeUtc };
        PeakCommittedGb = _highWater.PeakCommittedGb;
        PeakCommitLimitGbAtPeak = _highWater.CommitLimitGbAtPeak;
        _peakTimestampUtc = _highWater.PeakTimestampUtc;

        Performance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PerformanceViewModel.Uptime)) OnPropertyChanged(nameof(SystemUptimeText));
            // #423: ModifiedGb is set on every Performance tick (RefreshCoreAsync), so this is a
            // convenient "once per tick" hook to recompute the reconciliation below - same trick
            // SystemUptimeText's own subscription uses, just against a different always-updated
            // property. #428/#429/#432/#437/#438 reuse the same hook for their own per-tick
            // recomputation, none of which involve any new I/O.
            if (e.PropertyName == nameof(PerformanceViewModel.ModifiedGb))
            {
                RefreshReconciliation();
                RefreshCommitProjection();
                RefreshHighWaterMark();
                RefreshThrashingState();
                RefreshSystemCacheRunaway();
                RefreshCompressionStats();
            }
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

        // #430/#431
        RefreshPageFileConfigCommand = new AsyncRelayCommand(RefreshPageFileConfigAsync, () => !IsLoadingPageFileConfig);

        // #433
        ScanPageFaultsCommand = new AsyncRelayCommand(ScanPageFaultsAsync, () => !IsScanningPageFaults);

        // #435
        PurgeStandbyListCommand = new AsyncRelayCommand(() => RunMemoryPurgeAsync(
            "Empty standby list",
            "This frees the reclaimable standby (file cache) list now, showing more “available” RAM immediately - but every file Windows had cached there will need to be re-read from disk the next time it's opened, which will be slower until the cache rebuilds.\n\nContinue?",
            MemoryListPurgeService.PurgeStandbyList), () => !IsPurgingMemoryList);
        PurgeLowPriorityStandbyListCommand = new AsyncRelayCommand(() => RunMemoryPurgeAsync(
            "Empty low-priority standby list",
            "This frees only the lowest-priority tier of the standby list (the portion Windows itself already reclaims first under pressure) - a smaller, lower-impact version of “Empty standby list.”\n\nContinue?",
            MemoryListPurgeService.PurgeLowPriorityStandbyList), () => !IsPurgingMemoryList);
        FlushModifiedListCommand = new AsyncRelayCommand(() => RunMemoryPurgeAsync(
            "Flush modified page list",
            "This forces Windows to write out every dirty page currently waiting in the modified list right now, instead of at its own pace - a burst of disk write activity, then a smaller modified list.\n\nContinue?",
            MemoryListPurgeService.FlushModifiedList), () => !IsPurgingMemoryList);
        EmptyWorkingSetsCommand = new AsyncRelayCommand(() => RunMemoryPurgeAsync(
            "Empty working sets",
            "This trims every running process's working set down to the minimum right now - reported per-process RAM usage will drop immediately, but every process will take a burst of page faults refilling its working set as it keeps running, which can make things feel briefly sluggish.\n\nContinue?",
            MemoryListPurgeService.EmptyWorkingSets), () => !IsPurgingMemoryList);

        // #452
        StartPatternTestCommand = new AsyncRelayCommand(RunPatternTestAsync, () => !IsPatternTestRunning);
        AbortPatternTestCommand = new RelayCommand(_ => _patternTestCts?.Cancel(), _ => IsPatternTestRunning);

        _leakGrowthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LeakGrowthRefreshInterval };
        _leakGrowthTimer.Tick += (_, _) => RefreshLeakGrowthProjections();
        _leakGrowthTimer.Start();
        RefreshLeakGrowthProjections();
        RefreshReconciliation();
        RefreshCompressionStats();
        _ = RefreshPageFileConfigAsync();
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

    /// <summary>#428: least-squares slope/R² of Performance.CommittedHistory (in GB, over elapsed
    /// hours at the current poll interval) extrapolated toward Performance.CommitLimitGb - same
    /// standard regression formula as ProcessHistoryService.Regress, just over this tab's own
    /// fixed-size history buffer instead of a per-process sample list. Leading zero entries (the
    /// buffer's initial fill before the app has been running long enough to overwrite them all)
    /// are excluded so early-session noise can't fake a dramatic slope.</summary>
    private void RefreshCommitProjection()
    {
        var samples = Performance.CommittedHistory
            .Select((value, index) => (Value: value, Index: index))
            .Where(t => t.Value > 0)
            .ToList();

        if (samples.Count < 10 || Performance.CommitLimitGb <= 0)
        {
            MinutesToCommitLimitExhaustion = null;
            return;
        }

        double intervalHours = Performance.PollIntervalSeconds / 3600.0;
        double n = samples.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0, sumYY = 0;
        foreach (var (value, index) in samples)
        {
            double x = index * intervalHours;
            double y = value / 1024.0 / 1024.0 / 1024.0; // bytes -> GB
            sumX += x; sumY += y; sumXY += x * y; sumXX += x * x; sumYY += y * y;
        }

        double denomSlope = n * sumXX - sumX * sumX;
        if (denomSlope <= 1e-9)
        {
            MinutesToCommitLimitExhaustion = null;
            return;
        }

        double slopeGbPerHour = (n * sumXY - sumX * sumY) / denomSlope;
        double denomCorr = Math.Sqrt(Math.Max(0, (n * sumXX - sumX * sumX) * (n * sumYY - sumY * sumY)));
        double r2 = denomCorr <= 1e-9
            ? (Math.Abs(slopeGbPerHour) < 1e-9 ? 1.0 : 0.0)
            : Math.Pow((n * sumXY - sumX * sumY) / denomCorr, 2);

        double remainingGb = Performance.CommitLimitGb - Performance.CommittedGb;

        MinutesToCommitLimitExhaustion = slopeGbPerHour >= MinCommitGrowthGbPerHourToProject
            && r2 >= MinCommitTrendRSquared
            && remainingGb > 0
                ? Math.Round(remainingGb / slopeGbPerHour * 60.0, 1)
                : null;

        OnPropertyChanged(nameof(ShowCommitLimitProjection));
        OnPropertyChanged(nameof(CommitLimitProjectionText));
    }

    /// <summary>#429: bumps and persists the peak-committed high-water mark whenever the current
    /// tick's committed figure exceeds it. Persisting on every increase (rather than throttled)
    /// is fine - CommittedGb strictly increasing tick-over-tick is the uncommon case in practice,
    /// not something that would otherwise hammer disk every second.</summary>
    private void RefreshHighWaterMark()
    {
        if (Performance.CommittedGb <= PeakCommittedGb) return;

        PeakCommittedGb = Performance.CommittedGb;
        PeakCommitLimitGbAtPeak = Performance.CommitLimitGb;
        _peakTimestampUtc = DateTime.UtcNow;
        OnPropertyChanged(nameof(PeakCommittedText));

        _highWater.PeakCommittedGb = PeakCommittedGb;
        _highWater.CommitLimitGbAtPeak = PeakCommitLimitGbAtPeak;
        _highWater.PeakTimestampUtc = _peakTimestampUtc;
        _highWater.LastKnownBootTimeUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        MemoryHighWaterService.Save(_highWater);

        RefreshPageFileAdvisories(); // #431's "peak exceeded RAM with no page file" flag depends on this.
    }

    /// <summary>#430: reads Win32_PageFileSetting/Win32_PageFileUsage off the UI thread - a single,
    /// cheap WMI query (page file config only changes on a reboot), run once at startup and again
    /// on demand via RefreshPageFileConfigCommand rather than on any tick.</summary>
    private async Task RefreshPageFileConfigAsync()
    {
        IsLoadingPageFileConfig = true;
        try
        {
            var snapshot = await Task.Run(PageFileConfigurationService.Query);

            PageFileConfigs.Clear();
            foreach (var f in snapshot.Files) PageFileConfigs.Add(f);
            PageFileAutomaticallyManaged = snapshot.IsAutomaticallyManaged;
            OnPropertyChanged(nameof(PageFileManagementText));

            PageFileConfigStatusText = PageFileConfigs.Count == 0
                ? "No page file is currently configured on this system."
                : $"{PageFileConfigs.Count:N0} page file(s) configured.";

            RefreshPageFileAdvisories();
        }
        finally
        {
            IsLoadingPageFileConfig = false;
        }
    }

    /// <summary>#431: "quick flag, not a verdict" page file sizing advisories - a fixed page file
    /// capped below its own recorded peak usage (the commit limit genuinely couldn't grow past
    /// that ceiling at least once), or no page file at all on a machine whose peak committed
    /// memory this session already exceeded installed RAM (the commit limit is capped at physical
    /// RAM without one). Recomputed whenever either input changes - PageFileConfigs (on load) or
    /// PeakCommittedGb (on every new high-water mark).</summary>
    private void RefreshPageFileAdvisories()
    {
        var advisories = new List<string>();

        foreach (var f in PageFileConfigs.Where(f => f.IsCappedBelowPeakUsage))
        {
            advisories.Add($"{f.Volume} page file: fixed maximum ({f.MaximumSizeMb:N0} MB) is smaller than its recorded peak usage ({f.PeakUsageMb:N0} MB) - the commit limit couldn't grow past that cap when it mattered most.");
        }

        if (PageFileConfigs.Count == 0 && PeakCommittedGb > 0 && Performance.RamTotalGb > 0 && PeakCommittedGb > Performance.RamTotalGb)
        {
            advisories.Add($"No page file is configured, and this session's peak committed memory ({PeakCommittedGb:0.0} GB) already exceeded installed RAM ({Performance.RamTotalGb:0.0} GB) - without a page file, the commit limit can't exceed physical RAM.");
        }

        PageFileAdvisories.Clear();
        foreach (var a in advisories) PageFileAdvisories.Add(a);
    }

    /// <summary>#432: combines the actual page-file I/O rate, how full the page file is, and its
    /// volume's own disk queue length into one thrashing flag with a "how long has this been going
    /// on" duration - see the class-level remarks by ThrashingPagingActivityThreshold for why all
    /// three matter together rather than any one alone. An unknown (null) volume queue length
    /// doesn't rule thrashing out - it just means that one signal can't confirm or deny it.</summary>
    private void RefreshThrashingState()
    {
        double pagingActivity = Performance.PagesInputPerSec + Performance.PagesOutputPerSec;
        bool queueBacked = Performance.PageFileVolumeQueueLength is not { } q || q >= ThrashingQueueLengthThreshold;
        bool candidate = pagingActivity >= ThrashingPagingActivityThreshold
            && Performance.PageFilePercent >= ThrashingPageFilePercentThreshold
            && queueBacked;

        if (candidate)
        {
            _thrashingStartUtc ??= DateTime.UtcNow;
            var elapsed = DateTime.UtcNow - _thrashingStartUtc.Value;
            IsThrashing = true;
            ThrashingStatusText = $"Thrashing for {FormatDuration(elapsed)} - heavy page-file I/O with the page file nearly full"
                + (Performance.PageFileVolumeQueueLength is { } ql ? $" and its disk queue backed up ({ql:0.0})." : ".");
        }
        else
        {
            _thrashingStartUtc = null;
            IsThrashing = false;
            ThrashingStatusText = "Not thrashing";
        }
    }

    private static string FormatDuration(TimeSpan span)
        => span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{(int)span.TotalMinutes}m {span.Seconds}s";

    /// <summary>#433: repeated Process\Page Faults/sec sampling over a 30-second window (via
    /// ProcessPerfCounterService, already used elsewhere in this app for the same category/rate-
    /// counter shape), ranked by average rate - the in-app half of "Find what's paging". Windows
    /// exposes no per-process *hard*-fault-only counter, so this counts all faults (soft+hard);
    /// it's useful as "who's driving paging activity right now" specifically when the system-wide
    /// hard-fault rate elsewhere on this tab is elevated, not as a literal hard-fault count. Runs
    /// PageFaultTraceService's best-effort raw kernel-trace capture alongside it as a bonus - see
    /// that service's remarks for why the .etl file isn't parsed in-app.</summary>
    private async Task ScanPageFaultsAsync()
    {
        IsScanningPageFaults = true;
        PageFaultTraceFilePath = null;
        PageFaultAttributionRows.Clear();
        PageFaultScanStatusText = "Sampling per-process page fault rate for 30 seconds (also attempting a raw kernel trace capture)...";

        var traceStart = await PageFaultTraceService.StartAsync();

        using var counter = new ProcessPerfCounterService("Process", "Page Faults/sec", isRate: true);
        var totals = new Dictionary<int, (double Sum, double Peak, int Samples)>();
        var namesByPid = new Dictionary<int, string>();

        try
        {
            // Prime once - a rate counter's very first read is always a meaningless 0.
            await Task.Run(() => counter.ReadByPid());

            var deadline = DateTime.UtcNow + PageFaultScanWindow;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PageFaultSampleInterval);

                var reading = await Task.Run(() => counter.ReadByPid());
                foreach (var (pid, rate) in reading)
                {
                    var existing = totals.TryGetValue(pid, out var t) ? t : (0.0, 0.0, 0);
                    totals[pid] = (existing.Item1 + rate, Math.Max(existing.Item2, rate), existing.Item3 + 1);
                }

                // Snapshot names while each process is still alive - one that exits mid-window
                // keeps whatever name was last seen for it rather than losing its row entirely.
                foreach (var p in _processes.Processes)
                    namesByPid[p.Pid] = p.Name;
            }
        }
        finally
        {
            string traceMessage;
            if (traceStart.Success)
            {
                var stopResult = await PageFaultTraceService.StopAsync();
                PageFaultTraceFilePath = stopResult.Success ? traceStart.FilePath : null;
                traceMessage = stopResult.Success
                    ? $" Raw kernel trace saved to {traceStart.FilePath} - open it in Windows Performance Analyzer for full detail."
                    : $" Kernel trace capture couldn't be finalized: {stopResult.Message}";
            }
            else
            {
                traceMessage = $" Kernel trace capture wasn't available: {traceStart.Message}";
            }

            var rows = totals
                .Select(kv => new PageFaultAttributionRow
                {
                    Pid = kv.Key,
                    ProcessName = namesByPid.TryGetValue(kv.Key, out var n) ? n : $"(pid {kv.Key})",
                    AvgPageFaultsPerSec = Math.Round(kv.Value.Sum / Math.Max(1, kv.Value.Samples), 1),
                    PeakPageFaultsPerSec = Math.Round(kv.Value.Peak, 1),
                })
                .OrderByDescending(r => r.AvgPageFaultsPerSec)
                .Take(15)
                .ToList();

            foreach (var r in rows) PageFaultAttributionRows.Add(r);

            PageFaultScanStatusText = (rows.Count == 0
                ? "No measurable per-process page fault activity during the window."
                : $"Top {rows.Count:N0} processes by page-fault rate over the last 30 seconds - counts all faults (soft+hard); Windows exposes no per-process hard-fault-only counter.")
                + traceMessage;

            IsScanningPageFaults = false;
        }
    }

    /// <summary>#435: confirm-then-run helper shared by every memory-list purge command - see
    /// those commands' own warning text for what each action actually trades away.</summary>
    private async Task RunMemoryPurgeAsync(string title, string warning, Func<(bool Success, string? Error)> action)
    {
        var confirm = MessageBox.Show(warning, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        IsPurgingMemoryList = true;
        MemoryPurgeStatusText = $"{title}: in progress...";
        try
        {
            var (success, error) = await Task.Run(action);
            MemoryPurgeStatusText = success
                ? $"{title}: done."
                : $"{title} failed: {error}";
        }
        finally
        {
            IsPurgingMemoryList = false;
        }
    }

    /// <summary>#452: confirms (a multi-gigabyte allocation is real memory pressure while it
    /// runs, the same tradeoff #435's purge actions warn about), then allocates PatternTestSizeMb
    /// and runs MemoryPatternTestService's walking-pattern write/verify pass, reporting progress
    /// back via a captured-UI-thread Progress&lt;T&gt; so PatternTestProgressPercent/StatusText
    /// update live without any manual Dispatcher.Invoke plumbing.</summary>
    private async Task RunPatternTestAsync()
    {
        long sizeBytes = (long)(PatternTestSizeMb * 1024.0 * 1024.0);
        var confirm = MessageBox.Show(
            $"This allocates {PatternTestSizeMb:0} MB of RAM and writes/verifies a test pattern across it - a real, temporary increase in memory pressure while it runs (and slower than a boot-time test, since it can only touch pageable user memory the OS is willing to hand this one process; far weaker than Windows Memory Diagnostic).\n\nContinue?",
            "Run memory pattern test", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _patternTestCts = new CancellationTokenSource();
        IsPatternTestRunning = true;
        PatternTestFailed = false;
        PatternTestProgressPercent = 0;
        PatternTestStatusText = "Starting…";
        AbortPatternTestCommand.RaiseCanExecuteChanged();

        var progress = new Progress<MemoryPatternTestProgress>(p =>
        {
            PatternTestProgressPercent = p.PercentComplete;
            PatternTestStatusText = p.StatusText;
        });

        try
        {
            var result = await MemoryPatternTestService.RunAsync(sizeBytes, progress, _patternTestCts.Token);
            PatternTestFailed = !result.Passed;
            PatternTestProgressPercent = result.Completed ? 100 : PatternTestProgressPercent;
            PatternTestStatusText = result.StatusText;
        }
        catch (Exception ex)
        {
            PatternTestFailed = true;
            PatternTestStatusText = $"Test failed: {ex.Message}";
        }
        finally
        {
            IsPatternTestRunning = false;
            _patternTestCts?.Dispose();
            _patternTestCts = null;
            AbortPatternTestCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>#437: flags sustained growth of the file-system-cache component of Cache Bytes
    /// past a share of physical RAM - a small rolling window (not a full chart) is enough to tell
    /// "trending up over roughly the last couple minutes" from "fluctuating around a stable
    /// baseline". Cache is reclaimable under pressure the same as the standby list is, so this is
    /// informational, not a leak signal on its own - the UI carries that caveat explicitly.</summary>
    private void RefreshSystemCacheRunaway()
    {
        double current = Performance.SystemCacheResidentGb;
        _systemCacheSamples.Enqueue(current);
        while (_systemCacheSamples.Count > SystemCacheRunawayWindowSamples) _systemCacheSamples.Dequeue();

        double ramSharePercent = Performance.RamTotalGb <= 0 ? 0 : current / Performance.RamTotalGb * 100.0;
        bool aboveShare = ramSharePercent >= SystemCacheRunawayRamSharePercent;
        bool grewAcrossWindow = _systemCacheSamples.Count >= SystemCacheRunawayWindowSamples
            && current - _systemCacheSamples.Peek() >= SystemCacheRunawayGrowthGb;

        IsSystemCacheRunawayFlagged = aboveShare && grewAcrossWindow;
    }

    /// <summary>#438: MemCompression's own working set as a compressed-store-size proxy - the
    /// closest honest number Windows offers for "how much is compressed" (there's no documented
    /// API for the true compressed-store byte count). Looked up fresh each tick via a targeted
    /// Process.GetProcessesByName call rather than the Processes tab's own polling collection, so
    /// this stays correct even if that collection ever filters system processes differently.
    /// Null (not zero) when the process isn't present - compression can be disabled by policy, or
    /// absent entirely on older Windows versions - so the Memory tab falls back to its explanatory
    /// note rather than showing a misleading 0 GB.</summary>
    private void RefreshCompressionStats()
    {
        try
        {
            var procs = Process.GetProcessesByName("MemCompression");
            try
            {
                if (procs.Length == 0)
                {
                    CompressedStoreGb = null;
                    return;
                }

                long total = 0;
                foreach (var p in procs)
                {
                    try { total += p.WorkingSet64; }
                    catch { /* exited mid-read - skip it */ }
                }
                CompressedStoreGb = total / 1024.0 / 1024.0 / 1024.0;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        catch
        {
            CompressedStoreGb = null;
        }
        finally
        {
            OnPropertyChanged(nameof(HasCompressedStoreData));
        }
    }

    public void Dispose() => _leakGrowthTimer.Stop();
}
