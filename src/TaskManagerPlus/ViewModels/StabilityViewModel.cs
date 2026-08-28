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

    public ObservableCollection<StabilityEvent> RecentEvents { get; } = new();
    public ObservableCollection<MinidumpInfo> Minidumps { get; } = new();

    /// <summary>#122: "Known-bad IDs present on this PC" - which KB-flagged serious event IDs
    /// actually showed up in the lookback window, with count/last-seen/next-step, ordered by
    /// re-ranked severity (worst first) then by how often they occurred - see
    /// EventLogService.ScanForKnownBadIds and BuildKnownBadIdScorecard.</summary>
    public ObservableCollection<KnownBadIdScorecardRow> KnownBadIdScorecard { get; } = new();

    // Round 10, #66: repeated crashes grouped by faulting module, most frequent first - see
    // FaultingModuleSummary's remarks. Pure derived aggregation over RecentEvents, no new query.
    public ObservableCollection<FaultingModuleSummary> CrashesByModule { get; } = new();

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

        _dailyEventColumns = new ColumnSeries<double>
        {
            Values = DailyEventCounts,
            Fill = new SolidColorPaint(SKColors.OrangeRed.WithAlpha(200)),
            Stroke = null,
            MaxBarWidth = 12,
        };
        DailyEventSeries = new ISeries[] { _dailyEventColumns };
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
