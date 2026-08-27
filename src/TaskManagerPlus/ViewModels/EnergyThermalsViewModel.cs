using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the Energy &amp; Thermals tab. Unlike Cpu/Memory/Storage/Network (thin wrappers over the
/// shared PerformanceViewModel sampler), this owns its own SensorMonitorService and
/// DispatcherTimer - it's a genuinely independent, more expensive data source (LibreHardwareMonitorLib
/// sensor polling, not a PerformanceCounter read), so it doesn't fit the "share one sampler" case
/// the other tabs are built around.
/// </summary>
public sealed class EnergyThermalsViewModel : ObservableObject, IDisposable
{
    private const int HistoryLength = 60;

    private readonly SensorMonitorService _sensors = new();
    private readonly DispatcherTimer _timer;

    // #34: needed to know whether the CPU is actually running below its rated base clock under
    // load (a real throttle signal), not just "hot" - CpuViewModel's own thermal-throttle flag
    // reads the same two figures, but this view-model logs the *history* of when it happened.
    private readonly PerformanceViewModel _performance;
    private DateTime? _lastThrottleLogged;

    /// <summary>False when the sensor driver couldn't open at all (Smart App Control, missing
    /// driver signing, unsupported hardware, ...) - the view should show an "unavailable" state
    /// rather than a permanently-empty grid that looks broken.</summary>
    public bool SensorsAvailable => _sensors.IsAvailable;

    public ObservableCollection<SensorReading> Temperatures { get; } = new();
    public ObservableCollection<SensorReading> Fans { get; } = new();
    public ObservableCollection<SensorReading> Voltages { get; } = new();
    public ObservableCollection<SensorReading> Wattages { get; } = new();

    /// <summary>Battery sensors (charge level, degradation/wear level, voltage, charge/discharge
    /// rate) - empty on any desktop, since LibreHardwareMonitorLib simply reports no Battery
    /// hardware when there isn't one. "Degradation Level" is the closest thing to a real battery
    /// health report this app can show without a laptop-vendor-specific API: it's LibreHardwareMonitorLib's
    /// own (full charge capacity vs. design capacity) calculation, the same figure a "battery
    /// health" report from any other tool is ultimately built from.</summary>
    public ObservableCollection<SensorReading> Battery { get; } = new();

    public ObservableCollection<double> PowerHistory { get; } = NewHistory();
    private readonly LineSeries<double> _powerGlow;
    private readonly LineSeries<double> _powerCore;
    public ISeries[] PowerSeries { get; }
    public Axis[] HiddenXAxes { get; }
    public Axis[] PowerYAxes { get; }

    // #33: historical CPU temperature chart, same glow+core line pattern as the power chart above.
    public ObservableCollection<double> CpuTempHistory { get; } = NewHistory();
    private readonly LineSeries<double> _tempGlow;
    private readonly LineSeries<double> _tempCore;
    public ISeries[] TempSeries { get; }
    public Axis[] TempYAxes { get; }

    /// <summary>Timestamped log of when the CPU was detected running hot and meaningfully below
    /// its rated base clock under load (#33's "mark exactly when this happened") - a readable
    /// event list rather than an in-chart marker, capped to the 10 most recent, newest first.</summary>
    public ObservableCollection<string> ThrottleEvents { get; } = new();

    private double? _cpuPackageTempC;
    public double? CpuPackageTempC { get => _cpuPackageTempC; private set => SetProperty(ref _cpuPackageTempC, value); }

    private double? _totalPackagePowerW;
    public double? TotalPackagePowerW { get => _totalPackagePowerW; private set => SetProperty(ref _totalPackagePowerW, value); }

    /// <summary>GPU hotspot-vs-edge temperature differential (#28) - a large, sustained gap is a
    /// common sign of degraded thermal paste/pads on a GPU cooler, distinct from either reading
    /// alone being high. Null when either sensor isn't reported (no discrete GPU, or the vendor's
    /// LibreHardwareMonitorLib backend doesn't expose a hotspot/junction sensor).</summary>
    private double? _gpuHotspotDeltaC;
    public double? GpuHotspotDeltaC { get => _gpuHotspotDeltaC; private set => SetProperty(ref _gpuHotspotDeltaC, value); }

    // #46: running min/max per sensor since launch, keyed by Identifier - see SensorReading's
    // remarks for why this lives here rather than on SensorMonitorService.
    private readonly Dictionary<string, (float Min, float Max)> _temperatureBaseline = new();

    // #41: "dead fan" - a fan reading exactly 0 RPM while some temperature reading is clearly
    // under load, which a genuinely idle/passive fan wouldn't be paired with.
    private const float DeadFanTempThresholdC = 55f;

    private bool _deadFanDetected;
    public bool DeadFanDetected { get => _deadFanDetected; private set => SetProperty(ref _deadFanDetected, value); }

    private string _deadFanName = string.Empty;
    public string DeadFanName { get => _deadFanName; private set => SetProperty(ref _deadFanName, value); }

    private static readonly SKColor AxisTextColor = new(0x9A, 0x9A, 0xA2);
    private static readonly SKColor AxisSeparatorColor = new(0x33, 0x33, 0x3A, 160);
    private const float CoreStrokeWidth = 2f;
    private const float GlowStrokeWidth = 7f;

    public EnergyThermalsViewModel(PerformanceViewModel performance)
    {
        _performance = performance;

        HiddenXAxes = new[]
        {
            new Axis { IsVisible = false, MinLimit = 0, MaxLimit = HistoryLength - 1, ShowSeparatorLines = false },
        };
        PowerYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v:0.#} W",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        TempYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v:0}°C",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };

        var powerColor = SKColors.OrangeRed;
        _powerGlow = new LineSeries<double>
        {
            Values = PowerHistory,
            Stroke = new SolidColorPaint(powerColor.WithAlpha(70), GlowStrokeWidth),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0.3, IsHoverable = false, IsVisibleAtLegend = false,
        };
        _powerCore = new LineSeries<double>
        {
            Values = PowerHistory,
            Stroke = new SolidColorPaint(powerColor, CoreStrokeWidth),
            Fill = new LinearGradientPaint(powerColor.WithAlpha(90), powerColor.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)),
            GeometryStroke = null, GeometryFill = null, LineSmoothness = 0.3,
        };
        PowerSeries = new ISeries[] { _powerGlow, _powerCore };

        var tempColor = SKColors.DeepSkyBlue;
        _tempGlow = new LineSeries<double>
        {
            Values = CpuTempHistory,
            Stroke = new SolidColorPaint(tempColor.WithAlpha(70), GlowStrokeWidth),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0.3, IsHoverable = false, IsVisibleAtLegend = false,
        };
        _tempCore = new LineSeries<double>
        {
            Values = CpuTempHistory,
            Stroke = new SolidColorPaint(tempColor, CoreStrokeWidth),
            Fill = new LinearGradientPaint(tempColor.WithAlpha(90), tempColor.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)),
            GeometryStroke = null, GeometryFill = null, LineSmoothness = 0.3,
        };
        TempSeries = new ISeries[] { _tempGlow, _tempCore };

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
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

    /// <summary>Repaints chart axis text/gridlines to match the active theme family - see
    /// PerformanceViewModel.ApplyAxisTheme's remarks; same SkiaSharp-outside-WPF-resources gap.</summary>
    public void ApplyAxisTheme(Color text, Color separator)
    {
        var textSk = new SKColor(text.R, text.G, text.B);
        var sepSk = new SKColor(separator.R, separator.G, separator.B, separator.A);
        PowerYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        PowerYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        TempYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        TempYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
    }

    private async Task RefreshAsync()
    {
        List<SensorReading> readings;
        try
        {
            readings = await Task.Run(() => _sensors.Sample());
        }
        catch
        {
            return;
        }

        // LibreHardwareMonitorLib reports an exact 0 (not null) for a fair number of sensors it
        // enumerates but doesn't actually have working support for on a given board/CPU/drive
        // (varies a lot by hardware - e.g. a specific NVMe "Composite Temperature" duplicate, or
        // per-core power on some AMD SKUs). A real reading is never exactly 0 for these sensor
        // types on a running PC, so treat exact 0 the same as "no data" and drop it, rather than
        // showing a wall of misleading "0 °C"/"0 W"/"0 V" tiles. Fans are the one exception - 0
        // RPM is a normal, real reading for a semi-passive fan that's stopped at idle.
        var tempReadings = readings.Where(r => r.Type == SensorType.Temperature && HasNonZeroReading(r)).ToList();
        Replace(Temperatures, tempReadings.Select(WithSessionBaseline));

        var fanReadings = readings.Where(r => r.Type == SensorType.Fan && r.Value.HasValue).ToList();
        Replace(Fans, fanReadings);
        Replace(Voltages, readings.Where(r => r.Type == SensorType.Voltage && HasNonZeroReading(r)));
        Replace(Wattages, readings.Where(r => r.Type == SensorType.Power && HasNonZeroReading(r)));
        // Battery sensors mix several SensorTypes (Level for charge %/degradation %, Voltage,
        // Power for charge/discharge rate) - bucketed by HardwareType instead of SensorType, and
        // not zero-filtered like the others: 0% charge or 0W (fully idle, on AC) are both real,
        // normal readings for a battery, unlike a temperature/voltage/wattage sensor reading
        // exactly 0 (which usually means "unsupported", per the comment above).
        Replace(Battery, readings.Where(r => r.HardwareType == HardwareType.Battery && r.Value.HasValue));

        // #41: a fan pinned at 0 RPM while some temperature reading is clearly under load is a
        // real "this fan stopped spinning" signal, not a normal idle/passive-cooling reading.
        bool anyHot = tempReadings.Any(r => r.Value is float t && t >= DeadFanTempThresholdC);
        var deadFan = anyHot ? fanReadings.FirstOrDefault(r => r.Value is 0f) : null;
        DeadFanDetected = deadFan is not null;
        DeadFanName = deadFan is null ? string.Empty : $"{deadFan.HardwareName} {deadFan.SensorName}";

        // Sensor names aren't standardized across CPU vendors (Intel: "CPU Package"; AMD:
        // "Core (Tctl/Tdie)"; varies further by model), so try a few known hints in order
        // rather than one brittle exact-string lookup.
        CpuPackageTempC = FindByNameContains(Temperatures, "CPU Package", "Core (Tctl/Tdie)", "CPU");
        TotalPackagePowerW = FindByNameContains(Wattages, "CPU Package", "Package", "CPU Cores", "CPU");

        // #28: GPU hotspot/junction vs. edge/core temperature differential - restricted to GPU
        // hardware entries specifically (unlike the CPU lookups above) since sensor names like
        // "Core" collide with per-core CPU temperature readings otherwise.
        var gpuTemps = tempReadings.Where(r => IsGpu(r.HardwareType)).ToList();
        var gpuEdge = FindByNameContains(gpuTemps, "GPU Core", "Edge", "Core");
        var gpuHotspot = FindByNameContains(gpuTemps, "Hot Spot", "Junction");
        GpuHotspotDeltaC = gpuEdge.HasValue && gpuHotspot.HasValue && gpuHotspot > gpuEdge
            ? gpuHotspot - gpuEdge : null;

        if (TotalPackagePowerW.HasValue)
        {
            PowerHistory.Add(TotalPackagePowerW.Value);
            if (PowerHistory.Count > HistoryLength) PowerHistory.RemoveAt(0);
        }

        if (CpuPackageTempC is { } cpuTemp)
        {
            CpuTempHistory.Add(cpuTemp);
            if (CpuTempHistory.Count > HistoryLength) CpuTempHistory.RemoveAt(0);

            // #33: log (at most once per 30s, to avoid spamming the list) whenever the CPU is
            // both running hot and meaningfully below its rated base clock under load - the same
            // "hot AND actually throttled" condition CpuViewModel flags for its own banner, just
            // recorded here as a timestamped history rather than a live flag.
            bool throttlingNow = cpuTemp >= 85 && _performance.CpuVsBasePercent <= -8 && _performance.CpuCurrentPercent >= 60;
            if (throttlingNow && (_lastThrottleLogged is null || (DateTime.Now - _lastThrottleLogged.Value).TotalSeconds >= 30))
            {
                _lastThrottleLogged = DateTime.Now;
                ThrottleEvents.Insert(0, $"{DateTime.Now:T} — {cpuTemp:0}°C, {_performance.CpuVsBasePercent:0}% vs. base clock");
                while (ThrottleEvents.Count > 10) ThrottleEvents.RemoveAt(ThrottleEvents.Count - 1);
            }
        }
    }

    private static bool IsGpu(HardwareType type) => type is HardwareType.GpuAmd or HardwareType.GpuNvidia;

    /// <summary>Updates the running min/max for this reading's Identifier and returns a copy of
    /// the reading carrying that session range (#46) - the raw reading from SensorMonitorService
    /// only ever has the instantaneous value.</summary>
    private SensorReading WithSessionBaseline(SensorReading reading)
    {
        if (reading.Value is not float value) return reading;

        var (min, max) = _temperatureBaseline.TryGetValue(reading.Identifier, out var existing)
            ? (Math.Min(existing.Min, value), Math.Max(existing.Max, value))
            : (value, value);
        _temperatureBaseline[reading.Identifier] = (min, max);

        return new SensorReading
        {
            HardwareName = reading.HardwareName,
            HardwareType = reading.HardwareType,
            SensorName = reading.SensorName,
            Type = reading.Type,
            Value = reading.Value,
            Identifier = reading.Identifier,
            SessionMin = min,
            SessionMax = max,
        };
    }

    private static void Replace(ObservableCollection<SensorReading> target, IEnumerable<SensorReading> source)
    {
        target.Clear();
        foreach (var reading in source.OrderBy(r => r.HardwareName).ThenBy(r => r.SensorName))
            target.Add(reading);
    }

    private static bool HasNonZeroReading(SensorReading r) => r.Value is float v && v != 0f;

    private static double? FindByNameContains(IEnumerable<SensorReading> sensors, params string[] hints)
    {
        foreach (var hint in hints)
        {
            var match = sensors.FirstOrDefault(s => HasNonZeroReading(s) && s.SensorName.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Value;
        }
        return null;
    }

    public void Dispose()
    {
        _timer.Stop();
        _sensors.Dispose();
    }
}
