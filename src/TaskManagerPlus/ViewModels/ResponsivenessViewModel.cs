using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the new Responsiveness tab (suggestions.md #201-214) - "the single feature this whole
/// domain hangs off" per the backlog's own framing. Two independent cadences, like
/// EnergyThermalsViewModel/GpuViewModel before it doesn't fit the shared PerformanceViewModel
/// sampler:
///   1. A cheap, always-on DispatcherTimer (_lightTimer) sampling per-core DPC/interrupt time
///      (#205), DPC queue depth/rate (#206) and the DPC watchdog registry values (#204) - all
///      plain syscalls/perf-counter/registry reads, no ETW.
///   2. An explicit Start/Stop "measurement session" (#213) that repeatedly runs
///      DpcLatencyService.SampleOnceAsync (a logman-capture + tracerpt-parse cycle) while armed -
///      this is the expensive, ETW-backed path, so per CLAUDE.md's on-demand convention it never
///      runs on its own.
/// Driver identity (#211) and the known-offender hint table (#212) are loaded once at start-up
/// (a couple of shell-outs, not something that changes tick to tick) and joined into every driver
/// row DpcLatencyService produces.
/// </summary>
public sealed class ResponsivenessViewModel : ObservableObject, IDisposable
{
    private const int HistoryLength = 60;
    private static readonly SKColor AxisTextColor = new(0x9A, 0x9A, 0xA2);
    private static readonly SKColor AxisSeparatorColor = new(0x33, 0x33, 0x3A, 160);
    private const float CoreStrokeWidth = 2f;
    private const float GlowStrokeWidth = 7f;

    private readonly DpcLatencyService _dpc = new();
    private readonly PerCoreDpcService _perCore = new();
    private readonly DispatcherTimer _lightTimer;
    private Dictionary<string, DriverIdentityInfo> _driverIdentities = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _measureCts;
    private Task? _measureLoopTask;

    public ObservableCollection<DriverDpcRow> DriverDpcRows { get; } = new();
    public ObservableCollection<DriverIsrRow> DriverIsrRows { get; } = new();
    public ObservableCollection<CoreDpcRow> CoreDpcRows { get; } = new();
    public ObservableCollection<CoreDpcQueueRow> CoreDpcQueueRows { get; } = new();
    public ObservableCollection<DpcSpikeEvent> RecentSpikes { get; } = new();

    // #207: rolling max-DPC-latency-per-sample chart, following the app's glow+core LineSeries
    // convention, plus a flat dashed line at the audio-glitch threshold (#214) so a user can see
    // at a glance how often samples cross it. Only advances while a measurement session (#213) is
    // running, since that's the only thing that produces real samples - see the class remarks.
    public ObservableCollection<double> DpcLatencyHistory { get; } = NewHistory(0);
    public ObservableCollection<double> ThresholdHistory { get; } = NewHistory(1000);
    private readonly LineSeries<double> _latencyGlow;
    private readonly LineSeries<double> _latencyCore;
    private readonly LineSeries<double> _thresholdLine;
    public ISeries[] LatencySeries { get; }
    public Axis[] HiddenXAxes { get; }
    public Axis[] LatencyYAxes { get; }

    /// <summary>Whether logman.exe/tracerpt.exe are present - the measurement session (#201-203,
    /// #207-209, #213-214) is hidden with an explanation when this is false, per CLAUDE.md's
    /// "degrade to hidden, never fabricate" rule.</summary>
    public bool DpcToolsAvailable => _dpc.ToolsAvailable;

    /// <summary>Whether wpr.exe/tracerpt.exe are present for the offline capture button (#210).</summary>
    public bool WprToolsAvailable => WprCaptureService.IsAvailable;

    private bool _isMeasuring;
    public bool IsMeasuring { get => _isMeasuring; private set => SetProperty(ref _isMeasuring, value); }

    private string _measurementStatusText = "Not measuring - press Start to begin sampling DPC/ISR latency.";
    public string MeasurementStatusText { get => _measurementStatusText; private set => SetProperty(ref _measurementStatusText, value); }

    private string _sessionSummaryText = string.Empty;
    public string SessionSummaryText { get => _sessionSummaryText; private set => SetProperty(ref _sessionSummaryText, value); }

    public double HighestDpcUs => _dpc.HighestDpcUs;
    public string HighestDpcDriver => _dpc.HighestDpcDriver;
    public double RollingAvgUs => _dpc.RollingAvgUs;
    public double RollingP99Us => _dpc.RollingP99Us;
    public int AudioGlitchCount => _dpc.AudioGlitchCount;

    /// <summary>#214: the audio-glitch/spike-context (#209) cutoff in microseconds - one knob for
    /// both, default 1000us (~a dropout's worth of buffer at 48kHz).</summary>
    public double AudioGlitchThresholdUs
    {
        get => _dpc.AudioGlitchThresholdUs;
        set
        {
            double clamped = Math.Clamp(value, 50, 20000);
            if (Math.Abs(_dpc.AudioGlitchThresholdUs - clamped) < 0.01) return;
            _dpc.AudioGlitchThresholdUs = clamped;
            for (int i = 0; i < ThresholdHistory.Count; i++) ThresholdHistory[i] = clamped;
            OnPropertyChanged();
        }
    }

    private DpcWatchdogInfo _watchdog = new() { WatchdogEnabled = true, StatusText = "Loading..." };
    public DpcWatchdogInfo Watchdog { get => _watchdog; private set => SetProperty(ref _watchdog, value); }

    /// <summary>#204: how close the worst observed DPC/ISR run is to the watchdog's own bugcheck
    /// threshold, as a 0-100 percent - a simple text pointer to the Stability tab covers the
    /// "cross-linked to any 0x133 bugchecks" ask without new cross-tab plumbing.</summary>
    public double WatchdogHeadroomPercent
    {
        get
        {
            int timeoutSeconds = Watchdog.TimeoutValue is > 0 ? Watchdog.TimeoutValue.Value : DpcWatchdogService.DefaultTimeoutSeconds;
            double timeoutUs = timeoutSeconds * 1_000_000.0;
            return timeoutUs <= 0 ? 0 : Math.Clamp(HighestDpcUs / timeoutUs * 100.0, 0, 100);
        }
    }

    private string _wprStatusText = string.Empty;
    public string WprStatusText { get => _wprStatusText; private set => SetProperty(ref _wprStatusText, value); }

    private bool _isCapturing;
    public bool IsCapturing { get => _isCapturing; private set => SetProperty(ref _isCapturing, value); }

    private string? _lastEtlPath;
    private string? _lastReportPath;

    public AsyncRelayCommand StartMeasurementCommand { get; }
    public RelayCommand StopMeasurementCommand { get; }
    public RelayCommand CopySummaryCommand { get; }
    public AsyncRelayCommand CaptureWprCommand { get; }
    public RelayCommand OpenCaptureCommand { get; }
    public RelayCommand OpenReportCommand { get; }

    public ResponsivenessViewModel()
    {
        HiddenXAxes = new[]
        {
            new Axis { IsVisible = false, MinLimit = 0, MaxLimit = HistoryLength - 1, ShowSeparatorLines = false },
        };
        LatencyYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v:0} µs",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };

        var latColor = SKColors.OrangeRed;
        _latencyGlow = new LineSeries<double>
        {
            Values = DpcLatencyHistory,
            Stroke = new SolidColorPaint(latColor.WithAlpha(70), GlowStrokeWidth),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0.3, IsHoverable = false, IsVisibleAtLegend = false,
        };
        _latencyCore = new LineSeries<double>
        {
            Values = DpcLatencyHistory,
            Name = "Max DPC latency",
            Stroke = new SolidColorPaint(latColor, CoreStrokeWidth),
            Fill = new LinearGradientPaint(latColor.WithAlpha(90), latColor.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)),
            GeometryStroke = null, GeometryFill = null, LineSmoothness = 0.3,
        };
        // #214: flat marker line at the audio-glitch threshold - a plain constant-value LineSeries
        // rather than a chart-library-specific "section" primitive, matching every other series in
        // this app's chart code (kept simple and guaranteed to render the same way on any LiveChartsCore
        // version this project references).
        _thresholdLine = new LineSeries<double>
        {
            Values = ThresholdHistory,
            Name = "Audio-glitch threshold",
            Stroke = new SolidColorPaint(SKColors.Gray, 1.5f),
            Fill = null, GeometryStroke = null, GeometryFill = null, LineSmoothness = 0,
        };
        LatencySeries = new ISeries[] { _latencyGlow, _latencyCore, _thresholdLine };

        StartMeasurementCommand = new AsyncRelayCommand(() => StartMeasurementAsync(), () => !IsMeasuring && DpcToolsAvailable);
        StopMeasurementCommand = new RelayCommand(() => StopMeasurement(), () => IsMeasuring);
        CopySummaryCommand = new RelayCommand(() => CopySummary(), () => !string.IsNullOrEmpty(SessionSummaryText));
        CaptureWprCommand = new AsyncRelayCommand(() => CaptureWprAsync(), () => !IsCapturing && WprToolsAvailable);
        OpenCaptureCommand = new RelayCommand(() => { if (_lastEtlPath is not null) WprCaptureService.OpenInDefaultApp(_lastEtlPath); }, () => _lastEtlPath is not null);
        OpenReportCommand = new RelayCommand(() => { if (_lastReportPath is not null) WprCaptureService.OpenInDefaultApp(_lastReportPath); }, () => _lastReportPath is not null);

        _lightTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _lightTimer.Tick += (_, _) => SampleLight();
        _lightTimer.Start();
        SampleLight();

        Watchdog = DpcWatchdogService.Read();
        _ = LoadDriverIdentitiesAsync();
    }

    /// <summary>#211: driver metadata join, loaded once (a couple of shell-outs, not a per-tick
    /// cost) and re-applied to whatever driver rows already exist so the grid gains identity text
    /// as soon as the join finishes, even if it lands mid-session.</summary>
    private async Task LoadDriverIdentitiesAsync()
    {
        try
        {
            _driverIdentities = await DriverIdentityService.LoadAsync();
            RebuildDriverRows();
        }
        catch
        {
            // best-effort - rows just keep showing bare filenames
        }
    }

    /// <summary>#204/#205/#206: the cheap always-on tick - registry read plus two syscall/perf-
    /// counter samples, none of which need Task.Run (all are fast, non-blocking reads matching the
    /// other lightweight per-tick services in this app).</summary>
    private void SampleLight()
    {
        Watchdog = DpcWatchdogService.Read();
        OnPropertyChanged(nameof(WatchdogHeadroomPercent));

        var coreRows = _perCore.SampleCoreDpcInterrupt();
        if (coreRows.Count > 0)
        {
            CoreDpcRows.Clear();
            foreach (var r in coreRows) CoreDpcRows.Add(r);
        }

        var queueRows = _perCore.SampleQueueRates();
        if (queueRows.Count > 0)
        {
            CoreDpcQueueRows.Clear();
            foreach (var r in queueRows) CoreDpcQueueRows.Add(r);
        }
    }

    /// <summary>#213: Start button - resets the session, arms IsMeasuring, and kicks off a
    /// background loop of short SampleOnceAsync captures until Stop is pressed.</summary>
    private async Task StartMeasurementAsync()
    {
        if (IsMeasuring || !DpcToolsAvailable) return;

        _dpc.ResetSession();
        RebuildDriverRows();
        DriverIsrRows.Clear();
        RecentSpikes.Clear();
        for (int i = 0; i < DpcLatencyHistory.Count; i++) DpcLatencyHistory[i] = 0;
        SessionSummaryText = string.Empty;
        MeasurementStatusText = "Starting DPC/ISR capture...";

        _measureCts = new CancellationTokenSource();
        IsMeasuring = true;
        _measureLoopTask = MeasureLoopAsync(_measureCts.Token);
        await Task.CompletedTask;
    }

    private async Task MeasureLoopAsync(CancellationToken ct)
    {
        var window = TimeSpan.FromSeconds(3);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (ok, message, parsed) = await _dpc.SampleOnceAsync(window, ct);
                MeasurementStatusText = message;

                if (ok)
                {
                    RebuildDriverRows();
                    DriverIsrRows.Clear();
                    foreach (var r in _dpc.BuildDriverIsrRows()) DriverIsrRows.Add(r);

                    RecentSpikes.Clear();
                    foreach (var s in _dpc.RecentSpikes) RecentSpikes.Add(s);

                    DpcLatencyHistory.Add(_dpc.HighestDpcUs);
                    if (DpcLatencyHistory.Count > HistoryLength) DpcLatencyHistory.RemoveAt(0);
                    ThresholdHistory.Add(AudioGlitchThresholdUs);
                    if (ThresholdHistory.Count > HistoryLength) ThresholdHistory.RemoveAt(0);

                    RaiseHeadlineChanged();
                }

                if (parsed == 0 && !ok)
                {
                    // A hard failure (tools missing, access denied) won't fix itself by retrying -
                    // stop rather than spin forever on the same error.
                    await Application.Current.Dispatcher.InvokeAsync(StopMeasurement);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on Stop
        }
    }

    /// <summary>#213: Stop button - cancels the sample loop and builds the min/avg/max/p99-per-
    /// driver summary for the "Copy summary" button.</summary>
    private void StopMeasurement()
    {
        if (!IsMeasuring) return;
        _measureCts?.Cancel();
        IsMeasuring = false;

        var summary = _dpc.BuildSummary();
        SessionSummaryText = summary.ToSummaryText();
        MeasurementStatusText = $"Stopped - measured {summary.Duration:mm\\:ss}.";
    }

    private void CopySummary()
    {
        if (string.IsNullOrEmpty(SessionSummaryText)) return;
        try { Clipboard.SetText(SessionSummaryText); } catch { /* best-effort */ }
    }

    /// <summary>#210: offline wpr.exe capture for a user who'd rather not run the live measurement
    /// session - fixed 30s window (kept simple for v1; long enough to catch a reproducible stutter,
    /// short enough not to produce an unwieldy trace).</summary>
    private async Task CaptureWprAsync()
    {
        if (IsCapturing || !WprToolsAvailable) return;
        IsCapturing = true;
        WprStatusText = "Capturing for 30s (reproduce the stutter now)...";
        try
        {
            var (ok, message, etl, report) = await WprCaptureService.CaptureAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            WprStatusText = message;
            _lastEtlPath = ok ? etl : null;
            _lastReportPath = ok ? report : null;
            OpenCaptureCommand.RaiseCanExecuteChanged();
            OpenReportCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            IsCapturing = false;
        }
    }

    private void RebuildDriverRows()
    {
        DriverDpcRows.Clear();
        foreach (var row in _dpc.BuildDriverDpcRows(Enrich)) DriverDpcRows.Add(row);
    }

    /// <summary>#211/#212: joins a bare driver filename to its identity metadata plus the small
    /// known-offender hint table - passed into DpcLatencyService.BuildDriverDpcRows so both live
    /// entirely in the row-building step rather than being re-derived per binding.</summary>
    private (string? Hint, DriverIdentityInfo? Identity) Enrich(string driverName)
    {
        _driverIdentities.TryGetValue(driverName, out var identity);
        return (KnownOffenderDriverLookup.Hint(driverName), identity);
    }

    private void RaiseHeadlineChanged()
    {
        OnPropertyChanged(nameof(HighestDpcUs));
        OnPropertyChanged(nameof(HighestDpcDriver));
        OnPropertyChanged(nameof(RollingAvgUs));
        OnPropertyChanged(nameof(RollingP99Us));
        OnPropertyChanged(nameof(AudioGlitchCount));
        OnPropertyChanged(nameof(WatchdogHeadroomPercent));
    }

    /// <summary>Repaints chart axis text/gridlines to match the active theme family - see
    /// PerformanceViewModel.ApplyAxisTheme's remarks; same SkiaSharp-outside-WPF-resources gap.</summary>
    public void ApplyAxisTheme(Color text, Color separator)
    {
        var textSk = new SKColor(text.R, text.G, text.B);
        var sepSk = new SKColor(separator.R, separator.G, separator.B, separator.A);
        LatencyYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        LatencyYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
    }

    private static ObservableCollection<double> NewHistory(double fill = 0)
    {
        var col = new ObservableCollection<double>();
        for (int i = 0; i < HistoryLength; i++) col.Add(fill);
        return col;
    }

    public void Dispose()
    {
        _lightTimer.Stop();
        _measureCts?.Cancel();
        _perCore.Dispose();
    }
}
