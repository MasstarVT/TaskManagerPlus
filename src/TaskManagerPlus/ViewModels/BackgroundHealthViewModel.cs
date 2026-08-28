using System.Collections.ObjectModel;
using System.Linq;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>#962: one selectable chart metric - Key matches BackgroundHealthStoreService.GetMetricValue's
/// naming, Label is what the combo box shows.</summary>
public sealed record HealthMetricOption(string Key, string Label);

/// <summary>#964: one rule's alert-channel override row in the Background Health panel's alerting
/// section - Channel changes are auto-saved back to alerting.json (see
/// BackgroundHealthViewModel's constructor wiring).</summary>
public sealed class RuleChannelRow : ObservableObject
{
    public string RuleId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    private AlertChannel _channel;
    public AlertChannel Channel { get => _channel; set => SetProperty(ref _channel, value); }
}

/// <summary>
/// #962-#966: backs the Troubleshoot tab's "Background Health" sub-page - the same sibling-panel
/// pattern Timeline/Baselines already establish. Surfaces:
///   - #959/#966: the collector's enable/interval settings, its self-measured average cost, and
///     any automatic backoff state.
///   - #960: disk usage vs. budget, and how many days of history that represents.
///   - #962: a glow/core history chart for any stored metric over a chosen date range, plus a
///     "worst 10 moments" table.
///   - #963: an alert digest ("N alerts in the last 7 days, M of them the same rule") and a recent
///     alerts list.
///   - #964: quiet-hours settings and a per-rule alert-channel override list.
/// </summary>
public sealed class BackgroundHealthViewModel : ObservableObject, IDisposable
{
    private readonly BackgroundHealthCollectorService _collector;
    private readonly RulesEngineService _rulesEngine;
    private AlertingSettings _alertingSettings;
    private bool _suppressRuleChannelSave;

    // ----- #959: collector enable/interval ------------------------------------------------------

