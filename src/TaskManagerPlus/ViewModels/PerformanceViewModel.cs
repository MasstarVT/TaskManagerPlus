using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

public sealed class PerformanceViewModel : ObservableObject, IDisposable
{
    private const int HistoryLength = 60; // seconds of history shown

    private readonly HardwareMonitorService _hardware = new();
    private readonly DispatcherTimer _timer;

    // Topology is static (doesn't change at runtime), so it's queried once here rather than
    // per tick, and kept out of the per-tick HardwareSnapshot DTO.
    private readonly CpuTopologySnapshot _topology = CpuTopologyService.Query();

    /// <summary>True only on genuinely hybrid CPUs (Intel 12th-gen+ style P-core/E-core split) -
    /// the CPU tab should hide the P/E color distinction entirely when this is false.</summary>
    public bool HasHybridTopology => _topology.HasHybridTopology;

    /// <summary>True only when the system has more than one NUMA node - the CPU tab should show
    /// a single flat core grid (no "NUMA Node N" group headers) when this is false.</summary>
    public bool HasMultipleNumaNodes => _topology.HasMultipleNumaNodes;

    /// <summary>Round 8 #26: true when at least one physical core hosts more than one logical
    /// processor (SMT/Hyper-Threading) - see CpuTopologySnapshot.HasSmt.</summary>
    public bool HasSmt => _topology.HasSmt;

    public ObservableCollection<double> CpuHistory { get; } = NewHistory();
    public ObservableCollection<double> RamHistory { get; } = NewHistory();
    public ObservableCollection<double> DiskHistory { get; } = NewHistory();
    public ObservableCollection<double> NetworkReceiveHistory { get; } = NewHistory();
    public ObservableCollection<double> NetworkSendHistory { get; } = NewHistory();
    public ObservableCollection<double> CommittedHistory { get; } = NewHistory();

    public ObservableCollection<CoreUsage> Cores { get; } = new();

    // Each metric is drawn as a pair of series: a thick, translucent "glow" stroke behind a
    // crisp core line, so the charts read closer to the softly-glowing lines in the reference
    // design instead of a flat single stroke.
    private readonly LineSeries<double> _cpuGlow;
    private readonly LineSeries<double> _cpuCore;
    private readonly LineSeries<double> _ramGlow;
    private readonly LineSeries<double> _ramCore;
    private readonly LineSeries<double> _diskGlow;
    private readonly LineSeries<double> _diskCore;
    private readonly LineSeries<double> _netRecvGlow;
    private readonly LineSeries<double> _netRecvCore;
    private readonly LineSeries<double> _netSendGlow;
    private readonly LineSeries<double> _netSendCore;
    private readonly LineSeries<double> _committedGlow;
    private readonly LineSeries<double> _committedCore;

    public ISeries[] CpuSeries { get; }
    public ISeries[] RamSeries { get; }
    public ISeries[] DiskSeries { get; }
    public ISeries[] NetworkSeries { get; }
    public ISeries[] CommittedSeries { get; }
    public Axis[] PercentYAxes { get; }
    public Axis[] HiddenXAxes { get; }
    public Axis[] NetworkYAxes { get; }
    public Axis[] MemoryBytesYAxes { get; }

    // Shared paints so the Network chart's legend/tooltip (the only ones left visible) render
    // in the app's dark palette instead of LiveCharts' default light-theme black-on-white.
    // Settable (not just field-initialized) so ApplyAxisTheme can repaint them on a theme-family
    // switch - these live outside WPF's resource system (SkiaSharp paints), so DynamicResource
    // alone can't reach them.
    private SolidColorPaint _legendTextPaint = AxisTextPaint();
    public SolidColorPaint LegendTextPaint { get => _legendTextPaint; private set => SetProperty(ref _legendTextPaint, value); }

    private SolidColorPaint _legendBackgroundPaint = new(new SKColor(0x26, 0x26, 0x2B));
    public SolidColorPaint LegendBackgroundPaint { get => _legendBackgroundPaint; private set => SetProperty(ref _legendBackgroundPaint, value); }

    private SolidColorPaint _tooltipTextPaint = AxisTextPaint();
    public SolidColorPaint TooltipTextPaint { get => _tooltipTextPaint; private set => SetProperty(ref _tooltipTextPaint, value); }

