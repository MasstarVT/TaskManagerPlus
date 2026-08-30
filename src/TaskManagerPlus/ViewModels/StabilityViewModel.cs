using System.Collections.ObjectModel;
using System.Windows;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>#137: one checkable source filter chip shown above the unified incident timeline -
/// toggling it re-filters the already-built merged list (StabilityViewModel._allTimelineEntries)
/// rather than re-querying anything. Lives in ViewModels/ (not Models/, unlike every other type
/// this file binds) since it's stateful UI-reactive glue, not a plain data row.</summary>
public sealed class TimelineFilterChip : ObservableObject
{
    private readonly Action _onChanged;
    public TimelineSource Source { get; }
    public string Label { get; }

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (SetProperty(ref _isEnabled, value)) _onChanged(); }
    }

    public TimelineFilterChip(TimelineSource source, string label, Action onChanged)
    {
        Source = source;
        Label = label;
        _onChanged = onChanged;
    }
}

/// <summary>
/// Backs the Stability tab. Queried on demand (an initial load plus a manual Refresh command),
/// not on a live timer - unlike a PerformanceCounter read, an event log query walks potentially
/// thousands of log records and isn't cheap enough to repeat every second/few-seconds the way
/// every other tab's sampler does, the same "genuinely expensive, on-demand" tradeoff
/// SystemSpecsViewModel already makes for its WMI queries.
/// </summary>
public sealed class StabilityViewModel : ObservableObject, IDisposable
{
    private readonly EventLogService _service = new();

    // #633: needed for the inferred non-stock-Vcore evidence input to the combined
    // undervolt/overclock instability flag below - see EnergyThermalsViewModel.NonStockVcoreLooksLikely.
    private readonly EnergyThermalsViewModel _energyThermals;

    // #122: the same knowledge base the Events tab uses (#117) - a second, independent instance
    // rather than a shared reference, matching this app's existing "each ViewModel composes its own
    // Services/* instances directly" convention (no DI container - see CLAUDE.md).
    private readonly EventKnowledgeBaseService _kb = new();

    // #128/#130: the same anomaly-detection service the Events tab's deep-scan panel uses - another
    // independent instance (see _kb's remarks above for why), used here purely for its stateless
    // ComputeFirstOccurrences/ComputeDensityHeatmap math over RecentEvents below, no new event-log
    // query of its own (the EventLogExplorerService it's constructed with is only needed by the
    // Events tab's ReadWindow/ScanProviderChurn/FindBootMarkers methods, none of which this tab calls).
    private readonly EventAnomalyDetectionService _anomaly = new(new EventLogExplorerService());

    // #137-145: cross-channel timeline correlation - its own EventTimelineService instance (same
    // "each ViewModel composes its own Services/* instances directly" convention as _kb/_anomaly
    // above), plus a dedicated EventLogExplorerService for #138's crash-window drill-down (which
    // needs EventLogExplorerService.ReadMultiChannel/BuildStructuredQuery directly, the same pair
    // EventsViewModel.ShowAroundTimeAsync already uses for its own +/-5-minute lookup).
    private readonly EventTimelineService _timeline = new(new EventLogExplorerService());
    private readonly EventLogExplorerService _drillDownExplorer = new();

    // #161-167: Windows Error Reporting - its own EventLogExplorerService instance (same "each
    // ViewModel composes its own Services/* instances directly" convention as _kb/_anomaly/_timeline
    // above), needed for #163's "Application Error" 1000 combine and #164's "Application Hang" 1002 read.
    private readonly WerReportService _wer = new(new EventLogExplorerService());

    // #184-190: kernel/storage/driver event-family cards - its own EventLogExplorerService
    // instance (same "each ViewModel composes its own Services/* instances directly" convention as
    // _kb/_anomaly/_timeline/_wer above).
    private readonly KernelEventFamilyService _kernelFamily = new(new EventLogExplorerService());

    // #195/#196: Perflib counter-corruption card + the assorted subsystem error family rollup -
    // same "own EventLogExplorerService instance" convention as _kernelFamily above.
    private readonly SubsystemErrorFamilyService _subsystemFamily = new(new EventLogExplorerService());

    // #197-199: channel health / retention / log-clearing - only #199's DetectLogClearEvents is
    // used from this ViewModel (the #197/#198 channel-health detail lives on the Events tab, which
    // already has the channel tree this extends); same "own EventLogExplorerService instance"
    // convention as the services above.
    private readonly EventLogHealthService _logHealth = new(new EventLogExplorerService());

    /// <summary>The last WER report scan's results, stashed so RefreshTimelineExtrasAsync's
    /// BuildTimeline call (#161) can fold them into the unified timeline without a second scan.</summary>
    private List<WerReportInfo> _lastWerReports = new();

    /// <summary>#141: fires once per refresh (success or failure) - MainViewModel wires this to push
    /// fresh crash/error markers into PerformanceViewModel's charts, reusing this tab's own event
    /// data rather than adding a second poll.</summary>
    public event Action? Refreshed;

    public ObservableCollection<StabilityEvent> RecentEvents { get; } = new();
    public ObservableCollection<MinidumpInfo> Minidumps { get; } = new();

    // Round 14, items 13-19: binary-parsed dump rows (DUMP_HEADER64 / MINIDUMP format), one per
    // file under %SystemRoot%\Minidump - independent of, and a richer cross-check against, the
    // event-log-correlated Minidumps collection above. See MinidumpParserService.
    public ObservableCollection<DumpRowViewModel> DumpRows { get; } = new();

    // Round 14, item 19: third-party drivers present in every parseable dump's module list.
    public ObservableCollection<CommonDriverRow> CommonDrivers { get; } = new();

    // Round 14, items 21/22: live kernel events (watchdog dumps with no bluescreen).
    public ObservableCollection<LiveKernelReportInfo> LiveKernelReports { get; } = new();

    // Round 14, item 20: %SystemRoot%\MEMORY.DMP card.
    private MemoryDumpInfo? _memoryDump;
    public MemoryDumpInfo? MemoryDump { get => _memoryDump; private set => SetProperty(ref _memoryDump, value); }

    private DumpRowViewModel? _memoryDumpRow;
    public DumpRowViewModel? MemoryDumpRow { get => _memoryDumpRow; private set => SetProperty(ref _memoryDumpRow, value); }

    private string _memoryDumpStatusText = string.Empty;
    public string MemoryDumpStatusText { get => _memoryDumpStatusText; private set => SetProperty(ref _memoryDumpStatusText, value); }

    public RelayCommand OpenMemoryDumpFolderCommand { get; }
    public RelayCommand CopyMemoryDumpCommand { get; }
    public RelayCommand DeleteMemoryDumpCommand { get; }

    // Round 14, item 23: cdb.exe/windbg.exe availability, shared by every DumpRowViewModel.
    private DebuggerAvailability _debugger = new();
    public DebuggerAvailability Debugger { get => _debugger; private set => SetProperty(ref _debugger, value); }

    // Round 14, item 26: Minidump folder housekeeping.
    private MinidumpHousekeepingInfo? _housekeeping;
    public MinidumpHousekeepingInfo? Housekeeping { get => _housekeeping; private set => SetProperty(ref _housekeeping, value); }

    private int _deleteOlderThanDays = 30;
    public int DeleteOlderThanDays { get => _deleteOlderThanDays; set => SetProperty(ref _deleteOlderThanDays, value); }

    private int _minidumpsCountInput = 50;
    public int MinidumpsCountInput { get => _minidumpsCountInput; set => SetProperty(ref _minidumpsCountInput, value); }

    private string _housekeepingStatusText = string.Empty;
    public string HousekeepingStatusText { get => _housekeepingStatusText; private set => SetProperty(ref _housekeepingStatusText, value); }

    public RelayCommand DeleteOldDumpsCommand { get; }
    public RelayCommand SaveMinidumpsCountCommand { get; }

    // Round 14, item 25: crash-analysis settings (symbol cache folder + symbol path apply/test) -
    // lives on this view model since it's the natural owner of the settings-drawer's "Crash
    // analysis settings" section, the same way LoggingViewModel already owns its own settings
    // section reached from SettingsPanel.xaml via DataContext.Logging.
    private readonly CrashAnalysisSettings _crashAnalysisSettings = CrashAnalysisSettingsService.Load();

    public string SymbolCacheFolder
    {
        get => _crashAnalysisSettings.SymbolCacheFolder;
        set
        {
            if (_crashAnalysisSettings.SymbolCacheFolder == value) return;
            _crashAnalysisSettings.SymbolCacheFolder = value;
            CrashAnalysisSettingsService.Save(_crashAnalysisSettings);
            OnPropertyChanged();
        }
    }

    public string CurrentSymbolPathText => SymbolServerService.ReadCurrentSymbolPath() ?? "(not set)";

    private string _symbolServerStatusText = string.Empty;
    public string SymbolServerStatusText { get => _symbolServerStatusText; private set => SetProperty(ref _symbolServerStatusText, value); }

    public RelayCommand ApplySymbolPathCommand { get; }
    public AsyncRelayCommand TestSymbolServerCommand { get; }

    // Round 14, item 27: new-dump watcher - a background FileSystemWatcher, badge + tray toast
    // on a new file, no polling.
    private readonly NewDumpWatcherService _dumpWatcher = new();

    private bool _hasNewDumpAlert;
    public bool HasNewDumpAlert { get => _hasNewDumpAlert; private set => SetProperty(ref _hasNewDumpAlert, value); }

    private string? _newDumpAlertText;
    public string? NewDumpAlertText { get => _newDumpAlertText; private set => SetProperty(ref _newDumpAlertText, value); }

    public RelayCommand DismissNewDumpAlertCommand { get; }

    /// <summary>Fired (already on the UI thread) when item 27's watcher sees a new dump - the
    /// tuple is (title, body) for whatever toast mechanism the host window uses. MainWindow
    /// subscribes and forwards to the same NotifyIcon balloon tip #85 already set up, per this
    /// round's instruction to reuse the existing tray/toast mechanism rather than inventing a
    /// new one.</summary>
    public event Action<string, string>? ShowTrayToastRequested;

    /// <summary>#122: "Known-bad IDs present on this PC" - which KB-flagged serious event IDs
    /// actually showed up in the lookback window, with count/last-seen/next-step, ordered by
    /// re-ranked severity (worst first) then by how often they occurred - see
    /// EventLogService.ScanForKnownBadIds and BuildKnownBadIdScorecard.</summary>
    public ObservableCollection<KnownBadIdScorecardRow> KnownBadIdScorecard { get; } = new();

    // ---- #137: unified incident timeline ----

    /// <summary>The full merged set built by the last refresh, before the filter chips below are
    /// applied - kept so toggling a chip is a pure client-side re-filter, not a re-query.</summary>
    private List<TimelineEntry> _allTimelineEntries = new();

    public ObservableCollection<TimelineEntry> Timeline { get; } = new();

    /// <summary>One chip per source actually wired into BuildTimeline today - see
    /// TimelineSource's remarks for which sources exist yet.</summary>
    public ObservableCollection<TimelineFilterChip> TimelineFilters { get; } = new();

    // ---- #138: crash-window drill-down ----
    public ObservableCollection<EventRecordRow> CrashWindowResults { get; } = new();

    private bool _isCrashWindowLoading;
    public bool IsCrashWindowLoading { get => _isCrashWindowLoading; private set => SetProperty(ref _isCrashWindowLoading, value); }

    private string? _crashWindowStatusText;
    public string? CrashWindowStatusText { get => _crashWindowStatusText; private set => SetProperty(ref _crashWindowStatusText, value); }

    public RelayCommand DrillDownCommand { get; }

    // ---- #139: attribute a crash to the change that preceded it ----
    public ObservableCollection<PreCrashChange> ChangesBeforeCrash { get; } = new();

    private string? _changeAttributionStatusText;
    public string? ChangeAttributionStatusText { get => _changeAttributionStatusText; private set => SetProperty(ref _changeAttributionStatusText, value); }

    public RelayCommand FindChangesBeforeCrashCommand { get; }

    // ---- #142: sleep/resume incident chain ----
    public ObservableCollection<SleepResumeCycle> SleepResumeCycles { get; } = new();

    // ---- #143: "who rebooted this PC" ----
    public ObservableCollection<RebootAttribution> RebootAttributions { get; } = new();

    // ---- #144: uptime and session ledger ----
    public ObservableCollection<BootSessionRow> BootLedger { get; } = new();

    // Round 10, #66: repeated crashes grouped by faulting module, most frequent first - see
    // FaultingModuleSummary's remarks. Pure derived aggregation over RecentEvents, no new query.
    public ObservableCollection<FaultingModuleSummary> CrashesByModule { get; } = new();

    // Round 13, item 4: every Kernel-Power 41 occurrence in the lookback window, classified per
    // item 3 - see EventLogService.ReadUnexpectedShutdowns/ClassifyPowerEvent.
    public ObservableCollection<UnexpectedShutdownRecord> UnexpectedShutdowns { get; } = new();

    // Round 13, items 5/6: merged shutdown/restart/boot timeline - see
    // EventLogService.ReadShutdownTimeline.
    public ObservableCollection<ShutdownTimelineEntry> ShutdownTimeline { get; } = new();

    // Round 13, item 7: volmgr 161/162 "dump creation failed" events.
    public ObservableCollection<DumpFailureEvent> DumpFailures { get; } = new();

    // Round 13, items 9/10: WHEA hardware-error events, plus a (Severity, Source) grouped summary -
    // the same "flat list -> grouped summary" shape CrashesByModule already uses.
    public ObservableCollection<WheaErrorEvent> WheaErrors { get; } = new();
    public ObservableCollection<WheaSummaryRow> WheaSummary { get; } = new();

    // ---------------------------------------------------------------------------------------
    // Round 16, items 38-49: WER (Windows Error Reporting) archive/queue scanning - a crash
    // record source entirely separate from the event log above, and not subject to its 30-day
    // rollover (item 48) - see WerReportService.
    // ---------------------------------------------------------------------------------------

    // Item 39: bucket-grouped crashes (excludes hangs) - the primary grouped view of the card.
    public ObservableCollection<WerBucketRowViewModel> WerCrashBuckets { get; } = new();

    // Item 46: AppHang_XProcB1/AppHangB1/AppHangTransient reports, split into their own flat
    // section rather than counted alongside real crashes anywhere on this card.
    public ObservableCollection<WerReportRowViewModel> WerHangReports { get; } = new();

    // Every WerReportRowViewModel currently shown (shared by WerCrashBuckets' Reports and
    // WerHangReports - a report can only be in one or the other, but this app also needs one flat
    // list to subscribe to for item 49's live selected-count and to unsubscribe from on refresh).
    private readonly List<WerReportRowViewModel> _allWerReportRows = new();

    private bool _showWerHangs;

    /// <summary>Item 46: toggles which of WerCrashBuckets/WerHangReports the card shows - two
    /// buttons rather than a literal inner TabControl, since Dark.xaml's TabControl style is a
    /// TMOG-style top icon+label strip applied to every TabControl in the app (implicit, not
    /// keyed), not a style meant for a small in-card switcher.</summary>
    public bool ShowWerHangs { get => _showWerHangs; set => SetProperty(ref _showWerHangs, value); }

    public RelayCommand ShowWerCrashesCommand { get; }
    public RelayCommand ShowWerHangsCommand { get; }

    /// <summary>Item 49: how many WER report rows are currently selected, across both the
    /// crash-bucket and hang views - for the later support-bundle export chunk (#100) to build on;
    /// this chunk only surfaces the count.</summary>
    public int SelectedWerReportCount => _allWerReportRows.Count(r => r.IsSelected);

    /// <summary>Item 49: same idea for the Dump analysis (binary parse) card's rows.</summary>
    public int SelectedDumpCount => DumpRows.Count(r => r.IsSelected);

    // Items 41/44: "is Windows even collecting crash data" status + plain-English consent/prompt
    // read-out - always shown; the warning strip below only appears when WerStatus.LooksDisabled.
    private WerCollectionStatus? _werStatus;
    public WerCollectionStatus? WerStatus { get => _werStatus; private set => SetProperty(ref _werStatus, value); }

    private string _werCollectionSummaryText = string.Empty;
    public string WerCollectionSummaryText { get => _werCollectionSummaryText; private set => SetProperty(ref _werCollectionSummaryText, value); }

    private string _werConsentSummaryText = string.Empty;
    public string WerConsentSummaryText { get => _werConsentSummaryText; private set => SetProperty(ref _werConsentSummaryText, value); }

    private string _werStatusActionText = string.Empty;
    public string WerStatusActionText { get => _werStatusActionText; private set => SetProperty(ref _werStatusActionText, value); }

    public RelayCommand EnableWerCommand { get; }

    // Item 43: queue/archive size + explicit, warned purge action.
    private WerQueueSizeInfo? _werQueueSize;
    public WerQueueSizeInfo? WerQueueSize { get => _werQueueSize; private set => SetProperty(ref _werQueueSize, value); }

    private string _werPurgeStatusText = string.Empty;
    public string WerPurgeStatusText { get => _werPurgeStatusText; private set => SetProperty(ref _werPurgeStatusText, value); }

    public RelayCommand PurgeWerReportsCommand { get; }

    // Item 42: LocalDumps "Capture settings" - global by default (blank target), or for one
    // named executable.
    private string _localDumpsTargetExe = string.Empty;
    public string LocalDumpsTargetExe { get => _localDumpsTargetExe; set => SetProperty(ref _localDumpsTargetExe, value); }

    private string _localDumpsFolder = @"%LOCALAPPDATA%\CrashDumps";
    public string LocalDumpsFolder { get => _localDumpsFolder; set => SetProperty(ref _localDumpsFolder, value); }

    private int _localDumpsCount = 10;
    public int LocalDumpsCount { get => _localDumpsCount; set => SetProperty(ref _localDumpsCount, value); }

    // 0 = Custom, 1 = Mini, 2 = Full - matches LocalDumpsConfig.DumpType and the Capture settings
    // ComboBox's item order 1:1, so the XAML can bind SelectedIndex directly with no converter.
    private int _localDumpsType = 1;
    public int LocalDumpsType { get => _localDumpsType; set => SetProperty(ref _localDumpsType, value); }

    private string _localDumpsStatusText = string.Empty;
    public string LocalDumpsStatusText { get => _localDumpsStatusText; private set => SetProperty(ref _localDumpsStatusText, value); }

    public RelayCommand LoadLocalDumpsConfigCommand { get; }
    public RelayCommand SaveLocalDumpsConfigCommand { get; }
    public RelayCommand ClearLocalDumpsConfigCommand { get; }

    // Item 48: WER-archive-derived long-horizon crash history - not capped at 30 days like the
    // Reliability History chart above, since these folders aren't subject to log rollover.
    private const int WerHistoryDays = 90;
    public ObservableCollection<double> WerHistoryCounts { get; } = new();
    private readonly ColumnSeries<double> _werHistoryColumns;
    public ISeries[] WerHistorySeries { get; }
    public Axis[] WerHistoryXAxes { get; }
    public Axis[] WerHistoryYAxes { get; }

    // ---------------------------------------------------------------------------------------
    // Round 17, items 50-63: application crash/hang forensics beyond the raw event log - see
    // BuildCrashForensicsBundle/ApplyCrashForensicsBundle for how these are all computed off the
    // UI thread from the same handful of event-log/registry reads, one per refresh.
    // ---------------------------------------------------------------------------------------

    // Items 50/51/56/57: structured, enriched Application Error (1000) events - the anchor list
    // every other item below is a view/lookup/join over.
    public ObservableCollection<ApplicationCrashEvent> ApplicationCrashes { get; } = new();

    // Item 52: per-application crash leaderboard, above the raw grid.
    public ObservableCollection<AppCrashLeaderboardRow> AppCrashLeaderboard { get; } = new();

    // Item 53: Application Hang (1002) events, joined to a matching WER AppHang report.
    public ObservableCollection<ApplicationHangEvent> ApplicationHangs { get; } = new();

    // Items 54/55: managed (.NET/CLR) exceptions + exception-type/top-frame clustering.
    public ObservableCollection<ManagedExceptionEvent> ManagedExceptions { get; } = new();
    public ObservableCollection<ManagedExceptionClusterRow> ManagedExceptionClusters { get; } = new();

    // Items 58/59: Service Control Manager crash/failure events + restart-loop warnings.
    public ObservableCollection<ServiceFailureEvent> ServiceFailures { get; } = new();
    public ObservableCollection<ServiceRestartLoopWarning> ServiceRestartLoopWarnings { get; } = new();

    /// <summary>Item 59: fired (already on the UI thread) when the user asks to jump from a
    /// restart-loop warning row to the matching entry on the Services tab - MainWindow subscribes
    /// and does the actual tab switch + filter (the same "raise an event, let the shell handle
    /// cross-view-model navigation" shape ShowTrayToastRequested above already uses), keeping this
    /// view model itself with no direct reference to ServicesViewModel or the tab strip.</summary>
    public event Action<string>? JumpToServiceRequested;
    public RelayCommand JumpToServiceCommand { get; }

    // Item 61: SilentProcessExit "Capture settings" fields - lives in the same card as LocalDumps
    // above (both are per-executable crash-capture registry config), sharing that card's own
    // LocalDumpsTargetExe textbox as the target rather than a second one.
    private string _silentExitReportingMode = "1";
    public string SilentExitReportingMode { get => _silentExitReportingMode; set => SetProperty(ref _silentExitReportingMode, value); }

    private string _silentExitLocalDumpFolder = @"%LOCALAPPDATA%\CrashDumps";
    public string SilentExitLocalDumpFolder { get => _silentExitLocalDumpFolder; set => SetProperty(ref _silentExitLocalDumpFolder, value); }

    private string _silentExitMonitorProcess = string.Empty;
    public string SilentExitMonitorProcess { get => _silentExitMonitorProcess; set => SetProperty(ref _silentExitMonitorProcess, value); }

    private string _silentExitStatusText = string.Empty;
    public string SilentExitStatusText { get => _silentExitStatusText; private set => SetProperty(ref _silentExitStatusText, value); }

    public RelayCommand LoadSilentExitConfigCommand { get; }
    public RelayCommand SaveSilentExitConfigCommand { get; }
    public RelayCommand ClearSilentExitConfigCommand { get; }

    // ---------------------------------------------------------------------------------------
    // Round 17 chunk 64-70, item 70: forced-crash toggles (CrashOnCtrlScroll, NMICrashDump) -
    // lives in the same "Capture settings" card as LocalDumps/SilentProcessExit above (all three
    // configure a registry-level crash-capture mechanism), clearly labelled since these two
    // actually bluescreen the machine on demand rather than just improving diagnostics for a
    // crash that's already happened.
    // ---------------------------------------------------------------------------------------

    /// <summary>Plain-English read-out of both toggles' current registry state, rebuilt on demand
    /// (a cheap registry read, no caching needed) rather than stored - see ForcedCrashService.</summary>
    public string ForcedCrashConfigText => BuildForcedCrashConfigText();

    private string _forcedCrashStatusText = string.Empty;
    public string ForcedCrashStatusText { get => _forcedCrashStatusText; private set => SetProperty(ref _forcedCrashStatusText, value); }

    public RelayCommand RefreshForcedCrashStatusCommand { get; }
    public RelayCommand EnableCrashOnCtrlScrollCommand { get; }
    public RelayCommand DisableCrashOnCtrlScrollCommand { get; }
    public RelayCommand EnableNmiCrashDumpCommand { get; }
    public RelayCommand DisableNmiCrashDumpCommand { get; }

    // ---------------------------------------------------------------------------------------
    // Round 18, items 71-80: "dump configuration and capture health" - a sibling "Crash dump
    // configuration" card to the "Capture settings" card above, plus a headline pass/fail
    // checklist (item 80) at the top of the whole tab - see CrashDumpConfigService and
    // CrashDumpConfiguration's own remarks for the full field list this reads.
    // ---------------------------------------------------------------------------------------

    private CrashDumpConfiguration? _crashDumpConfig;
    public CrashDumpConfiguration? CrashDumpConfig { get => _crashDumpConfig; private set => SetProperty(ref _crashDumpConfig, value); }

    // Item 80: headline checklist - placed at the top of the tab (see StabilityView.xaml).
    public ObservableCollection<CrashCaptureChecklistItem> CrashCaptureChecklistItems { get; } = new();

    private CrashCaptureVerdict _crashCaptureVerdict = CrashCaptureVerdict.Uncertain;
    public CrashCaptureVerdict CrashCaptureVerdict { get => _crashCaptureVerdict; private set => SetProperty(ref _crashCaptureVerdict, value); }

