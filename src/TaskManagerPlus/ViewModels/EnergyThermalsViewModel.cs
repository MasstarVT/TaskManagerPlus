using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
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
    private bool _isRefreshing;

    // #601: a second, driver-free throttle source (Windows' own "Thermal Zone Information" perf
    // counters) - kept as a separate service/collection from SensorMonitorService's Temperatures
    // above since it works even when SensorsAvailable is false.
    private readonly ThermalZoneService _thermalZones = new();
    public bool ThermalZonesAvailable => _thermalZones.IsAvailable;
    public ObservableCollection<ThermalZoneReading> ThermalZones { get; } = new();

    // #602: firmware-limit events (Kernel-Processor-Power 37/38) - an event-log query, so this is
    // loaded once at startup plus on-demand via LoadFirmwareEventsCommand, never on the tick timer
    // (see CLAUDE.md's on-demand-vs-polled convention).
    private readonly EventLogService _eventLog = new();
    public ObservableCollection<FirmwareThrottleEvent> FirmwareThrottleEvents { get; } = new();
    public AsyncRelayCommand LoadFirmwareEventsCommand { get; }

    /// <summary>Best-effort "is a firmware limit active right now" snapshot (#602/#603) - true
    /// only when the most recently loaded firmware event is an unrecovered 37 (no later 38). Only
    /// as fresh as the last load (startup + manual refresh), not live - an event-log query isn't
    /// cheap enough to run on the tick timer.</summary>
    private bool _firmwareLimitActive;
    public bool FirmwareLimitActive { get => _firmwareLimitActive; private set => SetProperty(ref _firmwareLimitActive, value); }

    // #604: persisted throttle-episode history - loaded once at startup and kept in memory
    // (updated in place as new episodes close) rather than re-read from disk every tick.
    private List<ThrottleEpisode> _persistedEpisodes = new();
    public ObservableCollection<ThrottleEpisode> RecentThrottleEpisodes { get; } = new();

    private ThrottleEpisode? _activeEpisode;
    private readonly List<double> _activeEpisodeClockSamples = new();
    private DateTime? _sustainedLoadStartedAt;

    private double? _currentTimeToThrottleSeconds;
    public double? CurrentTimeToThrottleSeconds { get => _currentTimeToThrottleSeconds; private set => SetProperty(ref _currentTimeToThrottleSeconds, value); }

    private string _timeToThrottleText = "No throttle observed yet this session";
    /// <summary>#605: "Time to throttle: 4m 10s (was 11m 30s in May)" - the header readout on this
    /// tab. Falling time-to-throttle across weeks is the clearest single signal of degrading
    /// cooling, more legible at a glance than the raw episode history below.</summary>
    public string TimeToThrottleText { get => _timeToThrottleText; private set => SetProperty(ref _timeToThrottleText, value); }

    // #604: per-week episode-count sparkline, same glow+core LineOf pattern as every other history
    // chart on this tab.
    private const int WeeklySparklineWeeks = 12;
    public ObservableCollection<double> WeeklyEpisodeCounts { get; } = NewFixedSeries(WeeklySparklineWeeks);
    private readonly LineSeries<double> _weeklyEpisodeGlow;
    private readonly LineSeries<double> _weeklyEpisodeCore;
    public ISeries[] WeeklyEpisodeSeries { get; }
    public Axis[] WeeklyEpisodeXAxes { get; }
    public Axis[] WeeklyEpisodeYAxes { get; }

    // #609: idle-temperature baseline drift - one median-idle-temp sample per calendar day,
    // charted "since first run" rather than a fixed recent window (the whole point is seeing a
    // slow, multi-month drift).
    private readonly List<double> _idleTempSamples = new();
    private DateTime? _idleWindowStartedAt;
    private const double IdleCpuThresholdPercent = 5.0;
    private const double IdleWindowSeconds = 60.0;

    public ObservableCollection<double> IdleBaselineHistory { get; } = new();
    private readonly LineSeries<double> _idleBaselineGlow;
    private readonly LineSeries<double> _idleBaselineCore;
    public ISeries[] IdleBaselineSeries { get; }
    public Axis[] IdleBaselineXAxes { get; }
    public Axis[] IdleBaselineYAxes { get; }

    // #607: per-core temperature spread (max - min across "Core #N" sensors) - a persistently
    // large spread on a desktop points at uneven cooler mount or poor pump contact rather than
    // general heat.
    private static readonly Regex CoreTempNameRegex = new(@"Core\s*#\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private double? _coreTempSpreadC;
    public double? CoreTempSpreadC { get => _coreTempSpreadC; private set => SetProperty(ref _coreTempSpreadC, value); }

    // #608: thermal-headroom - remaining °C before an inferred/reported throttle point, which is
    // what actually predicts "will this throttle under a heavier load" rather than an absolute
    // temperature reading alone.
    private double? _cpuThermalHeadroomC;
    public double? CpuThermalHeadroomC { get => _cpuThermalHeadroomC; private set => SetProperty(ref _cpuThermalHeadroomC, value); }

    private double? _gpuTempC;
    public double? GpuTempC { get => _gpuTempC; private set => SetProperty(ref _gpuTempC, value); }

    private double? _gpuThermalHeadroomC;
    public double? GpuThermalHeadroomC { get => _gpuThermalHeadroomC; private set => SetProperty(ref _gpuThermalHeadroomC, value); }

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

    // Round 12, #95: fan RPM history, complementing the temp-vs-RPM scatter above - a scatter
    // cloud shows the overall curve shape well but hides *when* a fan hunts/oscillates (RPM
    // repeatedly ramping up and down at a near-constant temperature); a plain time-series line
    // makes that oscillation directly visible the way the scatter plot can't. Same glow+core
    // LineOf pattern as every other history chart on this tab, tracking the same "primary CPU
    // fan" RefreshAsync already resolves for the fan curve.
    public ObservableCollection<double> FanRpmHistory { get; } = NewHistory();
    private readonly LineSeries<double> _fanRpmGlow;
    private readonly LineSeries<double> _fanRpmCore;
    public ISeries[] FanRpmSeries { get; }
    public Axis[] FanRpmYAxes { get; }

    // Round 12, #90: power scheme listing/switching (powercfg /list, /setactive) - on-demand
    // (a "Load power info" button), the same "known Windows tool, shell out, don't poll" tradeoff
    // ScheduledTaskService/ServiceControlService's recovery-action reader already take, since
    // power plans essentially never change outside a direct user action.
    public ObservableCollection<PowerPlanInfo> PowerPlans { get; } = new();
    public AsyncRelayCommand LoadPowerInfoCommand { get; }
    public AsyncRelayCommand SetPowerPlanCommand { get; }

    private string _sleepStateSupportText = string.Empty;
    /// <summary>Round 12, #91: Modern Standby (S0) vs. legacy S3 sleep support - see
    /// PowerPlanService.ReadSleepStateSupport's remarks.</summary>
    public string SleepStateSupportText { get => _sleepStateSupportText; private set => SetProperty(ref _sleepStateSupportText, value); }

    private string _powerPlanStatusText = string.Empty;
    public string PowerPlanStatusText { get => _powerPlanStatusText; private set => SetProperty(ref _powerPlanStatusText, value); }

    // Round 12, #92: per-USB-device selective-suspend status - on-demand (can be a couple dozen
    // devices, each looked up by a best-effort prefix match; see UsbPowerService's remarks for
    // why SelectiveSuspendEnabled is "Unknown" far more often than a hard true/false).
    public ObservableCollection<UsbDevicePowerInfo> UsbDevices { get; } = new();
    public AsyncRelayCommand LoadUsbDevicesCommand { get; }

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

    // Round 12, #93: GPU power-limit/TDP readout, alongside the existing wattage figure. Not
    // every GPU/vendor backend in LibreHardwareMonitorLib exposes a distinct "power limit" sensor
    // (most only report instantaneous draw) - null (and the tile hidden) is the common case,
    // matching the same sparse-sensor honesty every other LHM-dependent readout in this app
    // already documents (fan/voltage sections, GPU hotspot differential, etc.).
    private double? _gpuPowerLimitW;
    public double? GpuPowerLimitW { get => _gpuPowerLimitW; private set => SetProperty(ref _gpuPowerLimitW, value); }

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
        FanRpmYAxes = new[]
        {
            new Axis
            {
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

        var fanRpmColor = SKColors.Goldenrod;
        _fanRpmGlow = new LineSeries<double>
        {
            Values = FanRpmHistory,
            Stroke = new SolidColorPaint(fanRpmColor.WithAlpha(70), GlowStrokeWidth),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0.3, IsHoverable = false, IsVisibleAtLegend = false,
        };
        _fanRpmCore = new LineSeries<double>
        {
            Values = FanRpmHistory,
            Stroke = new SolidColorPaint(fanRpmColor, CoreStrokeWidth),
            Fill = new LinearGradientPaint(fanRpmColor.WithAlpha(90), fanRpmColor.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)),
            GeometryStroke = null, GeometryFill = null, LineSmoothness = 0.3,
        };
        FanRpmSeries = new ISeries[] { _fanRpmGlow, _fanRpmCore };

        LoadPowerInfoCommand = new AsyncRelayCommand(_ => LoadPowerInfoAsync());
        SetPowerPlanCommand = new AsyncRelayCommand(SetPowerPlanAsync);
        LoadUsbDevicesCommand = new AsyncRelayCommand(_ => LoadUsbDevicesAsync());
        LoadFirmwareEventsCommand = new AsyncRelayCommand(_ => LoadFirmwareEventsAsync());

        // #604: per-week episode-count sparkline - same glow+core LineOf pattern as every other
        // history chart on this tab.
        WeeklyEpisodeXAxes = new[]
        {
            new Axis { IsVisible = false, MinLimit = 0, MaxLimit = WeeklySparklineWeeks - 1, ShowSeparatorLines = false },
        };
        WeeklyEpisodeYAxes = new[]
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
        var weeklyColor = SKColors.MediumPurple;
        _weeklyEpisodeGlow = new LineSeries<double>
        {
            Values = WeeklyEpisodeCounts,
            Stroke = new SolidColorPaint(weeklyColor.WithAlpha(70), GlowStrokeWidth),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0.3, IsHoverable = false, IsVisibleAtLegend = false,
        };
        _weeklyEpisodeCore = new LineSeries<double>
        {
            Values = WeeklyEpisodeCounts,
            Stroke = new SolidColorPaint(weeklyColor, CoreStrokeWidth),
            Fill = new LinearGradientPaint(weeklyColor.WithAlpha(90), weeklyColor.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)),
            GeometryStroke = null, GeometryFill = null, LineSmoothness = 0.3,
        };
        WeeklyEpisodeSeries = new ISeries[] { _weeklyEpisodeGlow, _weeklyEpisodeCore };

        // #609: idle-baseline chart, X axis labeled by calendar date ("since first run") rather
        // than a fixed sample count - built as a categorical axis the same way StabilityViewModel's
        // Reliability History chart labels its daily buckets.
        IdleBaselineXAxes = new[]
        {
            new Axis
            {
                Labels = Array.Empty<string>(),
                LabelsRotation = 0,
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = null,
            },
        };
        IdleBaselineYAxes = new[]
        {
            new Axis
            {
                Labeler = v => $"{v:0.#}°C",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        var idleColor = SKColors.CadetBlue;
        _idleBaselineGlow = new LineSeries<double>
        {
            Values = IdleBaselineHistory,
            Stroke = new SolidColorPaint(idleColor.WithAlpha(70), GlowStrokeWidth),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0.3, IsHoverable = false, IsVisibleAtLegend = false,
        };
        _idleBaselineCore = new LineSeries<double>
        {
            Values = IdleBaselineHistory,
            Stroke = new SolidColorPaint(idleColor, CoreStrokeWidth),
            Fill = new LinearGradientPaint(idleColor.WithAlpha(90), idleColor.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)),
            GeometryStroke = null, GeometryFill = null, LineSmoothness = 0.3,
        };
        IdleBaselineSeries = new ISeries[] { _idleBaselineGlow, _idleBaselineCore };

        // #604/#605: load persisted throttle-episode history once at startup (kept in memory and
        // updated in place as new episodes close, rather than re-read from disk every tick).
        _persistedEpisodes = ThrottleHistoryService.Load();
        foreach (var ep in _persistedEpisodes.OrderByDescending(e => e.Start).Take(10)) RecentThrottleEpisodes.Add(ep);
        RebuildWeeklyEpisodeSparkline();

        // #609: initial idle-baseline chart populate from whatever history already exists.
        RefreshIdleBaselineChart();

        // #602: cheap enough for a one-time startup read (not per-tick) - see LoadFirmwareEventsAsync.
        _ = LoadFirmwareEventsAsync();

        // Round 12, #100: configurable poll interval - see PollIntervalSettingsService's remarks.
        // Loaded fresh (not cached in a field) on every read/write so a slider change here can
        // never clobber another tab's own interval setting saved to the same shared JSON file.
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(PollIntervalSettingsService.Load().EnergyThermalsSeconds),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    /// <summary>Round 12, #100: how often this tab's LibreHardwareMonitorLib sensor poll runs -
    /// default unchanged (1.5s, CLAUDE.md's documented "sensor enumeration is heavier than a flat
    /// PerformanceCounter read" reasoning), adjustable via the Settings drawer for battery/
    /// low-power use.</summary>
    public double PollIntervalSeconds
    {
        get => _timer.Interval.TotalSeconds;
        set
        {
            double clamped = Math.Clamp(value, 0.5, 10.0);
            if (Math.Abs(_timer.Interval.TotalSeconds - clamped) < 0.01) return;

            _timer.Interval = TimeSpan.FromSeconds(clamped);
            var settings = PollIntervalSettingsService.Load();
            settings.EnergyThermalsSeconds = clamped;
            PollIntervalSettingsService.Save(settings);
            OnPropertyChanged();
        }
    }

    /// <summary>Round 12, #90/#91: loads both the power-scheme list and the sleep-state support
    /// text in one on-demand action - both are cheap powercfg shell-outs, so there's no reason to
    /// split them into two separate buttons. Both go through PowerPlanService's async
    /// Process/ReadToEndAsync-based shell-out (see the deadlock-fix note on PowerPlanService
    /// itself), so awaiting them here already keeps the UI thread free without a Task.Run
    /// wrapper.</summary>
    private async Task LoadPowerInfoAsync()
    {
        var plans = await PowerPlanService.ListPowerPlansAsync();
        PowerPlans.Clear();
        foreach (var p in plans) PowerPlans.Add(p);

        SleepStateSupportText = await PowerPlanService.ReadSleepStateSupportAsync();
        PowerPlanStatusText = plans.Count == 0 ? "Couldn't read power plans (powercfg unavailable)." : string.Empty;
    }

    /// <summary>Round 12, #90: switches the active power plan via powercfg /setactive - same
    /// genuinely-async shell-out as LoadPowerInfoAsync, no Task.Run wrapper needed.</summary>
    private async Task SetPowerPlanAsync(object? param)
    {
        if (param is not string guid || string.IsNullOrWhiteSpace(guid)) return;

        var (success, error) = await PowerPlanService.SetActivePlanAsync(guid);
        PowerPlanStatusText = success ? "Power plan switched." : $"Couldn't switch power plan: {error}";
        if (success) await LoadPowerInfoAsync();
    }

    /// <summary>Round 12, #92: on-demand USB selective-suspend read - see UsbPowerService's
    /// remarks for why this can take a moment and often reports "Unknown" per device. Unlike
    /// PowerPlanService, UsbPowerService does synchronous WMI work (ManagementObjectSearcher), so
    /// this still needs an explicit Task.Run to keep it off the UI thread (same pattern as
    /// StorageViewModel.CheckFragmentationAsync).</summary>
    private async Task LoadUsbDevicesAsync()
    {
        var devices = await Task.Run(() => UsbPowerService.ReadUsbSelectiveSuspend());
        UsbDevices.Clear();
        foreach (var d in devices) UsbDevices.Add(d);
    }

    private static ObservableCollection<double> NewHistory()
    {
        var col = new ObservableCollection<double>();
        for (int i = 0; i < HistoryLength; i++) col.Add(0);
        return col;
    }

    private static ObservableCollection<double> NewFixedSeries(int length)
    {
        var col = new ObservableCollection<double>();
        for (int i = 0; i < length; i++) col.Add(0);
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
        FanRpmYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        FanRpmYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        WeeklyEpisodeYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        WeeklyEpisodeYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        IdleBaselineXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        IdleBaselineYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        IdleBaselineYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            await RefreshCoreAsync();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task RefreshCoreAsync()
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

        // #601: a second, driver-free throttle source - sampled independently of
        // SensorMonitorService above (and unaffected by SensorsAvailable), so it still works when
        // the LibreHardwareMonitorLib driver couldn't open at all.
        List<ThermalZoneReading> zoneReadings;
        try { zoneReadings = await Task.Run(() => _thermalZones.Sample()); }
        catch { zoneReadings = new List<ThermalZoneReading>(); }
        ThermalZones.Clear();
        foreach (var z in zoneReadings.OrderBy(z => z.ZoneName)) ThermalZones.Add(z);

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
        // #96: out-of-spec voltage-rail flagging - see WithVoltageSpecCheck's remarks.
        Replace(Voltages, readings.Where(r => r.Type == SensorType.Voltage && HasNonZeroReading(r)).Select(WithVoltageSpecCheck));
        var wattageReadings = readings.Where(r => r.Type == SensorType.Power && HasNonZeroReading(r)).ToList();
        Replace(Wattages, wattageReadings);
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
        GpuTempC = gpuEdge;

        // #607: per-core temperature spread (max - min across "Core #N" sensors) - a persistently
        // large spread on a desktop points at uneven cooler mount or poor pump contact rather than
        // general heat.
        var coreTemps = tempReadings
            .Where(r => r.HardwareType == HardwareType.Cpu && r.Value.HasValue && CoreTempNameRegex.IsMatch(r.SensorName))
            .Select(r => (double)r.Value!.Value)
            .ToList();
        CoreTempSpreadC = coreTemps.Count >= 2 ? coreTemps.Max() - coreTemps.Min() : null;

        // #608: thermal headroom - remaining °C before an inferred/reported throttle point, which
        // predicts "will this throttle under more load" better than an absolute reading alone.
        // Prefer a directly reported ceiling (rare - most LibreHardwareMonitorLib backends don't
        // expose TjMax), falling back to the lowest peak temperature ever recorded across this
        // machine's own persisted Thermal episodes (#604) as the closest available proxy for
        // "the temperature this CPU actually starts throttling at". Null (tile hidden) when
        // neither is available yet - never a fabricated ceiling.
        var cpuTempsForCeiling = tempReadings.Where(r => r.HardwareType == HardwareType.Cpu);
        double? cpuCeiling = FindByNameContains(cpuTempsForCeiling, "TjMax", "Tj Max", "Max Temperature", "Temperature Limit");
        if (cpuCeiling is null)
        {
            var thermalEpisodes = _persistedEpisodes.Where(e => e.ReasonClass == ThrottleReasonClass.Thermal).ToList();
            if (thermalEpisodes.Count > 0) cpuCeiling = thermalEpisodes.Min(e => e.PeakTempC);
        }
        CpuThermalHeadroomC = cpuCeiling.HasValue && CpuPackageTempC.HasValue ? cpuCeiling - CpuPackageTempC : null;

        // GPU headroom relies on a directly reported ceiling only - this round doesn't add a
        // separate GPU throttle-episode detector, so there's no inferred fallback the way the CPU
        // side has one; degrades to null (tile hidden) rather than guess.
        double? gpuCeiling = FindByNameContains(gpuTemps, "TjMax", "Tj Max", "Max Temperature", "Temperature Limit", "Throttle Point");
        GpuThermalHeadroomC = gpuCeiling.HasValue && GpuTempC.HasValue ? gpuCeiling - GpuTempC : null;

        // #93: GPU power-limit/TDP readout - restricted to GPU hardware entries (same reasoning
        // as the hotspot lookup above) since "Power Limit"/"TDP" style names could otherwise
        // collide with a CPU or motherboard sensor. Most GPU backends in LibreHardwareMonitorLib
        // only expose instantaneous draw, not a distinct limit/TDP sensor - null (tile hidden) is
        // the expected common case, not a bug.
        var gpuWattages = wattageReadings.Where(r => IsGpu(r.HardwareType)).ToList();
        GpuPowerLimitW = FindByNameContains(gpuWattages, "Power Limit", "TDP Limit", "TDP");

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

        // #95: same primary fan as the scatter above, plotted as a plain time series instead -
        // makes hunting/oscillation (RPM repeatedly ramping at a near-constant temperature)
        // directly visible in a way the scatter cloud doesn't.
        if (primaryFan is not null)
        {
            FanRpmHistory.Add(primaryFan.Value!.Value);
            if (FanRpmHistory.Count > HistoryLength) FanRpmHistory.RemoveAt(0);
        }

        // #603/#604: full reason-class verdict for this tick, using the same shared classifier
        // CpuViewModel's own dwell breakdown uses - see ThrottleClassificationService's remarks
        // for why this deliberately duplicates CpuViewModel's own per-tick classification rather
        // than sharing a live instance (no circular ViewModel reference), the same "two
        // independent samplers, one shared formula" shape the pre-existing throttlingNow local
        // already established for this exact condition.
        var zoneThrottlePercents = ThermalZones.Where(z => z.ThrottlePercent.HasValue).Select(z => z.ThrottlePercent!.Value).ToList();
        double? maxZoneThrottle = zoneThrottlePercents.Count > 0 ? zoneThrottlePercents.Max() : null;
        var reasonClass = ThrottleClassificationService.Classify(
            CpuPackageTempC, _performance.CpuCurrentPercent, _performance.CpuVsBasePercent,
            TotalPackagePowerW, PowerSessionMaxW, _performance.ParkedCoreCount, _performance.Cores.Count,
            maxZoneThrottle, FirmwareLimitActive);
        bool throttlingNow = reasonClass != ThrottleReasonClass.None;

        if (CpuPackageTempC is { } cpuTemp)
        {
            CpuTempHistory.Add(cpuTemp);
            if (CpuTempHistory.Count > HistoryLength) CpuTempHistory.RemoveAt(0);

            // #25: log (at most once per 30s, to avoid spamming the list) whenever the CPU is
            // throttling for any classified reason - a timestamped history, not just a live flag.
            if (throttlingNow && (_lastThrottleLogged is null || (DateTime.Now - _lastThrottleLogged.Value).TotalSeconds >= 30))
            {
                _lastThrottleLogged = DateTime.Now;
                ThrottleEvents.Insert(0, $"{DateTime.Now:T} — {cpuTemp:0}°C, {_performance.CpuVsBasePercent:0}% vs. base clock ({DescribeReasonClass(reasonClass)})");
                while (ThrottleEvents.Count > 10) ThrottleEvents.RemoveAt(ThrottleEvents.Count - 1);
            }
        }

        // #604/#605: sustained-load stopwatch + episode open/close/persist tracking.
        TrackSustainedLoadAndEpisode(throttlingNow, reasonClass, CpuPackageTempC);

        // #609: idle-temperature baseline drift.
        TrackIdleBaseline(CpuPackageTempC);
    }

    private static string DescribeReasonClass(ThrottleReasonClass c) => c switch
    {
        ThrottleReasonClass.Thermal => "thermal",
        ThrottleReasonClass.Power => "power",
        ThrottleReasonClass.Firmware => "firmware — confirmed by Windows",
        ThrottleReasonClass.CoreParked => "core-parked",
        _ => "none",
    };

    /// <summary>#605: tracks a sustained-load stopwatch (CPU &gt; 80% for &gt; 15s) and, on top of
    /// it, #604's episode open/close/persist lifecycle - the two share one tracking pass since an
    /// episode's TimeToThrottleSeconds is only meaningful measured from a freshly tracked
    /// sustained-load start.</summary>
    private void TrackSustainedLoadAndEpisode(bool throttlingNow, ThrottleReasonClass reasonClass, double? cpuTemp)
    {
        var now = DateTime.Now;

        if (_performance.CpuCurrentPercent > 80) _sustainedLoadStartedAt ??= now;
        else _sustainedLoadStartedAt = null;

        if (throttlingNow)
        {
            if (_activeEpisode is null)
            {
                double? timeToThrottle = _sustainedLoadStartedAt is { } loadStart && (now - loadStart).TotalSeconds >= 15
                    ? (now - loadStart).TotalSeconds
                    : null;

                _activeEpisode = new ThrottleEpisode
                {
                    Start = now,
                    End = now,
                    ReasonClass = reasonClass,
                    PeakTempC = cpuTemp ?? 0,
                    PeakPackagePowerW = TotalPackagePowerW ?? 0,
                    TimeToThrottleSeconds = timeToThrottle,
                };
                _activeEpisodeClockSamples.Clear();

                if (timeToThrottle is { } ttt)
                {
                    CurrentTimeToThrottleSeconds = ttt;
                    UpdateTimeToThrottleText();
                }
            }

            _activeEpisode.End = now;
            _activeEpisode.PeakTempC = Math.Max(_activeEpisode.PeakTempC, cpuTemp ?? _activeEpisode.PeakTempC);
            _activeEpisode.PeakPackagePowerW = Math.Max(_activeEpisode.PeakPackagePowerW, TotalPackagePowerW ?? _activeEpisode.PeakPackagePowerW);
            _activeEpisodeClockSamples.Add(_performance.CpuCurrentClockGhz * 1000.0);
        }
        else if (_activeEpisode is not null)
        {
            _activeEpisode.MeanEffectiveMhz = _activeEpisodeClockSamples.Count > 0 ? _activeEpisodeClockSamples.Average() : 0;
            var closed = _activeEpisode;
            _activeEpisode = null;

            ThrottleHistoryService.Append(closed);
            _persistedEpisodes.Add(closed);
            RecentThrottleEpisodes.Insert(0, closed);
            while (RecentThrottleEpisodes.Count > 10) RecentThrottleEpisodes.RemoveAt(RecentThrottleEpisodes.Count - 1);
            RebuildWeeklyEpisodeSparkline();
        }
    }

    /// <summary>#605: "Time to throttle: 4m 10s (was 11m 30s in May)" - compares this session's
    /// most recent measured time-to-throttle against the average recorded in the most recent
    /// earlier calendar month that actually has data (comparing against partial data from earlier
    /// in the *same* month would just be noise).</summary>
    private void UpdateTimeToThrottleText()
    {
        if (CurrentTimeToThrottleSeconds is not { } current)
        {
            TimeToThrottleText = "No throttle observed yet this session";
            return;
        }

        string nowText = FormatDuration(current);

        int thisMonth = DateTime.Now.Month, thisYear = DateTime.Now.Year;
        var priorMonthEpisodes = _persistedEpisodes
            .Where(e => e.TimeToThrottleSeconds.HasValue && (e.Start.Year != thisYear || e.Start.Month != thisMonth))
            .OrderByDescending(e => e.Start)
            .ToList();

        if (priorMonthEpisodes.Count == 0)
        {
            TimeToThrottleText = $"Time to throttle: {nowText}";
            return;
        }

        var latest = priorMonthEpisodes[0].Start;
        var sameMonth = priorMonthEpisodes.Where(e => e.Start.Year == latest.Year && e.Start.Month == latest.Month).ToList();
        double avg = sameMonth.Average(e => e.TimeToThrottleSeconds!.Value);

        TimeToThrottleText = $"Time to throttle: {nowText} (was {FormatDuration(avg)} in {latest:MMMM})";
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes}m {ts.Seconds}s" : $"{ts.Seconds}s";
    }

    /// <summary>#604: per-week episode count over the last <see cref="WeeklySparklineWeeks"/>
    /// weeks, oldest first, zero-filled for weeks with no episodes at all.</summary>
    private void RebuildWeeklyEpisodeSparkline()
    {
        var today = DateTime.Now.Date;
        var counts = new double[WeeklySparklineWeeks];
        foreach (var ep in _persistedEpisodes)
        {
            int weeksAgo = (int)((today - ep.Start.Date).TotalDays / 7);
            int idx = WeeklySparklineWeeks - 1 - weeksAgo;
            if (idx >= 0 && idx < WeeklySparklineWeeks) counts[idx]++;
        }
        for (int i = 0; i < WeeklySparklineWeeks && i < WeeklyEpisodeCounts.Count; i++)
            WeeklyEpisodeCounts[i] = counts[i];
    }

    /// <summary>#609: tracks a genuinely-idle window (CPU &lt; 5% for 60s) and, once one completes,
    /// records that window's median CPU package temperature as today's baseline entry.</summary>
    private void TrackIdleBaseline(double? cpuTemp)
    {
        bool idleNow = _performance.CpuCurrentPercent < IdleCpuThresholdPercent;
        if (!idleNow || cpuTemp is not { } temp)
        {
            _idleWindowStartedAt = null;
            _idleTempSamples.Clear();
            return;
        }

        _idleWindowStartedAt ??= DateTime.Now;
        _idleTempSamples.Add(temp);

        if ((DateTime.Now - _idleWindowStartedAt.Value).TotalSeconds < IdleWindowSeconds) return;

        var sorted = _idleTempSamples.OrderBy(v => v).ToList();
        double median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;

        ThermalBaselineService.RecordToday(median);
        RefreshIdleBaselineChart();

        // Reset so a long idle stretch records one entry per 60s window, not a continuous
        // re-trigger every tick past the first qualifying window.
        _idleWindowStartedAt = DateTime.Now;
        _idleTempSamples.Clear();
    }

    private void RefreshIdleBaselineChart()
    {
        var entries = ThermalBaselineService.Load();
        IdleBaselineHistory.Clear();
        foreach (var e in entries) IdleBaselineHistory.Add(e.MedianIdleTempC);

        int labelStep = entries.Count <= 6 ? 1 : Math.Max(1, entries.Count / 6);
        IdleBaselineXAxes[0].Labels = entries
            .Select((e, i) => i == 0 || i == entries.Count - 1 || i % labelStep == 0 ? e.Date.ToString("M/d/yy") : string.Empty)
            .ToArray();
    }

    /// <summary>#602: loads firmware-limit events once at startup, plus on-demand via
    /// LoadFirmwareEventsCommand - an event-log query, so never on the tick timer.</summary>
    private async Task LoadFirmwareEventsAsync()
    {
        var events = await Task.Run(() => _eventLog.ReadFirmwareThrottleEvents());
        FirmwareThrottleEvents.Clear();
        foreach (var e in events) FirmwareThrottleEvents.Add(e);

        var latest = events.OrderByDescending(e => e.TimeCreated).FirstOrDefault();
        FirmwareLimitActive = latest is not null && !latest.IsRecovery;
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

    /// <summary>Round 12, #96: flags a recognized 12V/5V/3.3V rail reading more than ~5% off its
    /// nominal value - the same simple threshold check PSU monitoring utilities have used for
    /// decades. Matched by sensor name (LibreHardwareMonitorLib doesn't standardize voltage
    /// sensor names any more than it standardizes CPU/battery sensor names - "+12V"/"12V"/"12 V"
    /// all show up depending on motherboard vendor), so an unrecognized rail name is left as
    /// IsVoltageOutOfSpec = null ("not checked"), never a false accusation against a rail this
    /// app doesn't confidently recognize.</summary>
    private static readonly (string[] Hints, float Nominal)[] VoltageRailSpecs =
    {
        (new[] { "12V", "+12V", "12 V" }, 12.0f),
        (new[] { "5V", "+5V", "5 V" }, 5.0f),
        (new[] { "3.3V", "+3.3V", "3.3 V" }, 3.3f),
    };

    private static SensorReading WithVoltageSpecCheck(SensorReading reading)
    {
        if (reading.Value is not float value) return reading;

        foreach (var (hints, nominal) in VoltageRailSpecs)
        {
            if (!hints.Any(h => reading.SensorName.Contains(h, StringComparison.OrdinalIgnoreCase))) continue;

            bool outOfSpec = Math.Abs(value - nominal) / nominal > 0.05f;
            return new SensorReading
            {
                HardwareName = reading.HardwareName,
                HardwareType = reading.HardwareType,
                SensorName = reading.SensorName,
                Type = reading.Type,
                Value = reading.Value,
                Identifier = reading.Identifier,
                SessionMin = reading.SessionMin,
                SessionMax = reading.SessionMax,
                IsVoltageOutOfSpec = outOfSpec,
            };
        }

        return reading; // unrecognized rail name - IsVoltageOutOfSpec stays null, not flagged
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
        _thermalZones.Dispose();
    }
}