    private SolidColorPaint _tooltipBackgroundPaint = new(new SKColor(0x26, 0x26, 0x2B));
    public SolidColorPaint TooltipBackgroundPaint { get => _tooltipBackgroundPaint; private set => SetProperty(ref _tooltipBackgroundPaint, value); }

    private Color _cpuColor;
    public Color CpuColor { get => _cpuColor; private set => SetProperty(ref _cpuColor, value); }

    private Color _ramColor;
    public Color RamColor { get => _ramColor; private set => SetProperty(ref _ramColor, value); }

    private Color _diskColor;
    public Color DiskColor { get => _diskColor; private set => SetProperty(ref _diskColor, value); }

    private Color _networkColor;
    public Color NetworkColor { get => _networkColor; private set => SetProperty(ref _networkColor, value); }

    private string _cpuName = string.Empty;
    public string CpuName { get => _cpuName; private set => SetProperty(ref _cpuName, value); }

    private string _cpuSpecs = string.Empty;
    public string CpuSpecs { get => _cpuSpecs; private set => SetProperty(ref _cpuSpecs, value); }

    private double _cpuCurrentPercent;
    public double CpuCurrentPercent { get => _cpuCurrentPercent; private set => SetProperty(ref _cpuCurrentPercent, value); }

    private double _cpuCurrentClockGhz;
    public double CpuCurrentClockGhz { get => _cpuCurrentClockGhz; private set => SetProperty(ref _cpuCurrentClockGhz, value); }

    private double _cpuBaseClockGhz;
    public double CpuBaseClockGhz { get => _cpuBaseClockGhz; private set => SetProperty(ref _cpuBaseClockGhz, value); }

    // Session min/max/avg clock speed (#11) - plain running accumulators, no history buffer
    // needed since only the summary values are shown, not a trend line.
    private double _cpuMinClockGhz = double.MaxValue;
    public double CpuMinClockGhz { get => _cpuMinClockGhz; private set => SetProperty(ref _cpuMinClockGhz, value); }

    private double _cpuMaxClockGhz;
    public double CpuMaxClockGhzSeen { get => _cpuMaxClockGhz; private set => SetProperty(ref _cpuMaxClockGhz, value); }

    private double _cpuAvgClockGhz;
    public double CpuAvgClockGhz { get => _cpuAvgClockGhz; private set => SetProperty(ref _cpuAvgClockGhz, value); }

    private double _cpuClockSampleSum;
    private long _cpuClockSampleCount;

    /// <summary>Base clock (rated spec, from WMI) vs. the live clock right now, as a percent
    /// delta (#18) - "+12%" reads as turbo boost, "~0%" (or negative, on some throttling
    /// scenarios) reads as "stuck at base clock".</summary>
    private double _cpuVsBasePercent;
    public double CpuVsBasePercent { get => _cpuVsBasePercent; private set => SetProperty(ref _cpuVsBasePercent, value); }

    // Round 8 #27: turbo-boost time-at-frequency histogram, accumulated across the whole session -
    // bucketed by CpuVsBasePercent (the base-vs-current comparison already computed each tick)
    // rather than raw GHz, so the buckets stay meaningful across different CPU models instead of
    // needing per-model frequency ranges. Six fixed buckets, updated in place each tick (no
    // Add/Remove) so the CPU tab's bar row doesn't flicker.
    private static readonly string[] TurboHistogramLabels = { "Below base", "At base", "Light turbo", "Turbo", "High turbo", "Max turbo" };
    private readonly long[] _turboBucketCounts = new long[TurboHistogramLabels.Length];
    private long _turboTotalSamples;
    public ObservableCollection<TurboHistogramBucket> TurboHistogram { get; } = new(
        TurboHistogramLabels.Select(l => new TurboHistogramBucket { Label = l }));

    private double _cpuInterruptPercent;
    public double CpuInterruptPercent { get => _cpuInterruptPercent; private set => SetProperty(ref _cpuInterruptPercent, value); }

    private double _cpuDpcPercent;
    public double CpuDpcPercent { get => _cpuDpcPercent; private set => SetProperty(ref _cpuDpcPercent, value); }

    private double _contextSwitchesPerSec;
    public double ContextSwitchesPerSec { get => _contextSwitchesPerSec; private set => SetProperty(ref _contextSwitchesPerSec, value); }

    private double _cpuQueueLength;
    public double CpuQueueLength { get => _cpuQueueLength; private set { if (SetProperty(ref _cpuQueueLength, value)) OnPropertyChanged(nameof(CpuQueueLengthGaugePercent)); } }

