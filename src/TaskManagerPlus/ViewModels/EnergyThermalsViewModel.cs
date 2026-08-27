using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
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

    // #25: needed to know whether the CPU is actually running below its rated base clock under
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

    // #88: real-time battery drain rate - pulled out of the generic Battery tile list above into
    // its own headline readout (same "find the one figure that matters, out of a generic sensor
    // list" treatment CpuPackageTempC/TotalPackagePowerW already get), since "how fast is this
    // draining right now" is the one question that directly answers "is some background process
    // killing my battery life". LibreHardwareMonitorLib names/signs this per-vendor, not
    // standardized the same way CPU sensor names aren't (see FindByNameContains's remarks) - name
    // hints are tried first, falling back to sign alone (negative conventionally means charging)
    // when no name match is found.
    private double? _batteryDrainRateW;
    public double? BatteryDrainRateW { get => _batteryDrainRateW; private set => SetProperty(ref _batteryDrainRateW, value); }

    private bool _batteryIsCharging;
    public bool BatteryIsCharging { get => _batteryIsCharging; private set => SetProperty(ref _batteryIsCharging, value); }

    public ObservableCollection<double> PowerHistory { get; } = NewHistory();
    private readonly LineSeries<double> _powerGlow;
    private readonly LineSeries<double> _powerCore;
    public ISeries[] PowerSeries { get; }
    public Axis[] HiddenXAxes { get; }
    public Axis[] PowerYAxes { get; }

    // #25: historical CPU temperature chart, same glow+core line pattern as the power chart above.
    public ObservableCollection<double> CpuTempHistory { get; } = NewHistory();
    private readonly LineSeries<double> _tempGlow;
    private readonly LineSeries<double> _tempCore;
    public ISeries[] TempSeries { get; }
    public Axis[] TempYAxes { get; }

    /// <summary>Timestamped log of when the CPU was detected running hot and meaningfully below
    /// its rated base clock under load (#25's "mark exactly when this happened") - a readable
    /// event list rather than an in-chart marker, capped to the 10 most recent, newest first.</summary>
    public ObservableCollection<string> ThrottleEvents { get; } = new();

    // #81: motherboard/VRM temperature trend - VRM overheating causes throttling that CPU
    // package temp alone won't show, so this is a second, independent chart rather than folded
    // into the CPU one. Same glow+core LineOf pattern, on the Motherboard hardware tree only (so
    // "System"/"VRM"-named sensors from a GPU or drive never collide with this lookup).
    public ObservableCollection<double> MotherboardTempHistory { get; } = NewHistory();
    private readonly LineSeries<double> _mbTempGlow;
    private readonly LineSeries<double> _mbTempCore;
    public ISeries[] MotherboardTempSeries { get; }
    public Axis[] MotherboardTempYAxes { get; }

    private double? _motherboardTempC;
    public double? MotherboardTempC { get => _motherboardTempC; private set => SetProperty(ref _motherboardTempC, value); }

    // #34: fan curve (RPM vs. temp) - a fan that isn't ramping with temperature shows up as a
    // flat/scattered cloud instead of the expected rising trend. Tracks the primary CPU fan
    // against CPU package temp specifically, since that's the one pairing present on virtually
    // every system with any fan sensor at all.
    private const int FanCurveWindow = 120;
    public ObservableCollection<ObservablePoint> FanCurvePoints { get; } = new();
    private readonly ScatterSeries<ObservablePoint> _fanCurveScatter;
    public ISeries[] FanCurveSeries { get; }
    public Axis[] FanCurveXAxes { get; }
    public Axis[] FanCurveYAxes { get; }

    private double? _cpuPackageTempC;
    public double? CpuPackageTempC { get => _cpuPackageTempC; private set => SetProperty(ref _cpuPackageTempC, value); }

    private double? _totalPackagePowerW;
    public double? TotalPackagePowerW
    {
        get => _totalPackagePowerW;
        private set
        {
            if (SetProperty(ref _totalPackagePowerW, value) && value is { } w)
                PowerSessionMaxW = PowerSessionMaxW is { } max ? Math.Max(max, w) : w;
        }
    }

    /// <summary>Highest package power draw seen this session (#35) - lets CpuViewModel tell
    /// "pinned at its own ceiling" (power-limited) apart from "just hot" (thermal-limited), the
    /// same distinction HWiNFO's vendor-proprietary limit-reason MSR reads make directly, which
    /// this app can't read without that same proprietary access.</summary>
    private double? _powerSessionMaxW;
    public double? PowerSessionMaxW { get => _powerSessionMaxW; private set => SetProperty(ref _powerSessionMaxW, value); }

    /// <summary>GPU hotspot-vs-edge temperature differential (#29) - a large, sustained gap is a
    /// common sign of degraded thermal paste/pads on a GPU cooler, distinct from either reading
    /// alone being high. Null when either sensor isn't reported (no discrete GPU, or the vendor's
    /// LibreHardwareMonitorLib backend doesn't expose a hotspot/junction sensor).</summary>
    private double? _gpuHotspotDeltaC;
    public double? GpuHotspotDeltaC { get => _gpuHotspotDeltaC; private set => SetProperty(ref _gpuHotspotDeltaC, value); }

    /// <summary>NVMe controller-vs-flash-die temperature split (round 9, #43) - the same "more
    /// than one temperature sensor on one hardware component" shape the GPU hotspot differential
    /// above already handles, restricted to HardwareType.Storage instead. LibreHardwareMonitorLib
    /// exposes this on some NVMe drives as two named sensors (commonly "Temperature"/"Controller"
    /// for the controller die and "Composite"/"Sensor 1"/"Sensor 2" for the flash package) - not
    /// every drive/driver reports more than one, so this is null (and the Storage tab hides the
    /// readout) on the common single-sensor case.</summary>
    private double? _storageHotspotDeltaC;
    public double? StorageHotspotDeltaC { get => _storageHotspotDeltaC; private set => SetProperty(ref _storageHotspotDeltaC, value); }

    private string _storageHotspotDriveName = string.Empty;
    public string StorageHotspotDriveName { get => _storageHotspotDriveName; private set => SetProperty(ref _storageHotspotDriveName, value); }

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
        MotherboardTempYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v:0}°C",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        FanCurveXAxes = new[]
        {
            new Axis
            {
                Name = "CPU temp (°C)",
                NameTextSize = 11,
                Labeler = v => $"{v:0}°C",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        FanCurveYAxes = new[]
        {
            new Axis
            {
                Name = "Fan RPM",
                NameTextSize = 11,
                MinLimit = 0,
                Labeler = v => $"{v:0}",
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

        var mbColor = SKColors.MediumSeaGreen;
        _mbTempGlow = new LineSeries<double>
        {
            Values = MotherboardTempHistory,
            Stroke = new SolidColorPaint(mbColor.WithAlpha(70), GlowStrokeWidth),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0.3, IsHoverable = false, IsVisibleAtLegend = false,
        };
        _mbTempCore = new LineSeries<double>
        {
            Values = MotherboardTempHistory,
            Stroke = new SolidColorPaint(mbColor, CoreStrokeWidth),
            Fill = new LinearGradientPaint(mbColor.WithAlpha(90), mbColor.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)),
            GeometryStroke = null, GeometryFill = null, LineSmoothness = 0.3,
        };
        MotherboardTempSeries = new ISeries[] { _mbTempGlow, _mbTempCore };

        // #34: fan curve scatter - no glow pair (a scatter cloud doesn't read well with one), just
        // small translucent points so overlapping samples at the same temp/RPM still show density.
        _fanCurveScatter = new ScatterSeries<ObservablePoint>
        {
            Values = FanCurvePoints,
            Fill = new SolidColorPaint(SKColors.DeepSkyBlue.WithAlpha(140)),
            Stroke = null,
            GeometrySize = 8,
        };
        FanCurveSeries = new ISeries[] { _fanCurveScatter };

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
        MotherboardTempYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        MotherboardTempYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        FanCurveXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        FanCurveXAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        FanCurveYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        FanCurveYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
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

        // #88: real-time drain rate - see the property's remarks for the name/sign heuristic.
        var dischargeReading = Battery.FirstOrDefault(r => r.Type == SensorType.Power &&
            r.SensorName.Contains("discharge", StringComparison.OrdinalIgnoreCase) && r.Value is not 0f);
        var chargeReading = Battery.FirstOrDefault(r => r.Type == SensorType.Power &&
            r.SensorName.Contains("charge", StringComparison.OrdinalIgnoreCase) &&
            !r.SensorName.Contains("discharge", StringComparison.OrdinalIgnoreCase) && r.Value is not 0f);
        var anyPowerReading = Battery.FirstOrDefault(r => r.Type == SensorType.Power && r.Value is not 0f);

        if (dischargeReading is not null) { BatteryDrainRateW = Math.Abs(dischargeReading.Value!.Value); BatteryIsCharging = false; }
        else if (chargeReading is not null) { BatteryDrainRateW = Math.Abs(chargeReading.Value!.Value); BatteryIsCharging = true; }
        else if (anyPowerReading is not null) { BatteryDrainRateW = Math.Abs(anyPowerReading.Value!.Value); BatteryIsCharging = anyPowerReading.Value!.Value < 0; }
        else { BatteryDrainRateW = null; BatteryIsCharging = false; }

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

        // #81: Motherboard hardware tree only, so a GPU/drive sensor also named "System" or
        // "VRM" can't collide with this lookup - see IsGpu's remarks below for the same concern
        // on the CPU-vs-GPU temperature lookups.
        var mbTemps = tempReadings.Where(r => r.HardwareType == HardwareType.Motherboard).ToList();
        MotherboardTempC = FindByNameContains(mbTemps, "VRM", "System", "Motherboard", "PCH", "Chipset");

        // #29: GPU hotspot/junction vs. edge/core temperature differential - restricted to GPU
        // hardware entries specifically (unlike the CPU lookups above) since sensor names like
        // "Core" collide with per-core CPU temperature readings otherwise.
        var gpuTemps = tempReadings.Where(r => IsGpu(r.HardwareType)).ToList();
        var gpuEdge = FindByNameContains(gpuTemps, "GPU Core", "Edge", "Core");
        var gpuHotspot = FindByNameContains(gpuTemps, "Hot Spot", "Junction");
        GpuHotspotDeltaC = gpuEdge.HasValue && gpuHotspot.HasValue && gpuHotspot > gpuEdge
            ? gpuHotspot - gpuEdge : null;

        // #43: first Storage-hardware component that reports more than one temperature sensor -
        // the differential between its hottest and coolest reading (controller vs. flash die).
        var storageGroup = tempReadings
            .Where(r => r.HardwareType == HardwareType.Storage)
            .GroupBy(r => r.HardwareName)
            .FirstOrDefault(g => g.Count() > 1);
        if (storageGroup is not null)
        {
            StorageHotspotDeltaC = storageGroup.Max(r => r.Value!.Value) - storageGroup.Min(r => r.Value!.Value);
            StorageHotspotDriveName = storageGroup.Key;
        }
        else
        {
            StorageHotspotDeltaC = null;
            StorageHotspotDriveName = string.Empty;
        }

        if (TotalPackagePowerW.HasValue)
        {
            PowerHistory.Add(TotalPackagePowerW.Value);
            if (PowerHistory.Count > HistoryLength) PowerHistory.RemoveAt(0);
        }

        if (MotherboardTempC is { } mbTemp)
        {
            MotherboardTempHistory.Add(mbTemp);
            if (MotherboardTempHistory.Count > HistoryLength) MotherboardTempHistory.RemoveAt(0);
        }

        // #34: one (temp, RPM) sample per tick for the primary CPU fan (falling back to whatever
        // fan is reported first when none is named "CPU") - a fan that isn't ramping with
        // temperature shows up as a flat/scattered cloud instead of a rising trend.
        var primaryFan = fanReadings.FirstOrDefault(r => r.SensorName.Contains("CPU", StringComparison.OrdinalIgnoreCase) && r.Value.HasValue)
            ?? fanReadings.FirstOrDefault(r => r.Value.HasValue);
        if (CpuPackageTempC is { } fanCurveTemp && primaryFan is not null)
        {
            FanCurvePoints.Add(new ObservablePoint(fanCurveTemp, primaryFan.Value!.Value));
            while (FanCurvePoints.Count > FanCurveWindow) FanCurvePoints.RemoveAt(0);
        }

        if (CpuPackageTempC is { } cpuTemp)
        {
            CpuTempHistory.Add(cpuTemp);
            if (CpuTempHistory.Count > HistoryLength) CpuTempHistory.RemoveAt(0);

            // #25: log (at most once per 30s, to avoid spamming the list) whenever the CPU is
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