    public bool IsCollectorEnabled
    {
        get => _collector.IsEnabled;
        set
        {
            if (_collector.IsEnabled == value) return;
            _collector.SetEnabled(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CollectorStatusText));
        }
    }

    public int IntervalSeconds
    {
        get => _collector.ConfiguredIntervalSeconds;
        set
        {
            if (_collector.ConfiguredIntervalSeconds == value) return;
            _collector.SetIntervalSeconds(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CollectorStatusText));
        }
    }

    public string CollectorStatusText => !IsCollectorEnabled
        ? "Background health collection is off."
        : _collector.IsBackedOff
            ? $"Collecting every {_collector.EffectiveIntervalSeconds}s (backed off from the configured {IntervalSeconds}s after repeated slow cycles)."
            : $"Collecting every {_collector.EffectiveIntervalSeconds}s.";

    // ----- #960: disk budget ----------------------------------------------------------------------

    public int BudgetMb
    {
        get => _collector.BudgetMb;
        set
        {
            if (_collector.BudgetMb == value) return;
            _collector.SetBudgetMb(value);
            OnPropertyChanged();
            RefreshUsage();
        }
    }

    private string _usageText = string.Empty;
    public string UsageText { get => _usageText; private set => SetProperty(ref _usageText, value); }

    // ----- #966: self-measured cost readout -------------------------------------------------------

    private string _costText = string.Empty;
    public string CostText { get => _costText; private set => SetProperty(ref _costText, value); }

    // ----- #962: metric/range selection + chart ---------------------------------------------------

    public ObservableCollection<HealthMetricOption> AvailableMetrics { get; } = new()
    {
        new("cpu.percent", "CPU %"),
        new("mem.percent", "RAM %"),
        new("thermal.cpuPackageC", "CPU temperature (C)"),
        new("disk.queueLength", "Disk queue length"),
        new("disk.latencyMs", "Disk latency (ms)"),
    };

    private HealthMetricOption _selectedMetric;
    public HealthMetricOption SelectedMetric
    {
        get => _selectedMetric;
        set { if (SetProperty(ref _selectedMetric, value)) RefreshChartAndWorstMoments(); }
    }

    public string[] RangeOptions { get; } = { "Last 24 hours", "Last 7 days", "Last 30 days", "All available" };

    private string _selectedRange;
    public string SelectedRange
    {
        get => _selectedRange;
        set { if (SetProperty(ref _selectedRange, value)) RefreshChartAndWorstMoments(); }
    }

    private static readonly SKColor AxisTextColor = new(0x9A, 0x9A, 0xA2);
    private static readonly SKColor AxisSeparatorColor = new(0x33, 0x33, 0x3A, 160);

    public ISeries[] ChartSeries { get; private set; } = Array.Empty<ISeries>();
    public Axis[] ChartXAxes { get; }
    public Axis[] ChartYAxes { get; }

    // ----- #962: worst 10 moments -------------------------------------------------------------------

    public ObservableCollection<WorstMomentRow> WorstMoments { get; } = new();

    // ----- #963: alert digest ---------------------------------------------------------------------

    private string _alertDigestText = string.Empty;
    public string AlertDigestText { get => _alertDigestText; private set => SetProperty(ref _alertDigestText, value); }

    public ObservableCollection<AlertHistoryEntry> RecentAlerts { get; } = new();

    // ----- #964: quiet hours + per-rule channel overrides ------------------------------------------

    public bool QuietHoursEnabled
    {
        get => _alertingSettings.QuietHoursEnabled;
        set
        {
            if (_alertingSettings.QuietHoursEnabled == value) return;
            _alertingSettings.QuietHoursEnabled = value;
            AlertingSettingsService.Save(_alertingSettings);
            OnPropertyChanged();
        }
    }

    public string QuietHoursStartText
    {
        get => _alertingSettings.QuietHoursStart.ToString(@"hh\:mm");
        set
        {
            if (!TimeSpan.TryParse(value, out var parsed)) return;
            _alertingSettings.QuietHoursStart = parsed;
            AlertingSettingsService.Save(_alertingSettings);
            OnPropertyChanged();
        }
    }

    public string QuietHoursEndText
    {
        get => _alertingSettings.QuietHoursEnd.ToString(@"hh\:mm");
        set
        {
            if (!TimeSpan.TryParse(value, out var parsed)) return;
            _alertingSettings.QuietHoursEnd = parsed;
            AlertingSettingsService.Save(_alertingSettings);
            OnPropertyChanged();
        }
    }

    public static Array AvailableChannels { get; } = Enum.GetValues(typeof(AlertChannel));

    public ObservableCollection<RuleChannelRow> RuleChannels { get; } = new();

    public RelayCommand RefreshCommand { get; }

    public BackgroundHealthViewModel(BackgroundHealthCollectorService collector, RulesEngineService rulesEngine)
    {
        _collector = collector;
        _rulesEngine = rulesEngine;
        _alertingSettings = AlertingSettingsService.Load();
        _selectedMetric = AvailableMetrics[0];
        _selectedRange = RangeOptions[1];

        ChartXAxes = new[]
        {
            new Axis
            {
                Labels = Array.Empty<string>(),
                LabelsRotation = 15,
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = null,
            },
        };
        ChartYAxes = new[]
        {
            new Axis
            {
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };

        RefreshCommand = new RelayCommand(_ => RefreshAll());

        _collector.Ticked += OnCollectorTicked;
        _rulesEngine.Reloaded += OnRulesReloaded;

        LoadRuleChannels();
        RefreshAll();
    }

    /// <summary>Fires on every collector cycle (at minimum every 10s) - kept deliberately cheap
    /// (in-memory rolling averages plus one file-size/one-segment read, not a full history parse)
    /// rather than driving a periodic full RefreshAll: CLAUDE.md's "on-demand vs. polled" convention
    /// says anything heavier than a trivial read belongs behind an explicit action, not a timer -
    /// the chart/worst-moments/alert-digest below only re-parse history on construction, a metric/
    /// range change, or the panel's own Refresh button (RefreshCommand), the same "initial load +
    /// manual refresh" shape Startup/SystemSpecs/Stability already use.</summary>
    private void OnCollectorTicked()
    {
        var app = System.Windows.Application.Current;
        app?.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(CollectorStatusText));
            RefreshCost();
            RefreshUsage();
        });
    }

    private void OnRulesReloaded()
    {
        var app = System.Windows.Application.Current;
        app?.Dispatcher.BeginInvoke(LoadRuleChannels);
    }

    private void RefreshAll()
    {
        RefreshUsage();
        RefreshCost();
        RefreshChartAndWorstMoments();
        RefreshAlertDigest();
    }

    // ----- #960 ---------------------------------------------------------------------------------

    private void RefreshUsage()
    {
        var (usedMb, budgetMb, coveredDays) = BackgroundHealthStoreService.GetUsageSummary(BudgetMb);
        UsageText = $"Using {usedMb:0.0} MB of a {budgetMb} MB budget, covering {coveredDays} day{(coveredDays == 1 ? "" : "s")}.";
    }

    // ----- #966 ---------------------------------------------------------------------------------

    private void RefreshCost()
    {
        if (!IsCollectorEnabled)
        {
            CostText = "Not currently collecting.";
            return;
        }
        CostText = $"Average {_collector.AverageCpuPercentEstimate:0.00}% CPU (estimated) for {_collector.AverageDurationMs:0.#} ms every {_collector.EffectiveIntervalSeconds}s.";
    }

    // ----- #962 ---------------------------------------------------------------------------------

    private DateTime RangeStartUtc() => _selectedRange switch
    {
        "Last 24 hours" => DateTime.UtcNow.AddHours(-24),
        "Last 7 days" => DateTime.UtcNow.AddDays(-7),
        "Last 30 days" => DateTime.UtcNow.AddDays(-30),
        _ => DateTime.MinValue,
    };

    private void RefreshChartAndWorstMoments()
    {
        var rows = BackgroundHealthStoreService.ReadRows(RangeStartUtc());
        string metricKey = SelectedMetric.Key;

        // #962: downsample for the chart (a 7/30-day range at a 60s collector interval is
        // thousands of points - a bucketed average keeps the chart responsive while still showing
        // the real trend) but never for the worst-moments table below, which needs the exact
        // extreme rows, not a bucket average.
        const int maxChartPoints = 300;
        var buckets = Downsample(rows, metricKey, maxChartPoints);

        var (glow, core) = LineOf(buckets.Select(b => b.Value).ToArray(), SKColors.DeepSkyBlue, SelectedMetric.Label);
        ChartSeries = new ISeries[] { glow, core };
        ChartXAxes[0].Labels = buckets.Select(b => b.Label).ToArray();
        OnPropertyChanged(nameof(ChartSeries));

        WorstMoments.Clear();
        foreach (var row in rows
            .Select(r => (Row: r, Value: BackgroundHealthStoreService.GetMetricValue(r, metricKey)))
            .Where(t => t.Value.HasValue)
            .OrderByDescending(t => t.Value!.Value)
            .Take(10))
        {
            WorstMoments.Add(new WorstMomentRow
            {
                TimestampLocal = row.Row.TimestampUtc.ToLocalTime(),
                Value = row.Value!.Value,
                MetricLabel = SelectedMetric.Label,
                TopProcessName = row.Row.TopProcessName,
            });
        }
    }

    private static List<(string Label, double Value)> Downsample(List<HealthHistoryRow> rows, string metricKey, int maxPoints)
    {
        var result = new List<(string, double)>();
        if (rows.Count == 0) return result;

        int bucketSize = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)maxPoints));
        for (int start = 0; start < rows.Count; start += bucketSize)
        {
            int end = Math.Min(rows.Count, start + bucketSize);
            var values = new List<double>();
            for (int i = start; i < end; i++)
            {
                var v = BackgroundHealthStoreService.GetMetricValue(rows[i], metricKey);
                if (v.HasValue) values.Add(v.Value);
            }
            var midRow = rows[start + (end - start) / 2];
            string label = midRow.TimestampUtc.ToLocalTime().ToString("M/d HH:mm");
            result.Add((label, values.Count > 0 ? values.Average() : double.NaN));
        }
        return result;
    }

    private const float CoreStrokeWidth = 2f;
    private const float GlowStrokeWidth = 6f;

    private static (LineSeries<double> Glow, LineSeries<double> Core) LineOf(double[] values, SKColor color, string name)
    {
        var glow = new LineSeries<double>
        {
            Values = values,
            Stroke = new SolidColorPaint(color.WithAlpha(70), GlowStrokeWidth),
            Fill = null,
            GeometryStroke = null,
            GeometryFill = null,
            LineSmoothness = 0.2,
            IsHoverable = false,
            IsVisibleAtLegend = false,
        };
        var core = new LineSeries<double>
        {
            Values = values,
            Name = name,
            Stroke = new SolidColorPaint(color, CoreStrokeWidth),
            Fill = null,
            GeometryStroke = null,
            GeometryFill = null,
            LineSmoothness = 0.2,
        };
        return (glow, core);
    }

    /// <summary>Repaints chart axis text/gridlines to match the active theme family - see
    /// PerformanceViewModel.ApplyAxisTheme's remarks.</summary>
    public void ApplyAxisTheme(System.Windows.Media.Color text, System.Windows.Media.Color separator)
    {
        var textSk = new SKColor(text.R, text.G, text.B);
        var sepSk = new SKColor(separator.R, separator.G, separator.B, separator.A);
        ChartXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        ChartYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        ChartYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
    }

    // ----- #963 ---------------------------------------------------------------------------------

    private void RefreshAlertDigest()
    {
        var recent = AlertHistoryService.LoadRecent(TimeSpan.FromDays(7));

        RecentAlerts.Clear();
        foreach (var a in recent.Take(50)) RecentAlerts.Add(a);

        if (recent.Count == 0)
        {
            AlertDigestText = "No alerts in the last 7 days.";
            return;
        }

        var topGroup = recent
            .GroupBy(a => string.IsNullOrEmpty(a.RuleId) ? a.Title : a.RuleId, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .First();

        AlertDigestText = topGroup.Count() > 1
            ? $"{recent.Count} alert{(recent.Count == 1 ? "" : "s")} in the last 7 days, {topGroup.Count()} of them \"{topGroup.First().Title}\"."
            : $"{recent.Count} alert{(recent.Count == 1 ? "" : "s")} in the last 7 days.";
    }

    // ----- #964: per-rule channel overrides -------------------------------------------------------

    private void LoadRuleChannels()
    {
        foreach (var row in RuleChannels) row.PropertyChanged -= OnRuleChannelRowChanged;
        RuleChannels.Clear();

        _suppressRuleChannelSave = true;
        try
        {
            foreach (var loaded in _rulesEngine.Rules.OrderBy(r => r.Rule.Title, StringComparer.OrdinalIgnoreCase))
            {
                var overrideEntry = _alertingSettings.RuleChannelOverrides
                    .FirstOrDefault(kv => string.Equals(kv.Key, loaded.Rule.Id, StringComparison.OrdinalIgnoreCase));
                var channel = overrideEntry.Key is not null ? overrideEntry.Value : loaded.Rule.AlertChannel;

                var row = new RuleChannelRow { RuleId = loaded.Rule.Id, Title = loaded.Rule.Title, Channel = channel };
                row.PropertyChanged += OnRuleChannelRowChanged;
                RuleChannels.Add(row);
            }
        }
        finally
        {
            _suppressRuleChannelSave = false;
        }
    }

    private void OnRuleChannelRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressRuleChannelSave || sender is not RuleChannelRow row || e.PropertyName != nameof(RuleChannelRow.Channel)) return;
        _alertingSettings.RuleChannelOverrides[row.RuleId] = row.Channel;
        AlertingSettingsService.Save(_alertingSettings);
    }

    public void Dispose()
    {
        _collector.Ticked -= OnCollectorTicked;
        _rulesEngine.Reloaded -= OnRulesReloaded;
        foreach (var row in RuleChannels) row.PropertyChanged -= OnRuleChannelRowChanged;
    }
}

/// <summary>#962: one "worst 10 moments" table row - the raw metric value plus whatever context
/// #959's compact collector actually stored for that tick (a timestamp, and optionally the
/// top-CPU-process name; nothing richer, per #959's own "compact" requirement).</summary>
public sealed class WorstMomentRow
{
    public DateTime TimestampLocal { get; init; }
    public double Value { get; init; }
    public string MetricLabel { get; init; } = string.Empty;
    public string? TopProcessName { get; init; }
}
