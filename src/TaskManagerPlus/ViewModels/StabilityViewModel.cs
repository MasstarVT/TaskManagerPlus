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

    public ObservableCollection<StabilityEvent> RecentEvents { get; } = new();
    public ObservableCollection<MinidumpInfo> Minidumps { get; } = new();

    // Round 10, #66: repeated crashes grouped by faulting module, most frequent first - see
    // FaultingModuleSummary's remarks. Pure derived aggregation over RecentEvents, no new query.
    public ObservableCollection<FaultingModuleSummary> CrashesByModule { get; } = new();

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

    // #488: corrected WHEA errors per day over the same lookback window - the exact same
    // ColumnSeries/Axis setup as DailyEventSeries above, just fed from a different daily bucket.
    public ObservableCollection<double> DailyWheaCorrectedCounts { get; } = new();
    private readonly ColumnSeries<double> _dailyWheaCorrectedColumns;
    public ISeries[] DailyWheaCorrectedSeries { get; }
    public Axis[] DailyWheaCorrectedXAxes { get; }
    public Axis[] DailyWheaCorrectedYAxes { get; }

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

        // #488: same ColumnSeries setup as DailyEventColumns above.
        _dailyWheaCorrectedColumns = new ColumnSeries<double>
        {
            Values = DailyWheaCorrectedCounts,
            Fill = new SolidColorPaint(SKColors.OrangeRed.WithAlpha(200)),
            Stroke = null,
            MaxBarWidth = 12,
        };
        DailyWheaCorrectedSeries = new ISeries[] { _dailyWheaCorrectedColumns };
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

        DailyWheaCorrectedXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        DailyWheaCorrectedYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        DailyWheaCorrectedYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var snapshot = await Task.Run(() => _service.Query());
            Apply(snapshot);
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