    private string _crashCaptureVerdictText = string.Empty;
    public string CrashCaptureVerdictText { get => _crashCaptureVerdictText; private set => SetProperty(ref _crashCaptureVerdictText, value); }

    // Item 74: dedicated dump file setup inputs.
    private string _dedicatedDumpFilePath = string.Empty;
    public string DedicatedDumpFilePath { get => _dedicatedDumpFilePath; set => SetProperty(ref _dedicatedDumpFilePath, value); }

    private int _dedicatedDumpFileSizeMb;
    public int DedicatedDumpFileSizeMb { get => _dedicatedDumpFileSizeMb; set => SetProperty(ref _dedicatedDumpFileSizeMb, value); }

    private string _crashDumpConfigStatusText = string.Empty;
    public string CrashDumpConfigStatusText { get => _crashDumpConfigStatusText; private set => SetProperty(ref _crashDumpConfigStatusText, value); }

    public RelayCommand SetDedicatedDumpFileCommand { get; }
    public RelayCommand ClearDedicatedDumpFileCommand { get; }
    public RelayCommand EnableAutoRebootCommand { get; }
    public RelayCommand DisableAutoRebootCommand { get; }
    public RelayCommand EnableFastStartupCommand { get; }
    public RelayCommand DisableFastStartupCommand { get; }
    public RelayCommand ApplyRecommendedCrashDumpConfigCommand { get; }

    // ---------------------------------------------------------------------------------------
    // Round 19, items 81-88: "Driver Verifier and kernel pool corruption" - a "Driver Verifier"
    // card (items 81/82/86) with a guided, multi-step wizard (items 83/84) for turning it on
    // safely, plus an on-demand "Kernel pool by tag" panel (item 87) with a targeted Special Pool
    // follow-up action (item 88). See DriverVerifierService/PoolTagMonitorService/
    // VerifierEnableHistoryService for the underlying reads/writes this section drives.
    // ---------------------------------------------------------------------------------------

    // Item 81: last-read verifier.exe status (both /query and /querysettings, folded together).
    private DriverVerifierStatus? _verifierStatus;
    public DriverVerifierStatus? VerifierStatus { get => _verifierStatus; private set => SetProperty(ref _verifierStatus, value); }

    // Item 86: "Verifier has been enabled for N days" - null when Verifier isn't currently
    // running (nothing to nag about) or this app never recorded an enable timestamp for it (e.g.
    // it was turned on outside this app, by a previous version, or manually via verifier.exe).
    private string? _verifierEnabledDurationText;
    public string? VerifierEnabledDurationText { get => _verifierEnabledDurationText; private set => SetProperty(ref _verifierEnabledDurationText, value); }

    private bool _verifierNagDue;
    public bool VerifierNagDue { get => _verifierNagDue; private set => SetProperty(ref _verifierNagDue, value); }

    // Item 86: "a configurable number of days or reboots" - editable copies of
    // VerifierEnableHistory's own two thresholds, kept in sync from UpdateVerifierEnabledDurationText
    // and written back by SaveVerifierNagSettingsCommand.
    private int _nagAfterDaysInput = 3;
    public int NagAfterDaysInput { get => _nagAfterDaysInput; set => SetProperty(ref _nagAfterDaysInput, value); }

    private int _nagAfterRebootsInput = 2;
    public int NagAfterRebootsInput { get => _nagAfterRebootsInput; set => SetProperty(ref _nagAfterRebootsInput, value); }

    public RelayCommand SaveVerifierNagSettingsCommand { get; }

    public RelayCommand RefreshVerifierStatusCommand { get; }

    // Item 82: one-click reset + reboot prompt - gated behind a confirmation like every other
    // mutating action on this tab.
    private string _verifierResetStatusText = string.Empty;
    public string VerifierResetStatusText { get => _verifierResetStatusText; private set => SetProperty(ref _verifierResetStatusText, value); }

    public AsyncRelayCommand ResetVerifierCommand { get; }

    // ---- Guided wizard (items 83/84) -------------------------------------------------------

    private bool _wizardOpen;
    public bool WizardOpen { get => _wizardOpen; private set => SetProperty(ref _wizardOpen, value); }

    // 1 = restore point, 2 = driver selection, 3 = Safe Mode recovery guidance, 4 = confirm/apply -
    // driven by DataTrigger Value="N" in XAML (the same "int step -> Visibility via DataTrigger"
    // shape CrashCaptureVerdict's Pass/Fail/Uncertain triggers already use above, just with an int
    // instead of an enum) rather than a new converter.
    private int _wizardStep;
    public int WizardStep { get => _wizardStep; private set => SetProperty(ref _wizardStep, value); }

    public RelayCommand OpenWizardCommand { get; }
    public RelayCommand CloseWizardCommand { get; }
    public RelayCommand BackWizardStepCommand { get; }

    // Step 1: restore point.
    private string _restorePointStatusText = string.Empty;
    public string RestorePointStatusText { get => _restorePointStatusText; private set => SetProperty(ref _restorePointStatusText, value); }

    public AsyncRelayCommand CreateRestorePointCommand { get; }
    public RelayCommand GoToDriverStepCommand { get; }

    // Step 2: driver selection.
    public ObservableCollection<DriverVerifierCandidateRow> WizardDriverCandidates { get; } = new();

    private string _wizardDriverStatusText = string.Empty;
    public string WizardDriverStatusText { get => _wizardDriverStatusText; private set => SetProperty(ref _wizardDriverStatusText, value); }

    public RelayCommand GoToSafeModeStepCommand { get; }

    // Step 3: Safe Mode recovery guidance (static text in XAML) -> step 4.
    public RelayCommand GoToConfirmStepCommand { get; }

    // Step 4: confirm + apply. Item 84's "apply without restarting" option - only the five
    // volatile-eligible flags are ever offered here (see DriverVerifierService.VolatileFlagOptions),
    // matching what `verifier /volatile /flags` actually accepts; index 0 is "none - just start
    // verifying the selected drivers".
    public IReadOnlyList<string> VolatileFlagOptionLabels { get; } =
        new[] { "None - just start verifying the selected drivers" }
            .Concat(DriverVerifierService.VolatileFlagOptions.Select(f => f.Name))
            .ToList();

    private bool _wizardApplyVolatile;
    public bool WizardApplyVolatile { get => _wizardApplyVolatile; set => SetProperty(ref _wizardApplyVolatile, value); }

    private int _wizardVolatileFlagIndex;
    public int WizardVolatileFlagIndex { get => _wizardVolatileFlagIndex; set => SetProperty(ref _wizardVolatileFlagIndex, value); }

    private string _wizardApplyStatusText = string.Empty;
    public string WizardApplyStatusText { get => _wizardApplyStatusText; private set => SetProperty(ref _wizardApplyStatusText, value); }

    public AsyncRelayCommand ApplyWizardCommand { get; }

    // ---- Item 87: on-demand kernel pool-by-tag monitor -------------------------------------

    private PoolTagSnapshot? _poolTagBaseline;

    public ObservableCollection<PoolTagRow> PoolTagRows { get; } = new();

    private string _poolTagStatusText = "Not sampled yet this session.";
    public string PoolTagStatusText { get => _poolTagStatusText; private set => SetProperty(ref _poolTagStatusText, value); }

    public AsyncRelayCommand SamplePoolTagsCommand { get; }
    public RelayCommand ResetPoolTagBaselineCommand { get; }

    // ---- Item 88: targeted Special Pool for a suspect tag ----------------------------------

    private string _specialPoolStatusText = string.Empty;
    public string SpecialPoolStatusText { get => _specialPoolStatusText; private set => SetProperty(ref _specialPoolStatusText, value); }

    public AsyncRelayCommand ApplySpecialPoolCommand { get; }

    // Round 17 chunk 64-70, item 66: persisted per-executable hang history - see
    // HangHistoryService. Shown on this same "Application hangs" card (item 53's own card) rather
    // than a new one, per this chunk's own instruction.
    public ObservableCollection<HangHistoryEntry> HangHistory { get; } = new();

    // Item 62: postmortem debugger + Image File Execution Options hijack audit - a plain
    // registry read, refreshed alongside everything else in RefreshAsync (no separate button;
    // see CLAUDE.md's "on-demand vs. polled" note - this is cheap, not an expensive scan).
    private PostmortemDebuggerInfo? _postmortemDebugger;
    public PostmortemDebuggerInfo? PostmortemDebugger { get => _postmortemDebugger; private set => SetProperty(ref _postmortemDebugger, value); }

    // #239: Windows Error Reporting AppHang reports (Report.wer under ReportArchive/ReportQueue) -
    // see AppHangReportService. Same on-demand refresh as everything else on this tab.
    public ObservableCollection<AppHangReportEntry> AppHangs { get; } = new();

    // #240: "top hanging apps in the last 30 days" from the Application-log "Application Hang"
    // (event 1002) source - see EventLogService.ReadApplicationHangEvents. Complements #239's
    // richer per-report detail, which Windows prunes sooner than the event log itself.
    public ObservableCollection<AppHangEventSummary> HangEventHistory { get; } = new();

    // #713: "Power & boot timeline" - see PowerTimelineService's remarks. A separate targeted
    // query (different providers/event IDs than the Critical/Error scan above), run alongside it
    // on the same on-demand Refresh rather than a second button.
    public ObservableCollection<PowerTimelineEntry> PowerTimeline { get; } = new();

    // #741: resume-from-hibernate entries that look like they failed - correlated from the same
    // timeline above (Kernel-Boot 27 boot type 2 followed by a 41/6008 failure signal), see
    // PowerTimelineService.ReadFailedResumes. Cross-links back to the Startup tab's hiberfile card.
    public ObservableCollection<FailedResumeEntry> FailedResumes { get; } = new();

    // #781: "did an update break this?" - Windows Update install events (read fresh here, off the
    // same Microsoft-Windows-WindowsUpdateClient/Operational log the Windows Health tab's own #769
    // card reads) correlated against RecentEvents' own recurring-faulting-module groups below. Pure
    // post-processing, see WindowsUpdateHistoryService.CorrelateWithStabilityFailures. Cross-links
    // forward into the Windows Health tab's #780 update-uninstall list.
    public ObservableCollection<UpdateBreakageFlag> UpdateBreakageFlags { get; } = new();

    // ---- #161/#162: WER crash reports, grouped by bucket signature ----
    public ObservableCollection<WerCrashBucket> CrashReportBuckets { get; } = new();

    // ---- #163: top crashing applications (WER + Application-log 1000 combined) ----
    public ObservableCollection<TopCrashingApplication> TopCrashingApplications { get; } = new();

    // ---- #164: hangs (Application Hang 1002) - kept separate from crashes above ----
    public ObservableCollection<WerHangInfo> Hangs { get; } = new();

    // ---- #166: WER storage footprint ----
    private WerStorageFootprint _werFootprint = new();
    public WerStorageFootprint WerFootprint { get => _werFootprint; private set => SetProperty(ref _werFootprint, value); }

    public RelayCommand RevealWerQueueCommand { get; }
    public RelayCommand RevealWerArchiveCommand { get; }

    // ---- #165: local crash dump capture (LocalDumps) toggle ----
    private LocalDumpsSettings _localDumpsSettings = new();
    public LocalDumpsSettings LocalDumpsSettings { get => _localDumpsSettings; private set => SetProperty(ref _localDumpsSettings, value); }

    private bool _canRevertLocalDumps;
    public bool CanRevertLocalDumps { get => _canRevertLocalDumps; private set => SetProperty(ref _canRevertLocalDumps, value); }

    private string? _localDumpsToggleStatusText;
    public string? LocalDumpsToggleStatusText { get => _localDumpsToggleStatusText; private set => SetProperty(ref _localDumpsToggleStatusText, value); }

    public RelayCommand EnableLocalDumpsCommand { get; }
    public RelayCommand RevertLocalDumpsCommand { get; }

    // ---- #167: error reporting configuration check ----
    private WerConfigStatus _werConfigStatus = new();
    public WerConfigStatus WerConfigStatus { get => _werConfigStatus; private set => SetProperty(ref _werConfigStatus, value); }

    // ---- #184: storage errors grouped by physical disk ----
    public ObservableCollection<StorageErrorDiskGroup> StorageErrorGroups { get; } = new();

    // ---- #185: shadow copy / VSS family ("Backup and restore points") ----
    public ObservableCollection<ShadowCopyEventInfo> ShadowCopyEvents { get; } = new();
    public ObservableCollection<ShadowStorageVolumeInfo> ShadowStorageVolumes { get; } = new();

    private string? _shadowCopyStatusText;
    public string? ShadowCopyStatusText { get => _shadowCopyStatusText; private set => SetProperty(ref _shadowCopyStatusText, value); }

    // ---- #186: WHEA corrected-hardware-error rate ----
    private WheaErrorSummary _wheaRateSummary = new();
    public WheaErrorSummary WheaRateSummary { get => _wheaRateSummary; private set => SetProperty(ref _wheaRateSummary, value); }

    public ObservableCollection<double> WheaDailyCounts { get; } = new();
    private readonly ColumnSeries<double> _wheaDailyColumns;
    public ISeries[] WheaDailySeries { get; }
    public Axis[] WheaDailyXAxes { get; }
    public Axis[] WheaDailyYAxes { get; }

    // #488: corrected WHEA errors per day over the same lookback window - the exact same
    // ColumnSeries/Axis setup as DailyEventSeries above, just fed from a different daily bucket.
    public ObservableCollection<double> DailyWheaCorrectedCounts { get; } = new();
    private readonly ColumnSeries<double> _dailyWheaCorrectedColumns;
    public ISeries[] DailyWheaCorrectedSeries { get; }
    public Axis[] DailyWheaCorrectedXAxes { get; }
    public Axis[] DailyWheaCorrectedYAxes { get; }

    // ---- #187: driver load failures ----
    public ObservableCollection<DriverFailureEvent> DriverFailures { get; } = new();

    // ---- #188: chkdsk / autochk results ----
    public ObservableCollection<ChkdskRunInfo> ChkdskResults { get; } = new();

    // ---- #189: Windows Memory Diagnostic results ----
    private MemoryDiagnosticStatus _memoryDiagnosticStatus = new();
    public MemoryDiagnosticStatus MemoryDiagnosticStatus { get => _memoryDiagnosticStatus; private set => SetProperty(ref _memoryDiagnosticStatus, value); }

    // ---- #190: power-transition failure family ----
    public ObservableCollection<PowerTransitionIncident> PowerTransitionIncidents { get; } = new();

    // ---- #195: Perflib counter-corruption card ----
    private PerflibFailureSummary _perflibSummary = new();
    public PerflibFailureSummary PerflibSummary { get => _perflibSummary; private set => SetProperty(ref _perflibSummary, value); }

    private string? _perflibStatusText;
    public string? PerflibStatusText { get => _perflibStatusText; private set => SetProperty(ref _perflibStatusText, value); }

    /// <summary>#195: `lodctr /R` - a real system change, gated behind its own explicit MessageBox
    /// confirmation (same shape as #165/#172's registry writes) - see RunLodctrRebuild.</summary>
    public RelayCommand RunLodctrRebuildCommand { get; }

    // ---- #196: assorted subsystem error families rollup ----
    public ObservableCollection<SubsystemFamilyGroup> SubsystemFamilies { get; } = new();

    // ---- #199: log-clearing detection - surfaced prominently (its own banner, ahead of the
    // known-bad-IDs scorecard) rather than silently folded into "no problems found". ----
    public ObservableCollection<LogClearEvent> LogClearEvents { get; } = new();

    /// <summary>#128: "New error types this week" - (provider, eventId) signatures present only in
    /// the last 7 days of RecentEvents' 30-day window, with no occurrence in the older 23 days of
    /// that same snapshot. A pure re-grouping of data this tab already reads, no new query - the
    /// single strongest "something changed" signal an event log can give.</summary>
    public ObservableCollection<FirstOccurrenceFlag> NewErrorTypesThisWeek { get; } = new();

    /// <summary>#130: day x hour-of-day Critical/Error density grid, bucketed from the same
    /// RecentEvents snapshot as the Reliability History chart above it - see
    /// EventAnomalyDetectionService.ComputeDensityHeatmap's remarks for why "day" is chronological,
    /// not a folded 1-31 day-of-month bucket.</summary>
    public ObservableCollection<ErrorDensityHeatmapCell> ErrorDensityHeatmap { get; } = new();

    // #427: the classic pool-starvation event signature (Srv 2019/2020, event 333, and
    // Resource-Exhaustion-Detector entries) - see EventLogService.ReadPoolExhaustionEvents.
    public ObservableCollection<PoolExhaustionEvent> PoolExhaustionEvents { get; } = new();

    // #439: out-of-memory incidents (Resource-Exhaustion-Detector event 2004), each carrying the
    // ranked top commit consumers Windows itself recorded at the moment - see
    // EventLogService.ReadOutOfMemoryIncidents.
    public ObservableCollection<OutOfMemoryIncident> OutOfMemoryIncidents { get; } = new();

    // #447: corrected-memory-error events (WHEA-Logger 47) - the same figure the System Specs
    // memory section shows, surfaced here too since a corrected-error trend is as much a
    // stability signal as a hardware-inventory fact.
    public ObservableCollection<CorrectedMemoryErrorEvent> CorrectedMemoryErrors { get; } = new();

    // #464: boot-start/system-start driver load failures (SCM 7000/7001/7026, kernel PnP event
    // 219) - the same figure the Devices & Drivers tab shows (EventLogService.
    // ReadBootDriverLoadFailures is read independently by each tab, no ViewModel coupling). This
    // tab had no distinct pre-existing "boot section" to fold this into, so it's its own small card.
    public ObservableCollection<BootDriverLoadFailure> BootDriverLoadFailures { get; } = new();

    // #487: every Microsoft-Windows-WHEA-Logger record found (any event ID) - the broad "hardware
    // errors" view; #447's CorrectedMemoryErrors above stays as its own narrower event-47 slice.
    public ObservableCollection<WheaHardwareErrorEvent> WheaHardwareErrors { get; } = new();

    // #492: crash/TDR/unexpected-shutdown events preceded by a WHEA hardware error within the
    // correlation window - see EventLogService.BuildHardwareErrorCorrelations.
    public ObservableCollection<HardwareErrorCorrelation> HardwareErrorCorrelations { get; } = new();

    private int _correctedMemoryErrorCount;
    public int CorrectedMemoryErrorCount { get => _correctedMemoryErrorCount; private set => SetProperty(ref _correctedMemoryErrorCount, value); }

    private string _lastCorrectedMemoryErrorText = "None in the last 30 days";
    public string LastCorrectedMemoryErrorText { get => _lastCorrectedMemoryErrorText; private set => SetProperty(ref _lastCorrectedMemoryErrorText, value); }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    /// <summary>Set when RefreshAsync's event-log query fails outright (e.g. denied access to a
    /// log) - empty/null the rest of the time. Mirrors the "...failed: {message}" convention this
    /// app's other on-demand actions already use rather than letting the exception propagate
    /// uncaught out of an async void command handler.</summary>
    private string? _refreshErrorText;
    public string? RefreshErrorText { get => _refreshErrorText; private set => SetProperty(ref _refreshErrorText, value); }

    private bool _wasLastShutdownUnexpected;
    public bool WasLastShutdownUnexpected { get => _wasLastShutdownUnexpected; private set => SetProperty(ref _wasLastShutdownUnexpected, value); }

    private string _lastUnexpectedShutdownText = string.Empty;
    public string LastUnexpectedShutdownText { get => _lastUnexpectedShutdownText; private set => SetProperty(ref _lastUnexpectedShutdownText, value); }

    // Round 13, item 3: labelled cause badge for the unexpected-shutdown banner, replacing the old
    // single generic "unclean shutdown" warning - see EventLogService.ClassifyPowerEvent.
    private string _lastShutdownCauseText = string.Empty;
    public string LastShutdownCauseText { get => _lastShutdownCauseText; private set => SetProperty(ref _lastShutdownCauseText, value); }

    // Round 13, items 1/2/8: the most recent authoritative bugcheck record, if any - drives the
    // "Full crash record" expander data on the Minidumps card (bound per-row via MinidumpInfo
    // itself, but also exposed here for a tab-level "last confirmed stop code" summary line).
    private BugCheckRecord? _latestBugCheck;
    public BugCheckRecord? LatestBugCheck { get => _latestBugCheck; private set => SetProperty(ref _latestBugCheck, value); }

    // Round 13, item 12: "is the 30-day lookback window even trustworthy" line shown under the
    // Refresh button - see EventLogService.ReadLogHealth / BuildLogCoverageText below.
    private string _logCoverageText = string.Empty;
    public string LogCoverageText { get => _logCoverageText; private set => SetProperty(ref _logCoverageText, value); }

    private int _tdrEventCount;
    public int TdrEventCount { get => _tdrEventCount; private set => SetProperty(ref _tdrEventCount, value); }

    private string _lastTdrEventText = "None in the last 30 days";
    public string LastTdrEventText { get => _lastTdrEventText; private set => SetProperty(ref _lastTdrEventText, value); }

    // Round 15, item 34: per-event TDR detail (driver/app/time) plus the live registry settings
    // that control TDR's timeout behavior - see EventLogService.ReadTdrEventDetails/
    // ReadTdrRegistrySettings.
    public ObservableCollection<TdrEventDetail> TdrEventDetails { get; } = new();

    private TdrRegistrySettings? _tdrSettings;
    public TdrRegistrySettings? TdrSettings { get => _tdrSettings; private set => SetProperty(ref _tdrSettings, value); }

    private string _timeSinceLastCrashText = "No crash found in the last 30 days";
    public string TimeSinceLastCrashText { get => _timeSinceLastCrashText; private set => SetProperty(ref _timeSinceLastCrashText, value); }

    // ---- #169-174: Reliability Monitor (Win32_ReliabilityStabilityMetrics / Win32_ReliabilityRecords) ----
    private readonly ReliabilityMonitorService _reliability = new();

    /// <summary>#169: Windows' own per-day stability index, folded down from
    /// Win32_ReliabilityStabilityMetrics' hourly samples (see ReliabilityMonitorService.
    /// BuildDailyIndex) to align with DailyEventCounts' exact day range so both can share one X axis
    /// on the Reliability History chart. A day with no WMI sample at all is a real null (a gap in
    /// the overlay line), never a fabricated 0.</summary>
    public ObservableCollection<double?> WmiStabilityIndexValues { get; } = new();
    private readonly LineSeries<double?> _wmiStabilityLine;

    /// <summary>#170: the full reliability record feed - application/Windows/miscellaneous
    /// failures, warnings, and informational entries (software installs/updates/uninstalls), newest
    /// first. See ReliabilityMonitorService.Classify for how Category is derived.</summary>
    public ObservableCollection<ReliabilityRecordInfo> ReliabilityRecords { get; } = new();

    /// <summary>#173: the Informational-category subset of ReliabilityRecords above, presented as a
    /// software change log - built alongside ReliabilityRecords in RefreshReliabilityMonitorAsync,
    /// then cross-highlighted against crash clusters from the unified timeline (PrecedesCrashClusterNote)
    /// via ReliabilityMonitorService.CorrelateChangesWithCrashClusters.</summary>
    public ObservableCollection<ReliabilityRecordInfo> SoftwareChangeLog { get; } = new();

    private ReliabilityAnalysisStatus _reliabilityAnalysisStatus = new();
    public ReliabilityAnalysisStatus ReliabilityAnalysisStatus { get => _reliabilityAnalysisStatus; private set => SetProperty(ref _reliabilityAnalysisStatus, value); }

    /// <summary>#172: drives whether the #169/#170/#173 cards render at all - hidden entirely (not
    /// an empty chart/grid) once #172 detects collection is off, per CLAUDE.md's "degrade to
    /// Unknown/0/hidden" convention.</summary>
    private bool _isReliabilityMonitorAvailable = true;
    public bool IsReliabilityMonitorAvailable { get => _isReliabilityMonitorAvailable; private set => SetProperty(ref _isReliabilityMonitorAvailable, value); }

    private bool _canRevertReliabilityAnalysis;
    public bool CanRevertReliabilityAnalysis { get => _canRevertReliabilityAnalysis; private set => SetProperty(ref _canRevertReliabilityAnalysis, value); }

    private string? _reliabilityAnalysisStatusText;
    public string? ReliabilityAnalysisStatusText { get => _reliabilityAnalysisStatusText; private set => SetProperty(ref _reliabilityAnalysisStatusText, value); }

