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
public sealed class StabilityViewModel : ObservableObject
{
    private readonly EventLogService _service = new();

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

    /// <summary>The last WER report scan's results, stashed so RefreshTimelineExtrasAsync's
    /// BuildTimeline call (#161) can fold them into the unified timeline without a second scan.</summary>
    private List<WerReportInfo> _lastWerReports = new();

    /// <summary>#141: fires once per refresh (success or failure) - MainViewModel wires this to push
    /// fresh crash/error markers into PerformanceViewModel's charts, reusing this tab's own event
    /// data rather than adding a second poll.</summary>
    public event Action? Refreshed;

    public ObservableCollection<StabilityEvent> RecentEvents { get; } = new();
    public ObservableCollection<MinidumpInfo> Minidumps { get; } = new();

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

    private string? _localDumpsStatusText;
    public string? LocalDumpsStatusText { get => _localDumpsStatusText; private set => SetProperty(ref _localDumpsStatusText, value); }

    public RelayCommand EnableLocalDumpsCommand { get; }
    public RelayCommand RevertLocalDumpsCommand { get; }

    // ---- #167: error reporting configuration check ----
    private WerConfigStatus _werConfigStatus = new();
    public WerConfigStatus WerConfigStatus { get => _werConfigStatus; private set => SetProperty(ref _werConfigStatus, value); }

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

    private int _tdrEventCount;
    public int TdrEventCount { get => _tdrEventCount; private set => SetProperty(ref _tdrEventCount, value); }

    private string _lastTdrEventText = "None in the last 30 days";
    public string LastTdrEventText { get => _lastTdrEventText; private set => SetProperty(ref _lastTdrEventText, value); }

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

    public AsyncRelayCommand RefreshCommand { get; }

    // #1: Reliability History - daily Critical/Error counts over the lookback window, the same
    // "crash/failure events over time" chart Windows' own Reliability Monitor shows, themed to
    // match this app instead of a bare column series.
    public ObservableCollection<double> DailyEventCounts { get; } = new();
    private readonly ColumnSeries<double> _dailyEventColumns;
    public ISeries[] DailyEventSeries { get; }
    public Axis[] DailyEventXAxes { get; }
    public Axis[] DailyEventYAxes { get; }

    private static readonly SKColor AxisTextColor = new(0x9A, 0x9A, 0xA2);
    private static readonly SKColor AxisSeparatorColor = new(0x33, 0x33, 0x3A, 160);

    public StabilityViewModel()
    {
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

        _dailyEventColumns = new ColumnSeries<double>
        {
            Values = DailyEventCounts,
            Fill = new SolidColorPaint(SKColors.OrangeRed.WithAlpha(200)),
            Stroke = null,
            MaxBarWidth = 12,
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

        DailyEventSeries = new ISeries[] { _dailyEventColumns, _wmiStabilityLine };
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
            // #169: secondary axis for Windows' own 1-10 stability index, right-hand side - kept at
            // a fixed color tied to the line series' own identity rather than the app's re-themed
            // palette (the same "hardcoded, not DynamicResource" choice DailyEventYAxes[0]'s
            // OrangeRed column fill already makes in this file).
            new Axis
            {
                MinLimit = 0,
                MaxLimit = 10,
                MinStep = 2,
                Position = LiveChartsCore.Measure.AxisPosition.End,
                Labeler = v => $"{v:0}",
                LabelsPaint = new SolidColorPaint(SKColors.CornflowerBlue),
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
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var snapshot = await Task.Run(() => _service.Query());
            Apply(snapshot);

            // #122: a second, narrow query scoped to exactly the KB's flagged serious IDs (folded
            // into this same on-demand refresh, not a new timer) - see EventLogService.
            // ScanForKnownBadIds's remarks for why this can't just reuse RecentEvents above.
            var flaggedIds = _kb.SeriousFlaggedIds();
            var hits = await Task.Run(() => _service.ScanForKnownBadIds(flaggedIds));
            BuildKnownBadIdScorecard(hits);

            // #161-167: WER report queue/archive scan, hangs, storage footprint, LocalDumps and
            // error-reporting-config reads - folded into this same on-demand refresh, never a new timer.
            await RefreshWerAsync();

            // #137/#142/#143/#144: folded into this same on-demand refresh, never a new timer.
            await RefreshTimelineExtrasAsync(snapshot);

            // #169/#170/#172/#173/#174: Reliability Monitor data - folded into this same on-demand
            // refresh (never a new timer), run after RefreshTimelineExtrasAsync above so #173's
            // correlation can see this refresh's own crash-flagged timeline entries.
            await RefreshReliabilityMonitorAsync();

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
            LocalDumpsStatusText = $"Local crash dump capture enabled - dumps will be written to {suggestedFolder}.";
        }
        else
        {
            WerReportService.ClearBackup(); // nothing actually changed - don't leave a stale backup around
            LocalDumpsStatusText = $"Couldn't enable local crash dump capture: {error}";
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
            LocalDumpsStatusText = "No previous local crash dump configuration was saved to revert to.";
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
            LocalDumpsStatusText = "Local crash dump configuration reverted to its previous state.";
        }
        else
        {
            LocalDumpsStatusText = $"Couldn't revert local crash dump capture: {error}";
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

    private void Apply(StabilitySnapshot snapshot)
    {
        RecentEvents.Clear();
        foreach (var e in snapshot.RecentEvents) RecentEvents.Add(e);

        Minidumps.Clear();
        foreach (var d in snapshot.Minidumps) Minidumps.Add(d);

        WasLastShutdownUnexpected = snapshot.WasLastShutdownUnexpected;
        LastUnexpectedShutdownText = snapshot.LastUnexpectedShutdown is { } shutdown
            ? shutdown.ToString("g") : "None found";

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

    private static string FormatSince(TimeSpan since)
    {
        if (since.TotalDays >= 1) return $"{(int)since.TotalDays}d {since.Hours}h ago";
        if (since.TotalHours >= 1) return $"{(int)since.TotalHours}h {since.Minutes}m ago";
        return $"{(int)since.TotalMinutes}m ago";
    }
}