    // Like the Storage tab's disk-latency gauges: queue length has no natural 0-100 range, so
    // this is a rough "how concerning" fill (past 2x logical processors = sustained
    // over-subscription), not an exact value - the numeric readout next to it is the real one.
    public double CpuQueueLengthGaugePercent => _logicalProcessors <= 0 ? 0 : Math.Clamp(CpuQueueLength / (2.0 * _logicalProcessors) * 100.0, 0, 100);

    private int _logicalProcessors = Environment.ProcessorCount;

    // #83: C-state residency - a CPU idling deeply for power savings (high C2/C3) reads very
    // differently from one thermal/power-throttled at a low clock under real load, even though
    // both can look like "the CPU seems slow" from the outside.
    private bool _cStatesAvailable;
    public bool CStatesAvailable { get => _cStatesAvailable; private set => SetProperty(ref _cStatesAvailable, value); }

    private double _cpuIdlePercent;
    public double CpuIdlePercent { get => _cpuIdlePercent; private set => SetProperty(ref _cpuIdlePercent, value); }

    private double _cpuC1Percent;
    public double CpuC1Percent { get => _cpuC1Percent; private set => SetProperty(ref _cpuC1Percent, value); }

    private double _cpuC2Percent;
    public double CpuC2Percent { get => _cpuC2Percent; private set => SetProperty(ref _cpuC2Percent, value); }

    private double _cpuC3Percent;
    public double CpuC3Percent { get => _cpuC3Percent; private set => SetProperty(ref _cpuC3Percent, value); }

    private double _ramUsedGb;
    public double RamUsedGb { get => _ramUsedGb; private set => SetProperty(ref _ramUsedGb, value); }

    private double _ramTotalGb;
    public double RamTotalGb { get => _ramTotalGb; private set => SetProperty(ref _ramTotalGb, value); }

    private double _ramPercent;
    public double RamPercent { get => _ramPercent; private set => SetProperty(ref _ramPercent, value); }

    // Windows-native memory breakdown (Available/Committed/Cached) - see CLAUDE.md's Memory
    // deep-dive notes for why these are the categories used instead of macOS-style
    // "wired"/"compressed" labels, which don't have a real Windows equivalent.
    private double _ramAvailableGb;
    public double RamAvailableGb { get => _ramAvailableGb; private set => SetProperty(ref _ramAvailableGb, value); }

    private double _ramAvailablePercent;
    public double RamAvailablePercent { get => _ramAvailablePercent; private set => SetProperty(ref _ramAvailablePercent, value); }

    private double _committedGb;
    public double CommittedGb { get => _committedGb; private set => SetProperty(ref _committedGb, value); }

    private double _commitLimitGb;
    public double CommitLimitGb { get => _commitLimitGb; private set => SetProperty(ref _commitLimitGb, value); }

    private double _committedPercent;
    public double CommittedPercent { get => _committedPercent; private set => SetProperty(ref _committedPercent, value); }

    private double _cachedGb;
    public double CachedGb { get => _cachedGb; private set => SetProperty(ref _cachedGb, value); }

    private double _cachedPercent;
    public double CachedPercent { get => _cachedPercent; private set => SetProperty(ref _cachedPercent, value); }

    private double _pageFileUsedGb;
    public double PageFileUsedGb { get => _pageFileUsedGb; private set => SetProperty(ref _pageFileUsedGb, value); }

    private double _pageFileTotalGb;
    public double PageFileTotalGb { get => _pageFileTotalGb; private set => SetProperty(ref _pageFileTotalGb, value); }

    private double _pageFilePercent;
    public double PageFilePercent { get => _pageFilePercent; private set => SetProperty(ref _pageFilePercent, value); }

    // Memory diagnostics (#20/#22/#24) - see HardwareSnapshot's remarks for what each figure means.
    private double _pageFaultsPerSec;
    public double PageFaultsPerSec { get => _pageFaultsPerSec; private set => SetProperty(ref _pageFaultsPerSec, value); }

    private double _hardFaultsPerSec;
    public double HardFaultsPerSec { get => _hardFaultsPerSec; private set => SetProperty(ref _hardFaultsPerSec, value); }

    /// <summary>Soft faults = total - hard, clamped at 0 (the two counters are read independently
    /// a few microseconds apart, so a tiny negative delta is possible without this).</summary>
    public double SoftFaultsPerSec => Math.Max(0, PageFaultsPerSec - HardFaultsPerSec);

