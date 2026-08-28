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

        _ = RefreshAsync();
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
        public MemoryDumpInfo MemoryDump { get; init; } = new();
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

        // Item 19: intersected across every dump this app could parse a module list from,
        // including MEMORY.DMP when it happens to be in the (much rarer) minidump/triage format.
        var forCommon = new List<ParsedDumpInfo>(parsedDumps);
        if (memDump.Parsed is not null) forCommon.Add(memDump.Parsed);

        return new DumpAnalysisBundle
        {
            Debugger = debugger,
            ParsedDumps = parsedDumps,
            MemoryDump = memDump,
            LiveKernelReports = liveReports,
            CommonDrivers = MinidumpParserService.FindCommonDrivers(forCommon),
            Housekeeping = housekeeping,
        };
    }

    private void ApplyDumpAnalysis(DumpAnalysisBundle bundle)
    {
        Debugger = bundle.Debugger;

        DumpRows.Clear();
        foreach (var p in bundle.ParsedDumps) DumpRows.Add(new DumpRowViewModel(p, bundle.Debugger));

        MemoryDump = bundle.MemoryDump;
        MemoryDumpRow = bundle.MemoryDump.Exists && bundle.MemoryDump.Parsed is not null
            ? new DumpRowViewModel(bundle.MemoryDump.Parsed, bundle.Debugger)
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
