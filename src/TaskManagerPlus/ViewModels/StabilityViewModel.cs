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

    // #633: needed for the inferred non-stock-Vcore evidence input to the combined
    // undervolt/overclock instability flag below - see EnergyThermalsViewModel.NonStockVcoreLooksLikely.
    private readonly EnergyThermalsViewModel _energyThermals;

    public ObservableCollection<StabilityEvent> RecentEvents { get; } = new();
    public ObservableCollection<MinidumpInfo> Minidumps { get; } = new();

    // Round 10, #66: repeated crashes grouped by faulting module, most frequent first - see
    // FaultingModuleSummary's remarks. Pure derived aggregation over RecentEvents, no new query.
    public ObservableCollection<FaultingModuleSummary> CrashesByModule { get; } = new();

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
    public ISeries[] DailyEventSeries { get; }
    public Axis[] DailyEventXAxes { get; }
    public Axis[] DailyEventYAxes { get; }

    private static readonly SKColor AxisTextColor = new(0x9A, 0x9A, 0xA2);
    private static readonly SKColor AxisSeparatorColor = new(0x33, 0x33, 0x3A, 160);

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

    public StabilityViewModel(EnergyThermalsViewModel energyThermals)
    {
        _energyThermals = energyThermals;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        LoadWheaEventsCommand = new AsyncRelayCommand(_ => LoadWheaEventsAsync());

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

        _ = RefreshAsync();
        _ = LoadWheaEventsAsync();
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
        WheaCorrectedXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        WheaCorrectedYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        WheaCorrectedYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
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

        StabilityIndex = ComputeStabilityIndex(snapshot);

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

    private static string FormatSince(TimeSpan since)
    {
        if (since.TotalDays >= 1) return $"{(int)since.TotalDays}d {since.Hours}h ago";
        if (since.TotalHours >= 1) return $"{(int)since.TotalHours}h {since.Minutes}m ago";
        return $"{(int)since.TotalMinutes}m ago";
    }
}