    private double _standbyGb;
    public double StandbyGb { get => _standbyGb; private set => SetProperty(ref _standbyGb, value); }

    private double _standbyPercent;
    public double StandbyPercent { get => _standbyPercent; private set => SetProperty(ref _standbyPercent, value); }

    private double _poolNonpagedGb;
    public double PoolNonpagedGb { get => _poolNonpagedGb; private set => SetProperty(ref _poolNonpagedGb, value); }

    private double _poolPagedGb;
    public double PoolPagedGb { get => _poolPagedGb; private set => SetProperty(ref _poolPagedGb, value); }

    // Round 8 #35: "memory in use by category" stacked-bar breakdown, built purely from figures
    // already read above (RamTotalGb/RamAvailableGb/StandbyGb) - no new signal. Matches the same
    // In Use / Standby / Free split Windows' own Resource Monitor shows: GlobalMemoryStatusEx's
    // "available" figure already folds the standby (reclaimable cache) list in, so "in use" here
    // is Total minus Available (excludes standby), Standby is its own slice, and Free is whatever
    // of Available isn't standby - the three sum back to the total.
    public double MemoryInUsePercent => RamTotalGb <= 0 ? 0 : Math.Clamp((RamTotalGb - RamAvailableGb) / RamTotalGb * 100.0, 0, 100);
    public double MemoryStandbyPercent => RamTotalGb <= 0 ? 0 : Math.Clamp(StandbyGb / RamTotalGb * 100.0, 0, 100);
    public double MemoryFreePercent => Math.Clamp(100.0 - MemoryInUsePercent - MemoryStandbyPercent, 0, 100);

    private double _diskPercent;
    public double DiskPercent { get => _diskPercent; private set => SetProperty(ref _diskPercent, value); }

    private double _diskReadBps;
    public double DiskReadBps { get => _diskReadBps; private set => SetProperty(ref _diskReadBps, value); }

    private double _diskWriteBps;
    public double DiskWriteBps { get => _diskWriteBps; private set => SetProperty(ref _diskWriteBps, value); }

    private double _diskQueueLength;
    public double DiskQueueLength { get => _diskQueueLength; private set { if (SetProperty(ref _diskQueueLength, value)) OnPropertyChanged(nameof(DiskQueueLengthGaugePercent)); } }

    private double _diskReadLatencyMs;
    public double DiskReadLatencyMs { get => _diskReadLatencyMs; private set { if (SetProperty(ref _diskReadLatencyMs, value)) OnPropertyChanged(nameof(DiskReadLatencyGaugePercent)); } }

    private double _diskWriteLatencyMs;
    public double DiskWriteLatencyMs { get => _diskWriteLatencyMs; private set { if (SetProperty(ref _diskWriteLatencyMs, value)) OnPropertyChanged(nameof(DiskWriteLatencyGaugePercent)); } }

    // Queue length and latency have no natural 0-100 range like a percentage does, but the VfdMeter/
    // MeterTile segmented bar reads a Percent regardless - these give it a meaningful "how concerning
    // is this reading" fill rather than leaving the bar always empty. Thresholds are rough diagnostic
    // rules of thumb (a sustained queue length above ~8, or latency above ~50ms, indicates the disk is
    // the bottleneck), not exact - the numeric readout next to them is still the real value.
    public double DiskQueueLengthGaugePercent => Math.Clamp(DiskQueueLength / 8.0 * 100.0, 0, 100);
    public double DiskReadLatencyGaugePercent => Math.Clamp(DiskReadLatencyMs / 50.0 * 100.0, 0, 100);
    public double DiskWriteLatencyGaugePercent => Math.Clamp(DiskWriteLatencyMs / 50.0 * 100.0, 0, 100);

    private double _networkReceiveBps;
    public double NetworkReceiveBps { get => _networkReceiveBps; private set => SetProperty(ref _networkReceiveBps, value); }

    private double _networkSendBps;
    public double NetworkSendBps { get => _networkSendBps; private set => SetProperty(ref _networkSendBps, value); }

    private long _networkInErrors;
    public long NetworkInErrors { get => _networkInErrors; private set => SetProperty(ref _networkInErrors, value); }

    private long _networkInDiscards;
    public long NetworkInDiscards { get => _networkInDiscards; private set => SetProperty(ref _networkInDiscards, value); }