    public RelayCommand EnableReliabilityAnalysisCommand { get; }
    public RelayCommand RevertReliabilityAnalysisCommand { get; }

    // ---- #171: on-demand RAC re-aggregation - its own action, not part of the general Refresh ----
    private bool _isReliabilityRefreshing;
    public bool IsReliabilityRefreshing { get => _isReliabilityRefreshing; private set => SetProperty(ref _isReliabilityRefreshing, value); }

    private string? _reliabilityRefreshStatusText;
    public string? ReliabilityRefreshStatusText { get => _reliabilityRefreshStatusText; private set => SetProperty(ref _reliabilityRefreshStatusText, value); }

    public AsyncRelayCommand RefreshReliabilityCommand { get; }

    // ---- #174: index disagreement flag ----
    private const double IndexDisagreementThreshold = 2.0;

    /// <summary>#174: Windows' own index averaged over the last 7 days that actually have a WMI
    /// sample (#169) - null when no WMI sample exists in that window at all (nothing to compare).</summary>
    private double? _windowsStabilityIndexRecent;
    public double? WindowsStabilityIndexRecent { get => _windowsStabilityIndexRecent; private set => SetProperty(ref _windowsStabilityIndexRecent, value); }

    private bool _indicesDisagree;
    public bool IndicesDisagree { get => _indicesDisagree; private set => SetProperty(ref _indicesDisagree, value); }

    private string? _indexDisagreementText;
    public string? IndexDisagreementText { get => _indexDisagreementText; private set => SetProperty(ref _indexDisagreementText, value); }

    // Round 8 #40: low-memory resource-exhaustion events - see EventLogService.ReadLowMemoryEvents.
    private int _lowMemoryEventCount;
    public int LowMemoryEventCount { get => _lowMemoryEventCount; private set => SetProperty(ref _lowMemoryEventCount, value); }

    private string _lastLowMemoryEventText = "None in the last 30 days";
    public string LastLowMemoryEventText { get => _lastLowMemoryEventText; private set => SetProperty(ref _lastLowMemoryEventText, value); }

    // Round 10, #68: single 0-10 stability index - see ComputeStabilityIndex for the documented
    // weighted formula.
    private double _stabilityIndex = 10.0;
    public double StabilityIndex { get => _stabilityIndex; private set => SetProperty(ref _stabilityIndex, value); }

    // #606: thermal-critical/shutdown event scan - a firmware thermal shutdown is otherwise
    // indistinguishable in the reliability log from a PSU death, so this gets its own explicit
    // red banner rather than being folded into RecentEvents.
    public ObservableCollection<StabilityEvent> ThermalCriticalEvents { get; } = new();
    public bool ThermalCriticalDetected => ThermalCriticalEvents.Count > 0;

    // #610: throttle-to-stutter correlation - cross-references #604's persisted throttle episodes
    // against the hitch/event timestamps this tab already holds (RecentEvents). Empty (banner
    // hidden) until there's at least one recorded episode and one recorded event to compare.
    private string _hitchThrottleCorrelationText = string.Empty;
    public string HitchThrottleCorrelationText { get => _hitchThrottleCorrelationText; private set => SetProperty(ref _hitchThrottleCorrelationText, value); }

    // #625: cross-references the shutdown banner's own Kernel-Power 41 timestamp against
    // PowerHistoryLogService's coarse persisted power trail - a reboot at peak draw with no
    // bugcheck code is the classic PSU-under-load signature. Empty (annotation hidden) until
    // there's both an unexpected shutdown and power-history data recorded near it.
    private string _powerDrawAtRebootText = string.Empty;
    public string PowerDrawAtRebootText { get => _powerDrawAtRebootText; private set => SetProperty(ref _powerDrawAtRebootText, value); }

    public AsyncRelayCommand RefreshCommand { get; }

    // #636-640: "Hardware errors (WHEA)" card - the app's first WHEA (Windows Hardware Error
    // Architecture) surface. On-demand (its own event-log query, separate from RefreshCommand's
    // System/Application scan above, reusing the same _service instance), loaded once at startup
    // plus a manual refresh button, same shape as EnergyThermalsViewModel's firmware-limit events.
    public ObservableCollection<WheaEvent> WheaEvents { get; } = new();

    // #638: two-column "conditions at the moment of each error" table, one row per WheaEvent -
    // temperature/power at the nearest PowerHistoryLogService sample to that event's timestamp.
    public ObservableCollection<WheaConditionRow> WheaConditionRows { get; } = new();

    public AsyncRelayCommand LoadWheaEventsCommand { get; }

    private int _wheaFatalCount;
    public int WheaFatalCount { get => _wheaFatalCount; private set => SetProperty(ref _wheaFatalCount, value); }

    private int _wheaCorrectedCount;
    public int WheaCorrectedCount { get => _wheaCorrectedCount; private set => SetProperty(ref _wheaCorrectedCount, value); }

    // #637: corrected-WHEA-errors-per-day column chart, alongside the existing reliability-history
    // chart above - a rising corrected-error rate is the earliest hardware-failure warning Windows
    // produces and is entirely invisible in Reliability Monitor.
    private const int WheaLookbackDays = 30; // matches EventLogService.LookbackDays
    public ObservableCollection<double> WheaCorrectedDailyCounts { get; } = new();
    private readonly ColumnSeries<double> _wheaCorrectedColumns;
    public ISeries[] WheaCorrectedSeries { get; }
    public Axis[] WheaCorrectedXAxes { get; }
    public Axis[] WheaCorrectedYAxes { get; }

    // #1: Reliability History - daily Critical/Error counts over the lookback window, the same
    // "crash/failure events over time" chart Windows' own Reliability Monitor shows, themed to
    // match this app instead of a bare column series.
    public ObservableCollection<double> DailyEventCounts { get; } = new();
    private readonly ColumnSeries<double> _dailyEventColumns;

    // Round 13, item 11: Microsoft's own Reliability Monitor per-day stability index
    // (Win32_ReliabilityStabilityMetrics, 0-10) as a second series on the same chart, plotted
    // against its own right-hand axis (DailyEventYAxes[1]) since it's a fixed 0-10 scale, not an
    // event count. A day with no Microsoft data is left null (a real gap in the line), not zero.
    public ObservableCollection<double?> ReliabilityIndexPoints { get; } = new();
    private readonly LineSeries<double?> _reliabilityIndexLine;

    public ISeries[] DailyEventSeries { get; }
    public Axis[] DailyEventXAxes { get; }
    public Axis[] DailyEventYAxes { get; }

    private static readonly SKColor AxisTextColor = new(0x9A, 0x9A, 0xA2);
    private static readonly SKColor AxisSeparatorColor = new(0x33, 0x33, 0x3A, 160);

    // ---------------------------------------------------------------------------------------
    // Round 20, items 89-95: "Clustering crashes over time and correlating with changes" - a
    // unified layer over EVERY per-source collection above (bugchecks/minidumps, live kernel
    // reports, WER reports, application crashes/hangs, service failures, TDRs, WHEA errors,
    // unexpected shutdowns), built once per refresh as its own bundle (see
    // BuildCorrelationBundle/ApplyCorrelationBundle) rather than re-querying anything - see
    // CrashCorrelationService.
    // ---------------------------------------------------------------------------------------

    // Item 89: the full merged timeline plus its source-filter chips. FilteredTimeline (capped,
    // newest first) is what the card actually binds to - rebuilt from the unfiltered
    // _unifiedTimeline whenever a chip is toggled, the new default view of this tab per item 89.
    private List<CrashTimelineRow> _unifiedTimeline = new();
    private const int MaxTimelineRowsShown = 300;
    public ObservableCollection<CrashSourceFilterOption> TimelineSourceFilters { get; } = new();
    public ObservableCollection<CrashTimelineRowViewModel> FilteredTimeline { get; } = new();

    // Item 90: kernel/user-mode fault clusters, displayed above the timeline - most frequent,
    // then most recent, first (see CrashCorrelationService.BuildClusters).
    public ObservableCollection<CrashClusterViewModel> CrashClusters { get; } = new();

    // Item 91: uptime-at-crash histogram + MTBF/longest-streak summary text.
    public ObservableCollection<double> UptimeHistogramCounts { get; } = new();
    private readonly ColumnSeries<double> _uptimeHistogramColumns;
    public ISeries[] UptimeHistogramSeries { get; }
    public Axis[] UptimeHistogramXAxes { get; }
    public Axis[] UptimeHistogramYAxes { get; }

    private string _mtbfSummaryText = string.Empty;
    public string MtbfSummaryText { get => _mtbfSummaryText; private set => SetProperty(ref _mtbfSummaryText, value); }

    // ---------------------------------------------------------------------------------------
    // Round 21, items 96-100: "Recovery, escalation and safe operation" - the final chunk of
    // this domain. Every action below is a genuinely dangerous or slow operation (reboot into
    // Safe Mode, chkdsk /f scheduling a reboot, DISM taking many minutes) - gated behind an
    // explicit button and a strongly-worded confirmation, never automatic, per this chunk's own
    // instructions. See BootRecoveryService/RestorePointService/SystemRepairService/
    // CrashSupportBundleService.
    // ---------------------------------------------------------------------------------------

    // Items 96/97: one shared `bcdedit /enum {current}` read - item 96's "already configured for
    // Safe Mode?" check and item 97's plain-English audit list both read off this same result,
    // refreshed automatically alongside VerifierStatus/CrashDumpConfig in RefreshAsync (a single
    // bcdedit call is cheap, same tier as those two).
    private BootConfigAudit? _bootConfigAudit;
    public BootConfigAudit? BootConfigAudit { get => _bootConfigAudit; private set => SetProperty(ref _bootConfigAudit, value); }

    private string _safeModeStatusText = string.Empty;
    public string SafeModeStatusText { get => _safeModeStatusText; private set => SetProperty(ref _safeModeStatusText, value); }

    public AsyncRelayCommand RebootToSafeModeMinimalCommand { get; }
    public AsyncRelayCommand RebootToSafeModeNetworkCommand { get; }
    public AsyncRelayCommand RebootToRecoveryEnvironmentCommand { get; }
    public AsyncRelayCommand RevertSafeModeBootCommand { get; }

    // Item 98: System Restore points + a best-effort "is System Protection even on" flag.
    private SystemProtectionStatus? _systemProtectionStatus;
    public SystemProtectionStatus? SystemProtectionStatus { get => _systemProtectionStatus; private set => SetProperty(ref _systemProtectionStatus, value); }

    private string _restorePointsStatusText = string.Empty;
    public string RestorePointsStatusText { get => _restorePointsStatusText; private set => SetProperty(ref _restorePointsStatusText, value); }

    public AsyncRelayCommand CreateRestorePointFromRecoveryCommand { get; }
    public RelayCommand LaunchRstruiCommand { get; }

    // Item 99: guided repair runner - each action owns its own busy flag and result text, per
    // this chunk's own "explicit button, live output, clear 'this takes a while' indicator"
    // instruction; sfc/DISM are the two genuinely long-running ones (SystemRepairService's own
    // 45-minute ceiling), chkdsk only schedules (instant), Memory Diagnostic only launches
    // (instant, the wait happens outside Windows entirely).
    private bool _isSfcRunning;
    public bool IsSfcRunning { get => _isSfcRunning; private set => SetProperty(ref _isSfcRunning, value); }
    private string _sfcResultText = string.Empty;
    public string SfcResultText { get => _sfcResultText; private set => SetProperty(ref _sfcResultText, value); }
    public AsyncRelayCommand RunSfcCommand { get; }

    private bool _isDismRunning;
    public bool IsDismRunning { get => _isDismRunning; private set => SetProperty(ref _isDismRunning, value); }
    private string _dismResultText = string.Empty;
    public string DismResultText { get => _dismResultText; private set => SetProperty(ref _dismResultText, value); }
    public AsyncRelayCommand RunDismCommand { get; }

    private string _chkdskResultText = string.Empty;
    public string ChkdskResultText { get => _chkdskResultText; private set => SetProperty(ref _chkdskResultText, value); }
    public AsyncRelayCommand ScheduleChkdskCommand { get; }

    public RelayCommand LaunchMemoryDiagnosticCommand { get; }
    public ObservableCollection<MemoryDiagnosticResultInfo> MemoryDiagnosticResults { get; } = new();

    private string _memoryDiagnosticStatusText = "Not checked yet this session - use \"Check for results\" below (or run the diagnostic first, which restarts the machine).";
    public string MemoryDiagnosticStatusText { get => _memoryDiagnosticStatusText; private set => SetProperty(ref _memoryDiagnosticStatusText, value); }

    public AsyncRelayCommand RefreshMemoryDiagnosticResultsCommand { get; }

    // Item 100: one-click crash support bundle.
    private bool _isBuildingSupportBundle;
    public bool IsBuildingSupportBundle { get => _isBuildingSupportBundle; private set => SetProperty(ref _isBuildingSupportBundle, value); }

    private string _supportBundleStatusText = string.Empty;
    public string SupportBundleStatusText { get => _supportBundleStatusText; private set => SetProperty(ref _supportBundleStatusText, value); }

    public AsyncRelayCommand BuildSupportBundleCommand { get; }

    // ---- #633: combined "possible unstable undervolt/overclock" flag ---------------------------
    // Three independent, individually weak signals - WHEA corrected errors (#636), Application-log
    // access-violation/illegal-instruction faults spread across more than one faulting module (no
    // single app looks responsible), and an inferred non-stock-looking Vcore reading under load
    // (EnergyThermalsViewModel's #622 Vcore-vs-power sampling) - become a meaningful "quick flag,
    // not a verdict" only when at least two of the three line up. Recomputed whenever either of the
    // two on-demand queries it depends on (RefreshAsync's event scan, LoadWheaEventsAsync's WHEA
    // count) finishes, plus once more on this tab's own load.
    private static readonly HashSet<string> UndervoltFaultCodes = new(StringComparer.OrdinalIgnoreCase) { "0xc0000005", "0xc000001d" };

    public ObservableCollection<string> UndervoltInstabilityEvidence { get; } = new();

    private bool _undervoltInstabilitySuspected;
    public bool UndervoltInstabilitySuspected { get => _undervoltInstabilitySuspected; private set => SetProperty(ref _undervoltInstabilitySuspected, value); }

    // #677: GPU resets (TDR + DXGI device-removed) and pre-TDR hang detections - the same
    // EventLogService.ReadGpuResetSummary GpuViewModel's own GPU-tab card reads, surfaced here too
    // since an *unrecovered* GPU reset (one that took the whole system down with it) is exactly the
    // kind of event this tab's bugcheck-focused view exists to catch. GpuHangEvents comes from
    // GpuHangHistoryService's persisted log, not a live poll - this tab never runs a timer.
    public ObservableCollection<GpuTdrEvent> GpuTdrEvents { get; } = new();
    public ObservableCollection<GpuHangEvent> GpuHangEvents { get; } = new();

    private int _gpuUnrecoveredResetCount;
    public int GpuUnrecoveredResetCount { get => _gpuUnrecoveredResetCount; private set => SetProperty(ref _gpuUnrecoveredResetCount, value); }

    public AsyncRelayCommand LoadGpuResetHistoryCommand { get; }

    private string _gpuResetHistoryStatusText = string.Empty;
    public string GpuResetHistoryStatusText { get => _gpuResetHistoryStatusText; private set => SetProperty(ref _gpuResetHistoryStatusText, value); }

