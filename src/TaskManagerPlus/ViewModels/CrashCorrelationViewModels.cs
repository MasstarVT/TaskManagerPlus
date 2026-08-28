using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>Round 20, item 89: one toggle chip in the unified timeline's source filter - a plain
/// checkbox list rather than a multi-select ComboBox, so every source's current on/off state is
/// visible at a glance without opening anything (the same reasoning ShowWerHangs' two-button
/// toggle already uses elsewhere on this tab, just for N sources instead of 2).</summary>
public sealed class CrashSourceFilterOption : ObservableObject
{
    public CrashTimelineSourceType SourceType { get; }
    public string Label { get; }

    private bool _isChecked = true;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!SetProperty(ref _isChecked, value)) return;
            Changed?.Invoke();
        }
    }

    /// <summary>Fired on the UI thread (toggled directly from a bound CheckBox) - StabilityViewModel
    /// subscribes once per option to rebuild FilteredTimeline.</summary>
    public event Action? Changed;

    public CrashSourceFilterOption(CrashTimelineSourceType sourceType, string label)
    {
        SourceType = sourceType;
        Label = label;
    }
}

/// <summary>
/// Round 20, items 89/95: one row in the Stability tab's unified crash timeline - wraps the
/// immutable CrashTimelineRow with the mutable, per-row UI state item 95's on-demand log-telemetry
/// expander needs (a plain init-only model can't carry that itself) - the same "wrap the model, add
/// row state" shape DumpRowViewModel/WerReportRowViewModel already use elsewhere on this tab.
/// Expanding a row lazily loads the two minutes of this app's own logged telemetry (CPU/RAM/
/// temperature/power) immediately before that row's own timestamp, per item 95's own "lazy-load
/// only when expanded" instruction - never computed eagerly for every row on every refresh.
/// </summary>
public sealed class CrashTimelineRowViewModel : ObservableObject
{
    public CrashTimelineRow Row { get; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (value && _logCorrelation is null && !_isLoadingLogCorrelation)
                _ = LoadLogCorrelationAsync();
        }
    }

    private bool _isLoadingLogCorrelation;
    public bool IsLoadingLogCorrelation { get => _isLoadingLogCorrelation; private set => SetProperty(ref _isLoadingLogCorrelation, value); }

    private CrashLogCorrelationResult? _logCorrelation;
    public CrashLogCorrelationResult? LogCorrelation { get => _logCorrelation; private set => SetProperty(ref _logCorrelation, value); }

    private ISeries[]? _logSeries;
    public ISeries[]? LogSeries { get => _logSeries; private set => SetProperty(ref _logSeries, value); }

    public Axis[] LogXAxes { get; }
    public Axis[] LogYAxes { get; }

    private string _telemetrySummaryText = string.Empty;
    public string TelemetrySummaryText { get => _telemetrySummaryText; private set => SetProperty(ref _telemetrySummaryText, value); }

    private static readonly SKColor AxisTextColor = new(0x9A, 0x9A, 0xA2);
    private static readonly SKColor AxisSeparatorColor = new(0x33, 0x33, 0x3A, 160);

    public CrashTimelineRowViewModel(CrashTimelineRow row)
    {
        Row = row;

        LogXAxes = new[]
        {
            new Axis { Labels = Array.Empty<string>(), LabelsPaint = new SolidColorPaint(AxisTextColor), SeparatorsPaint = null },
        };
        LogYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0, MaxLimit = 100, Labeler = v => $"{v:0}%",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
    }

    private async Task LoadLogCorrelationAsync()
    {
        IsLoadingLogCorrelation = true;
        try
        {
            var result = await CrashCorrelationService.BuildLogCorrelationAsync(Row.Timestamp);
            LogCorrelation = result;

            if (result.HasCoverage)
            {
                var cpu = result.Points.Select(p => p.CpuPercent).ToList();
                var ram = result.Points.Select(p => p.RamPercent).ToList();
                LogSeries = new ISeries[]
                {
                    new LineSeries<double>
                    {
                        Values = cpu, Name = "CPU %", Fill = null, GeometryStroke = null, GeometryFill = null,
                        Stroke = new SolidColorPaint(SKColors.DeepSkyBlue, 2f), LineSmoothness = 0.2,
                    },
                    new LineSeries<double>
                    {
                        Values = ram, Name = "RAM %", Fill = null, GeometryStroke = null, GeometryFill = null,
                        Stroke = new SolidColorPaint(SKColors.MediumPurple, 2f), LineSmoothness = 0.2,
                    },
                };

                int n = result.Points.Count;
                int labelEvery = Math.Max(1, n / 6);
                LogXAxes[0].Labels = result.Points
                    .Select((p, i) => i % labelEvery == 0 ? p.Timestamp.ToString("HH:mm:ss") : string.Empty)
                    .ToArray();

                // Item 95's actual point ("was it hot / was it under load / was memory exhausted")
                // is answered as plain peak-value text rather than squeezing an unrelated-unit
                // (°C/W) series onto the same 0-100% axis the CPU/RAM lines already use.
                double peakCpu = cpu.Count > 0 ? cpu.Max() : 0;
                double peakRam = ram.Count > 0 ? ram.Max() : 0;
                var temps = result.Points.Where(p => p.TemperatureC.HasValue).Select(p => p.TemperatureC!.Value).ToList();
                var powers = result.Points.Where(p => p.PowerW.HasValue).Select(p => p.PowerW!.Value).ToList();

                var parts = new List<string> { $"Peak CPU: {peakCpu:0}%", $"Peak RAM: {peakRam:0}%" };
                if (temps.Count > 0) parts.Add($"Peak temperature: {temps.Max():0.0}°C");
                if (powers.Count > 0) parts.Add($"Peak power draw: {powers.Max():0.0}W");
                TelemetrySummaryText = string.Join(" · ", parts);
            }
        }
        catch (Exception ex)
        {
            LogCorrelation = new CrashLogCorrelationResult { HasCoverage = false, StatusText = $"Couldn't load telemetry: {ex.Message}" };
        }
        finally
        {
            IsLoadingLogCorrelation = false;
        }
    }
}