    private long _networkOutErrors;
    public long NetworkOutErrors { get => _networkOutErrors; private set => SetProperty(ref _networkOutErrors, value); }

    private long _networkOutDiscards;
    public long NetworkOutDiscards { get => _networkOutDiscards; private set => SetProperty(ref _networkOutDiscards, value); }

    public bool HasNetworkErrors => NetworkInErrors > 0 || NetworkInDiscards > 0 || NetworkOutErrors > 0 || NetworkOutDiscards > 0;

    private double _tcpRetransmitsPerSec;
    public double TcpRetransmitsPerSec { get => _tcpRetransmitsPerSec; private set => SetProperty(ref _tcpRetransmitsPerSec, value); }

    private int _processCount;
    public int ProcessCount { get => _processCount; private set => SetProperty(ref _processCount, value); }

    private int _threadCount;
    public int ThreadCount { get => _threadCount; private set => SetProperty(ref _threadCount, value); }

    private int _handleCount;
    public int HandleCount { get => _handleCount; private set => SetProperty(ref _handleCount, value); }

    private string _uptime = string.Empty;
    public string Uptime { get => _uptime; private set => SetProperty(ref _uptime, value); }

    // Chart axes default to LiveCharts' light theme (dark text/gridlines meant for a white
    // background), which is why charts rendered as bright white boxes against the dark app
    // theme. Paint them explicitly to match the app's dark palette instead.
    private static readonly SKColor AxisTextColor = new(0x9A, 0x9A, 0xA2);   // TextSecondaryColor
    private static readonly SKColor AxisSeparatorColor = new(0x33, 0x33, 0x3A, 160); // BorderColor, translucent

    private static SolidColorPaint AxisTextPaint() => new(AxisTextColor);
    private static SolidColorPaint AxisSeparatorPaint() => new(AxisSeparatorColor) { StrokeThickness = 1 };