    public StabilityViewModel(EnergyThermalsViewModel energyThermals)
    {
        _energyThermals = energyThermals;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        DrillDownCommand = new RelayCommand(p => _ = DrillDownAsync(p as TimelineEntry));
        FindChangesBeforeCrashCommand = new RelayCommand(p => _ = FindChangesBeforeCrashAsync(p as TimelineEntry));

        // #166: reuses EtwTraceService.RevealInExplorer - no second `explorer.exe /select,` helper.
        RevealWerQueueCommand = new RelayCommand(() => EtwTraceService.RevealInExplorer(WerFootprint.QueuePath), () => WerFootprint.QueueExists);
        RevealWerArchiveCommand = new RelayCommand(() => EtwTraceService.RevealInExplorer(WerFootprint.ArchivePath), () => WerFootprint.ArchiveExists);

        // #165: both gated behind their own explicit MessageBox confirmation - see EnableLocalDumps/RevertLocalDumps.
        EnableLocalDumpsCommand = new RelayCommand(EnableLocalDumps);
        RevertLocalDumpsCommand = new RelayCommand(RevertLocalDumps, () => CanRevertLocalDumps);
        CanRevertLocalDumps = WerReportService.BackupExists();

        // #195: gated behind its own explicit MessageBox confirmation - see RunLodctrRebuild.
        RunLodctrRebuildCommand = new RelayCommand(RunLodctrRebuild);

        // #171: its own action (runs the RAC task, then re-queries) - not folded into RefreshCommand.
        RefreshReliabilityCommand = new AsyncRelayCommand(RunReliabilityRefreshAsync);

        // #172: gated behind their own explicit MessageBox confirmation, same shape as #165 above.
        EnableReliabilityAnalysisCommand = new RelayCommand(EnableReliabilityAnalysis);
        RevertReliabilityAnalysisCommand = new RelayCommand(RevertReliabilityAnalysis, () => CanRevertReliabilityAnalysis);
        CanRevertReliabilityAnalysis = ReliabilityMonitorService.BackupExists();

        // #137: one chip per source actually wired into BuildTimeline - see TimelineSource's remarks.
        TimelineFilters.Add(new TimelineFilterChip(TimelineSource.EventLog, "Event log", ApplyTimelineFilters));
        TimelineFilters.Add(new TimelineFilterChip(TimelineSource.Minidump, "Minidump", ApplyTimelineFilters));
        TimelineFilters.Add(new TimelineFilterChip(TimelineSource.Boot, "Boot", ApplyTimelineFilters));
        TimelineFilters.Add(new TimelineFilterChip(TimelineSource.Shutdown, "Shutdown", ApplyTimelineFilters));
        TimelineFilters.Add(new TimelineFilterChip(TimelineSource.WerReport, "WER report", ApplyTimelineFilters));

        // Round 14, item 20: MEMORY.DMP folder/copy/delete actions.
        OpenMemoryDumpFolderCommand = new RelayCommand(() =>
        {
            try
            {
                var path = MemoryDump?.FilePath ?? MinidumpParserService.MemoryDmpPath;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { MemoryDumpStatusText = $"Couldn't open folder: {ex.Message}"; }
        }, () => MemoryDump?.Exists == true);

        CopyMemoryDumpCommand = new RelayCommand(() =>
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = "MEMORY.DMP",
                    Filter = "Dump files (*.dmp)|*.dmp|All files (*.*)|*.*",
                };
                if (dlg.ShowDialog() == true && MemoryDump is not null)
                {
                    System.IO.File.Copy(MemoryDump.FilePath, dlg.FileName, overwrite: true);
                    MemoryDumpStatusText = $"Copied to {dlg.FileName}.";
                }
            }
            catch (Exception ex) { MemoryDumpStatusText = $"Couldn't copy: {ex.Message}"; }
        }, () => MemoryDump?.Exists == true);

        DeleteMemoryDumpCommand = new RelayCommand(() =>
        {
            try
            {
                if (MemoryDump is not null) System.IO.File.Delete(MemoryDump.FilePath);
                MemoryDumpStatusText = "MEMORY.DMP deleted.";
                _ = RefreshAsync();
            }
            catch (Exception ex) { MemoryDumpStatusText = $"Couldn't delete: {ex.Message}"; }
        }, () => MemoryDump?.Exists == true);

        // Round 14, item 26: Minidump folder housekeeping.
        DeleteOldDumpsCommand = new RelayCommand(() =>
        {
            int deleted = MinidumpHousekeepingService.DeleteOlderThan(DeleteOlderThanDays);
            HousekeepingStatusText = $"Deleted {deleted} dump file(s) older than {DeleteOlderThanDays} day(s).";
            _ = RefreshAsync();
        });

        SaveMinidumpsCountCommand = new RelayCommand(() =>
        {
            bool ok = MinidumpHousekeepingService.WriteMinidumpsCount(MinidumpsCountInput);
            HousekeepingStatusText = ok
                ? $"MinidumpsCount set to {MinidumpsCountInput}."
                : "Couldn't write the registry value.";
        });

        // Round 14, item 25: symbol path apply/test.
        ApplySymbolPathCommand = new RelayCommand(() =>
        {
            bool ok = SymbolServerService.ApplySymbolPath(SymbolCacheFolder);
            SymbolServerStatusText = ok ? "Symbol path applied for this user." : "Couldn't set the environment variable.";
            OnPropertyChanged(nameof(CurrentSymbolPathText));
        });

        TestSymbolServerCommand = new AsyncRelayCommand(async () =>
        {
            SymbolServerStatusText = "Testing...";
            var (_, detail) = await SymbolServerService.TestSymbolServerReachabilityAsync();
            SymbolServerStatusText = detail;
        });

        // Round 14, item 27: new-dump watcher - fires on a background thread, so hop back to the
        // UI thread (the same "background work off the UI thread, apply on the UI thread"
        // boundary every polled ViewModel in this app already respects) before touching any
        // bound property.
        DismissNewDumpAlertCommand = new RelayCommand(() => { HasNewDumpAlert = false; NewDumpAlertText = null; });
        _dumpWatcher.NewDumpDetected += OnNewDumpDetected;
        _dumpWatcher.Start();

        // Round 16, item 46: crashes/hangs toggle.
        ShowWerCrashesCommand = new RelayCommand(() => ShowWerHangs = false);
        ShowWerHangsCommand = new RelayCommand(() => ShowWerHangs = true);

        // Round 16, item 41: re-enable WER collection - the only mutating action this card offers
        // by default (see WerReportService.EnableWer's remarks on exactly what it does and doesn't
        // touch).
        EnableWerCommand = new RelayCommand(() =>
        {
            bool ok = WerReportService.EnableWer();
            WerStatusActionText = ok
                ? "Windows Error Reporting re-enabled. Refresh to see updated status."
                : "Couldn't write the registry value.";
            if (ok) _ = RefreshAsync();
        });

        // Round 16, item 43: explicit, warned purge - MessageBox confirmation, the same pattern
        // ProcessesViewModel.EndSelected already uses for a destructive action.
        PurgeWerReportsCommand = new RelayCommand(() =>
        {
            var confirm = System.Windows.MessageBox.Show(
                "Permanently delete every WER report folder this app found (machine and per-user, archive and queue)?\nThis destroys crash history and cannot be undone.",
                "Purge WER reports",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            int deleted = WerReportService.PurgeAll();
            WerPurgeStatusText = $"Deleted {deleted} report folder(s).";
            _ = RefreshAsync();
        });

        // Round 16, item 42: LocalDumps capture-settings load/save/clear.
        LoadLocalDumpsConfigCommand = new RelayCommand(() =>
        {
            var cfg = WerReportService.ReadLocalDumpsConfig(NormalizedLocalDumpsTarget());
            if (cfg.Exists)
            {
                LocalDumpsFolder = cfg.DumpFolder ?? LocalDumpsFolder;
                LocalDumpsCount = cfg.DumpCount ?? LocalDumpsCount;
                LocalDumpsType = cfg.DumpType ?? LocalDumpsType;
                LocalDumpsStatusText = $"Loaded existing configuration: {cfg.DumpTypeText}.";
            }
            else
            {
                LocalDumpsStatusText = "No LocalDumps configuration found for this target yet - showing the fields above as-is.";
            }
        });

        SaveLocalDumpsConfigCommand = new RelayCommand(() =>
        {
            bool ok = WerReportService.WriteLocalDumpsConfig(NormalizedLocalDumpsTarget(), LocalDumpsFolder, LocalDumpsCount, LocalDumpsType);
            LocalDumpsStatusText = ok
                ? $"Saved. Windows will now write a dump to {LocalDumpsFolder} the next time {(NormalizedLocalDumpsTarget() ?? "any app")} crashes."
                : "Couldn't write the registry value.";
        });

        ClearLocalDumpsConfigCommand = new RelayCommand(() =>
        {
            bool ok = WerReportService.ClearLocalDumpsConfig(NormalizedLocalDumpsTarget());
            LocalDumpsStatusText = ok ? "Override removed." : "Couldn't remove the registry value(s).";
        });

        // Round 17, item 61: SilentProcessExit load/save/clear - shares LocalDumpsTargetExe
        // above as the target executable field (see this section's own remarks).
        LoadSilentExitConfigCommand = new RelayCommand(() =>
        {
            var cfg = SilentProcessExitService.ReadConfig(LocalDumpsTargetExe);
            if (cfg.Exists)
            {
                SilentExitReportingMode = (cfg.ReportingMode ?? 1).ToString();
                SilentExitLocalDumpFolder = cfg.LocalDumpFolder ?? SilentExitLocalDumpFolder;
                SilentExitMonitorProcess = cfg.MonitorProcess ?? string.Empty;
                SilentExitStatusText = $"Loaded existing configuration: {cfg.ReportingModeText}.";
            }
            else
            {
                SilentExitStatusText = string.IsNullOrWhiteSpace(LocalDumpsTargetExe)
                    ? "SilentProcessExit needs a target executable (it has no global default) - fill in \"Target executable\" above first."
                    : "No SilentProcessExit configuration found for this target yet - showing the fields above as-is.";
            }
        });

        SaveSilentExitConfigCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(LocalDumpsTargetExe))
            {
                SilentExitStatusText = "SilentProcessExit needs a target executable (it has no global default) - fill in \"Target executable\" above first.";
                return;
            }
            int mode = int.TryParse(SilentExitReportingMode, out var m) ? m : 1;
            bool ok = SilentProcessExitService.WriteConfig(LocalDumpsTargetExe.Trim(), mode, SilentExitLocalDumpFolder, SilentExitMonitorProcess);
            SilentExitStatusText = ok
                ? $"Saved. Windows will now report a silent exit of {LocalDumpsTargetExe.Trim()} the way ReportingMode {mode} configures."
                : "Couldn't write the registry value.";
        });

        ClearSilentExitConfigCommand = new RelayCommand(() =>
        {
            bool ok = SilentProcessExitService.ClearConfig(LocalDumpsTargetExe);
            SilentExitStatusText = ok ? "SilentProcessExit configuration removed." : "Couldn't remove the registry value(s).";
        });

        // Item 70: forced-crash toggles - each Enable is gated behind an explicit warning
        // confirmation (the same MessageBox-confirm pattern PurgeWerReportsCommand above already
        // uses for a different, but similarly consequential, destructive action); Disable needs no
        // confirmation since it only ever turns a toggle back off.
        RefreshForcedCrashStatusCommand = new RelayCommand(() => OnPropertyChanged(nameof(ForcedCrashConfigText)));

        EnableCrashOnCtrlScrollCommand = new RelayCommand(() =>
        {
            var confirm = System.Windows.MessageBox.Show(
                "This makes pressing Ctrl+ScrollLock twice deliberately crash (bluescreen) this machine, so a hard hang with no dump today can be captured on demand instead.\n\n" +
                "Writes to HKLM\\...\\kbdhid\\Parameters and HKLM\\...\\i8042prt\\Parameters and only takes effect after a reboot. Continue?",
                "Enable keyboard-initiated crash",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            bool ok = ForcedCrashService.SetCrashOnCtrlScroll(true);
            ForcedCrashStatusText = ok
                ? "Enabled. Reboot for it to take effect - Ctrl+ScrollLock×2 will then bluescreen this machine on demand."
                : "Couldn't write the registry value(s).";
            OnPropertyChanged(nameof(ForcedCrashConfigText));
        });

        DisableCrashOnCtrlScrollCommand = new RelayCommand(() =>
        {
            bool ok = ForcedCrashService.SetCrashOnCtrlScroll(false);
            ForcedCrashStatusText = ok ? "Disabled." : "Couldn't write the registry value(s).";
            OnPropertyChanged(nameof(ForcedCrashConfigText));
        });

        EnableNmiCrashDumpCommand = new RelayCommand(() =>
        {
            var confirm = System.Windows.MessageBox.Show(
                "This tells Windows to bugcheck (and write a dump) when it receives a hardware NMI signal, instead of just logging it - only relevant on hardware that can actually issue an NMI (e.g. a server's NMI button/switch). Continue?",
                "Enable NMI crash dump",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            bool ok = ForcedCrashService.SetNmiCrashDump(true);
            ForcedCrashStatusText = ok ? "Enabled." : "Couldn't write the registry value.";
            OnPropertyChanged(nameof(ForcedCrashConfigText));
        });

        DisableNmiCrashDumpCommand = new RelayCommand(() =>
        {
            bool ok = ForcedCrashService.SetNmiCrashDump(false);
            ForcedCrashStatusText = ok ? "Disabled." : "Couldn't write the registry value.";
            OnPropertyChanged(nameof(ForcedCrashConfigText));
        });

        // Round 18, items 74/76/77/78: crash-dump-configuration write actions - each one is a
        // real, consequential registry/page-file change, so every Enable/Apply/Set below is
        // gated behind an explicit MessageBox confirmation first, the same pattern
        // EnableCrashOnCtrlScrollCommand/PurgeWerReportsCommand already use above for a similarly
        // consequential action; Disable needs no confirmation since it only ever turns a toggle
        // back to a safer state.
        SetDedicatedDumpFileCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(DedicatedDumpFilePath))
            {
                CrashDumpConfigStatusText = "Enter a target path first (e.g. D:\\MEMORY.DMP, on a volume with room).";
                return;
            }
            var confirm = System.Windows.MessageBox.Show(
                $"Point crash dumps at {DedicatedDumpFilePath.Trim()} instead of the system page file's own volume?\n\nWrites HKLM\\...\\CrashControl\\DedicatedDumpFile (and DumpFileSize, if set). Continue?",
                "Set dedicated dump file",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            bool ok = CrashDumpConfigService.WriteDedicatedDumpFile(DedicatedDumpFilePath.Trim(), DedicatedDumpFileSizeMb);
            CrashDumpConfigStatusText = ok ? "Saved. Takes effect on the next crash." : "Couldn't write the registry value(s).";
            if (ok) _ = RefreshAsync();
        });

        ClearDedicatedDumpFileCommand = new RelayCommand(() =>
        {
            bool ok = CrashDumpConfigService.ClearDedicatedDumpFile();
            CrashDumpConfigStatusText = ok ? "Dedicated dump file removed - dumps go back to the system page file's volume." : "Couldn't remove the registry value(s).";
            if (ok) _ = RefreshAsync();
        });

        EnableAutoRebootCommand = new RelayCommand(() =>
        {
            bool ok = CrashDumpConfigService.SetAutoReboot(true);
            CrashDumpConfigStatusText = ok ? "AutoReboot enabled." : "Couldn't write the registry value.";
            if (ok) _ = RefreshAsync();
        });

        DisableAutoRebootCommand = new RelayCommand(() =>
        {
            var confirm = System.Windows.MessageBox.Show(
                "Turns off automatic restart after a crash, so the blue-screen/stop-code screen stays up until manually restarted.\n\nThis is meant as temporary diagnostic practice, not a permanent setting - continue?",
                "Disable auto-reboot on crash",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            bool ok = CrashDumpConfigService.SetAutoReboot(false);
            CrashDumpConfigStatusText = ok ? "AutoReboot disabled." : "Couldn't write the registry value.";
            if (ok) _ = RefreshAsync();
        });

        EnableFastStartupCommand = new RelayCommand(() =>
        {
            bool ok = CrashDumpConfigService.SetHiberbootEnabled(true);
            CrashDumpConfigStatusText = ok ? "Fast Startup enabled." : "Couldn't write the registry value.";
            if (ok) _ = RefreshAsync();
        });

        DisableFastStartupCommand = new RelayCommand(() =>
        {
            var confirm = System.Windows.MessageBox.Show(
                "Turns off Fast Startup (HiberbootEnabled), so a normal shutdown fully clears driver state instead of hibernating it - makes \"did rebooting fix it\" testing meaningful again.\n\nContinue?",
                "Disable Fast Startup",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            bool ok = CrashDumpConfigService.SetHiberbootEnabled(false);
            CrashDumpConfigStatusText = ok ? "Fast Startup disabled." : "Couldn't write the registry value.";
            if (ok) _ = RefreshAsync();
        });

        ApplyRecommendedCrashDumpConfigCommand = new RelayCommand(() =>
        {
            var confirm = System.Windows.MessageBox.Show(
                "Sets the dump type to Automatic memory dump, sets MinidumpsCount to 10, and switches the page file to system-managed so Windows sizes it itself.\n\n" +
                "This changes system-wide page file and crash-dump behavior. Continue?",
                "Apply recommended crash-capture configuration",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            var (ok, notes) = CrashDumpConfigService.ApplyRecommendedConfiguration();
            CrashDumpConfigStatusText = (ok ? "Applied. " : "Some settings couldn't be applied. ") + string.Join(" ", notes);
            _ = RefreshAsync();
        });

        // Round 17, item 59: jump from a restart-loop warning row to the matching Services-tab
        // entry - see JumpToServiceRequested's own remarks.
        JumpToServiceCommand = new RelayCommand(param =>
        {
            if (param is string name && !string.IsNullOrWhiteSpace(name))
                JumpToServiceRequested?.Invoke(name);
        });

        // Round 19, item 86: user-adjustable nag thresholds.
        SaveVerifierNagSettingsCommand = new RelayCommand(() =>
        {
            VerifierEnableHistoryService.SetNagAfterDays(NagAfterDaysInput);
            VerifierEnableHistoryService.SetNagAfterReboots(NagAfterRebootsInput);
            UpdateVerifierEnabledDurationText(VerifierStatus ?? new DriverVerifierStatus());
        });

        // Round 19, item 82: refresh-status button + one-click reset.
        RefreshVerifierStatusCommand = new RelayCommand(() => _ = RefreshVerifierStatusAsync());

        ResetVerifierCommand = new AsyncRelayCommand(async () =>
        {
            var confirm = System.Windows.MessageBox.Show(
                "Clears every Driver Verifier setting (verifier /reset).\n\nA reboot is needed for a currently-running verified session to actually stop, and for the machine to go back to full speed. This app will also forget when Verifier was turned on. Continue?",
                "Reset Driver Verifier",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            bool ok = await DriverVerifierService.ResetAsync();
            VerifierResetStatusText = ok
                ? "Reset. Reboot for this to fully take effect - Driver Verifier will not verify any driver after that reboot."
                : "Couldn't reset Driver Verifier - see the status above for what verifier.exe reported.";
            if (ok) VerifierEnableHistoryService.ClearEnabled();
            await RefreshVerifierStatusAsync();
        });

        // Round 19, items 83/84: guided wizard - restore point -> driver selection -> Safe Mode
        // guidance -> confirm/apply. Every step forward is a plain state change (no destructive
        // action happens until ApplyWizardCommand, which is itself confirmation-gated) - matching
        // this chunk's own instruction that the safety steps are the point, not a checkbox to
        // rush past.
        OpenWizardCommand = new RelayCommand(() =>
        {
            WizardOpen = true;
            WizardStep = 1;
            RestorePointStatusText = string.Empty;
            WizardDriverStatusText = string.Empty;
            WizardApplyStatusText = string.Empty;
            WizardApplyVolatile = false;
            WizardVolatileFlagIndex = 0;
            WizardDriverCandidates.Clear();
        });

        CloseWizardCommand = new RelayCommand(() => WizardOpen = false);

        BackWizardStepCommand = new RelayCommand(param =>
        {
            if (param is string s && int.TryParse(s, out var step)) WizardStep = step;
        });

        CreateRestorePointCommand = new AsyncRelayCommand(async () =>
        {
            RestorePointStatusText = "Creating restore point...";
            var (ok, message) = await Task.Run(() => RestorePointService.TryCreate("Task Manager Plus - before enabling Driver Verifier"));
            RestorePointStatusText = message;
        });

        GoToDriverStepCommand = new RelayCommand(() =>
        {
            WizardStep = 2;
            _ = LoadWizardDriversAsync();
        });

        GoToSafeModeStepCommand = new RelayCommand(() =>
        {
            if (!WizardDriverCandidates.Any(d => d.IsSelected))
            {
                WizardDriverStatusText = "Select at least one driver to verify before continuing.";
                return;
            }
            WizardStep = 3;
        });

        GoToConfirmStepCommand = new RelayCommand(() => WizardStep = 4);

        ApplyWizardCommand = new AsyncRelayCommand(async () =>
        {
            var selected = WizardDriverCandidates.Where(d => d.IsSelected).Select(d => d.FileName).ToList();
            if (selected.Count == 0)
            {
                WizardApplyStatusText = "No drivers selected.";
                return;
            }

            string modeText = WizardApplyVolatile
                ? "immediately, with no reboot (volatile)"
                : "after the next reboot (standard, persistent)";
            var confirm = System.Windows.MessageBox.Show(
                $"Start verifying {selected.Count} driver(s) {modeText}?\n\n{string.Join(", ", selected)}\n\n" +
                "This deliberately makes Windows bugcheck (BSOD) the moment one of these drivers breaks a rule it's now checking - that's the point, it catches the exact offender. Make sure you've noted the Safe Mode recovery steps from the previous step first.",
                "Enable Driver Verifier",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            bool ok;
            string output;
            if (WizardApplyVolatile)
            {
                uint? flag = WizardVolatileFlagIndex > 0
                    ? DriverVerifierService.VolatileFlagOptions[WizardVolatileFlagIndex - 1].Value
                    : null;
                (ok, output) = await DriverVerifierService.ApplyVolatileAsync(selected, flag);
            }
            else
            {
                (ok, output) = await DriverVerifierService.ApplyStandardAsync(selected);
            }

            if (ok) VerifierEnableHistoryService.RecordEnabled();
            WizardApplyStatusText = ok
                ? (WizardApplyVolatile
                    ? "Verifier is now verifying the selected driver(s) - no reboot needed."
                    : "Saved - reboot for Driver Verifier to start verifying the selected driver(s).")
                : $"Couldn't apply the change: {output}";

            if (ok)
            {
                WizardOpen = false;
                await RefreshVerifierStatusAsync();
            }
        });

        // Round 19, item 87: on-demand pool-tag sampling - never a timer, per this chunk's own
        // instructions and CLAUDE.md's "on-demand vs. polled" convention.
        SamplePoolTagsCommand = new AsyncRelayCommand(async () =>
        {
            PoolTagStatusText = "Sampling...";
            var snapshot = await Task.Run(PoolTagMonitorService.Sample);
            if (snapshot is null)
            {
                PoolTagStatusText = "Pool tag information isn't available on this system.";
                return;
            }

            bool isFirstSample = _poolTagBaseline is null;
            _poolTagBaseline ??= snapshot;
            var baselineByTag = _poolTagBaseline.Tags.ToDictionary(t => t.Tag, t => t, StringComparer.Ordinal);

            var rows = snapshot.Tags
                .Select(t =>
                {
                    baselineByTag.TryGetValue(t.Tag, out var baseline);
                    return new PoolTagRow
                    {
                        Tag = t.Tag,
                        NonPagedUsedBytes = t.NonPagedUsedBytes,
                        NonPagedGrowthBytes = t.NonPagedUsedBytes - (baseline?.NonPagedUsedBytes ?? 0),
                        PagedUsedBytes = t.PagedUsedBytes,
                        PagedGrowthBytes = t.PagedUsedBytes - (baseline?.PagedUsedBytes ?? 0),
                        NonPagedAllocs = t.NonPagedAllocs,
                    };
                })
                .Where(r => r.NonPagedUsedBytes > 0 || r.PagedUsedBytes > 0)
                .OrderByDescending(r => r.NonPagedGrowthBytes)
                .ThenByDescending(r => r.NonPagedUsedBytes)
                .Take(200)
                .ToList();

            PoolTagRows.Clear();
            foreach (var r in rows) PoolTagRows.Add(r);

            PoolTagStatusText = isFirstSample
                ? $"Baseline sample taken at {snapshot.TakenAt:T} - {snapshot.Tags.Count} tags. Sample again later in this session to see growth."
                : $"Sampled at {snapshot.TakenAt:T} - {snapshot.Tags.Count} tags, diffed against the baseline from {_poolTagBaseline.TakenAt:T}.";
        });

        ResetPoolTagBaselineCommand = new RelayCommand(() =>
        {
            _poolTagBaseline = null;
            PoolTagRows.Clear();
            PoolTagStatusText = "Baseline cleared - the next sample becomes the new baseline.";
        });

        // Round 19, item 88: targeted Special Pool for one suspect tag - reachable both from a
        // pool-tag row above and from a 0xC2/0xC5 crash's own decoded PoolTagRaw (see
        // StabilityView.xaml). Best-effort owning-driver resolution reuses PoolTagLookup (#35)
        // rather than a second lookup mechanism.
        ApplySpecialPoolCommand = new AsyncRelayCommand(async param =>
        {
            if (param is not string tag || string.IsNullOrWhiteSpace(tag)) return;

            var (driver, source) = PoolTagLookup.Resolve(tag);
            string scopeText = driver is not null
                ? $"scoped to {driver} ({source})"
                : "applied system-wide (no specific owning driver could be identified - the PoolTag registry list still limits the extra memory/performance cost to just this tag)";

            var confirm = System.Windows.MessageBox.Show(
                $"Enable Special Pool for pool tag '{tag}', {scopeText}?\n\n" +
                "This makes Windows catch the exact driver that overruns or underruns an allocation carrying this tag, at the cost of extra memory and speed for those allocations. Requires a reboot to take effect.",
                "Apply Special Pool",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            var (ok, output) = await DriverVerifierService.ApplySpecialPoolForTagAsync(tag, driver);
            SpecialPoolStatusText = ok
                ? $"Special Pool enabled for '{tag}'. Reboot for it to take effect."
                : $"Couldn't apply Special Pool: {output}";
            if (ok) await RefreshVerifierStatusAsync();
        });

        LoadWheaEventsCommand = new AsyncRelayCommand(_ => LoadWheaEventsAsync());
        LoadGpuResetHistoryCommand = new AsyncRelayCommand(_ => LoadGpuResetHistoryAsync());

        _dailyEventColumns = new ColumnSeries<double>
        {
            Values = DailyEventCounts,
            Fill = new SolidColorPaint(SKColors.OrangeRed.WithAlpha(200)),
            Stroke = null,
            MaxBarWidth = 12,
        };
        // #169/Round 11: both independently built their own read of Windows' own per-day
        // reliability/stability index (Win32_ReliabilityStabilityMetrics) - kept as two distinctly
        // named/colored lines rather than merged into one, since they're two separate computed
        // series (ReliabilityIndexPoints vs WmiStabilityIndexValues) and deduplicating risks
        // silently dropping whichever one turns out not to be a pure duplicate of the other.
        _reliabilityIndexLine = new LineSeries<double?>
        {
            Values = ReliabilityIndexPoints,
            Name = "Microsoft reliability index",
            Fill = null,
            GeometrySize = 4,
            GeometryStroke = new SolidColorPaint(SKColors.DeepSkyBlue) { StrokeThickness = 2 },
            GeometryFill = new SolidColorPaint(SKColors.DeepSkyBlue),
            Stroke = new SolidColorPaint(SKColors.DeepSkyBlue) { StrokeThickness = 2 },
            ScalesYAt = 1,
        };

        // #169: Windows' own per-day stability index, overlaid on this same chart area rather than
        // a second standalone chart - "your judgement" call per this chunk's instructions: this is
        // explicitly a comparison against the column series beside it, so one crisp line reads more
        // clearly as "here's the other number" than a whole second chart control would. No glow/
        // gradient pairing (CLAUDE.md's glow+core convention is for standalone history charts -
        // pairing it here would visually compete with the columns underneath). Scaled on its own
        // secondary (right-hand) Y axis since a 1-10 index and a daily event count don't share a
        // sensible scale - see DailyEventYAxes[1] below.
        _wmiStabilityLine = new LineSeries<double?>
        {
            Values = WmiStabilityIndexValues,
            Name = "Windows stability index",
            Stroke = new SolidColorPaint(SKColors.CornflowerBlue, 2),
            Fill = null,
            GeometrySize = 0,
            LineSmoothness = 0,
            ScalesYAt = 1,
        };

        DailyEventSeries = new ISeries[] { _dailyEventColumns, _reliabilityIndexLine, _wmiStabilityLine };
        DailyEventXAxes = new[]
        {
            new Axis
            {
                Labels = Array.Empty<string>(),
                LabelsRotation = 0,
                MinStep = 1,
                ForceStepToMin = true,
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = null,
            },
        };
        DailyEventYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                MinStep = 1,
                Labeler = v => $"{v:0}",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
            // Right-hand axis for the Microsoft reliability index (item 11) / Windows' own #169
            // stability index - fixed 0-10 scale, an entirely different unit from the left axis'
            // daily event count, so it gets its own. Shared by both ScalesYAt=1 lines above (they're
            // both 0-10 index values) rather than each getting a separate axis, since LiveCharts2
            // only supports one axis per index and the app's neutral AxisTextColor styling doesn't
            // favor either line's own color once both share it.
            new Axis
            {
                Position = LiveChartsCore.Measure.AxisPosition.End,
                MinLimit = 0,
                MaxLimit = 10,
                MinStep = 2,
                Labeler = v => $"{v:0}",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = null,
            },
        };

        // #186: WHEA corrected-hardware-error daily count - the same small single-series
        // column-chart shape as DailyEventColumns above, just without the #169 overlay line (WHEA
        // has no equivalent second data source to compare against).
        _wheaDailyColumns = new ColumnSeries<double>
        {
            Values = WheaDailyCounts,
            Fill = new SolidColorPaint(SKColors.Goldenrod.WithAlpha(200)),
            Stroke = null,
            MaxBarWidth = 10,
        };
        WheaDailySeries = new ISeries[] { _wheaDailyColumns };
        WheaDailyXAxes = new[]
        {
            new Axis
            {
                Labels = Array.Empty<string>(),
                LabelsRotation = 0,
                MinStep = 1,
                ForceStepToMin = true,
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = null,
            },
        };
        WheaDailyYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                MinStep = 1,
                Labeler = v => $"{v:0}",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };

        // #488: same ColumnSeries setup as DailyEventColumns above.
        _dailyWheaCorrectedColumns = new ColumnSeries<double>
        {
            Values = DailyWheaCorrectedCounts,
            Fill = new SolidColorPaint(SKColors.OrangeRed.WithAlpha(200)),
            Stroke = null,
            MaxBarWidth = 12,
        };
        DailyWheaCorrectedSeries = new ISeries[] { _dailyWheaCorrectedColumns };

        // #637: corrected-WHEA-errors-per-day column chart - same ColumnSeries shape as the
        // reliability-history chart above, a different (Goldenrod) color to read as a distinct
        // series when the two cards are visually close together on the tab.
        _wheaCorrectedColumns = new ColumnSeries<double>
        {
            Values = WheaCorrectedDailyCounts,
            Fill = new SolidColorPaint(SKColors.Goldenrod.WithAlpha(200)),
            Stroke = null,
            MaxBarWidth = 12,
        };
        WheaCorrectedSeries = new ISeries[] { _wheaCorrectedColumns };
        WheaCorrectedXAxes = new[]
        {
            new Axis
            {
                Labels = Array.Empty<string>(),
                LabelsRotation = 0,
                MinStep = 1,
                ForceStepToMin = true,
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = null,
            },
        };
        WheaCorrectedYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                MinStep = 1,
                Labeler = v => $"{v:0}",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };

        DailyWheaCorrectedXAxes = new[]
        {
            new Axis
            {
                Labels = Array.Empty<string>(),
                LabelsRotation = 0,
                MinStep = 1,
                ForceStepToMin = true,
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = null,
            },
        };
        DailyWheaCorrectedYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                MinStep = 1,
                Labeler = v => $"{v:0}",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };

        // Round 16, item 48: WER-archive-derived long-horizon crash history - same ColumnSeries
        // approach as the Reliability History chart above, minus the second Microsoft-index line
        // (there's no equivalent long-horizon Microsoft series to overlay here).
        _werHistoryColumns = new ColumnSeries<double>
        {
            Values = WerHistoryCounts,
            Fill = new SolidColorPaint(SKColors.OrangeRed.WithAlpha(200)),
            Stroke = null,
            MaxBarWidth = 8,
        };
        WerHistorySeries = new ISeries[] { _werHistoryColumns };
        WerHistoryXAxes = new[]
        {
            new Axis
            {
                Labels = Array.Empty<string>(),
                LabelsRotation = 0,
                MinStep = 1,
                ForceStepToMin = true,
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = null,
            },
        };
        WerHistoryYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                MinStep = 1,
                Labeler = v => $"{v:0}",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };

        // Round 20, item 89: source-filter chips, all on by default - every source contributes to
        // the timeline until the user narrows it down.
        foreach (var (type, label) in new (CrashTimelineSourceType, string)[]
        {
            (CrashTimelineSourceType.Bugcheck, "Bugchecks"),
            (CrashTimelineSourceType.LiveKernelReport, "Live kernel reports"),
            (CrashTimelineSourceType.WerCrash, "WER crashes"),
            (CrashTimelineSourceType.WerHang, "WER hangs"),
            (CrashTimelineSourceType.ApplicationCrash, "Application crashes"),
            (CrashTimelineSourceType.ApplicationHang, "Application hangs"),
            (CrashTimelineSourceType.ServiceFailure, "Service failures"),
            (CrashTimelineSourceType.Tdr, "GPU timeouts (TDR)"),
            (CrashTimelineSourceType.Whea, "Hardware errors (WHEA)"),
            (CrashTimelineSourceType.UnexpectedShutdown, "Unexpected shutdowns"),
        })
        {
            var option = new CrashSourceFilterOption(type, label);
            option.Changed += ApplyTimelineFilter;
            TimelineSourceFilters.Add(option);
        }

        // Round 20, item 91: uptime-at-crash histogram - same ColumnSeries approach as the
        // Reliability History / WER history charts above.
        _uptimeHistogramColumns = new ColumnSeries<double>
        {
            Values = UptimeHistogramCounts,
            Fill = new SolidColorPaint(SKColors.OrangeRed.WithAlpha(200)),
            Stroke = null,
            MaxBarWidth = 28,
        };
        UptimeHistogramSeries = new ISeries[] { _uptimeHistogramColumns };
        UptimeHistogramXAxes = new[]
        {
            new Axis { Labels = Array.Empty<string>(), LabelsRotation = 0, LabelsPaint = new SolidColorPaint(AxisTextColor), SeparatorsPaint = null },
        };
        UptimeHistogramYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                MinStep = 1,
                Labeler = v => $"{v:0}",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };

        // ---- Round 21, items 96/97: Safe Mode / WinRE reboot -----------------------------------
        // Safe Mode is genuinely disruptive - it persists across every subsequent boot, not just
        // the next one, until reverted - so the confirmation names the revert action explicitly,
        // per this chunk's own instructions.
        RebootToSafeModeMinimalCommand = new AsyncRelayCommand(() => RebootToSafeModeAsync(withNetworking: false));
        RebootToSafeModeNetworkCommand = new AsyncRelayCommand(() => RebootToSafeModeAsync(withNetworking: true));

        RebootToRecoveryEnvironmentCommand = new AsyncRelayCommand(async () =>
        {
            var confirm = System.Windows.MessageBox.Show(
                "This restarts the machine immediately into the Windows Recovery Environment (Advanced Startup Options) - save any open work first.\n\nContinue?",
                "Restart into Recovery Environment",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            var (_, message) = await BootRecoveryService.RebootToRecoveryEnvironmentAsync();
            SafeModeStatusText = message;
        });

        RevertSafeModeBootCommand = new AsyncRelayCommand(async () =>
        {
            var (ok, message) = await BootRecoveryService.RevertSafeModeBootAsync();
            SafeModeStatusText = message;
            if (ok) await RefreshBootConfigAsync();
        });

        // ---- Item 98: System Restore points ----------------------------------------------------
        CreateRestorePointFromRecoveryCommand = new AsyncRelayCommand(async () =>
        {
            RestorePointsStatusText = "Creating restore point...";
            var (ok, message) = await Task.Run(() => RestorePointService.TryCreate("Task Manager Plus - manual restore point"));
            RestorePointsStatusText = message;
            if (ok) await RefreshRestorePointsAsync();
        });

        LaunchRstruiCommand = new RelayCommand(() =>
        {
            bool ok = RestorePointService.LaunchRstrui();
            RestorePointsStatusText = ok ? "System Restore opened in a separate window." : "Couldn't launch rstrui.exe.";
        });

        // ---- Item 99: guided repair runner -----------------------------------------------------
        RunSfcCommand = new AsyncRelayCommand(async () =>
        {
            var confirm = System.Windows.MessageBox.Show(
                "Runs sfc /scannow - verifies and repairs Windows' own protected system files. This can take several minutes and will use noticeable CPU/disk while it runs. Continue?",
                "Run System File Checker",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            IsSfcRunning = true;
            SfcResultText = "Running sfc /scannow - this can take several minutes...";
            try
            {
                var (ok, output) = await SystemRepairService.RunSfcAsync();
                SfcResultText = (ok ? "Completed. " : "Finished with a non-zero exit code. ") + output.Trim();
            }
            catch (Exception ex)
            {
                SfcResultText = $"Couldn't run sfc: {ex.Message}";
            }
            finally
            {
                IsSfcRunning = false;
            }
        });

        RunDismCommand = new AsyncRelayCommand(async () =>
        {
            var confirm = System.Windows.MessageBox.Show(
                "Runs DISM /Online /Cleanup-Image /RestoreHealth - repairs the Windows component store itself (what sfc's own repairs draw clean copies from), downloading replacement files from Windows Update if needed. This can take several minutes and needs an internet connection to fully repair anything. Continue?",
                "Run DISM repair",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            IsDismRunning = true;
            DismResultText = "Running DISM /RestoreHealth - this can take several minutes...";
            try
            {
                var (ok, output) = await SystemRepairService.RunDismRestoreHealthAsync();
                DismResultText = (ok ? "Completed. " : "Finished with a non-zero exit code. ") + output.Trim();
            }
            catch (Exception ex)
            {
                DismResultText = $"Couldn't run DISM: {ex.Message}";
            }
            finally
            {
                IsDismRunning = false;
            }
        });

        ScheduleChkdskCommand = new AsyncRelayCommand(async () =>
        {
            var confirm = System.Windows.MessageBox.Show(
                "Schedules a full disk check (chkdsk/autochk) on the system drive for the next time this machine restarts - it runs before Windows finishes starting and can add several minutes to that next boot. This does NOT run now and does NOT restart the machine by itself. Continue?",
                "Schedule a disk check",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            var (_, message) = await SystemRepairService.ScheduleChkdskOnSystemVolumeAsync();
            ChkdskResultText = message;
        });

        LaunchMemoryDiagnosticCommand = new RelayCommand(() =>
        {
            var confirm = System.Windows.MessageBox.Show(
                "Launches the Windows Memory Diagnostic tool, which restarts this machine immediately to run a memory test before Windows loads - save any open work first. Come back to this card and use \"Check for results\" after Windows starts back up. Continue?",
                "Run Windows Memory Diagnostic",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            bool ok = SystemRepairService.LaunchMemoryDiagnostic();
            MemoryDiagnosticStatusText = ok
                ? "Windows Memory Diagnostic launched - the machine will restart shortly."
                : "Couldn't launch mdsched.exe.";
        });

        RefreshMemoryDiagnosticResultsCommand = new AsyncRelayCommand(async () =>
        {
            var results = await Task.Run(() => _service.ReadMemoryDiagnosticsResults(EventLogService.LookbackDays));
            MemoryDiagnosticResults.Clear();
            foreach (var r in results) MemoryDiagnosticResults.Add(r);
            MemoryDiagnosticStatusText = results.Count == 0
                ? $"No Memory Diagnostic results found in the last {EventLogService.LookbackDays} days - either it hasn't been run recently, or the result event has already rolled off the log."
                : $"{results.Count} result(s) found.";
        });

        // ---- Item 100: one-click crash support bundle ------------------------------------------
        BuildSupportBundleCommand = new AsyncRelayCommand(async () =>
        {
            string bundleDir = AppPaths.GetPath("SupportBundles");
            try { System.IO.Directory.CreateDirectory(bundleDir); } catch { /* SaveFileDialog still works without a pre-created folder */ }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save crash support bundle",
                Filter = "Zip files (*.zip)|*.zip|All files (*.*)|*.*",
                DefaultExt = ".zip",
                FileName = $"TaskManagerPlus-CrashSupportBundle-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip",
                InitialDirectory = bundleDir,
            };
            if (dialog.ShowDialog() != true) return;

            IsBuildingSupportBundle = true;
            SupportBundleStatusText = "Building support bundle - this can take a minute or two (system information, event log exports, driver inventory)...";
            try
            {
                var selectedDumps = DumpRows.Where(d => d.IsSelected).Select(d => d.Parsed.FilePath).ToList();
                var selectedWer = _allWerReportRows.Where(r => r.IsSelected).Select(r => r.Report.ReportFolder).ToList();
                var clusters = CrashClusters.Select(c => c.Cluster).ToList();

                var (_, message, _) = await CrashSupportBundleService.BuildAsync(
                    selectedDumps, selectedWer, CrashDumpConfig, clusters, dialog.FileName);
                SupportBundleStatusText = message;
            }
            catch (Exception ex)
            {
                SupportBundleStatusText = $"Couldn't build the support bundle: {ex.Message}";
            }
            finally
            {
                IsBuildingSupportBundle = false;
            }
        });

        _ = RefreshAsync();
        _ = LoadWheaEventsAsync();
        _ = LoadGpuResetHistoryAsync();
    }

    /// <summary>#677: on-demand GPU reset/hang read - same "button + startup load" shape as
    /// LoadWheaEventsAsync above, reusing EventLogService.ReadGpuResetSummary (owned by this class's
    /// own _service instance, same as every other on-demand query on this tab) rather than
    /// duplicating its TDR-parsing logic.</summary>
    private async Task LoadGpuResetHistoryAsync()
    {
        var summary = await Task.Run(() => _service.ReadGpuResetSummary());
        var hangs = await Task.Run(GpuHangHistoryService.Load);

        GpuTdrEvents.Clear();
        foreach (var e in summary.TdrEvents) GpuTdrEvents.Add(e);
        GpuUnrecoveredResetCount = summary.UnrecoveredResetCount;

        GpuHangEvents.Clear();
        foreach (var h in hangs) GpuHangEvents.Add(h);

        GpuResetHistoryStatusText = summary.TdrEvents.Count == 0 && summary.DeviceRemovedEvents.Count == 0 && hangs.Count == 0
            ? "No GPU resets or pre-TDR hangs found."
            : string.Empty;
    }

    private async Task RebootToSafeModeAsync(bool withNetworking)
    {
        string modeLabel = withNetworking ? "Safe Mode with Networking" : "Safe Mode (minimal)";
        var confirm = System.Windows.MessageBox.Show(
            $"This restarts the machine immediately into {modeLabel}, and - unlike a normal Safe Mode F8 boot - it will keep booting into Safe Mode on every restart after this one too, not just this one, until you come back to this Recovery section and use \"Revert Safe Mode boot\" to undo it.\n\n" +
            "Save any open work first. Continue?",
            "Reboot into Safe Mode",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        var (_, message) = await BootRecoveryService.RebootToSafeModeAsync(withNetworking);
        SafeModeStatusText = message;
    }

    private async Task RefreshBootConfigAsync()
    {
        BootConfigAudit = await BootRecoveryService.ReadBootConfigAuditAsync();
    }

    private async Task RefreshRestorePointsAsync()
    {
        SystemProtectionStatus = await Task.Run(RestorePointService.ReadSystemProtectionStatus);
    }

    /// <summary>Item 42: null (global default) for a blank/whitespace-only target executable
    /// textbox, else the trimmed exe name - shared by the Load/Save/Clear commands above so all
    /// three always agree on what "the current target" means.</summary>
    private string? NormalizedLocalDumpsTarget()
        => string.IsNullOrWhiteSpace(LocalDumpsTargetExe) ? null : LocalDumpsTargetExe.Trim();

    /// <summary>Item 70: plain-English read-out of both forced-crash toggles' current registry
    /// state - a mismatch between the two CrashOnCtrlScroll keys (set under one driver but not the
    /// other) is called out explicitly rather than folded into a flat yes/no, since which key
    /// actually matters depends on hardware this app can't reliably detect.</summary>
    private static string BuildForcedCrashConfigText()
    {
        static string Describe(bool? v) => v switch { true => "Enabled", false => "Disabled", null => "Not set" };

        var (kbdhid, i8042) = ForcedCrashService.ReadCrashOnCtrlScrollStatus();
        string ctrlScroll = (kbdhid, i8042) switch
        {
            (true, true) => "Enabled",
            (null, null) => "Not configured (Windows default: off)",
            (false, false) => "Disabled",
            _ => $"Partially configured (kbdhid: {Describe(kbdhid)}, i8042prt: {Describe(i8042)}) - set both the same way for this to reliably work",
        };

        return $"Ctrl+ScrollLock crash: {ctrlScroll} · NMI crash dump: {Describe(ForcedCrashService.ReadNmiCrashDumpEnabled())}";
    }

    /// <summary>Round 19, items 81/82/86: re-reads verifier.exe's status and recomputes the
    /// "enabled for N days" text (VerifierNagDue/VerifierEnabledDurationText below). Called once
    /// from RefreshAsync (this tab's own on-demand load/Refresh-button cadence, which runs once
    /// automatically at startup since every tab ViewModel is constructed eagerly - CLAUDE.md's
    /// architecture notes) and again after any wizard/reset/special-pool action that could have
    /// changed Verifier's live state. SummaryViewModel's Health Check card (item 82) reads
    /// VerifierNagDue/VerifierEnabledDurationText straight off this ViewModel via its own existing
    /// `_stability` reference - the same "read a sibling ViewModel's already-computed property"
    /// shape every other Health Check rule already uses, rather than a second cache.</summary>
    private async Task RefreshVerifierStatusAsync()
    {
        var status = await DriverVerifierService.ReadStatusAsync();
        VerifierStatus = status;
        UpdateVerifierEnabledDurationText(status);
    }

    /// <summary>Item 86: "Verifier has been enabled for N days," or null when there's nothing to
    /// say (Verifier isn't running right now). An unknown enable time (running, but never recorded
    /// by this app's own wizard - e.g. turned on manually, or by an older app version) is still
    /// flagged as nag-due rather than silently skipped, since "we don't know how long" is itself a
    /// reason to go check.</summary>
    private void UpdateVerifierEnabledDurationText(DriverVerifierStatus status)
    {
        // Load the persisted nag thresholds into their editable copies unconditionally (not just
        // while Verifier happens to be running right now) - they're a standing preference, not
        // session state, so the input boxes should always reflect whatever was last saved.
        var history = VerifierEnableHistoryService.Load();
        NagAfterDaysInput = history.NagAfterDays;
        NagAfterRebootsInput = history.NagAfterReboots;

        if (!status.IsRunning)
        {
            VerifierEnabledDurationText = null;
            VerifierNagDue = false;
            return;
        }

        // Item 86's "or reboots" half - only meaningful to track while Verifier is actually
        // confirmed running, same cadence RefreshVerifierStatusAsync already calls this at.
        VerifierEnableHistoryService.RecordBootObservationIfChanged();
        history = VerifierEnableHistoryService.Load();

        if (history.EnabledAtUtc is not { } enabledAtUtc)
        {
            VerifierEnabledDurationText = "Verifier is running, but this app doesn't know when it was turned on (it wasn't enabled through this app's wizard, or the record was lost).";
            VerifierNagDue = true;
            return;
        }

        int days = Math.Max(0, (int)(DateTime.UtcNow - enabledAtUtc).TotalDays);
        int reboots = history.RebootsSinceEnabled;
        VerifierEnabledDurationText = days == 0
            ? $"Verifier was enabled less than a day ago ({reboots} reboot{(reboots == 1 ? "" : "s")} since)."
            : $"Verifier has been enabled for {days} day{(days == 1 ? "" : "s")} ({reboots} reboot{(reboots == 1 ? "" : "s")} since).";
        VerifierNagDue = days >= history.NagAfterDays || reboots >= history.NagAfterReboots;
    }

    /// <summary>Item 83's driver-selection step - scans loaded non-Microsoft drivers off the UI
    /// thread and populates WizardDriverCandidates.</summary>
    private async Task LoadWizardDriversAsync()
    {
        WizardDriverStatusText = "Scanning loaded drivers...";
        WizardDriverCandidates.Clear();

        var candidates = await DriverVerifierService.ListNonMicrosoftDriversAsync();
        foreach (var c in candidates) WizardDriverCandidates.Add(new DriverVerifierCandidateRow(c));

        WizardDriverStatusText = candidates.Count == 0
            ? "No non-Microsoft loaded drivers were found on this system."
            : $"{candidates.Count} non-Microsoft loaded driver(s) found - select the ones to verify.";
    }

    /// <summary>Repaints chart axis text/gridlines to match the active theme family - see
    /// PerformanceViewModel.ApplyAxisTheme's remarks.</summary>
    public void ApplyAxisTheme(System.Windows.Media.Color text, System.Windows.Media.Color separator)
    {
        var textSk = new SKColor(text.R, text.G, text.B);
        var sepSk = new SKColor(separator.R, separator.G, separator.B, separator.A);
        DailyEventXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        DailyEventYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        DailyEventYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        DailyEventYAxes[1].LabelsPaint = new SolidColorPaint(textSk);
        WerHistoryXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        WerHistoryYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        WerHistoryYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        UptimeHistogramXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        UptimeHistogramYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        UptimeHistogramYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };

        WheaDailyXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        WheaDailyYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        WheaDailyYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };

        DailyWheaCorrectedXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        DailyWheaCorrectedYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        DailyWheaCorrectedYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };

        WheaCorrectedXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        WheaCorrectedYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        WheaCorrectedYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var snapshotTask = Task.Run(() => _service.Query());
            // #239/#240: same on-demand refresh, run alongside the main event-log query rather than
            // after it - a WER folder walk and a second targeted event-log query are each
            // independent I/O, no reason to serialize them.
            var appHangsTask = Task.Run(AppHangReportService.Read);
            var hangEventsTask = Task.Run(() => _service.ReadApplicationHangEvents(TimeSpan.FromDays(30)));
            await Task.WhenAll(snapshotTask, appHangsTask, hangEventsTask);

            var snapshot = snapshotTask.Result;

            var timeline = await Task.Run(PowerTimelineService.Read);
            // #741: reuses the timeline just read above rather than re-querying the System log.
            var failedResumes = await Task.Run(() => PowerTimelineService.ReadFailedResumes(timeline));
            // #781: a second, independent event-log read (the WU client log, not the System/
            // Application logs _service.Query() above already covers) - correlated against
            // snapshot.RecentEvents' own faulting-module data, no new query beyond this one read.
            var wuEvents = await Task.Run(WindowsUpdateHistoryService.ReadUpdateClientHistory);
            var breakageFlags = WindowsUpdateHistoryService.CorrelateWithStabilityFailures(wuEvents, snapshot.RecentEvents);
            Apply(snapshot, timeline, failedResumes, breakageFlags);

            AppHangs.Clear();
            foreach (var h in appHangsTask.Result) AppHangs.Add(h);

            HangEventHistory.Clear();
            foreach (var h in hangEventsTask.Result) HangEventHistory.Add(h);

            // #122: a second, narrow query scoped to exactly the KB's flagged serious IDs (folded
            // into this same on-demand refresh, not a new timer) - see EventLogService.
            // ScanForKnownBadIds's remarks for why this can't just reuse RecentEvents above.
            var flaggedIds = _kb.SeriousFlaggedIds();
            var hits = await Task.Run(() => _service.ScanForKnownBadIds(flaggedIds));
            BuildKnownBadIdScorecard(hits);

            // #161-167: WER report queue/archive scan, hangs, storage footprint, LocalDumps and
            // error-reporting-config reads - folded into this same on-demand refresh, never a new timer.
            await RefreshWerAsync();

            // #184-190: kernel/storage/driver event-family cards - folded into this same on-demand
            // refresh, never a new timer.
            await RefreshKernelEventFamiliesAsync();

            // #195/#196/#199: Perflib card, the assorted-family rollup, and log-clear detection -
            // folded into this same on-demand refresh, never a new timer.
            await RefreshSubsystemAndLogHealthAsync();

            // #137/#142/#143/#144: folded into this same on-demand refresh, never a new timer.
            await RefreshTimelineExtrasAsync(snapshot);

            // #169/#170/#172/#173/#174: Reliability Monitor data - folded into this same on-demand
            // refresh (never a new timer), run after RefreshTimelineExtrasAsync above so #173's
            // correlation can see this refresh's own crash-flagged timeline entries.
            await RefreshReliabilityMonitorAsync();

            // Round 14: the binary dump parse (items 13-22) is independent of the event-log
            // query above, computed on a background thread, and applied separately - a failure
            // here doesn't blank out anything Apply() already populated.
            var bundle = await Task.Run(BuildDumpAnalysisBundle);
            ApplyDumpAnalysis(bundle);

            // Round 16, items 38-49: WER archive/queue scan - independent of both reads above
            // (its own file-system/registry sources), computed on a background thread and applied
            // separately, same shape as the dump-analysis bundle just above.
            var werBundle = await Task.Run(BuildWerBundle);
            ApplyWerBundle(werBundle);

            // Round 17, items 50-63: application crash/hang forensics - independent of the two
            // reads above (its own event-log queries plus a couple of registry reads), computed
            // off the UI thread and applied separately, same shape as the two bundles above. Runs
            // after werBundle so item 53's hang join has werBundle's own AppHang reports to join
            // against without a second WER scan.
            var crashBundle = await Task.Run(() => BuildCrashForensicsBundle(werBundle.HangReports));
            ApplyCrashForensicsBundle(crashBundle);

            // Round 20, items 89-95: unified timeline/clusters/MTBF - a pure layer over the
            // snapshot/bundle/werBundle/crashBundle data every step above just finished building
            // (no new expensive query here; items 92-95's own new queries are deferred, on-demand,
            // per-cluster/per-row - see CrashClusterViewModel/CrashTimelineRowViewModel).
            var correlationBundle = await Task.Run(() => BuildCorrelationBundle(snapshot, bundle, werBundle, crashBundle));
            ApplyCorrelationBundle(correlationBundle);

            // Round 18, items 71-80: dump configuration and capture-health checklist -
            // independent of everything above (its own registry/WMI/page-file/powercfg/
            // manage-bde reads), applied separately, same shape as the bundles above.
            var crashDumpConfig = await CrashDumpConfigService.ReadConfigurationAsync();
            ApplyCrashDumpConfig(crashDumpConfig);

            // Round 19, items 81/82/86: Driver Verifier status - independent of everything above
            // (its own verifier.exe shell-out), applied separately, same shape as the reads above.
            await RefreshVerifierStatusAsync();

            // Round 21, items 96-98: boot configuration audit + restore points - both cheap reads
            // (one bcdedit call, one WMI enumeration), refreshed automatically here too, same
            // cadence as VerifierStatus/CrashDumpConfig above rather than needing their own button.
            await RefreshBootConfigAsync();
            await RefreshRestorePointsAsync();

            RefreshErrorText = null;
        }
        catch (Exception ex)
        {
            RefreshErrorText = $"Couldn't refresh stability data: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }

        // #141: fires whether or not the refresh above succeeded - PerformanceViewModel just wants
        // whatever RecentEvents currently holds, same as every other reader of this tab's data.
        Refreshed?.Invoke();
    }

    /// <summary>#137/#142/#143/#144: builds the unified timeline plus the sleep/resume, who-
    /// rebooted, and boot-ledger cards. Each sub-computation is wrapped independently so one failing
    /// scan (e.g. a locked-down channel, or a Windows edition without the WindowsUpdateClient
    /// operational log enabled) doesn't blank out the others that already succeeded - the same
    /// "degrade, never fabricate" rule every event-log read in this app follows.</summary>
    private async Task RefreshTimelineExtrasAsync(StabilitySnapshot snapshot)
    {
        List<DateTime> bootMarkers = new();
        try { bootMarkers = await Task.Run(() => _anomaly.FindBootMarkers(30, CancellationToken.None)); }
        catch { /* degrade to no boot markers on the timeline */ }

        List<RebootAttribution> attributions = new();
        try
        {
            attributions = await Task.Run(() => _timeline.ComputeRebootAttributions());
            RebootAttributions.Clear();
            foreach (var a in attributions) RebootAttributions.Add(a);
        }
        catch { /* degrade to an empty "who rebooted" list */ }

        try
        {
            var cycles = await Task.Run(() => _timeline.ReconstructSleepResumeCycles());
            SleepResumeCycles.Clear();
            foreach (var c in cycles) SleepResumeCycles.Add(c);
        }
        catch { /* degrade to an empty sleep/resume list */ }

        try
        {
            var ledger = await Task.Run(() => _timeline.BuildBootLedger());

            // #144 enrichment: join each session's end with the closest #143 attribution (within 5
            // minutes) so the ledger's EndReason names who/what caused it, not just "clean"/"unclean".
            foreach (var session in ledger)
            {
                if (session.EndTime is not { } end) continue;
                var match = attributions
                    .Where(a => Math.Abs((a.Timestamp - end).TotalMinutes) <= 5)
                    .OrderBy(a => Math.Abs((a.Timestamp - end).TotalMinutes))
                    .FirstOrDefault();
                if (match is not null) session.EndReason = match.Answer;
            }

            BootLedger.Clear();
            foreach (var s in ledger) BootLedger.Add(s);
        }
        catch { /* degrade to an empty boot ledger */ }

        try
        {
            _allTimelineEntries = await Task.Run(() => _timeline.BuildTimeline(snapshot.RecentEvents, snapshot.Minidumps, bootMarkers, attributions, werReports: _lastWerReports));
            ApplyTimelineFilters();
        }
        catch { /* degrade to an empty timeline */ }
    }

    /// <summary>#161-167: WER report queue/archive scan (buckets + top crashing apps), hangs, storage
    /// footprint, and the LocalDumps/error-reporting-config reads - each wrapped independently so one
    /// failing part (e.g. a locked-down ProgramData folder, or WerSvc missing on this Windows
    /// edition) doesn't blank out the others that already succeeded, same as
    /// RefreshTimelineExtrasAsync above.</summary>
    private async Task RefreshWerAsync()
    {
        try { _lastWerReports = await Task.Run(() => _wer.ReadReports()); }
        catch { _lastWerReports = new List<WerReportInfo>(); }

        try
        {
            var buckets = _wer.GroupByBucket(_lastWerReports);
            CrashReportBuckets.Clear();
            foreach (var b in buckets) CrashReportBuckets.Add(b);
        }
        catch { /* degrade to an empty bucket list */ }

        try
        {
            var topApps = await Task.Run(() => _wer.ComputeTopCrashingApplications(_lastWerReports));
            TopCrashingApplications.Clear();
            foreach (var a in topApps) TopCrashingApplications.Add(a);
        }
        catch { /* degrade to an empty top-crashing-apps list */ }

        try
        {
            var hangs = await Task.Run(() => _wer.ReadHangs());
            Hangs.Clear();
            foreach (var h in hangs) Hangs.Add(h);
        }
        catch { /* degrade to an empty hang list */ }

        try { WerFootprint = await Task.Run(() => _wer.ComputeStorageFootprint()); }
        catch { /* degrade - keeps whatever footprint the last successful scan found */ }

        try { LocalDumpsSettings = await Task.Run(() => _wer.ReadLocalDumpsSettings()); }
        catch { /* degrade - keeps its previous value */ }
        CanRevertLocalDumps = WerReportService.BackupExists();

        try { WerConfigStatus = await Task.Run(() => _wer.ReadConfigStatus()); }
        catch { /* degrade - keeps its previous value (Unknown on first load) */ }
    }

    /// <summary>#184-190: each family read is wrapped independently so one failing part (a locked-
    /// down channel, driverquery.exe blocked by policy, vssadmin.exe missing) doesn't blank out the
    /// others that already succeeded - the same tolerance RefreshWerAsync/RefreshTimelineExtrasAsync
    /// above already apply to their own multi-part reads.</summary>
    private async Task RefreshKernelEventFamiliesAsync()
    {
        try
        {
            var groups = await Task.Run(() => _kernelFamily.ReadStorageErrors());
            StorageErrorGroups.Clear();
            foreach (var g in groups) StorageErrorGroups.Add(g);
        }
        catch { /* degrade to an empty storage-error list */ }

        try
        {
            var status = await Task.Run(() => _kernelFamily.ReadShadowCopyStatus());
            ShadowCopyEvents.Clear();
            foreach (var e in status.Events) ShadowCopyEvents.Add(e);
            ShadowStorageVolumes.Clear();
            foreach (var v in status.StorageVolumes) ShadowStorageVolumes.Add(v);
            ShadowCopyStatusText = status.VssAdminError;
        }
        catch (Exception ex) { ShadowCopyStatusText = $"Couldn't read shadow copy status: {ex.Message}"; }

        try
        {
            WheaRateSummary = await Task.Run(() => _kernelFamily.ReadWheaErrors());
            WheaDailyCounts.Clear();
            foreach (var d in WheaRateSummary.DailyCounts) WheaDailyCounts.Add(d.Count);
            WheaDailyXAxes[0].Labels = WheaRateSummary.DailyCounts
                .Select((d, i) => i % 5 == 0 ? d.Date.ToString("M/d") : string.Empty)
                .ToArray();
        }
        catch { /* degrade to an empty WHEA chart */ }

        try
        {
            var failures = await Task.Run(() => _kernelFamily.ReadDriverFailures());
            DriverFailures.Clear();
            foreach (var f in failures) DriverFailures.Add(f);
        }
        catch { /* degrade to an empty driver-failures list */ }

        try
        {
            var chkdsk = await Task.Run(() => _kernelFamily.ReadChkdskResults());
            ChkdskResults.Clear();
            foreach (var c in chkdsk) ChkdskResults.Add(c);
        }
        catch { /* degrade to an empty chkdsk list */ }

        try { MemoryDiagnosticStatus = await Task.Run(() => _kernelFamily.ReadMemoryDiagnosticStatus()); }
        catch { /* degrade - keeps its previous value */ }

        try
        {
            var incidents = await Task.Run(() => _kernelFamily.ReadPowerTransitionIncidents());
            PowerTransitionIncidents.Clear();
            foreach (var i in incidents) PowerTransitionIncidents.Add(i);
        }
        catch { /* degrade to an empty power-transition list */ }
    }

    /// <summary>#195/#196/#199: Perflib counter-corruption card, the assorted subsystem-family
    /// rollup, and log-clear detection - each read wrapped independently so one failing part doesn't
    /// blank out the others, same tolerance every other multi-part refresh step in this method
    /// already applies.</summary>
    private async Task RefreshSubsystemAndLogHealthAsync()
    {
        try { PerflibSummary = await Task.Run(() => _subsystemFamily.ReadPerflibFailures()); }
        catch { /* degrade - keeps whatever the last successful scan found */ }

        try
        {
            var families = await Task.Run(() => _subsystemFamily.ReadSubsystemFamilies());
            SubsystemFamilies.Clear();
            foreach (var f in families) SubsystemFamilies.Add(f);
        }
        catch { /* degrade to an empty rollup */ }

        try
        {
            var clears = await Task.Run(() => _logHealth.DetectLogClearEvents());
            LogClearEvents.Clear();
            foreach (var c in clears) LogClearEvents.Add(c);
        }
        catch { /* degrade to an empty list - never claim "no clears" when the scan itself failed */ }
    }

    /// <summary>#195: explicit confirmation before running `lodctr /R` - a real system change (it
    /// resets every performance-counter provider's registration to its installed defaults), same
    /// "state what the write does, then confirm" shape as EnableLocalDumps/EnableReliabilityAnalysis
    /// above. No backup/revert offered here (unlike #165/#172's registry-value toggles) - lodctr /R
    /// is a repair action with no meaningful "previous state" to restore, not a settings change.</summary>
    private void RunLodctrRebuild()
    {
        var confirm = MessageBox.Show(
            "This runs `lodctr /R`, which rebuilds the Windows performance-counter registry from the "
            + ".ini files each counter provider installed with. This can take a minute or more, and "
            + "every performance-counter provider currently in a broken state is reset to its "
            + "installed defaults.\n\n"
            + "Rebuild the performance-counter registry now?",
            "Rebuild performance counters",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _ = RunLodctrRebuildInnerAsync();
    }

    private async Task RunLodctrRebuildInnerAsync()
    {
        PerflibStatusText = "Running lodctr /R - this can take a minute or more…";
        var (success, output) = await SubsystemErrorFamilyService.RunLodctrRebuildAsync();
        string trimmed = output.Length > 300 ? output[..300] + "…" : output;
        PerflibStatusText = success
            ? "Performance-counter registry rebuilt. Click Refresh to see if the Perflib failures above clear."
            : $"lodctr /R reported a problem: {trimmed}";
    }

    /// <summary>#169/#170/#172/#173/#174: Reliability Monitor data - reads Windows' own per-day
    /// stability index and the full reliability record feed, applies #172's disabled-collection
    /// gate (hiding the #169/#170/#173 cards rather than showing them empty), cross-highlights
    /// #170's informational records against this refresh's own crash clusters (#173), and computes
    /// the index-disagreement flag (#174). Factored out of RefreshAsync into its own method since
    /// #171's "Refresh reliability data" button re-runs exactly this step (after running the RAC
    /// task) without re-running everything else in RefreshAsync.</summary>
    private async Task RefreshReliabilityMonitorAsync()
    {
        try { ReliabilityAnalysisStatus = await Task.Run(() => _reliability.ReadAnalysisStatus()); }
        catch { /* degrade - keeps its previous value (enabled/Unknown on first load) */ }
        CanRevertReliabilityAnalysis = ReliabilityMonitorService.BackupExists();

        IsReliabilityMonitorAvailable = !ReliabilityAnalysisStatus.IsCollectionDisabled;
        if (!IsReliabilityMonitorAvailable)
        {
            // #172: collection is off - hide the #169/#170/#173 cards entirely rather than show an
            // empty chart/grid (CLAUDE.md's "degrade to hidden" convention). Clear anything left
            // over from a previous refresh so a stale chart/list doesn't linger under a hidden card.
            WmiStabilityIndexValues.Clear();
            ReliabilityRecords.Clear();
            SoftwareChangeLog.Clear();
            WindowsStabilityIndexRecent = null;
            ApplyIndexDisagreement();
            return;
        }

        try
        {
            var samples = await Task.Run(() => _reliability.ReadStabilityMetrics());
            var daily = ReliabilityMonitorService.BuildDailyIndex(samples, Math.Max(DailyEventCounts.Count, 1));

            WmiStabilityIndexValues.Clear();
            foreach (var v in daily) WmiStabilityIndexValues.Add(v);

            // #174: recent-window average (last 7 days that actually have a WMI sample) - null when
            // none do, so ApplyIndexDisagreement below never flags a disagreement against nothing.
            var recentWithData = daily.TakeLast(7).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            WindowsStabilityIndexRecent = recentWithData.Count > 0 ? Math.Round(recentWithData.Average(), 1) : null;
        }
        catch
        {
            // degrade to whatever the previous refresh already showed
        }

        ApplyIndexDisagreement();

        try
        {
            var records = await Task.Run(() => _reliability.ReadRecords());

            // #173: cross-highlight the informational subset against crash clusters from this same
            // refresh's already-built unified timeline (#137) - correlation only, see
            // ReliabilityMonitorService.CorrelateChangesWithCrashClusters's remarks.
            var crashTimestamps = _allTimelineEntries.Where(e => e.IsCrash).Select(e => e.Timestamp).ToList();
            ReliabilityMonitorService.CorrelateChangesWithCrashClusters(records, crashTimestamps);

            ReliabilityRecords.Clear();
            foreach (var r in records) ReliabilityRecords.Add(r);

            SoftwareChangeLog.Clear();
            foreach (var r in records.Where(r => r.Category == ReliabilityRecordCategory.Informational))
                SoftwareChangeLog.Add(r);
        }
        catch
        {
            // degrade - ReliabilityRecords/SoftwareChangeLog keep whatever the last successful read
            // produced rather than being cleared out from under a transient failure.
        }
    }

    /// <summary>
    /// #174: flags when Windows' own recent-window index (averaged over the last 7 days that
    /// actually have a WMI sample - #169) and this app's own StabilityIndex (its own weighted
    /// formula - see ComputeStabilityIndex) diverge by more than IndexDisagreementThreshold (2.0)
    /// points on the shared 1-10 scale. The two are expected to disagree sometimes - different
    /// weightings (this app's own formula vs. Windows' undocumented internal one), different
    /// lookback windows (a last-7-day average here vs. whatever period RAC's own aggregation
    /// covers), and Windows' number lags behind however long it's been since the RAC task last ran
    /// (#171) - so this explains the divergence rather than implying either number is "the correct
    /// one" (CLAUDE.md's "quick flag, not a verdict"). Never flagged when there's no WMI data to
    /// compare against at all (WindowsStabilityIndexRecent is null) - nothing to disagree with isn't
    /// a disagreement.
    /// </summary>
    private void ApplyIndexDisagreement()
    {
        if (WindowsStabilityIndexRecent is not { } windowsIndex)
        {
            IndicesDisagree = false;
            IndexDisagreementText = null;
            return;
        }

        double diff = Math.Abs(windowsIndex - StabilityIndex);
        IndicesDisagree = diff > IndexDisagreementThreshold;
        IndexDisagreementText = IndicesDisagree
            ? $"Windows' own Reliability Monitor index ({windowsIndex:0.0}/10, last 7 days) and this app's stability index "
              + $"({StabilityIndex:0.0}/10) disagree by {diff:0.0} points. That's expected sometimes — the two use different "
              + "weightings, different lookback windows, and Windows' number lags until the RAC task next re-aggregates "
              + "(see \"Refresh reliability data\" below). Neither number is more correct than the other."
            : null;
    }

    /// <summary>#171: runs the RAC scheduled task, then re-runs RefreshReliabilityMonitorAsync - a
    /// separate action from the general RefreshCommand per this chunk's instructions, since this one
    /// also performs the schtasks side-effect first, not just a read.</summary>
    private async Task RunReliabilityRefreshAsync()
    {
        IsReliabilityRefreshing = true;
        ReliabilityRefreshStatusText = "Running the RAC scheduled task...";
        try
        {
            var (_, message) = await ReliabilityMonitorService.RunRacTaskAsync();
            ReliabilityRefreshStatusText = message;

            // Re-query regardless of whether schtasks itself reported success - #171's own goal is
            // "the last few hours of failures actually appear", and a re-query costs nothing even if
            // the task run failed or Windows is still catching up.
            await RefreshReliabilityMonitorAsync();
        }
        catch (Exception ex)
        {
            ReliabilityRefreshStatusText = $"Couldn't refresh reliability data: {ex.Message}";
        }
        finally
        {
            IsReliabilityRefreshing = false;
        }
    }

    /// <summary>#172: explicit confirmation before the registry write, mirroring EnableLocalDumps
    /// below (and WerReportService's LocalDumps toggle, #165) exactly - states what the write does,
    /// saves the pre-change value first (ReliabilityMonitorService.SaveBackup) so
    /// RevertReliabilityAnalysis below can restore it even after an app restart.</summary>
    private void EnableReliabilityAnalysis()
    {
        var confirm = MessageBox.Show(
            "This writes to HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Reliability Analysis\\WMI\\WMIEnable "
            + "(sets it to 1) so Windows resumes recording Reliability Monitor data on this PC.\n\n"
            + "Re-enable Reliability Monitor data collection now?",
            "Re-enable Reliability Monitor",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var previous = _reliability.ReadAnalysisStatus();
        ReliabilityMonitorService.SaveBackup(previous);

        var (success, error) = _reliability.EnableAnalysis();
        if (success)
        {
            ReliabilityAnalysisStatus = _reliability.ReadAnalysisStatus();
            CanRevertReliabilityAnalysis = true;
            IsReliabilityMonitorAvailable = !ReliabilityAnalysisStatus.IsCollectionDisabled;
            ReliabilityAnalysisStatusText = "Reliability Monitor data collection re-enabled - new data will start appearing the next time the RAC task runs (see \"Refresh reliability data\" below).";
        }
        else
        {
            ReliabilityMonitorService.ClearBackup(); // nothing actually changed - don't leave a stale backup around
            ReliabilityAnalysisStatusText = $"Couldn't re-enable Reliability Monitor: {error}";
        }
    }

    /// <summary>#172: one-click revert - restores whatever WMIEnable looked like right before
    /// EnableReliabilityAnalysis above last wrote to it. Mirrors RevertLocalDumps below.</summary>
    private void RevertReliabilityAnalysis()
    {
        var backup = ReliabilityMonitorService.LoadBackup();
        if (backup is null)
        {
            ReliabilityAnalysisStatusText = "No previous Reliability Monitor configuration was saved to revert to.";
            return;
        }

        var confirm = MessageBox.Show(
            "This restores the Reliability Analysis\\WMI registry configuration to what it was before this app last changed it"
            + (backup.KeyExists ? "." : " (the WMIEnable value didn't exist before - it will be removed again.)")
            + "\n\nRevert Reliability Monitor data collection now?",
            "Revert Reliability Monitor",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = _reliability.RestoreAnalysisStatus(backup);
        if (success)
        {
            ReliabilityMonitorService.ClearBackup();
            ReliabilityAnalysisStatus = _reliability.ReadAnalysisStatus();
            CanRevertReliabilityAnalysis = false;
            IsReliabilityMonitorAvailable = !ReliabilityAnalysisStatus.IsCollectionDisabled;
            ReliabilityAnalysisStatusText = "Reliability Monitor configuration reverted to its previous state.";
        }
        else
        {
            ReliabilityAnalysisStatusText = $"Couldn't revert Reliability Monitor configuration: {error}";
        }
    }

    /// <summary>#165: a real confirmation dialog stating the disk-space implication, matching the
    /// "explicit permission required for a registry write" convention CLAUDE.md documents - never
    /// writes without this. Saves the pre-change values first (WerReportService.SaveBackup) so
    /// RevertLocalDumps below can restore them even after an app restart.</summary>
    private void EnableLocalDumps()
    {
        string suggestedFolder = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\CrashDumps");

        var confirm = MessageBox.Show(
            "This writes to HKLM\\SOFTWARE\\Microsoft\\Windows\\Windows Error Reporting\\LocalDumps so Windows "
            + "keeps a local copy of future crash dumps instead of only uploading them and discarding the copy.\n\n"
            + $"Dumps will be written to:\n{suggestedFolder}\n\n"
            + "Up to 10 mini dumps will be kept (older ones are deleted automatically as new ones arrive) - each "
            + "one is small, but a machine with several crashing apps will accumulate more of them over time.\n\n"
            + "Enable local crash dump capture now?",
            "Enable local crash dump capture",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var previous = _wer.ReadLocalDumpsSettings();
        WerReportService.SaveBackup(previous);

        var (success, error) = _wer.WriteLocalDumpsSettings(suggestedFolder, dumpCount: 10, dumpType: 1);
        if (success)
        {
            LocalDumpsSettings = _wer.ReadLocalDumpsSettings();
            CanRevertLocalDumps = true;
            LocalDumpsToggleStatusText = $"Local crash dump capture enabled - dumps will be written to {suggestedFolder}.";
        }
        else
        {
            WerReportService.ClearBackup(); // nothing actually changed - don't leave a stale backup around
            LocalDumpsToggleStatusText = $"Couldn't enable local crash dump capture: {error}";
        }
    }

    /// <summary>#165: one-click revert - restores whatever LocalDumps looked like right before
    /// EnableLocalDumps above last wrote to it (persisted to disk, so this still works after an app
    /// restart, not just within the same session).</summary>
    private void RevertLocalDumps()
    {
        var backup = WerReportService.LoadBackup();
        if (backup is null)
        {
            LocalDumpsToggleStatusText = "No previous local crash dump configuration was saved to revert to.";
            return;
        }

        var confirm = MessageBox.Show(
            "This restores the LocalDumps registry configuration to what it was before this app last changed it"
            + (backup.KeyExists ? "." : " (the LocalDumps key didn't exist before - it will be removed again.)")
            + "\n\nRevert local crash dump capture now?",
            "Revert local crash dump capture",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var (success, error) = _wer.RestoreLocalDumpsSettings(backup);
        if (success)
        {
            WerReportService.ClearBackup();
            LocalDumpsSettings = _wer.ReadLocalDumpsSettings();
            CanRevertLocalDumps = false;
            LocalDumpsToggleStatusText = "Local crash dump configuration reverted to its previous state.";
        }
        else
        {
            LocalDumpsToggleStatusText = $"Couldn't revert local crash dump capture: {error}";
        }
    }

    /// <summary>#137: re-filters the already-built merged list by whichever source chips are
    /// currently checked - never re-queries anything.</summary>
    private void ApplyTimelineFilters()
    {
        var enabledSources = TimelineFilters.Where(f => f.IsEnabled).Select(f => f.Source).ToHashSet();
        Timeline.Clear();
        foreach (var entry in _allTimelineEntries.Where(e => enabledSources.Contains(e.Source)))
            Timeline.Add(entry);
    }

    /// <summary>#138: "±5 minutes, every readable channel" for whatever timeline entry was clicked -
    /// reuses EventLogExplorerService.ReadMultiChannel/BuildStructuredQuery exactly the way
    /// EventsViewModel.ShowAroundTimeAsync already does for a selected grid row. Channel list
    /// defaults to System+Application (the Stability tab has no channel selector of its own) plus
    /// the entry's own channel, when it has one and it isn't already one of those two.</summary>
    private async Task DrillDownAsync(TimelineEntry? entry)
    {
        if (entry is null) return;

        var start = entry.Timestamp.ToUniversalTime().AddMinutes(-5);
        var end = entry.Timestamp.ToUniversalTime().AddMinutes(5);
        string xpath = $"*[System[TimeCreated[@SystemTime>='{start:o}'] and TimeCreated[@SystemTime<='{end:o}']]]";

        var channels = new List<string> { "System", "Application" };
        if (entry.SourceEvent?.ChannelName is { Length: > 0 } ch && !channels.Contains(ch, StringComparer.OrdinalIgnoreCase))
            channels.Add(ch);

        string structuredXml = EventLogExplorerService.BuildStructuredQuery(channels, xpath);

        IsCrashWindowLoading = true;
        CrashWindowResults.Clear();
        CrashWindowStatusText = $"Loading events within +/-5 minutes of {entry.Timestamp:g}...";
        try
        {
            var result = await Task.Run(() => _drillDownExplorer.ReadMultiChannel(structuredXml, null, pageSize: 500));
            if (result.ErrorText is not null)
            {
                CrashWindowStatusText = $"Couldn't load the surrounding window: {result.ErrorText}";
                return;
            }
            foreach (var r in result.Rows) CrashWindowResults.Add(r);
            CrashWindowStatusText = $"{CrashWindowResults.Count} event(s) within +/-5 minutes of {entry.Timestamp:g} (all levels).";
        }
        finally
        {
            IsCrashWindowLoading = false;
        }
    }

    /// <summary>#139: "changes shortly before this crash" for whatever crash-flagged timeline entry
    /// was clicked - explicitly correlation, not causation (see StabilityView.xaml's card copy).</summary>
    private async Task FindChangesBeforeCrashAsync(TimelineEntry? entry)
    {
        if (entry is null) return;

        ChangesBeforeCrash.Clear();
        ChangeAttributionStatusText = $"Looking for changes in the 7 days before {entry.Timestamp:g}...";
        try
        {
            var changes = await Task.Run(() => _timeline.FindChangesBeforeCrash(entry.Timestamp));
            foreach (var c in changes) ChangesBeforeCrash.Add(c);
            ChangeAttributionStatusText = changes.Count == 0
                ? "No driver/update/service-install changes found in the 7 days before this crash."
                : $"{changes.Count} change(s) found in the 7 days before this crash — correlation, not proof of cause.";
        }
        catch (Exception ex)
        {
            ChangeAttributionStatusText = $"Couldn't search for preceding changes: {ex.Message}";
        }
    }

    /// <summary>Round 14: everything computed off the UI thread for the "Dump analysis"
    /// section - kept as one plain data bundle (rather than touching ObservableCollections
    /// directly from the background thread) so ApplyDumpAnalysis can do the actual UI-thread
    /// collection updates in one place, matching Apply(StabilitySnapshot)'s shape above.</summary>
    private sealed class DumpAnalysisBundle
    {
        public DebuggerAvailability Debugger { get; init; } = new();
        public List<ParsedDumpInfo> ParsedDumps { get; init; } = new();

        // Round 15, items 28-37: one BugcheckDecodedInfo per ParsedDumps entry, same index -
        // computed here (off the UI thread) rather than inside DumpRowViewModel's constructor,
        // since item #35's pool-tag resolution can involve a bounded driver-binary scan.
        public List<BugcheckDecodedInfo> DecodedInfos { get; init; } = new();
        public MemoryDumpInfo MemoryDump { get; init; } = new();
        public BugcheckDecodedInfo? MemoryDumpDecoded { get; init; }
        public List<LiveKernelReportInfo> LiveKernelReports { get; init; } = new();
        public List<CommonDriverRow> CommonDrivers { get; init; } = new();
        public MinidumpHousekeepingInfo Housekeeping { get; init; } = new();
    }

    private static DumpAnalysisBundle BuildDumpAnalysisBundle()
    {
        var debugger = DebuggerToolsService.DetectDebugger();
        var parsedDumps = MinidumpParserService.ScanMinidumpFolder();
        var memDump = MinidumpParserService.ReadMemoryDumpInfo();
        var liveReports = MinidumpParserService.ScanLiveKernelReports();
        var housekeeping = MinidumpHousekeepingService.ReadHousekeeping();

        var decodedInfos = parsedDumps
            .Select(p => BugcheckDecoder.Decode(p.BugcheckCode, p.BugcheckParameters))
            .ToList();
        var memDumpDecoded = memDump.Parsed is not null
            ? BugcheckDecoder.Decode(memDump.Parsed.BugcheckCode, memDump.Parsed.BugcheckParameters)
            : null;

        // Item 19: intersected across every dump this app could parse a module list from,
        // including MEMORY.DMP when it happens to be in the (much rarer) minidump/triage format.
        var forCommon = new List<ParsedDumpInfo>(parsedDumps);
        if (memDump.Parsed is not null) forCommon.Add(memDump.Parsed);

        return new DumpAnalysisBundle
        {
            Debugger = debugger,
            ParsedDumps = parsedDumps,
            DecodedInfos = decodedInfos,
            MemoryDump = memDump,
            MemoryDumpDecoded = memDumpDecoded,
            LiveKernelReports = liveReports,
            CommonDrivers = MinidumpParserService.FindCommonDrivers(forCommon),
            Housekeeping = housekeeping,
        };
    }

    private void ApplyDumpAnalysis(DumpAnalysisBundle bundle)
    {
        Debugger = bundle.Debugger;

        // Round 16, item 49: unsubscribe the outgoing rows' IsSelected notifications before
        // clearing, so SelectedDumpCount doesn't keep listening to rows this refresh is about to
        // discard.
        foreach (var old in DumpRows) old.PropertyChanged -= OnDumpRowPropertyChanged;

        DumpRows.Clear();
        for (int i = 0; i < bundle.ParsedDumps.Count; i++)
        {
            var row = new DumpRowViewModel(bundle.ParsedDumps[i], bundle.Debugger, bundle.DecodedInfos[i]);
            row.PropertyChanged += OnDumpRowPropertyChanged;
            DumpRows.Add(row);
        }
        OnPropertyChanged(nameof(SelectedDumpCount));

        MemoryDump = bundle.MemoryDump;
        MemoryDumpRow = bundle.MemoryDump.Exists && bundle.MemoryDump.Parsed is not null
            ? new DumpRowViewModel(bundle.MemoryDump.Parsed, bundle.Debugger, bundle.MemoryDumpDecoded ?? new BugcheckDecodedInfo())
            : null;
        OpenMemoryDumpFolderCommand.RaiseCanExecuteChanged();
        CopyMemoryDumpCommand.RaiseCanExecuteChanged();
        DeleteMemoryDumpCommand.RaiseCanExecuteChanged();

        LiveKernelReports.Clear();
        foreach (var r in bundle.LiveKernelReports) LiveKernelReports.Add(r);

        CommonDrivers.Clear();
        foreach (var c in bundle.CommonDrivers) CommonDrivers.Add(c);

        Housekeeping = bundle.Housekeeping;
        MinidumpsCountInput = bundle.Housekeeping.MinidumpsCountRegistryValue ?? 50;
    }

    private void OnDumpRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DumpRowViewModel.IsSelected))
            OnPropertyChanged(nameof(SelectedDumpCount));
    }

    /// <summary>Round 16, items 38-49: everything computed off the UI thread for the "Error
    /// reports" card - kept as one plain data bundle, same shape as DumpAnalysisBundle above, so
    /// ApplyWerBundle can do the actual UI-thread collection updates in one place.</summary>
    private sealed class WerBundle
    {
        public List<WerBucketGroup> CrashBuckets { get; init; } = new();
        public List<WerReport> HangReports { get; init; } = new();
        public WerCollectionStatus Status { get; init; } = new();
        public WerQueueSizeInfo QueueSize { get; init; } = new();
        public List<WerDailyCount> History { get; init; } = new();
    }

    private WerBundle BuildWerBundle()
    {
        var reports = WerReportService.ScanAll();

        // Item 47: join to the Application-log event 1000 (Application Error) - a longer lookback
        // than EventLogService.LookbackDays' default 30 days, since item 48's WER-archive history
        // commonly reaches back further than that.
        var appEvents = _service.ReadApplicationCrashEvents(WerHistoryDays);
        reports = WerReportService.JoinApplicationErrorEvents(reports, appEvents);

        var crashes = reports.Where(r => !r.IsHang).ToList();
        var hangs = reports.Where(r => r.IsHang).OrderByDescending(r => r.ReportTimestamp).ToList();

        return new WerBundle
        {
            CrashBuckets = WerReportService.GroupByBucket(crashes),
            HangReports = hangs,
            Status = WerReportService.ReadCollectionStatus(),
            QueueSize = WerReportService.ReadQueueSize(),
            History = WerReportService.BuildLongHorizonHistory(reports, WerHistoryDays),
        };
    }

    private void ApplyWerBundle(WerBundle bundle)
    {
        // Item 49: unsubscribe every outgoing row before rebuilding, same reason as
        // ApplyDumpAnalysis's DumpRows loop above.
        foreach (var old in _allWerReportRows) old.PropertyChanged -= OnWerReportRowPropertyChanged;
        _allWerReportRows.Clear();

        WerCrashBuckets.Clear();
        foreach (var bucket in bundle.CrashBuckets)
        {
            var rows = bucket.Reports.Select(r => new WerReportRowViewModel(r)).ToList();
            foreach (var row in rows)
            {
                row.PropertyChanged += OnWerReportRowPropertyChanged;
                _allWerReportRows.Add(row);
            }
            WerCrashBuckets.Add(new WerBucketRowViewModel(bucket, rows));
        }

        WerHangReports.Clear();
        foreach (var report in bundle.HangReports)
        {
            var row = new WerReportRowViewModel(report);
            row.PropertyChanged += OnWerReportRowPropertyChanged;
            _allWerReportRows.Add(row);
            WerHangReports.Add(row);
        }
        OnPropertyChanged(nameof(SelectedWerReportCount));

        WerStatus = bundle.Status;
        WerCollectionSummaryText = BuildWerCollectionSummaryText(bundle.Status);
        WerConsentSummaryText = BuildWerConsentSummaryText(bundle.Status);

        WerQueueSize = bundle.QueueSize;

        WerHistoryCounts.Clear();
        foreach (var d in bundle.History) WerHistoryCounts.Add(d.Count);
        WerHistoryXAxes[0].Labels = bundle.History
            .Select((d, i) => i % 10 == 0 ? d.Date.ToString("M/d") : string.Empty)
            .ToArray();
    }

    private void OnWerReportRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WerReportRowViewModel.IsSelected))
            OnPropertyChanged(nameof(SelectedWerReportCount));
    }

    /// <summary>Items 41: plain-English summary of whether WER is even collecting crash data at
    /// all - separate from item 44's consent/prompt summary below since these two groups of
    /// registry values gate genuinely different things (whether a report is captured locally, vs.
    /// whether/how it's sent and whether a dialog appears).</summary>
    private static string BuildWerCollectionSummaryText(WerCollectionStatus status)
    {
        string disabled = status.Disabled switch { true => "Yes", false => "No", null => "Not set (enabled)" };
        string dontSend = status.DontSendAdditionalData switch { true => "Yes", false => "No", null => "Not set" };
        return $"Collection disabled: {disabled} · Don't send additional data: {dontSend} · WerSvc: {status.ServiceStatusText}" +
            (status.ServiceLooksBlocked ? " (start type: Disabled)" : string.Empty);
    }

    /// <summary>Item 44: plain-English DefaultConsent/DontShowUI read-out.</summary>
    private static string BuildWerConsentSummaryText(WerCollectionStatus status)
    {
        string dontShowUi = status.DontShowUi switch
        {
            true => "Yes (reports are sent silently, no crash dialog)",
            false => "No (the crash dialog is shown)",
            null => "Not set (Windows default applies)",
        };
        return $"Default consent: {status.DefaultConsentText} · Don't show UI: {dontShowUi}";
    }

    // ---------------------------------------------------------------------------------------
    // Round 17, items 50-63: application crash/hang forensics bundle - same "compute off the UI
    // thread, apply on the UI thread" shape as DumpAnalysisBundle/WerBundle above.
    // ---------------------------------------------------------------------------------------

    // Item 60's 30-day window - independent of item 48's 90-day WER-archive history and the
    // plain 30-day EventLogService.LookbackDays used elsewhere on this tab (same number, kept as
    // its own constant since it's driven by item 60's own "crashes (30d)" wording, not shared
    // state with the rest of the tab).
    private const int CrashForensicsLookbackDays = 30;

    private sealed class CrashForensicsBundle
    {
        public List<ApplicationCrashEvent> Crashes { get; init; } = new();
        public List<AppCrashLeaderboardRow> Leaderboard { get; init; } = new();
        public List<ApplicationHangEvent> Hangs { get; init; } = new();
        public List<ManagedExceptionEvent> ManagedExceptions { get; init; } = new();
        public List<ManagedExceptionClusterRow> ManagedExceptionClusters { get; init; } = new();
        public List<ServiceFailureEvent> ServiceFailures { get; init; } = new();
        public List<ServiceRestartLoopWarning> ServiceRestartLoops { get; init; } = new();
        public PostmortemDebuggerInfo PostmortemDebugger { get; init; } = new();

        // Item 66: persisted per-executable hang history - a local JSON read (HangHistoryService),
        // not an event-log query, but grouped into this same bundle so it refreshes on the same
        // cadence as everything else on this card rather than needing its own load path.
        public List<HangHistoryEntry> HangHistory { get; init; } = new();
    }

    private CrashForensicsBundle BuildCrashForensicsBundle(List<WerReport> werHangReports)
    {
        // Items 50/51: raw structured parse (item 51's exception-code text is filled in inside
        // the parse itself - see EventLogService.ReadApplicationCrashEvents).
        var rawCrashes = _service.ReadApplicationCrashEvents(CrashForensicsLookbackDays);
        // Items 56/57: foreign-module flag + injection-surface cross-check.
        var crashes = ApplicationCrashService.EnrichWithModuleForensics(rawCrashes);
        // Item 52: per-application leaderboard over the enriched list.
        var leaderboard = ApplicationCrashService.BuildLeaderboard(crashes);

        // Item 53: hang events, joined to the WER AppHang reports werBundle already scanned.
        var rawHangs = _service.ReadApplicationHangEvents(CrashForensicsLookbackDays);
        var hangs = WerReportService.JoinApplicationHangEvents(rawHangs, werHangReports);

        // Item 60 support: refresh the cross-tab crash-count cache from the same lists just read
        // above - no second event-log query for the Processes tab's own "crashes (30d)" column.
        CrashHistoryCacheService.UpdateFrom(crashes, hangs);

        // Items 54/55: managed exceptions + clustering.
        var managedExceptions = _service.ReadClrExceptionEvents(CrashForensicsLookbackDays);
        var clusters = BuildManagedExceptionClusters(managedExceptions);

        // Items 58/59: service failures + restart-loop detection.
        var serviceFailures = _service.ReadServiceFailureEvents(CrashForensicsLookbackDays);
        var restartLoops = DetectServiceRestartLoops(serviceFailures);

        // Item 62: postmortem debugger + IFEO audit - a plain registry read, cheap enough to run
        // alongside everything else here rather than needing its own button.
        var postmortem = PostmortemDebuggerService.Read();

        // Item 66: persisted hang history, most frequent offender first.
        var hangHistory = HangHistoryService.Load().Entries
            .OrderByDescending(h => h.HangCount)
            .ThenByDescending(h => h.LastHangTime)
            .ToList();

        return new CrashForensicsBundle
        {
            Crashes = crashes,
            Leaderboard = leaderboard,
            Hangs = hangs,
            ManagedExceptions = managedExceptions,
            ManagedExceptionClusters = clusters,
            ServiceFailures = serviceFailures,
            ServiceRestartLoops = restartLoops,
            PostmortemDebugger = postmortem,
            HangHistory = hangHistory,
        };
    }

    private void ApplyCrashForensicsBundle(CrashForensicsBundle bundle)
    {
        ApplicationCrashes.Clear();
        foreach (var c in bundle.Crashes.OrderByDescending(c => c.TimeCreated)) ApplicationCrashes.Add(c);

        AppCrashLeaderboard.Clear();
        foreach (var l in bundle.Leaderboard) AppCrashLeaderboard.Add(l);

        ApplicationHangs.Clear();
        foreach (var h in bundle.Hangs.OrderByDescending(h => h.TimeCreated)) ApplicationHangs.Add(h);

        ManagedExceptions.Clear();
        foreach (var m in bundle.ManagedExceptions.OrderByDescending(m => m.TimeCreated)) ManagedExceptions.Add(m);

        ManagedExceptionClusters.Clear();
        foreach (var c in bundle.ManagedExceptionClusters) ManagedExceptionClusters.Add(c);

        ServiceFailures.Clear();
        foreach (var f in bundle.ServiceFailures.OrderByDescending(f => f.TimeCreated)) ServiceFailures.Add(f);

        ServiceRestartLoopWarnings.Clear();
        foreach (var w in bundle.ServiceRestartLoops) ServiceRestartLoopWarnings.Add(w);

        PostmortemDebugger = bundle.PostmortemDebugger;

        HangHistory.Clear();
        foreach (var h in bundle.HangHistory) HangHistory.Add(h);
    }

    // ---------------------------------------------------------------------------------------
    // Round 20, items 89-95: unified timeline / clusters / MTBF - a pure layer over the plain
    // Model lists the snapshot/bundle/werBundle/crashBundle above already hold, same
    // "compute off the UI thread, apply on the UI thread" shape as every other bundle on this
    // tab. See CrashCorrelationService for the actual computation.
    // ---------------------------------------------------------------------------------------

    private sealed class CorrelationBundle
    {
        public List<CrashTimelineRow> Timeline { get; init; } = new();
        public List<CrashCluster> Clusters { get; init; } = new();
        public List<UptimeAtCrashBucket> Histogram { get; init; } = new();
        public MtbfSummary Mtbf { get; init; } = new();
    }

    private static CorrelationBundle BuildCorrelationBundle(
        StabilitySnapshot snapshot, DumpAnalysisBundle dumpBundle, WerBundle werBundle, CrashForensicsBundle crashBundle)
    {
        var werCrashReports = werBundle.CrashBuckets.SelectMany(b => b.Reports).ToList();

        var timeline = CrashCorrelationService.BuildTimeline(
            minidumps: snapshot.Minidumps,
            parsedDumps: dumpBundle.ParsedDumps,
            liveKernelReports: dumpBundle.LiveKernelReports,
            werCrashReports: werCrashReports,
            werHangReports: werBundle.HangReports,
            applicationCrashes: crashBundle.Crashes,
            applicationHangs: crashBundle.Hangs,
            serviceFailures: crashBundle.ServiceFailures,
            tdrEvents: snapshot.TdrEventDetails,
            wheaErrors: snapshot.WheaErrors,
            unexpectedShutdowns: snapshot.UnexpectedShutdowns);

        var clusters = CrashCorrelationService.BuildClusters(timeline);

        // Item 91: reuses the boot markers item 6's own shutdown/restart timeline already found
        // (EventLogService.ReadShutdownTimeline's Kernel-General/Kernel-Boot pairing walk) rather
        // than a second boot-time query.
        var bootTimes = snapshot.ShutdownTimeline.Where(e => e.Kind == "Boot").Select(e => e.TimeCreated).ToList();

        // "A crash" for MTBF purposes means a machine-level crash (something that actually took
        // the whole system down) - bugchecks, watchdog-triggered live kernel reports, and any
        // unexpected shutdown this app itself classified as bugcheck-caused; BuildUptimeHistogramAndMtbf
        // de-dupes near-simultaneous entries from these different sources describing the same event.
        var crashTimes = snapshot.Minidumps.Select(m => m.Timestamp)
            .Concat(dumpBundle.LiveKernelReports.Select(l => l.Timestamp))
            .Concat(snapshot.UnexpectedShutdowns.Where(u => u.Cause == ShutdownCause.Bugcheck).Select(u => u.TimeCreated))
            .ToList();

        var (histogram, mtbf) = CrashCorrelationService.BuildUptimeHistogramAndMtbf(crashTimes, bootTimes);

        return new CorrelationBundle { Timeline = timeline, Clusters = clusters, Histogram = histogram, Mtbf = mtbf };
    }

    private void ApplyCorrelationBundle(CorrelationBundle bundle)
    {
        _unifiedTimeline = bundle.Timeline;
        ApplyTimelineFilter();

        CrashClusters.Clear();
        foreach (var c in bundle.Clusters) CrashClusters.Add(new CrashClusterViewModel(c));

        UptimeHistogramCounts.Clear();
        foreach (var b in bundle.Histogram) UptimeHistogramCounts.Add(b.Count);
        UptimeHistogramXAxes[0].Labels = bundle.Histogram.Select(b => b.Label).ToArray();

        MtbfSummaryText = BuildMtbfSummaryText(bundle.Mtbf);
    }

    /// <summary>Item 89: rebuilds FilteredTimeline from the unfiltered _unifiedTimeline against
    /// whichever TimelineSourceFilters chips are currently checked - called once after every
    /// refresh and again whenever a chip is toggled. Capped at MaxTimelineRowsShown (newest first)
    /// so a busy 30-day window doesn't fully realize hundreds of row view models (and their own
    /// lazy chart state) at once.</summary>
    private void ApplyTimelineFilter()
    {
        var active = TimelineSourceFilters.Where(f => f.IsChecked).Select(f => f.SourceType).ToHashSet();
        FilteredTimeline.Clear();
        foreach (var row in _unifiedTimeline.Where(r => active.Contains(r.SourceType)).Take(MaxTimelineRowsShown))
            FilteredTimeline.Add(new CrashTimelineRowViewModel(row));
    }

    /// <summary>Item 91: plain-English MTBF/longest-streak read-out under the uptime histogram.</summary>
    private static string BuildMtbfSummaryText(MtbfSummary mtbf)
    {
        if (mtbf.CrashCount == 0) return "No kernel-level crash found in the current lookback window.";

        var parts = new List<string> { $"{mtbf.CrashCount} kernel-level crash(es) across {mtbf.BootCount} known boot(s)." };
        parts.Add(mtbf.MeanTimeBetweenFailures is { } m
            ? $"MTBF: about {CrashCorrelationService.FormatTimeSpan(m)}."
            : "MTBF: not enough crashes yet to compute (need at least two).");
        if (mtbf.LongestCrashFreeStreak is { } s)
            parts.Add($"Longest crash-free streak: {CrashCorrelationService.FormatTimeSpan(s)}.");
        if (mtbf.UnknownUptimeCount > 0)
            parts.Add($"{mtbf.UnknownUptimeCount} crash(es) had no matching boot record, so aren't in the histogram above.");

        return string.Join(" ", parts);
    }

    /// <summary>Round 18, items 71-80: applies the freshly-read CrashDumpConfiguration plus its
    /// derived checklist (item 80) - see CrashDumpConfigService.BuildChecklist's remarks on how
    /// the verdict is computed from the individual rows.</summary>
    private void ApplyCrashDumpConfig(CrashDumpConfiguration cfg)
    {
        CrashDumpConfig = cfg;

        var checklist = CrashDumpConfigService.BuildChecklist(cfg);
        CrashCaptureChecklistItems.Clear();
        foreach (var item in checklist.Items) CrashCaptureChecklistItems.Add(item);
        CrashCaptureVerdict = checklist.Verdict;
        CrashCaptureVerdictText = checklist.VerdictText;

        // Item 74: prefill the dedicated-dump-file inputs from whatever's currently configured,
        // without clobbering a value the user is actively typing into an as-yet-unsaved field.
        if (string.IsNullOrWhiteSpace(DedicatedDumpFilePath) && !string.IsNullOrWhiteSpace(cfg.DedicatedDumpFile))
            DedicatedDumpFilePath = cfg.DedicatedDumpFile;
        if (DedicatedDumpFileSizeMb == 0 && cfg.DumpFileSizeMb is { } mb)
            DedicatedDumpFileSizeMb = mb;
    }

    /// <summary>Item 55: clusters the already-parsed managed-exception list by (ExceptionType, top
    /// stack frame) - the same "flat list -> grouped cluster" shape CrashesByModule/WheaSummary
    /// already use elsewhere on this tab, applied to managed exceptions instead.</summary>
    private static List<ManagedExceptionClusterRow> BuildManagedExceptionClusters(List<ManagedExceptionEvent> events)
    {
        return events
            .GroupBy(e => (Type: e.ExceptionType ?? "Unknown", Frame: e.TopFrameText), StringComparer_ExceptionCluster.Instance)
            .Select(g => new ManagedExceptionClusterRow
            {
                ExceptionType = g.Key.Type,
                TopFrame = g.Key.Frame,
                Count = g.Count(),
                LastSeen = g.Max(e => e.TimeCreated),
                ApplicationName = g.Select(e => e.ApplicationName).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
            })
            .OrderByDescending(c => c.Count)
            .ThenByDescending(c => c.LastSeen)
            .ToList();
    }

    /// <summary>Item 59: for each service with at least one 7031/7034 ("terminated unexpectedly")
    /// occurrence, finds the densest 60-minute sliding window of those events across the whole
    /// lookback window (a two-pointer scan over the sorted timestamps) - a service whose worst
    /// window reaches RestartLoopThreshold or more is a chronic restart loop, surfaced as a
    /// warning row even though it never produced a single user-visible crash dialog.</summary>
    private static readonly TimeSpan RestartLoopWindow = TimeSpan.FromHours(1);
    private const int RestartLoopThreshold = 3;

    private static List<ServiceRestartLoopWarning> DetectServiceRestartLoops(List<ServiceFailureEvent> failures)
    {
        var result = new List<ServiceRestartLoopWarning>();

        var byService = failures
            .Where(f => (f.EventId == 7031 || f.EventId == 7034) && !string.IsNullOrWhiteSpace(f.ServiceName))
            .GroupBy(f => f.ServiceName!, StringComparer.OrdinalIgnoreCase);

        foreach (var g in byService)
        {
            var times = g.Select(f => f.TimeCreated).OrderBy(t => t).ToList();
            int best = 0;
            DateTime bestStart = default, bestEnd = default;
            int left = 0;
            for (int right = 0; right < times.Count; right++)
            {
                while (times[right] - times[left] > RestartLoopWindow) left++;
                int windowCount = right - left + 1;
                if (windowCount > best) { best = windowCount; bestStart = times[left]; bestEnd = times[right]; }
            }

            if (best >= RestartLoopThreshold)
            {
                result.Add(new ServiceRestartLoopWarning
                {
                    ServiceName = g.Key,
                    OccurrencesInWindow = best,
                    WindowStart = bestStart,
                    WindowEnd = bestEnd,
                    LastSeen = times[^1],
                });
            }
        }

        return result.OrderByDescending(w => w.OccurrencesInWindow).ThenByDescending(w => w.LastSeen).ToList();
    }

    /// <summary>Tiny tuple-key comparer for BuildManagedExceptionClusters' GroupBy above - the
    /// default (Type, Frame) tuple equality is case-sensitive, which would split
    /// "System.NullReferenceException" and a differently-cased duplicate into two clusters.</summary>
    private sealed class StringComparer_ExceptionCluster : IEqualityComparer<(string Type, string Frame)>
    {
        public static readonly StringComparer_ExceptionCluster Instance = new();
        public bool Equals((string Type, string Frame) x, (string Type, string Frame) y) =>
            string.Equals(x.Type, y.Type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Frame, y.Frame, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Type, string Frame) obj) =>
            HashCode.Combine(obj.Type.ToUpperInvariant(), obj.Frame.ToUpperInvariant());
    }

    /// <summary>Round 14, item 27: fired on the FileSystemWatcher's own background thread -
    /// hops to the UI thread before touching any bound property, then waits briefly before
    /// reading the file (Windows can still be mid-write right when Created fires) so the toast
    /// text has a real bugcheck description instead of always falling back to the bare file
    /// name.</summary>
    private void OnNewDumpDetected(string filePath)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.InvokeAsync(async () =>
        {
            string fileName = System.IO.Path.GetFileName(filePath);
            string description = $"A new crash dump was written: {fileName}";

            await Task.Delay(2000);
            try
            {
                var parsed = await Task.Run(() => MinidumpParserService.ParseDumpFile(filePath));
                if (!string.IsNullOrEmpty(parsed.BugcheckCode))
                    description = $"A new crash dump was written — {BugcheckCodeLookup.Describe(parsed.BugcheckCode)}";
            }
            catch { /* best-effort - fall back to the plain file-name message */ }

            HasNewDumpAlert = true;
            NewDumpAlertText = description;
            ShowTrayToastRequested?.Invoke("Task Manager Plus — new crash dump", description);

            _ = RefreshAsync();
        });
    }

    public void Dispose()
    {
        _dumpWatcher.NewDumpDetected -= OnNewDumpDetected;
        _dumpWatcher.Dispose();
    }

    private void Apply(StabilitySnapshot snapshot, List<PowerTimelineEntry> timeline, List<FailedResumeEntry> failedResumes, List<UpdateBreakageFlag> breakageFlags)
    {
        PowerTimeline.Clear();
        foreach (var e in timeline) PowerTimeline.Add(e);

        FailedResumes.Clear();
        foreach (var f in failedResumes) FailedResumes.Add(f);

        UpdateBreakageFlags.Clear();
        foreach (var f in breakageFlags) UpdateBreakageFlags.Add(f);

        RecentEvents.Clear();
        foreach (var e in snapshot.RecentEvents) RecentEvents.Add(e);

        Minidumps.Clear();
        foreach (var d in snapshot.Minidumps) Minidumps.Add(d);

        WasLastShutdownUnexpected = snapshot.WasLastShutdownUnexpected;
        LastUnexpectedShutdownText = snapshot.LastUnexpectedShutdown is { } shutdown
            ? shutdown.ToString("g") : "None found";
        LastShutdownCauseText = DescribeShutdownCause(snapshot.LastShutdownCause);

        TdrEventCount = snapshot.TdrEventCount;
        LastTdrEventText = snapshot.LastTdrEvent is { } tdr
            ? $"Last: {tdr:g}" : "None in the last 30 days";

        // Round 15, item 34.
        TdrEventDetails.Clear();
        foreach (var t in snapshot.TdrEventDetails) TdrEventDetails.Add(t);
        TdrSettings = snapshot.TdrSettings;

        TimeSinceLastCrashText = snapshot.LastCrashTime is { } crash
            ? $"{Formatting.FormatSpan(DateTime.Now - crash)} ago"
            : "No crash found in the last 30 days";

        LowMemoryEventCount = snapshot.LowMemoryEventCount;
        LastLowMemoryEventText = snapshot.LastLowMemoryEvent is { } lowMem
            ? $"Last: {lowMem:g}" : "None in the last 30 days";

        DailyEventCounts.Clear();
        foreach (var d in snapshot.DailyCounts) DailyEventCounts.Add(d.Count);
        DailyEventXAxes[0].Labels = snapshot.DailyCounts
            .Select((d, i) => i % 5 == 0 ? d.Date.ToString("M/d") : string.Empty)
            .ToArray();

        // Round 13, item 11: Microsoft's own per-day reliability index, aligned to the same 30
        // daily buckets as DailyEventCounts above - a day with no Microsoft data is a real gap
        // (null), not a fabricated zero.
        var metricsByDate = snapshot.ReliabilityMetrics.ToDictionary(m => m.Date.Date, m => m.Index);
        ReliabilityIndexPoints.Clear();
        foreach (var d in snapshot.DailyCounts)
            ReliabilityIndexPoints.Add(metricsByDate.TryGetValue(d.Date.Date, out var idx) ? idx : (double?)null);

        // #66: repeated application crashes grouped by faulting module, most frequent first - a
        // pure re-grouping of the same RecentEvents list above, no new query.
        CrashesByModule.Clear();
        foreach (var g in snapshot.RecentEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.FaultingModule))
            .GroupBy(e => e.FaultingModule!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FaultingModuleSummary { Module = g.Key, Count = g.Count(), LastSeen = g.Max(e => e.TimeCreated) })
            .OrderByDescending(s => s.Count))
        {
            CrashesByModule.Add(g);
        }

        LatestBugCheck = snapshot.LatestBugCheck;

        UnexpectedShutdowns.Clear();
        foreach (var s in snapshot.UnexpectedShutdowns) UnexpectedShutdowns.Add(s);

        ShutdownTimeline.Clear();
        foreach (var t in snapshot.ShutdownTimeline) ShutdownTimeline.Add(t);

        DumpFailures.Clear();
        foreach (var f in snapshot.DumpFailures) DumpFailures.Add(f);

        WheaErrors.Clear();
        foreach (var w in snapshot.WheaErrors) WheaErrors.Add(w);

        // Round 13, item 9: WHEA rows grouped by (Severity, Source), most frequent first - the
        // same "flat list -> grouped summary" derivation CrashesByModule already uses above.
        WheaSummary.Clear();
        foreach (var g in snapshot.WheaErrors
            .GroupBy(w => (w.Severity, w.Source))
            .Select(g => new WheaSummaryRow { Severity = g.Key.Severity, Source = g.Key.Source, Count = g.Count(), LastSeen = g.Max(w => w.TimeCreated) })
            .OrderByDescending(s => s.Count))
        {
            WheaSummary.Add(g);
        }

        LogCoverageText = BuildLogCoverageText(snapshot.LogHealth);

        // #500: feed the faulting-module names seen this refresh into the session-lifetime bridge
        // the Devices & Drivers tab's known-problem-driver matcher (and, transitively, the Summary
        // Health Check card) reads - see StabilityCrashSummaryState's remarks.
        StabilityCrashSummaryState.Report(snapshot.RecentEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.FaultingModule))
            .Select(e => e.FaultingModule!));

        // #427
        PoolExhaustionEvents.Clear();
        foreach (var e in snapshot.PoolExhaustionEvents) PoolExhaustionEvents.Add(e);

        // #439
        OutOfMemoryIncidents.Clear();
        foreach (var e in snapshot.OutOfMemoryIncidents) OutOfMemoryIncidents.Add(e);

        // #447
        CorrectedMemoryErrors.Clear();
        foreach (var e in snapshot.CorrectedMemoryErrors) CorrectedMemoryErrors.Add(e);
        CorrectedMemoryErrorCount = snapshot.CorrectedMemoryErrorCount;
        LastCorrectedMemoryErrorText = snapshot.LastCorrectedMemoryError is { } last
            ? $"Last: {last:g}" : "None in the last 30 days";

        // #464
        BootDriverLoadFailures.Clear();
        foreach (var f in snapshot.BootDriverLoadFailures) BootDriverLoadFailures.Add(f);

        // #487
        WheaHardwareErrors.Clear();
        foreach (var e in snapshot.WheaHardwareErrors) WheaHardwareErrors.Add(e);

        // #488
        DailyWheaCorrectedCounts.Clear();
        foreach (var d in snapshot.DailyWheaCorrectedCounts) DailyWheaCorrectedCounts.Add(d.Count);
        DailyWheaCorrectedXAxes[0].Labels = snapshot.DailyWheaCorrectedCounts
            .Select((d, i) => i % 5 == 0 ? d.Date.ToString("M/d") : string.Empty)
            .ToArray();

        // #492
        HardwareErrorCorrelations.Clear();
        foreach (var c in snapshot.HardwareErrorCorrelations) HardwareErrorCorrelations.Add(c);

        StabilityIndex = ComputeStabilityIndex(snapshot);

        // #128: first-ever-occurrence signatures within RecentEvents' own 30-day window - reuses
        // the snapshot already read above, no new event-log query.
        NewErrorTypesThisWeek.Clear();
        foreach (var flag in _anomaly.ComputeFirstOccurrences(
            snapshot.RecentEvents.Select(e => (e.ProviderName, e.EventId, e.TimeCreated, (string?)e.Message)),
            DateTime.Now, recentWindowDays: 7))
        {
            NewErrorTypesThisWeek.Add(flag);
        }

        // #130: same reuse - the day x hour-of-day density grid over the same 30-day snapshot.
        ErrorDensityHeatmap.Clear();
        foreach (var cell in _anomaly.ComputeDensityHeatmap(snapshot.RecentEvents.Select(e => e.TimeCreated)))
            ErrorDensityHeatmap.Add(cell);

        // #606: thermal-critical/shutdown events.
        ThermalCriticalEvents.Clear();
        foreach (var e in snapshot.ThermalCriticalEvents) ThermalCriticalEvents.Add(e);
        OnPropertyChanged(nameof(ThermalCriticalDetected));

        // #610: throttle-to-stutter correlation - cross-references #604's persisted throttle
        // episodes against the same RecentEvents timestamps this tab already shows.
        ComputeHitchThrottleCorrelation();

        // #625: cross-references the shutdown banner's own unexpected-shutdown timestamp against
        // the persisted power-history log.
        ComputePowerDrawAtRebootCorrelation(snapshot);

        // #633: RecentEvents just changed - recompute the combined instability flag's
        // fault-evidence input (the WHEA/Vcore inputs are refreshed from their own load paths).
        RefreshUndervoltInstabilityFlag();
    }

    /// <summary>Round 13, item 3: plain-English label for the badge on the unexpected-shutdown
    /// banner - see EventLogService.ClassifyPowerEvent's remarks on how tentative this
    /// classification actually is.</summary>
    private static string DescribeShutdownCause(ShutdownCause? cause) => cause switch
    {
        ShutdownCause.Bugcheck => "Cause: bugcheck (BSOD)",
        ShutdownCause.PowerButtonHeld => "Cause: power button held",
        ShutdownCause.PowerLoss => "Cause: looks like a sudden loss of power",
        ShutdownCause.HardHang => "Cause: looks like a hard hang (shutdown never completed)",
        _ => "Cause: unknown",
    };

    /// <summary>Round 13, item 12: "is the lookback window even trustworthy" line - flags a log
    /// that was cleared recently, or whose actual oldest record doesn't reach back the full
    /// lookback window, so a clean "no crashes found" elsewhere on this tab isn't mistaken for a
    /// confirmed clean bill of health.</summary>
    private static string BuildLogCoverageText(EventLogHealth? health)
    {
        if (health is null) return "Log coverage: unknown.";

        var parts = new List<string>();
        if (health.OldestRecordTime is { } oldest)
        {
            int days = Math.Max(0, (int)(DateTime.Now - oldest).TotalDays);
            parts.Add($"Oldest available System-log record: {oldest:g} ({days}d of history)");
            if (days < EventLogService.LookbackDays)
                parts.Add($"— shorter than the {EventLogService.LookbackDays}-day lookback window, so \"nothing found\" above may just mean the log doesn't go back far enough");
        }
        else
        {
            parts.Add("Oldest available System-log record: unknown");
        }

        if (health.WasClearedRecently && health.LastClearedTime is { } cleared)
            parts.Add($"log was cleared on {cleared:g}");

        return "Log coverage: " + string.Join(", ", parts) + ".";
    }

    /// <summary>#633: see the property block's remarks above.</summary>
    private void RefreshUndervoltInstabilityFlag()
    {
        UndervoltInstabilityEvidence.Clear();

        bool wheaEvidence = WheaCorrectedCount >= 3;
        if (wheaEvidence)
            UndervoltInstabilityEvidence.Add($"{WheaCorrectedCount} corrected WHEA hardware errors recorded in the last 30 days.");

        var faultEvents = RecentEvents.Where(e => e.ExceptionCode is { } code && UndervoltFaultCodes.Contains(code)).ToList();
        var distinctModules = faultEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.FaultingModule))
            .Select(e => e.FaultingModule!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        bool faultEvidence = faultEvents.Count >= 2 && distinctModules.Count >= 2;
        if (faultEvidence)
            UndervoltInstabilityEvidence.Add($"{faultEvents.Count} access-violation/illegal-instruction crashes (0xc0000005/0xc000001d) across {distinctModules.Count} different faulting modules - no single app looks responsible.");

        bool vcoreEvidence = _energyThermals.NonStockVcoreLooksLikely;
        if (vcoreEvidence)
            UndervoltInstabilityEvidence.Add(_energyThermals.NonStockVcoreEvidenceText);

        // Two or more independent pieces of evidence, out of the three above, before this reads as
        // more than a single ambiguous signal - "quick flag, not a verdict".
        UndervoltInstabilitySuspected = UndervoltInstabilityEvidence.Count >= 2;
    }

    /// <summary>#610: "N of M recorded hitches occurred while thermally throttled - quick flag,
    /// not a verdict" - a hitch (here, RecentEvents' Critical/Error timestamps, the same "hitch
    /// and event-log timestamps" this tab already holds) counts as inside a throttle window when
    /// it falls within [Start, End] of any persisted episode (#604).</summary>
    private void ComputeHitchThrottleCorrelation()
    {
        var episodes = ThrottleHistoryService.Load();
        if (episodes.Count == 0 || RecentEvents.Count == 0)
        {
            HitchThrottleCorrelationText = string.Empty;
            return;
        }

        int total = RecentEvents.Count;
        int inWindow = RecentEvents.Count(e => episodes.Any(ep => e.TimeCreated >= ep.Start && e.TimeCreated <= ep.End));
        HitchThrottleCorrelationText = $"{inWindow} of {total} recorded hitches occurred while thermally throttled — quick flag, not a verdict.";
    }

    /// <summary>#625: joins the shutdown banner's own most-recent-unexpected-shutdown timestamp
    /// against PowerHistoryLogService's coarse persisted power trail (EnergyThermalsViewModel
    /// appends to it about once a minute). A reboot recorded near this machine's recent peak draw,
    /// with no bugcheck code extracted from the Kernel-Power 41 event itself, is the classic
    /// PSU-under-load signature (a clean, instant power-off rather than a Windows-detected
    /// exception) - quick flag, not a verdict, same tier as every other heuristic in this app.
    /// Empty (annotation hidden) until there's both an unexpected shutdown and power-history data
    /// recorded anywhere near it.</summary>
    private void ComputePowerDrawAtRebootCorrelation(StabilitySnapshot snapshot)
    {
        if (!snapshot.WasLastShutdownUnexpected || snapshot.LastUnexpectedShutdown is not { } shutdownAt)
        {
            PowerDrawAtRebootText = string.Empty;
            return;
        }

        var history = PowerHistoryLogService.Load();
        var nearest = PowerHistoryLogService.FindNearest(history, shutdownAt, TimeSpan.FromMinutes(10));
        if (nearest is null || (nearest.PackagePowerW is null && nearest.GpuPowerW is null))
        {
            PowerDrawAtRebootText = "No power-draw history recorded close enough to the last unexpected shutdown to correlate yet.";
            return;
        }

        double drawAtShutdown = (nearest.PackagePowerW ?? 0) + (nearest.GpuPowerW ?? 0);
        var recentWindow = history.Where(s => s.Timestamp >= shutdownAt.AddDays(-7) && s.Timestamp <= shutdownAt.AddMinutes(10)).ToList();
        double peakRecentDraw = recentWindow.Count > 0
            ? recentWindow.Max(s => (s.PackagePowerW ?? 0) + (s.GpuPowerW ?? 0))
            : drawAtShutdown;

        bool nearPeak = peakRecentDraw > 0 && drawAtShutdown >= peakRecentDraw * 0.85;
        bool noBugcheck = string.IsNullOrEmpty(snapshot.LastUnexpectedShutdownBugcheckCode);

        PowerDrawAtRebootText = nearPeak && noBugcheck
            ? $"Power draw near the last unexpected reboot was {drawAtShutdown:0}W - close to this machine's recent peak ({peakRecentDraw:0}W), with no bugcheck code recorded. That's the classic PSU-under-load reboot signature - quick flag, not a verdict."
            : $"Power draw near the last unexpected reboot was {drawAtShutdown:0}W (recent peak {peakRecentDraw:0}W).";
    }

    // ================================================================================
    // #636-640: WHEA (Windows Hardware Error Architecture) hardware-error card
    // ================================================================================

    /// <summary>On-demand WHEA-Logger event-log read (#636), resolving PCIe device names (#639)
    /// in one WMI pass shared across every event in this batch, then joining each event against
    /// the persisted power-history log for the "conditions at the moment of each error" table
    /// (#638). The whole batch runs off the UI thread, same shape as RefreshAsync above.</summary>
    private async Task LoadWheaEventsAsync()
    {
        var (events, conditionRows) = await Task.Run(() =>
        {
            var raw = _service.ReadWheaEvents();

            // #639: one WMI enumeration for the whole batch, not one per event.
            var locationMap = PciDeviceResolverService.BuildLocationMap();
            var resolved = raw.Select(e => ResolveWheaPcieDevice(e, locationMap)).ToList();

            // #638: joined against whatever power-history samples exist - null fields (shown as
            // "Unknown") when nothing was recorded within the join's tolerance window.
            var history = PowerHistoryLogService.Load();
            var rows = resolved.Select(e => BuildWheaConditionRow(e, history)).ToList();

            return (resolved, rows);
        });

        WheaEvents.Clear();
        foreach (var e in events) WheaEvents.Add(e);

        WheaConditionRows.Clear();
        foreach (var r in conditionRows) WheaConditionRows.Add(r);

        WheaFatalCount = events.Count(e => e.IsFatal);
        WheaCorrectedCount = events.Count(e => !e.IsFatal);

        RefreshWheaCorrectedDailyChart(events);

        // #633: WHEA count just changed - recompute the combined instability flag.
        RefreshUndervoltInstabilityFlag();
    }

    /// <summary>#639: resolves a parsed PCIe bus/device/function against the shared location map -
    /// returns the event unchanged (ResolvedDeviceName stays empty) when there's no PCIe location
    /// on this event at all. When there IS a location but no Win32_PnPEntity matched it (a device
    /// that's since been removed, or a location string format this app's regex didn't recognize),
    /// still surfaces the raw address rather than showing nothing - marked "(unresolved)" so it
    /// reads differently from a genuinely named device.</summary>
    private static WheaEvent ResolveWheaPcieDevice(WheaEvent e, Dictionary<(int Bus, int Device, int Function), (string Name, string DeviceId)> locationMap)
    {
        if (e.PcieBus is not { } bus || e.PcieDevice is not { } device || e.PcieFunction is not { } function) return e;

        string name;
        string deviceId;
        if (locationMap.TryGetValue((bus, device, function), out var resolved))
        {
            name = string.IsNullOrEmpty(resolved.DeviceId) ? resolved.Name : $"{resolved.Name} ({resolved.DeviceId})";
            deviceId = resolved.DeviceId;
        }
        else
        {
            name = $"PCIe Bus {bus}, Device {device}, Function {function} (unresolved)";
            deviceId = string.Empty;
        }

        return new WheaEvent
        {
            TimeCreated = e.TimeCreated,
            EventId = e.EventId,
            IsFatal = e.IsFatal,
            CategoryText = e.CategoryText,
            ErrorSourceText = e.ErrorSourceText,
            Bank = e.Bank,
            BankHintText = e.BankHintText,
            PcieSegment = e.PcieSegment,
            PcieBus = e.PcieBus,
            PcieDevice = e.PcieDevice,
            PcieFunction = e.PcieFunction,
            ResolvedDeviceName = name,
            ResolvedDeviceId = deviceId,
            Message = e.Message,
        };
    }

    /// <summary>#638: one WHEA event joined against the nearest power-history sample within a
    /// 5-minute tolerance - wider than #625's 10-minute reboot-correlation tolerance since a WHEA
    /// event doesn't kill the app's own sampling the way a reboot does, so a closer match is
    /// usually available.</summary>
    private static WheaConditionRow BuildWheaConditionRow(WheaEvent e, List<PowerTempSample> history)
    {
        var nearest = PowerHistoryLogService.FindNearest(history, e.TimeCreated, TimeSpan.FromMinutes(5));
        string summary = string.IsNullOrEmpty(e.ResolvedDeviceName) ? e.CategoryText : $"{e.CategoryText} — {e.ResolvedDeviceName}";
        double? powerW = nearest is null || (!nearest.PackagePowerW.HasValue && !nearest.GpuPowerW.HasValue)
            ? null
            : (nearest.PackagePowerW ?? 0) + (nearest.GpuPowerW ?? 0);

        return new WheaConditionRow
        {
            TimeCreated = e.TimeCreated,
            ErrorSummary = summary,
            TempCAtEvent = nearest?.TempC,
            PowerWAtEvent = powerW,
        };
    }

    /// <summary>#637: corrected (non-fatal) WHEA events per day over the same lookback window
    /// EventLogService.ReadWheaEvents queries, zero-filled for days with none - same bucketing
    /// shape as the reliability-history chart's BuildDailyCounts.</summary>
    private void RefreshWheaCorrectedDailyChart(List<WheaEvent> events)
    {
        var counts = events.Where(e => !e.IsFatal)
            .GroupBy(e => e.TimeCreated.Date)
            .ToDictionary(g => g.Key, g => g.Count());

        var today = DateTime.Now.Date;
        var values = new double[WheaLookbackDays];
        var labels = new string[WheaLookbackDays];
        for (int i = 0; i < WheaLookbackDays; i++)
        {
            var day = today.AddDays(-(WheaLookbackDays - 1 - i));
            values[i] = counts.TryGetValue(day, out var c) ? c : 0;
            labels[i] = i % 5 == 0 ? day.ToString("M/d") : string.Empty;
        }

        WheaCorrectedDailyCounts.Clear();
        foreach (var v in values) WheaCorrectedDailyCounts.Add(v);
        WheaCorrectedXAxes[0].Labels = labels;
    }

    /// <summary>
    /// #68: single 0-10 stability index - a simple, documented weighted formula (not a black box),
    /// entirely over data this tab already reads (no new event-log query). Starts at a perfect 10
    /// and subtracts:
    ///  1. Recent daily Critical/Error density - the average of the last 7 days' counts, up to 4
    ///     points off (0.5 points per average daily event).
    ///  2. An unexpected shutdown detected for the current boot - 1.5 points flat.
    ///  3. TDR (GPU driver reset) events in the 30-day lookback window - 0.3 points each, up to 2.
    ///  4. Low-memory resource-exhaustion events in the same window - 0.1 points each, up to 1.
    ///  5. How recently the last crash happened - 2 points off if within the last 24 hours, 1 point
    ///     if within the last 7 days, none otherwise.
    /// Clamped to [0, 10] and rounded to one decimal - a rough, at-a-glance complement to the daily
    /// bar chart above, not a scientific reliability metric.
    /// </summary>
    private static double ComputeStabilityIndex(StabilitySnapshot snapshot)
    {
        double score = 10.0;

        double avgLast7 = snapshot.DailyCounts.Count == 0 ? 0 : snapshot.DailyCounts.TakeLast(7).Average(d => d.Count);
        score -= Math.Min(avgLast7 * 0.5, 4.0);

        if (snapshot.WasLastShutdownUnexpected) score -= 1.5;

        score -= Math.Min(snapshot.TdrEventCount * 0.3, 2.0);

        score -= Math.Min(snapshot.LowMemoryEventCount * 0.1, 1.0);

        if (snapshot.LastCrashTime is { } crash)
        {
            var since = DateTime.Now - crash;
            if (since.TotalHours < 24) score -= 2.0;
            else if (since.TotalDays < 7) score -= 1.0;
        }

        return Math.Round(Math.Clamp(score, 0, 10), 1);
    }

    /// <summary>#122: joins each raw scan hit with its knowledge-base entry's text and sorts
    /// worst-first (re-ranked severity, then occurrence count) - "the top of the list is actually
    /// the top of the problem," the same ordering rule #120 asks for in the Events tab.</summary>
    private void BuildKnownBadIdScorecard(List<KnownBadIdScanHit> hits)
    {
        var rows = new List<KnownBadIdScorecardRow>();
        foreach (var hit in hits)
        {
            var entry = _kb.Lookup(hit.Provider, hit.EventId);
            if (entry is null) continue; // shouldn't happen - hits come from the KB's own flagged-ID set

            rows.Add(new KnownBadIdScorecardRow
            {
                Provider = hit.Provider,
                EventId = hit.EventId,
                Count = hit.Count,
                LastSeen = hit.LastSeen,
                Meaning = entry.Meaning,
                NextStep = entry.NextStep,
                SeverityLabel = entry.SeverityRank.ToString(),
                SeverityRank = (int)entry.SeverityRank,
            });
        }

        rows.Sort((a, b) => b.SeverityRank != a.SeverityRank ? b.SeverityRank - a.SeverityRank : b.Count - a.Count);

        KnownBadIdScorecard.Clear();
        foreach (var row in rows) KnownBadIdScorecard.Add(row);
    }
}