/// <summary>
/// Round 20, item 90 (and the items 92-94 "what changed" panel): one row in the Stability tab's
/// "Crash signature clusters" card - wraps the immutable CrashCluster with the mutable per-row UI
/// state its own on-demand "What changed before this started" expander needs. Expanding it
/// lazily runs CrashCorrelationService.BuildWhatChangedAsync for this one cluster only (never
/// eagerly for every cluster on every refresh - setupapi.dev.log parsing / event-log queries /
/// WMI / registry sweeps aren't free, per this chunk's own instructions).
/// </summary>
public sealed class CrashClusterViewModel : ObservableObject
{
    public CrashCluster Cluster { get; }
    public ObservableCollection<CrashTimelineRowViewModel> Occurrences { get; }

    private bool _isWhatChangedExpanded;
    public bool IsWhatChangedExpanded
    {
        get => _isWhatChangedExpanded;
        set
        {
            if (!SetProperty(ref _isWhatChangedExpanded, value)) return;
            if (value && _whatChanged is null && !_isLoadingWhatChanged)
                _ = LoadWhatChangedAsync();
        }
    }

    private bool _isLoadingWhatChanged;
    public bool IsLoadingWhatChanged { get => _isLoadingWhatChanged; private set => SetProperty(ref _isLoadingWhatChanged, value); }

    private WhatChangedResult? _whatChanged;
    public WhatChangedResult? WhatChanged { get => _whatChanged; private set => SetProperty(ref _whatChanged, value); }

    public CrashClusterViewModel(CrashCluster cluster)
    {
        Cluster = cluster;
        Occurrences = new ObservableCollection<CrashTimelineRowViewModel>(cluster.Occurrences.Select(r => new CrashTimelineRowViewModel(r)));
    }

    private async Task LoadWhatChangedAsync()
    {
        IsLoadingWhatChanged = true;
        try
        {
            WhatChanged = await CrashCorrelationService.BuildWhatChangedAsync(Cluster.FirstSeen);
        }
        catch (Exception ex)
        {
            WhatChanged = new WhatChangedResult { ComputedOk = false, ErrorText = ex.Message };
        }
        finally
        {
            IsLoadingWhatChanged = false;
        }
    }
}
