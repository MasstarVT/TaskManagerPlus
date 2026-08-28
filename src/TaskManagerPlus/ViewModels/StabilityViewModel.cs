using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

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

    // Round 17 chunk 64-70, item 66: persisted per-executable hang history - see
    // HangHistoryService. Shown on this same "Application hangs" card (item 53's own card) rather
    // than a new one, per this chunk's own instruction.
    public ObservableCollection<HangHistoryEntry> HangHistory { get; } = new();

    // Item 62: postmortem debugger + Image File Execution Options hijack audit - a plain
    // registry read, refreshed alongside everything else in RefreshAsync (no separate button;
    // see CLAUDE.md's "on-demand vs. polled" note - this is cheap, not an expensive scan).
    private PostmortemDebuggerInfo? _postmortemDebugger;
    public PostmortemDebuggerInfo? PostmortemDebugger { get => _postmortemDebugger; private set => SetProperty(ref _postmortemDebugger, value); }

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

    // Round 8 #40: low-memory resource-exhaustion events - see EventLogService.ReadLowMemoryEvents.
    private int _lowMemoryEventCount;
    public int LowMemoryEventCount { get => _lowMemoryEventCount; private set => SetProperty(ref _lowMemoryEventCount, value); }

    private string _lastLowMemoryEventText = "None in the last 30 days";
    public string LastLowMemoryEventText { get => _lastLowMemoryEventText; private set => SetProperty(ref _lastLowMemoryEventText, value); }

    // Round 10, #68: single 0-10 stability index - see ComputeStabilityIndex for the documented
    // weighted formula.
    private double _stabilityIndex = 10.0;
    public double StabilityIndex { get => _stabilityIndex; private set => SetProperty(ref _stabilityIndex, value); }

    public AsyncRelayCommand RefreshCommand { get; }

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

    public StabilityViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);

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

        // Round 17, item 59: jump from a restart-loop warning row to the matching Services-tab
        // entry - see JumpToServiceRequested's own remarks.
        JumpToServiceCommand = new RelayCommand(param =>
        {
            if (param is string name && !string.IsNullOrWhiteSpace(name))
                JumpToServiceRequested?.Invoke(name);
        });

        _dailyEventColumns = new ColumnSeries<double>
        {
            Values = DailyEventCounts,
            Fill = new SolidColorPaint(SKColors.OrangeRed.WithAlpha(200)),
            Stroke = null,
            MaxBarWidth = 12,
        };
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
        DailyEventSeries = new ISeries[] { _dailyEventColumns, _reliabilityIndexLine };
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
            // Right-hand axis for the Microsoft reliability index (item 11) - fixed 0-10 scale, an
            // entirely different unit from the left axis' daily event count, so it gets its own.
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

        _ = RefreshAsync();
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
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var snapshot = await Task.Run(() => _service.Query());
            Apply(snapshot);

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

    private void Apply(StabilitySnapshot snapshot)
    {
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
            ? FormatSince(DateTime.Now - crash)
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

        StabilityIndex = ComputeStabilityIndex(snapshot);
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

    private static string FormatSince(TimeSpan since)
    {
        if (since.TotalDays >= 1) return $"{(int)since.TotalDays}d {since.Hours}h ago";
        if (since.TotalHours >= 1) return $"{(int)since.TotalHours}h {since.Minutes}m ago";
        return $"{(int)since.TotalMinutes}m ago";
    }
}