    public PerformanceViewModel()
    {
        HiddenXAxes = new[]
        {
            new Axis
            {
                IsVisible = false,
                MinLimit = 0,
                MaxLimit = HistoryLength - 1,
                ShowSeparatorLines = false,
            },
        };
        PercentYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                MaxLimit = 100,
                MinStep = 25,
                Labeler = v => $"{v:0}%",
                LabelsPaint = AxisTextPaint(),
                SeparatorsPaint = AxisSeparatorPaint(),
            },
        };
        NetworkYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => Formatting.FormatByteRate(v),
                LabelsPaint = AxisTextPaint(),
                SeparatorsPaint = AxisSeparatorPaint(),
            },
        };
        MemoryBytesYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => Formatting.FormatBytes(v),
                LabelsPaint = AxisTextPaint(),
                SeparatorsPaint = AxisSeparatorPaint(),
            },
        };

        (_cpuGlow, _cpuCore) = LineOf(CpuHistory, SKColors.DeepSkyBlue);
        (_ramGlow, _ramCore) = LineOf(RamHistory, SKColors.MediumPurple);
        (_diskGlow, _diskCore) = LineOf(DiskHistory, SKColors.Orange);
        (_netRecvGlow, _netRecvCore) = LineOf(NetworkReceiveHistory, SKColors.LimeGreen, "Receive");
        (_netSendGlow, _netSendCore) = LineOf(NetworkSendHistory, SKColors.OrangeRed, "Send");
        (_committedGlow, _committedCore) = LineOf(CommittedHistory, SKColors.MediumPurple);

        CpuSeries = new ISeries[] { _cpuGlow, _cpuCore };
        RamSeries = new ISeries[] { _ramGlow, _ramCore };
        DiskSeries = new ISeries[] { _diskGlow, _diskCore };
        NetworkSeries = new ISeries[] { _netRecvGlow, _netRecvCore, _netSendGlow, _netSendCore };
        CommittedSeries = new ISeries[] { _committedGlow, _committedCore };

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    private static ObservableCollection<double> NewHistory()
    {
        var col = new ObservableCollection<double>();
        for (int i = 0; i < HistoryLength; i++) col.Add(0);
        return col;
    }

    private const float CoreStrokeWidth = 2f;
    private const float GlowStrokeWidth = 7f;

    private static (LineSeries<double> Glow, LineSeries<double> Core) LineOf(ObservableCollection<double> values, SKColor color, string? name = null)
    {
        // The glow series shares the same Values as its core line, is drawn first (so the core
        // line renders crisply on top of it), and is hidden from tooltips/legends so it reads
        // purely as a visual effect rather than a second data series.
        var glow = new LineSeries<double>
        {
            Values = values,
            Stroke = new SolidColorPaint(color.WithAlpha(70), GlowStrokeWidth),
            Fill = null,
            GeometryStroke = null,
            GeometryFill = null,
            LineSmoothness = 0.3,
            IsHoverable = false,
            IsVisibleAtLegend = false,
        };
        var core = new LineSeries<double>
        {
            Values = values,
            Name = name,
            Stroke = new SolidColorPaint(color, CoreStrokeWidth),
            Fill = GradientFillOf(color),
            GeometryStroke = null,
            GeometryFill = null,
            LineSmoothness = 0.3,
        };
        return (glow, core);
    }

    private static LinearGradientPaint GradientFillOf(SKColor color)
        => new(color.WithAlpha(90), color.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1));

    /// <summary>Recolors the charts to match user-chosen theme colors. Called once at startup and whenever the user changes a color.</summary>
    public void ApplyColors(Color cpu, Color ram, Color disk, Color networkReceive, Color networkSend)
    {
        Recolor(_cpuGlow, _cpuCore, cpu);
        Recolor(_ramGlow, _ramCore, ram);
        Recolor(_diskGlow, _diskCore, disk);
        Recolor(_netRecvGlow, _netRecvCore, networkReceive);
        Recolor(_netSendGlow, _netSendCore, networkSend);
        Recolor(_committedGlow, _committedCore, ram); // Committed shares RAM's color - both memory metrics

        CpuColor = cpu;
        RamColor = ram;
        DiskColor = disk;
        NetworkColor = networkReceive;
    }

    /// <summary>
    /// Repaints chart axis text/gridlines and the network chart's legend/tooltip to match the
    /// active theme family. These are SkiaSharp paints that live outside WPF's resource system,
    /// so a DynamicResource-driven theme-family switch can't reach them on its own - called from
    /// MainViewModel whenever ThemeViewModel.ThemeModeChanged fires.
    /// </summary>
    public void ApplyAxisTheme(Color text, Color separator, Color panelBackground)
    {
        var textSk = new SKColor(text.R, text.G, text.B);
        var sepSk = new SKColor(separator.R, separator.G, separator.B, separator.A);
        var panelSk = new SKColor(panelBackground.R, panelBackground.G, panelBackground.B);

        var textPaint = new SolidColorPaint(textSk);
        var sepPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };

        PercentYAxes[0].LabelsPaint = textPaint;
        PercentYAxes[0].SeparatorsPaint = sepPaint;
        NetworkYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        NetworkYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        MemoryBytesYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        MemoryBytesYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };

        LegendTextPaint = new SolidColorPaint(textSk);
        TooltipTextPaint = new SolidColorPaint(textSk);
        LegendBackgroundPaint = new SolidColorPaint(panelSk);
        TooltipBackgroundPaint = new SolidColorPaint(panelSk);
    }

    private static void Recolor(LineSeries<double> glow, LineSeries<double> core, Color color)
    {
        var sk = new SKColor(color.R, color.G, color.B);
        glow.Stroke = new SolidColorPaint(sk.WithAlpha(70), GlowStrokeWidth);
        core.Stroke = new SolidColorPaint(sk, CoreStrokeWidth);
        core.Fill = GradientFillOf(sk);
    }

    private static void PushHistory(ObservableCollection<double> history, double value)
    {
        history.Add(value);
        if (history.Count > HistoryLength)
            history.RemoveAt(0);
    }

    private async Task RefreshAsync()
    {
        HardwareSnapshot snapshot;
        try
        {
            snapshot = await Task.Run(() => _hardware.Sample());
        }
        catch
        {
            return;
        }

        PushHistory(CpuHistory, snapshot.CpuTotalPercent);
        PushHistory(RamHistory, snapshot.RamPercent);
        PushHistory(DiskHistory, snapshot.DiskActivePercent);
        PushHistory(NetworkReceiveHistory, snapshot.NetworkReceiveBytesPerSec);
        PushHistory(NetworkSendHistory, snapshot.NetworkSendBytesPerSec);
        PushHistory(CommittedHistory, snapshot.CommittedBytes);

        SyncCores(snapshot.CpuPerCorePercent, snapshot.CoreParkedFlags);

        CpuName = string.IsNullOrWhiteSpace(snapshot.CpuName) ? "Unknown CPU" : snapshot.CpuName;
        CpuSpecs = $"{snapshot.PhysicalCores} cores, {snapshot.LogicalProcessors} logical processors  •  Base speed {snapshot.CpuBaseClockGhz:0.00} GHz";
        CpuCurrentPercent = snapshot.CpuTotalPercent;
        CpuCurrentClockGhz = snapshot.CpuCurrentClockGhz;
        CpuBaseClockGhz = snapshot.CpuBaseClockGhz;
        _logicalProcessors = snapshot.LogicalProcessors;

        if (snapshot.CpuCurrentClockGhz > 0)
        {
            CpuMinClockGhz = Math.Min(CpuMinClockGhz, snapshot.CpuCurrentClockGhz);
            CpuMaxClockGhzSeen = Math.Max(CpuMaxClockGhzSeen, snapshot.CpuCurrentClockGhz);
            _cpuClockSampleSum += snapshot.CpuCurrentClockGhz;
            _cpuClockSampleCount++;
            CpuAvgClockGhz = _cpuClockSampleSum / _cpuClockSampleCount;
        }
        CpuVsBasePercent = snapshot.CpuBaseClockGhz <= 0 ? 0 : (snapshot.CpuCurrentClockGhz - snapshot.CpuBaseClockGhz) / snapshot.CpuBaseClockGhz * 100.0;

        // #27: bucket this tick's base-vs-current reading into the session-long turbo histogram.
        if (snapshot.CpuBaseClockGhz > 0)
        {
            int bucket = CpuVsBasePercent switch
            {
                < 0 => 0,
                < 5 => 1,
                < 15 => 2,
                < 30 => 3,
                < 50 => 4,
                _ => 5,
            };
            _turboBucketCounts[bucket]++;
            _turboTotalSamples++;
            for (int i = 0; i < _turboBucketCounts.Length; i++)
                TurboHistogram[i].Percent = Math.Round(_turboBucketCounts[i] / (double)_turboTotalSamples * 100.0, 1);
        }

        CpuInterruptPercent = snapshot.CpuInterruptPercent;
        CpuDpcPercent = snapshot.CpuDpcPercent;
        ContextSwitchesPerSec = snapshot.ContextSwitchesPerSec;
        CpuQueueLength = snapshot.CpuQueueLength;

        CStatesAvailable = snapshot.CStatesAvailable;
        CpuIdlePercent = snapshot.CpuIdlePercent;
        CpuC1Percent = snapshot.CpuC1Percent;
        CpuC2Percent = snapshot.CpuC2Percent;
        CpuC3Percent = snapshot.CpuC3Percent;

        RamUsedGb = snapshot.RamUsedBytes / 1024.0 / 1024.0 / 1024.0;
        RamTotalGb = snapshot.RamTotalBytes / 1024.0 / 1024.0 / 1024.0;
        RamPercent = snapshot.RamPercent;

        RamAvailableGb = snapshot.RamAvailableBytes / 1024.0 / 1024.0 / 1024.0;
        RamAvailablePercent = snapshot.RamTotalBytes == 0 ? 0 : (double)snapshot.RamAvailableBytes / snapshot.RamTotalBytes * 100.0;
        CommittedGb = snapshot.CommittedBytes / 1024.0 / 1024.0 / 1024.0;
        CommitLimitGb = snapshot.CommitLimitBytes / 1024.0 / 1024.0 / 1024.0;
        CommittedPercent = snapshot.CommitLimitBytes == 0 ? 0 : (double)snapshot.CommittedBytes / snapshot.CommitLimitBytes * 100.0;
        CachedGb = snapshot.CacheBytes / 1024.0 / 1024.0 / 1024.0;
        CachedPercent = snapshot.RamTotalBytes == 0 ? 0 : (double)snapshot.CacheBytes / snapshot.RamTotalBytes * 100.0;

        PageFileUsedGb = snapshot.PageFileUsedBytes / 1024.0 / 1024.0 / 1024.0;
        PageFileTotalGb = snapshot.PageFileTotalBytes / 1024.0 / 1024.0 / 1024.0;
        PageFilePercent = snapshot.PageFileTotalBytes == 0 ? 0 : (double)snapshot.PageFileUsedBytes / snapshot.PageFileTotalBytes * 100.0;

        PageFaultsPerSec = snapshot.PageFaultsPerSec;
        HardFaultsPerSec = snapshot.HardFaultsPerSec;
        OnPropertyChanged(nameof(SoftFaultsPerSec));
        StandbyGb = snapshot.StandbyListBytes / 1024.0 / 1024.0 / 1024.0;
        StandbyPercent = snapshot.RamTotalBytes == 0 ? 0 : (double)snapshot.StandbyListBytes / snapshot.RamTotalBytes * 100.0;
        PoolNonpagedGb = snapshot.PoolNonpagedBytes / 1024.0 / 1024.0 / 1024.0;
        PoolPagedGb = snapshot.PoolPagedBytes / 1024.0 / 1024.0 / 1024.0;
        OnPropertyChanged(nameof(MemoryInUsePercent));
        OnPropertyChanged(nameof(MemoryStandbyPercent));
        OnPropertyChanged(nameof(MemoryFreePercent));

        DiskPercent = snapshot.DiskActivePercent;
        DiskReadBps = snapshot.DiskReadBytesPerSec;
        DiskWriteBps = snapshot.DiskWriteBytesPerSec;
        DiskQueueLength = snapshot.DiskQueueLength;
        DiskReadLatencyMs = snapshot.DiskReadLatencyMs;
        DiskWriteLatencyMs = snapshot.DiskWriteLatencyMs;

        NetworkReceiveBps = snapshot.NetworkReceiveBytesPerSec;
        NetworkSendBps = snapshot.NetworkSendBytesPerSec;
        NetworkInErrors = snapshot.NetworkInErrors;
        NetworkInDiscards = snapshot.NetworkInDiscards;
        NetworkOutErrors = snapshot.NetworkOutErrors;
        NetworkOutDiscards = snapshot.NetworkOutDiscards;
        OnPropertyChanged(nameof(HasNetworkErrors));
        TcpRetransmitsPerSec = snapshot.TcpRetransmitsPerSec;

        ProcessCount = snapshot.ProcessCount;
        ThreadCount = snapshot.ThreadCount;
        HandleCount = snapshot.HandleCount;

        var up = snapshot.Uptime;
        Uptime = $"{(int)up.TotalDays}d {up.Hours:00}h {up.Minutes:00}m {up.Seconds:00}s";
    }

    private void SyncCores(double[] percentages, bool[] parkedFlags)
    {
        bool ParkedAt(int i) => i < parkedFlags.Length && parkedFlags[i];

        if (Cores.Count != percentages.Length)
        {
            Cores.Clear();
            // #26: group lookup for SMT/Hyper-Threading sibling pairing - only computed here (on
            // an actual core-count change), not per tick, same as the rest of this rebuild branch.
            var byGroup = _topology.Cores.GroupBy(c => c.PhysicalCoreGroup)
                .ToDictionary(g => g.Key, g => g.Select(c => c.LogicalIndex).ToList());

            for (int i = 0; i < percentages.Length; i++)
            {
                // Topology is indexed by logical processor number; percentages[] is expected to
                // line up 1:1 with it (see HardwareMonitorService's numeric node/core sort), but
                // guard the lookup anyway in case core counts ever disagree.
                var topo = i < _topology.Cores.Count ? _topology.Cores[i] : null;

                int sibling = -1;
                if (topo is not null && byGroup.TryGetValue(topo.PhysicalCoreGroup, out var siblings) && siblings.Count > 1)
                    sibling = siblings.First(idx => idx != i);

                Cores.Add(new CoreUsage
                {
                    Index = i,
                    Percent = percentages[i],
                    NumaNode = topo?.NumaNode ?? 0,
                    IsPCore = topo?.IsPCore ?? true,
                    IsParked = ParkedAt(i),
                    SiblingIndex = sibling,
                });
            }
            ParkedCoreCount = parkedFlags.Count(p => p);
            return;
        }

        for (int i = 0; i < percentages.Length; i++)
        {
            Cores[i].Percent = percentages[i];
            Cores[i].IsParked = ParkedAt(i);
        }
        ParkedCoreCount = parkedFlags.Count(p => p);
    }

    // #78: summary count for the CPU tab's diagnostics row - "how many cores are parked right
    // now" without needing to scan the whole per-core grid.
    private int _parkedCoreCount;
    public int ParkedCoreCount { get => _parkedCoreCount; private set => SetProperty(ref _parkedCoreCount, value); }

    public void Dispose()
    {
        _timer.Stop();
        _hardware.Dispose();
    }
}
