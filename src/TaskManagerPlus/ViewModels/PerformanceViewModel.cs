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

    public ObservableCollection<double> CpuHistory { get; } = NewHistory();
    public ObservableCollection<double> RamHistory { get; } = NewHistory();
    public ObservableCollection<double> DiskHistory { get; } = NewHistory();
    public ObservableCollection<double> NetworkReceiveHistory { get; } = NewHistory();
    public ObservableCollection<double> NetworkSendHistory { get; } = NewHistory();

    public ObservableCollection<CoreUsage> Cores { get; } = new();

    private readonly LineSeries<double> _cpuLine;
    private readonly LineSeries<double> _ramLine;
    private readonly LineSeries<double> _diskLine;
    private readonly LineSeries<double> _netRecvLine;
    private readonly LineSeries<double> _netSendLine;

    public ISeries[] CpuSeries { get; }
    public ISeries[] RamSeries { get; }
    public ISeries[] DiskSeries { get; }
    public ISeries[] NetworkSeries { get; }
    public Axis[] PercentYAxes { get; }
    public Axis[] HiddenXAxes { get; }
    public Axis[] NetworkYAxes { get; }

    // Shared paints so the Network chart's legend/tooltip (the only ones left visible) render
    // in the app's dark palette instead of LiveCharts' default light-theme black-on-white.
    public SolidColorPaint LegendTextPaint { get; } = AxisTextPaint();
    public SolidColorPaint LegendBackgroundPaint { get; } = new(new SKColor(0x26, 0x26, 0x2B));
    public SolidColorPaint TooltipTextPaint { get; } = AxisTextPaint();
    public SolidColorPaint TooltipBackgroundPaint { get; } = new(new SKColor(0x26, 0x26, 0x2B));

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

    private double _ramUsedGb;
    public double RamUsedGb { get => _ramUsedGb; private set => SetProperty(ref _ramUsedGb, value); }

    private double _ramTotalGb;
    public double RamTotalGb { get => _ramTotalGb; private set => SetProperty(ref _ramTotalGb, value); }

    private double _ramPercent;
    public double RamPercent { get => _ramPercent; private set => SetProperty(ref _ramPercent, value); }

    private double _diskPercent;
    public double DiskPercent { get => _diskPercent; private set => SetProperty(ref _diskPercent, value); }

    private double _networkReceiveBps;
    public double NetworkReceiveBps { get => _networkReceiveBps; private set => SetProperty(ref _networkReceiveBps, value); }

    private double _networkSendBps;
    public double NetworkSendBps { get => _networkSendBps; private set => SetProperty(ref _networkSendBps, value); }

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
                Labeler = v => FormatBytes(v),
                LabelsPaint = AxisTextPaint(),
                SeparatorsPaint = AxisSeparatorPaint(),
            },
        };

        _cpuLine = LineOf(CpuHistory, SKColors.DeepSkyBlue);
        _ramLine = LineOf(RamHistory, SKColors.MediumPurple);
        _diskLine = LineOf(DiskHistory, SKColors.Orange);
        _netRecvLine = LineOf(NetworkReceiveHistory, SKColors.LimeGreen, "Receive");
        _netSendLine = LineOf(NetworkSendHistory, SKColors.OrangeRed, "Send");

        CpuSeries = new ISeries[] { _cpuLine };
        RamSeries = new ISeries[] { _ramLine };
        DiskSeries = new ISeries[] { _diskLine };
        NetworkSeries = new ISeries[] { _netRecvLine, _netSendLine };

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

    private static LineSeries<double> LineOf(ObservableCollection<double> values, SKColor color, string? name = null)
        => new()
        {
            Values = values,
            Name = name,
            Stroke = new SolidColorPaint(color, 2),
            Fill = new SolidColorPaint(color.WithAlpha(40)),
            GeometryStroke = null,
            GeometryFill = null,
            LineSmoothness = 0.3,
        };

    /// <summary>Recolors the charts to match user-chosen theme colors. Called once at startup and whenever the user changes a color.</summary>
    public void ApplyColors(Color cpu, Color ram, Color disk, Color networkReceive, Color networkSend)
    {
        Recolor(_cpuLine, cpu);
        Recolor(_ramLine, ram);
        Recolor(_diskLine, disk);
        Recolor(_netRecvLine, networkReceive);
        Recolor(_netSendLine, networkSend);

        CpuColor = cpu;
        RamColor = ram;
        DiskColor = disk;
        NetworkColor = networkReceive;
    }

    private static void Recolor(LineSeries<double> series, Color color)
    {
        var sk = new SKColor(color.R, color.G, color.B);
        series.Stroke = new SolidColorPaint(sk, 2);
        series.Fill = new SolidColorPaint(sk.WithAlpha(40));
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

        SyncCores(snapshot.CpuPerCorePercent);

        CpuName = string.IsNullOrWhiteSpace(snapshot.CpuName) ? "Unknown CPU" : snapshot.CpuName;
        CpuSpecs = $"{snapshot.PhysicalCores} cores, {snapshot.LogicalProcessors} logical processors  •  Base speed {snapshot.CpuBaseClockGhz:0.00} GHz";
        CpuCurrentPercent = snapshot.CpuTotalPercent;
        CpuCurrentClockGhz = snapshot.CpuCurrentClockGhz;

        RamUsedGb = snapshot.RamUsedBytes / 1024.0 / 1024.0 / 1024.0;
        RamTotalGb = snapshot.RamTotalBytes / 1024.0 / 1024.0 / 1024.0;
        RamPercent = snapshot.RamPercent;

        DiskPercent = snapshot.DiskActivePercent;

        NetworkReceiveBps = snapshot.NetworkReceiveBytesPerSec;
        NetworkSendBps = snapshot.NetworkSendBytesPerSec;

        ProcessCount = snapshot.ProcessCount;
        ThreadCount = snapshot.ThreadCount;
        HandleCount = snapshot.HandleCount;

        var up = snapshot.Uptime;
        Uptime = $"{(int)up.TotalDays}d {up.Hours:00}h {up.Minutes:00}m {up.Seconds:00}s";
    }

    private void SyncCores(double[] percentages)
    {
        if (Cores.Count != percentages.Length)
        {
            Cores.Clear();
            for (int i = 0; i < percentages.Length; i++)
                Cores.Add(new CoreUsage { Index = i, Percent = percentages[i] });
            return;
        }

        for (int i = 0; i < percentages.Length; i++)
            Cores[i].Percent = percentages[i];
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        double abs = Math.Abs(bytes);
        int i = 0;
        while (abs >= 1024 && i < units.Length - 1) { abs /= 1024; i++; }
        return $"{abs:0.#} {units[i]}";
    }

    public void Dispose()
    {
        _timer.Stop();
        _hardware.Dispose();
    }
}
