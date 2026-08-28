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

    // ================================================================================
    // #660-#664: Power plans and processor power management - extends the power-plan card above.
    // ================================================================================

    // ---- #660: powercfg /energy 60-second diagnostic scan -----------------------------------
    public ObservableCollection<PowerEfficiencyFinding> PowerEfficiencyFindings { get; } = new();

    private string _powerEfficiencyStatusText = "Not run yet - this takes about 60 seconds.";
    public string PowerEfficiencyStatusText { get => _powerEfficiencyStatusText; private set => SetProperty(ref _powerEfficiencyStatusText, value); }

    private bool _isPowerEfficiencyScanRunning;
    public bool IsPowerEfficiencyScanRunning { get => _isPowerEfficiencyScanRunning; private set => SetProperty(ref _isPowerEfficiencyScanRunning, value); }

    public AsyncRelayCommand RunPowerEfficiencyScanCommand { get; }

    // ---- #661: active-plan setting diff against Balanced defaults ---------------------------
    public ObservableCollection<PowerPlanSettingDiff> PowerPlanSettingDiffs { get; } = new();

    private string _powerPlanDiffStatusText = string.Empty;
    public string PowerPlanDiffStatusText { get => _powerPlanDiffStatusText; private set => SetProperty(ref _powerPlanDiffStatusText, value); }

    public AsyncRelayCommand LoadPowerPlanDiffCommand { get; }

    // ---- #662: hidden high-impact settings, paired AC | DC ----------------------------------
    public ObservableCollection<HiddenPowerSettingRow> HiddenPowerSettings { get; } = new();

    private string _hiddenPowerSettingsStatusText = string.Empty;
    public string HiddenPowerSettingsStatusText { get => _hiddenPowerSettingsStatusText; private set => SetProperty(ref _hiddenPowerSettingsStatusText, value); }

    public AsyncRelayCommand LoadHiddenPowerSettingsCommand { get; }

    // ---- #663: passive-cooling-policy flag + one-click fix -----------------------------------
    private bool _passiveCoolingOnAcDetected;
    public bool PassiveCoolingOnAcDetected { get => _passiveCoolingOnAcDetected; private set => SetProperty(ref _passiveCoolingOnAcDetected, value); }

    private string _passiveCoolingFixStatusText = string.Empty;
    public string PassiveCoolingFixStatusText { get => _passiveCoolingFixStatusText; private set => SetProperty(ref _passiveCoolingFixStatusText, value); }

    public AsyncRelayCommand FixPassiveCoolingCommand { get; }

    // ---- #664: power-plan change history ------------------------------------------------------
    // Detected by comparing the active scheme GUID between polls of the tick timer, throttled to
    // PowerPlanCheckInterval (same "let the tick fire often, do the heavier work every N minutes"
    // shape PowerHistoryAppendInterval/CoolingFlushInterval already establish in this file) -
    // powercfg /list is a real subprocess call, so this still isn't a per-tick read, just a
    // periodic one, consistent with CLAUDE.md's on-demand-vs-polled convention.
    private static readonly TimeSpan PowerPlanCheckInterval = TimeSpan.FromMinutes(2);
    private DateTime _lastPowerPlanCheck = DateTime.MinValue;
    private string? _lastKnownActivePlanGuid;
    private string _lastKnownActivePlanName = string.Empty;

    public ObservableCollection<PowerPlanChangeEvent> PowerPlanChangeHistory { get; } = new();

    // ---- #665-#669: USB power and over-current - extends the USB card below. ------------------

    // Round 12, #92: per-USB-device selective-suspend status - on-demand (can be a couple dozen
    // devices, each looked up by a best-effort prefix match; see UsbPowerService's remarks for
    // why SelectiveSuspendEnabled is "Unknown" far more often than a hard true/false).
    public ObservableCollection<UsbDevicePowerInfo> UsbDevices { get; } = new();
    public AsyncRelayCommand LoadUsbDevicesCommand { get; }

    // ---- #665/#667: USB over-current/port-reset events + per-device re-enumeration counts -----
    public ObservableCollection<UsbPowerEvent> UsbOverCurrentEvents { get; } = new();

    private string _usbEventsStatusText = string.Empty;
    public string UsbEventsStatusText { get => _usbEventsStatusText; private set => SetProperty(ref _usbEventsStatusText, value); }

    public AsyncRelayCommand LoadUsbEventsCommand { get; }

    // ---- #666: USB hub inventory + system-wide port-occupancy proxy ---------------------------
    public ObservableCollection<UsbHubPowerInfo> UsbHubs { get; } = new();

    private string _usbHubStatusText = string.Empty;
    public string UsbHubStatusText { get => _usbHubStatusText; private set => SetProperty(ref _usbHubStatusText, value); }

    // ---- #668: selective-suspend risk fix actions ----------------------------------------------
    public AsyncRelayCommand ToggleUsbDeviceSuspendCommand { get; }
    public AsyncRelayCommand DisablePlanUsbSuspendCommand { get; }

    private string _usbFixStatusText = string.Empty;
    public string UsbFixStatusText { get => _usbFixStatusText; private set => SetProperty(ref _usbFixStatusText, value); }

    // ---- #669: USB-C/Thunderbolt PD negotiation readout (UCSI, hidden when absent) -------------
    public ObservableCollection<UsbPdConnectorInfo> UsbPdConnectors { get; } = new();
    public bool HasUsbPdConnectors => UsbPdConnectors.Count > 0;

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

    /// <summary>#621/#625/#638: actual instantaneous GPU power draw (distinct from
    /// GpuPowerLimitW's configured ceiling above) - needed as the "GPU power" half of "package and
    /// GPU power" for the rail-sag correlation, the power-history log, and the reboot/WHEA
    /// power-draw correlations. Null when no discrete GPU wattage sensor is reported.</summary>
    private double? _gpuPowerDrawW;
    public double? GpuPowerDrawW { get => _gpuPowerDrawW; private set => SetProperty(ref _gpuPowerDrawW, value); }

    /// <summary>GPU hotspot-vs-edge temperature differential (#29) - a large, sustained gap is a
    /// common sign of degraded thermal paste/pads on a GPU cooler, distinct from either reading
    /// alone being high. Null when either sensor isn't reported (no discrete GPU, or the vendor's
    /// LibreHardwareMonitorLib backend doesn't expose a hotspot/junction sensor).</summary>
    private double? _gpuHotspotDeltaC;
    public double? GpuHotspotDeltaC { get => _gpuHotspotDeltaC; private set => SetProperty(ref _gpuHotspotDeltaC, value); }

    /// <summary>#675: GPU memory-junction temperature - present only on GDDR6X-equipped cards
    /// (LibreHardwareMonitorLib names it "GPU Memory Junction"/"Memory Junction"), tracked
    /// separately from GpuTempC (core/edge) and GpuHotspotDeltaC (hotspot vs. edge) since VRAM
    /// throttling is entirely invisible in either of those - a card can look comfortably cool at
    /// the core while its memory is already past the ~105°C throttle reference. Null (tile hidden)
    /// on every card without this sensor, which is most of them - never inferred from the core/
    /// hotspot readings.</summary>
    private double? _gpuMemoryJunctionTempC;
    public double? GpuMemoryJunctionTempC { get => _gpuMemoryJunctionTempC; private set => SetProperty(ref _gpuMemoryJunctionTempC, value); }

    // #675: the well-known ~105°C GDDR6X memory-junction throttle reference (Micron/Samsung GDDR6X
    // datasheets and NVIDIA's own driver both target this figure) - not a per-card reported limit
    // (no LibreHardwareMonitorLib backend exposes one), so this is a fixed comparison point, shown
    // as such in the UI rather than a measured/reported ceiling.
    public const double GpuMemoryJunctionThrottleReferenceC = 105.0;

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

    // ---- #620: ATX rail out-of-spec verdict + excursion counter --------------------------------
    // Turns the flat IsVoltageOutOfSpec bool (Round 12, #96) into a verdict: signed deviation
    // percent plus a per-hour excursion count, so one boot-time glitch doesn't read the same as
    // continuous instability. Excursions are edge-triggered (a transition from in-spec to
    // out-of-spec), tracked per rail identifier in a rolling one-hour timestamp window.
    private readonly Dictionary<string, bool> _railWasOutOfSpec = new();
    private readonly Dictionary<string, List<DateTime>> _railExcursionTimestamps = new();
    private static readonly TimeSpan RailExcursionWindow = TimeSpan.FromHours(1);

    // ---- #621/#622: rail-sag-under-load correlation + Vcore droop/load-line chart --------------
    // "Power delivery" section: a 12V rail that measurably drops as system power rises is a loaded
    // or failing PSU (#621); Vcore plotted against package power exposes the effective load-line
    // calibration curve, where excessive droop shows as an unusually steep slope (#622). Both are
    // rolling-window scatter clouds with a live least-squares fit line, the same shape the fan
    // curve's ghost-overlay regression already established, just recomputed live instead of
    // persisted per month.
    private const int PowerDeliveryWindow = 180;

    public ObservableCollection<ObservablePoint> RailSagPoints { get; } = new();
    private readonly ScatterSeries<ObservablePoint> _railSagScatter;
    public ObservableCollection<ObservablePoint> RailSagFitLine { get; } = new();
    private readonly LineSeries<ObservablePoint> _railSagFitSeries;
    public ISeries[] RailSagSeries { get; }
    public Axis[] RailSagXAxes { get; }
    public Axis[] RailSagYAxes { get; }

    private string _railSagVerdictText = "Not enough samples yet to assess rail sag under load.";
    public string RailSagVerdictText { get => _railSagVerdictText; private set => SetProperty(ref _railSagVerdictText, value); }

    public ObservableCollection<ObservablePoint> VcoreLoadPoints { get; } = new();
    private readonly ScatterSeries<ObservablePoint> _vcoreLoadScatter;
    public ObservableCollection<ObservablePoint> VcoreLoadFitLine { get; } = new();
    private readonly LineSeries<ObservablePoint> _vcoreLoadFitSeries;
    public ISeries[] VcoreLoadSeries { get; }
    public Axis[] VcoreLoadXAxes { get; }
    public Axis[] VcoreLoadYAxes { get; }

    private string _vcoreLoadSlopeText = "Not enough samples yet to chart Vcore load-line behavior.";
    public string VcoreLoadSlopeText { get => _vcoreLoadSlopeText; private set => SetProperty(ref _vcoreLoadSlopeText, value); }

    private static readonly (string[] Hints, float Nominal)[] Rail12VHints = { (new[] { "12V", "+12V", "12 V" }, 12.0f) };
    private static readonly string[] VcoreNameHints = { "Vcore", "CPU Vcore", "CPU Core Voltage", "Core Voltage", "CPU Core" };

    // ---- #623: VRM temperature attribution ------------------------------------------------------
    // The tab already trends generic motherboard temperature (MotherboardTempC, hint list
    // "VRM"/"System"/"Motherboard"/"PCH"/"Chipset"); this adds an explicit VRM/MOS-named-sensor-
    // only reading plus a "VRM hot while package is not" flag, which indicts a poorly-cooled VRM
    // heatsink rather than the CPU cooler. Its own tile and history line, hidden entirely when no
    // VRM-named motherboard sensor exists at all.
    private static readonly string[] VrmNameHints = { "VRM", "MOS", "MOSFET" };
    private const double VrmHotThresholdC = 75.0;
    private const double VrmHotPackageNotHotThresholdC = 60.0;

    private double? _vrmTempC;
    public double? VrmTempC { get => _vrmTempC; private set => SetProperty(ref _vrmTempC, value); }

    public ObservableCollection<double> VrmTempHistory { get; } = NewHistory();
    private readonly LineSeries<double> _vrmTempGlow;
    private readonly LineSeries<double> _vrmTempCore;
    public ISeries[] VrmTempSeries { get; }
    public Axis[] VrmTempYAxes { get; }

    private bool _vrmHotWhilePackageNotFlag;
    public bool VrmHotWhilePackageNotFlag { get => _vrmHotWhilePackageNotFlag; private set => SetProperty(ref _vrmHotWhilePackageNotFlag, value); }

    // ---- #632: AMD PPT/TDC/EDC limit approximation -----------------------------------------------
    // Where LibreHardwareMonitorLib exposes a Ryzen SoC/package current sensor alongside the
    // package power this tab already reads (TotalPackagePowerW), tracks sustained dwell at each
    // apparent ceiling (power vs. current, both compared against their own session-high) under
    // sustained load and labels whichever one is more consistently pinned at its ceiling as "the
    // more consistently binding limit". Explicitly approximate: Ryzen's real limit-reason telemetry
    // needs the vendor SMU access this app deliberately does not take. Gated on the CPU name
    // looking like AMD/Ryzen so this framing never shows on Intel silicon, where PPT/TDC/EDC don't
    // apply - degrades to a hidden card, never a fabricated reading, on any other CPU.
    public bool IsAmdCpu => _performance.CpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
        _performance.CpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] AmdCurrentNameHints = { "CPU Core", "SoC Current", "Core", "SoC", "CPU" };
    private const double AmdCeilingFraction = 0.98; // "at its ceiling" = within ~2% of this session's own max

    public ObservableCollection<SensorReading> Currents { get; } = new();

    private double? _amdCurrentA;
    public double? AmdCurrentA { get => _amdCurrentA; private set => SetProperty(ref _amdCurrentA, value); }

    private double? _amdCurrentSessionMaxA;
    public double? AmdCurrentSessionMaxA { get => _amdCurrentSessionMaxA; private set => SetProperty(ref _amdCurrentSessionMaxA, value); }

    private double _amdPowerCeilingDwellSeconds;
    private double _amdCurrentCeilingDwellSeconds;
    private double _amdTotalDwellSeconds;
    private DateTime? _lastAmdDwellTick;

    private string _amdLimitVerdictText = string.Empty;
    public string AmdLimitVerdictText { get => _amdLimitVerdictText; private set => SetProperty(ref _amdLimitVerdictText, value); }

    // ---- #633: inferred non-stock Vcore-vs-frequency evidence ------------------------------------
    // One of three independent, individually weak inputs to StabilityViewModel's combined
    // undervolt/overclock instability flag - reuses the #622 Vcore-vs-package-power sampling above
    // rather than reading Vcore a second time. No vendor "stock" reference curve is available, so
    // this is a coarse sanity threshold (unusually low Vcore while boosting), not a real comparison
    // against this CPU's actual stock curve.
    private const double NonStockVcoreThresholdV = 1.0;

    public bool NonStockVcoreLooksLikely =>
        VcoreLoadPoints.Count > 0 &&
        _performance.CpuVsBasePercent >= 0 &&
        VcoreLoadPoints[^1].Y is { } lastVcore && lastVcore < NonStockVcoreThresholdV;

    public string NonStockVcoreEvidenceText => NonStockVcoreLooksLikely
        ? $"Vcore reads {VcoreLoadPoints[^1].Y:0.###} V while at/above rated base clock under load - unusually low for that state on typical silicon (inferred, not compared against this CPU's actual stock curve)."
        : string.Empty;

    // ---- #624: PSU inventory + wattage sanity check ---------------------------------------------
    // Win32_PowerSupply/Win32_SystemEnclosure (PsuService) when an OEM populated them; otherwise
    // (the common case) a user-entered wattage persisted to psu.json (PsuSettingsService). Either
    // way, estimated total draw (CPU package + GPU + a fixed platform allowance) is compared
    // against it, and sustained draw above ~80% flags a brownout/shutdown risk.
    private const double PsuPlatformAllowanceW = 30.0;
    private const double PsuBrownoutLoadFraction = 0.80;
    private const int PsuSustainedSampleCount = 10; // ~10 ticks at the default poll interval

    private PsuInfo? _psuInventory;
    private string _psuInventoryText = "Not checked yet.";
    public string PsuInventoryText { get => _psuInventoryText; private set => SetProperty(ref _psuInventoryText, value); }

    private double? _psuRatedWattageW;
    public double? PsuRatedWattageW { get => _psuRatedWattageW; private set => SetProperty(ref _psuRatedWattageW, value); }

    private string _psuWattageInputText = string.Empty;
    public string PsuWattageInputText { get => _psuWattageInputText; set => SetProperty(ref _psuWattageInputText, value); }

    private double? _estimatedTotalDrawW;
    public double? EstimatedTotalDrawW { get => _estimatedTotalDrawW; private set => SetProperty(ref _estimatedTotalDrawW, value); }

    private double? _psuLoadPercent;
    public double? PsuLoadPercent { get => _psuLoadPercent; private set => SetProperty(ref _psuLoadPercent, value); }

    private bool _psuBrownoutRiskDetected;
    public bool PsuBrownoutRiskDetected { get => _psuBrownoutRiskDetected; private set => SetProperty(ref _psuBrownoutRiskDetected, value); }

    private readonly List<double> _psuLoadPercentSamples = new();

    public AsyncRelayCommand LoadPsuInfoCommand { get; }
    public RelayCommand SavePsuWattageCommand { get; }

    // ---- #625: coarse power-history log (correlated against reboot/WHEA timestamps after the
    // fact by StabilityViewModel) ------------------------------------------------------------------
    private static readonly TimeSpan PowerHistoryAppendInterval = TimeSpan.FromMinutes(1);
    private DateTime _lastPowerHistoryAppend = DateTime.MinValue;

    // ---- #626: DC-jack / adapter power-source flapping -------------------------------------------
    // On-demand (event-log query), same "loaded once at startup plus a manual refresh" shape as
    // #602's firmware-limit events.
    public ObservableCollection<PowerSourceChangeEvent> PowerSourceChangeEvents { get; } = new();
    public AsyncRelayCommand LoadPowerSourceEventsCommand { get; }

    private int _powerSourceChangesLastHour;
    public int PowerSourceChangesLastHour { get => _powerSourceChangesLastHour; private set => SetProperty(ref _powerSourceChangesLastHour, value); }

    private string _powerSourceFlapText = string.Empty;
    public string PowerSourceFlapText { get => _powerSourceFlapText; private set => SetProperty(ref _powerSourceFlapText, value); }

    private bool _powerSourceFlapWarning;
    public bool PowerSourceFlapWarning { get => _powerSourceFlapWarning; private set => SetProperty(ref _powerSourceFlapWarning, value); }

    // ---- #611: same-load-hotter-over-months cooling-degradation tracking ----------------------
    // Every tick with a valid (temp, load, power) triple is bucketed into (load decile x power
    // decile) and accumulated in memory; every few minutes the accumulated samples for each
    // touched bucket are median-reduced and merged into cooling-baseline.json
    // (CoolingBaselineService), keyed by calendar month. Bucketing by load/power removes workload
    // as a confounder that a naive "average temp is up" comparison would get wrong.
    private static readonly TimeSpan CoolingFlushInterval = TimeSpan.FromMinutes(5);
    private readonly Dictionary<(int Load, int Power), List<double>> _coolingBucketSamples = new();
    private DateTime _lastCoolingFlush = DateTime.Now;
    public ObservableCollection<CoolingDegradationRow> CoolingDegradationRows { get; } = new();

    // ---- #612: fan efficiency index + monthly fan-curve regression overlay --------------------
    // Cooling delivered per RPM: package power divided by temperature-above-ambient, normalized
    // by fan RPM. A rising index (need more RPM for the same cooling) points at dust/a clogged
    // heatsink. The monthly linear fit of the fan-curve cloud is persisted so last month's fit
    // can be drawn ghosted behind this month's scatter on the existing fan-curve chart.
    private double? _fanEfficiencyIndex;
    public double? FanEfficiencyIndex { get => _fanEfficiencyIndex; private set => SetProperty(ref _fanEfficiencyIndex, value); }

    private const int FanCurveMonthlyReservoirSize = 1500;
    private readonly List<(double Temp, double Rpm)> _fanCurveMonthSamples = new();
    private readonly Random _fanCurveReservoirRandom = new();
    private long _fanCurveMonthSampleCount;
    private int _fanCurveMonthYear = -1;
    private int _fanCurveMonthMonth = -1;
    private string _fanCurveMonthFanIdentifier = string.Empty;
    private List<FanCurveMonthlyFit> _fanCurveFits = new();

    public ObservableCollection<ObservablePoint> FanCurveGhostLine { get; } = new();
    private readonly LineSeries<ObservablePoint> _fanCurveGhostSeries;

    // ---- #613: fan stall / hunting detector ----------------------------------------------------
    // Distinct from the exactly-0-RPM DeadFanDetected check above - oscillation (repeated ±20%
    // RPM swings at a near-constant temperature) or sub-minimum-RPM dwell under load is a failing
    // bearing or a badly-tuned firmware fan curve, not a stopped fan.
    private bool _fanHuntingDetected;
    public bool FanHuntingDetected { get => _fanHuntingDetected; private set => SetProperty(ref _fanHuntingDetected, value); }

    private string _fanHuntingReason = string.Empty;
    public string FanHuntingReason { get => _fanHuntingReason; private set => SetProperty(ref _fanHuntingReason, value); }

    // ---- #614: fan RPM step-loss (historical max vs. this session's max) ----------------------
    private readonly Dictionary<string, double> _fanHistoricalMaxRpm;
    private readonly Dictionary<string, double> _fanSessionMaxRpm = new();

    // ---- #616: coolant-pump variance monitoring -------------------------------------------------
    private const int PumpRpmWindowSize = 40;
    private readonly Dictionary<string, List<double>> _pumpRpmWindows = new();
    public ObservableCollection<PumpStatus> CoolantPumps { get; } = new();

    // ---- #617: heat-soak detection (burst vs. steady state under one sustained load) ----------
    private double? _burstTempC;
    private double? _soakedTempC;
    private string _heatSoakText = "No sustained 10-minute load observed yet this session.";
    public string HeatSoakText { get => _heatSoakText; private set => SetProperty(ref _heatSoakText, value); }

    // ---- #618: cooldown-rate measurement --------------------------------------------------------
    private double _loadPeakTempC;
    private bool _wasUnderHeavyLoad;
    private DateTime? _cooldownStartedAt;
    private double? _cooldownPeakTempC;

    private string _cooldownText = "No cooldown measured yet this session.";
    public string CooldownText { get => _cooldownText; private set => SetProperty(ref _cooldownText, value); }

    private const int CooldownTrendMonths = 12;
    public ObservableCollection<double> CooldownMonthlyHistory { get; } = NewFixedSeries(CooldownTrendMonths);
    private readonly LineSeries<double> _cooldownGlow;
    private readonly LineSeries<double> _cooldownCore;
    public ISeries[] CooldownSeries { get; }
    public Axis[] CooldownXAxes { get; }
    public Axis[] CooldownYAxes { get; }

    // ---- #619: ambient-temperature proxy + normalize-to-ambient toggle -------------------------
    // Lowest of the motherboard "System" sensor / drive temperatures at the close of a genuinely
    // idle window (same window TrackIdleBaseline already measures) - the closest available proxy
    // for room-ambient temperature. Without this, a hot summer and a dying cooler look identical
    // in every month-over-month trend above.
    private double? _ambientProxyC;
    public double? AmbientProxyC { get => _ambientProxyC; private set => SetProperty(ref _ambientProxyC, value); }

    private bool _normalizeToAmbient;
    public bool NormalizeToAmbient
    {
        get => _normalizeToAmbient;
        set { if (SetProperty(ref _normalizeToAmbient, value)) RefreshIdleBaselineChart(); }
    }

    private static readonly Regex PumpNameRegex = new(@"pump|aio|w[_\s]?pump", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly SKColor AxisTextColor = new(0x9A, 0x9A, 0xA2);
    private static readonly SKColor AxisSeparatorColor = new(0x33, 0x33, 0x3A, 160);
    private const float CoreStrokeWidth = 2f;
    private const float GlowStrokeWidth = 7f;

    // ================================================================================
    // #641-#648: battery report panel - powercfg /batteryreport + WMI fallback, capacity-fade
    // projection, SRUM drain attribution, charge-rate/stall detection, design-vs-actual runtime,
    // gauge-dropout log, and charge-ceiling/hot-pack-charging awareness. See
    // RefreshBatteryReportLiveStateAsync for the per-tick half of this and LoadBatteryReportAsync/
    // LoadBatteryDrainAttributionAsync for the on-demand (button-gated) half.
    // ================================================================================

    // #647: sticky "this session has seen real battery hardware at least once" flag. The
    // pre-existing Battery-section visibility gate in EnergyThermalsView.xaml used to bind
    // directly to Battery.Count == 0 - fine for "hide entirely on a desktop that never has a
    // battery", but that would also hide this whole panel (including the dropout log below)
    // during the very gauge dropout #647 exists to catch, since Battery.Count genuinely does hit
    // 0 for the duration of a dropout. Latches true the first time either data source reports a
    // battery and never reverts, so a laptop's Energy tab stays visible through a transient
    // dropout while a real desktop (never any battery evidence, ever) still collapses the section
    // exactly as before - same hide-the-section outcome, just backed by a flag robust to the one
    // new failure mode this round explicitly adds detection for.
    private bool _hasBattery;
    public bool HasBattery { get => _hasBattery; private set => SetProperty(ref _hasBattery, value); }

    private double? _batteryChargePercent;
    public double? BatteryChargePercent { get => _batteryChargePercent; private set => SetProperty(ref _batteryChargePercent, value); }

    /// <summary>#648: battery-pack temperature, when a vendor sensor happens to report one (rare -
    /// most laptops don't expose this at all). Battery is already scoped to HardwareType.Battery,
    /// so a Type==Temperature entry there specifically means "this reading is the pack", not the
    /// CPU/motherboard. Null (card hidden) is the common, honest case.</summary>
    private double? _batteryTemperatureC;
    public double? BatteryTemperatureC { get => _batteryTemperatureC; private set => SetProperty(ref _batteryTemperatureC, value); }

    // ---- #641/#643: full battery report (powercfg /batteryreport XML, WMI fallback) ------------
    private BatteryReportInfo? _batteryReport;
    public BatteryReportInfo? BatteryReport { get => _batteryReport; private set => SetProperty(ref _batteryReport, value); }
    public bool HasBatteryReport => BatteryReport is not null;

    private string _batteryReportStatusText = "Not checked yet.";
    public string BatteryReportStatusText { get => _batteryReportStatusText; private set => SetProperty(ref _batteryReportStatusText, value); }

    public AsyncRelayCommand LoadBatteryReportCommand { get; }

    // ---- #642: capacity-fade chart + linear projection to 50% of design capacity ---------------
    public ObservableCollection<ObservablePoint> CapacityHistoryPoints { get; } = new();
    private readonly LineSeries<ObservablePoint> _capacityHistoryGlow;
    private readonly LineSeries<ObservablePoint> _capacityHistoryCore;
    public ObservableCollection<ObservablePoint> CapacityProjectionLine { get; } = new();
    private readonly LineSeries<ObservablePoint> _capacityProjectionSeries;
    public ISeries[] CapacityHistorySeries { get; }
    public Axis[] CapacityHistoryXAxes { get; }
    public Axis[] CapacityHistoryYAxes { get; }
    private DateTime _capacityHistoryOriginDate = DateTime.Now;

    private string _capacityProjectionText = string.Empty;
    public string CapacityProjectionText { get => _capacityProjectionText; private set => SetProperty(ref _capacityProjectionText, value); }

    // ---- #644: SRUM battery-drain-by-process attribution -----------------------------------------
    public ObservableCollection<BatteryDrainAttributionRow> BatteryDrainAttribution { get; } = new();

    private string _batteryDrainAttributionStatusText = "Not scanned yet.";
    public string BatteryDrainAttributionStatusText { get => _batteryDrainAttributionStatusText; private set => SetProperty(ref _batteryDrainAttributionStatusText, value); }

    public AsyncRelayCommand LoadBatteryDrainAttributionCommand { get; }

    // ---- #645: charge-rate / charge-stall detection -----------------------------------------------
    private const double ChargeStallWindowMinutes = 15.0;
    private const double ChargeStallMinDeltaPercent = 1.0;
    private const double ChargeStallMaxPercent = 99.0;
    private const double WeakChargerFraction = 0.6;
    private const double WeakChargerBelowPercent = 90.0;

    private readonly List<(DateTime When, double Percent)> _chargePercentSamples = new();

    private double? _chargeRateSessionMaxW;
    public double? ChargeRateSessionMaxW { get => _chargeRateSessionMaxW; private set => SetProperty(ref _chargeRateSessionMaxW, value); }

    private string _chargeRateVerdictText = string.Empty;
    public string ChargeRateVerdictText { get => _chargeRateVerdictText; private set => SetProperty(ref _chargeRateVerdictText, value); }

    private bool _weakChargerSuspected;
    public bool WeakChargerSuspected { get => _weakChargerSuspected; private set => SetProperty(ref _weakChargerSuspected, value); }

    private bool _chargeStallDetected;
    public bool ChargeStallDetected { get => _chargeStallDetected; private set => SetProperty(ref _chargeStallDetected, value); }

    // ---- #646: design-vs-actual runtime estimate ---------------------------------------------------
    private string _runtimeComparisonText = string.Empty;
    public string RuntimeComparisonText { get => _runtimeComparisonText; private set => SetProperty(ref _runtimeComparisonText, value); }

    // ---- #647: intermittent battery / gauge-dropout log ---------------------------------------------
    private bool? _batteryPresentLastTick;
    public ObservableCollection<BatteryPresenceEvent> BatteryDropoutEvents { get; } = new();

    // ---- #648: charge-threshold (vendor conservation ceiling) + hot-pack charge throttle ------------
    private const double ChargeCeilingWindowMinutes = 20.0;
    private const double ChargeCeilingStableSpreadPercent = 2.0;
    private const double HotBatteryThresholdC = 45.0;
    private static readonly double[] CommonChargeCeilings = { 50, 60, 70, 75, 80, 85, 90 };

    private readonly List<(DateTime When, double Percent, bool OnAcNotCharging)> _chargeCeilingSamples = new();

    private string _chargeCeilingText = string.Empty;
    public string ChargeCeilingText { get => _chargeCeilingText; private set => SetProperty(ref _chargeCeilingText, value); }

    private string _hotChargeThrottleText = string.Empty;
    public string HotChargeThrottleText { get => _hotChargeThrottleText; private set => SetProperty(ref _hotChargeThrottleText, value); }

    // ================================================================================
    // #649-#657: Sleep panel - sleepstudy report + ranked offenders (#649/#650), Modern-Standby-
    // vs-legacy-S3 routing (#651), live power-request blockers (#652), wake-armed device inventory
    // + disable action (#653), wake-history attribution (#654), wake-timer inventory (#655),
    // failed-sleep/vetoed-transition detection (#656), and the overnight standby-drain calculator
    // (#657). #658's hibernation status/toggle has its own small properties block further below.
    // All of the on-demand reads here are real subprocess/event-log calls, gated behind their own
    // buttons only - never the tick timer (CLAUDE.md's on-demand-vs-polled convention; item 652
    // calls this out explicitly for /requests specifically).
    // ================================================================================

    // ---- #649/#650: sleepstudy report + cross-session ranked offenders --------------------------
    public ObservableCollection<SleepStudySession> SleepStudySessions { get; } = new();
    public ObservableCollection<SleepStudyOffender> TopStandbyOffenders { get; } = new();

    private string _sleepStudyStatusText = "Not checked yet.";
    public string SleepStudyStatusText { get => _sleepStudyStatusText; private set => SetProperty(ref _sleepStudyStatusText, value); }

    public AsyncRelayCommand LoadSleepStudyCommand { get; }

    /// <summary>#656: the #650 top-ranked, *repeated* (appeared in 2+ sessions) offender name, kept
    /// so a later vetoed-transition correlation can attach it as a general hint - null until a
    /// sleepstudy report has been loaded at least once this session and found one.</summary>
    private string? _topStandbyOffenderHint;

    // ---- #652: live power-request blocker list ---------------------------------------------------
    public ObservableCollection<PowerRequestEntry> PowerRequests { get; } = new();

    private string _powerRequestsStatusText = "Not checked yet.";
    public string PowerRequestsStatusText { get => _powerRequestsStatusText; private set => SetProperty(ref _powerRequestsStatusText, value); }

    public AsyncRelayCommand LoadPowerRequestsCommand { get; }

    // ---- #653: wake-armed device inventory + disable action ---------------------------------------
    public ObservableCollection<WakeArmedDevice> WakeArmedDevices { get; } = new();

    private string _wakeArmedDevicesStatusText = "Not checked yet.";
    public string WakeArmedDevicesStatusText { get => _wakeArmedDevicesStatusText; private set => SetProperty(ref _wakeArmedDevicesStatusText, value); }

    public AsyncRelayCommand LoadWakeArmedDevicesCommand { get; }
    public AsyncRelayCommand DisableDeviceWakeCommand { get; }

    // ---- #654/#655/#656: wake history, wake sources (timers + wake-enabled tasks), vetoed
    // transitions - all populated together by one "Load wake history" action, since #656's
    // correlation and #657's drain reconciliation both need the same wake-history read anyway. ----
    public ObservableCollection<WakeHistoryEntry> WakeHistory { get; } = new();
    public ObservableCollection<WakeSourceRow> WakeSources { get; } = new();
    public ObservableCollection<SleepTransitionRecord> VetoedSleepTransitions { get; } = new();

    private string _wakeHistoryStatusText = "Not checked yet.";
    public string WakeHistoryStatusText { get => _wakeHistoryStatusText; private set => SetProperty(ref _wakeHistoryStatusText, value); }

    public AsyncRelayCommand LoadWakeHistoryCommand { get; }

    // ---- #657: overnight standby-drain calculator, persisted to standby-drain.json - loaded once
    // at startup (cheap JSON read) so the summary is visible before the user ever clicks anything,
    // then reconciled against fresh wake-history data each time LoadWakeHistoryCommand runs. -------
    public ObservableCollection<StandbyDrainSession> StandbyDrainSessions { get; } = new();

    private string _standbyDrainSummaryText = "Not enough data yet - load wake history at least once after an overnight sleep to start tracking standby drain.";
    public string StandbyDrainSummaryText { get => _standbyDrainSummaryText; private set => SetProperty(ref _standbyDrainSummaryText, value); }

    // ---- #658: hibernation configuration + enable/disable action, plus #659's Fast Startup note
    // (read directly by MainViewModel via HibernationService.ReadFastStartupEnabled - not surfaced
    // as a property here, since #659's footer annotation is independent of this Sleep panel). ------
    private HibernationStatus? _hibernationStatus;
    public HibernationStatus? Hibernation { get => _hibernationStatus; private set => SetProperty(ref _hibernationStatus, value); }

    public AsyncRelayCommand LoadHibernationStatusCommand { get; }
    public AsyncRelayCommand ToggleHibernationCommand { get; }

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
        // #612: last month's fitted fan curve, ghosted (low alpha, no markers) behind this
        // month's scatter cloud above - a curve shifted right (more RPM for the same
        // temperature) is dust or a clogged heatsink. Only ever holds 0 or 2 points (empty until
        // a prior month's fit exists for the currently-tracked fan).
        _fanCurveGhostSeries = new LineSeries<ObservablePoint>
        {
            Values = FanCurveGhostLine,
            Stroke = new SolidColorPaint(SKColors.DeepSkyBlue.WithAlpha(80), 2f),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0, IsHoverable = false, IsVisibleAtLegend = false,
        };
        FanCurveSeries = new ISeries[] { _fanCurveGhostSeries, _fanCurveScatter };

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

        // #660-#664: power-plan-card extensions - every one a real subprocess call, gated behind
        // its own button (the #664 change-history detector is the one exception, folded into the
        // existing tick timer but throttled to PowerPlanCheckInterval - see its remarks).
        RunPowerEfficiencyScanCommand = new AsyncRelayCommand(_ => RunPowerEfficiencyScanAsync());
        LoadPowerPlanDiffCommand = new AsyncRelayCommand(_ => LoadPowerPlanDiffAsync());
        LoadHiddenPowerSettingsCommand = new AsyncRelayCommand(_ => LoadHiddenPowerSettingsAsync());
        FixPassiveCoolingCommand = new AsyncRelayCommand(_ => FixPassiveCoolingAsync());
        PowerPlanChangeHistory.Clear();
        foreach (var e in PowerPlanHistoryService.Load()) PowerPlanChangeHistory.Add(e);

        // #665-#669: USB-card extensions - all on-demand (event-log scans and WMI reads).
        LoadUsbEventsCommand = new AsyncRelayCommand(_ => LoadUsbEventsAsync());
        ToggleUsbDeviceSuspendCommand = new AsyncRelayCommand(ToggleUsbDeviceSuspendAsync);
        DisablePlanUsbSuspendCommand = new AsyncRelayCommand(_ => DisablePlanUsbSuspendAsync());
        LoadFirmwareEventsCommand = new AsyncRelayCommand(_ => LoadFirmwareEventsAsync());
        LoadPsuInfoCommand = new AsyncRelayCommand(_ => LoadPsuInfoAsync());
        SavePsuWattageCommand = new RelayCommand(_ => SavePsuWattage());
        LoadPowerSourceEventsCommand = new AsyncRelayCommand(_ => LoadPowerSourceEventsAsync());

        // #641/#644: both real subprocess calls (powercfg /batteryreport, /srumutil) - gated
        // behind their own explicit buttons only, never auto-run at startup or on the tick timer
        // (unlike the cheap WMI/event-log on-demand reads above, which this app does fire once at
        // startup for a non-empty first paint - see CLAUDE.md's on-demand-vs-polled convention).
        LoadBatteryReportCommand = new AsyncRelayCommand(_ => LoadBatteryReportAsync());
        LoadBatteryDrainAttributionCommand = new AsyncRelayCommand(_ => LoadBatteryDrainAttributionAsync());

        // #649-#658: Sleep panel commands - every one of these is a real subprocess/event-log
        // call, gated behind its own button (see the properties block above).
        LoadSleepStudyCommand = new AsyncRelayCommand(_ => LoadSleepStudyAsync());
        LoadPowerRequestsCommand = new AsyncRelayCommand(_ => LoadPowerRequestsAsync());
        LoadWakeArmedDevicesCommand = new AsyncRelayCommand(_ => LoadWakeArmedDevicesAsync());
        DisableDeviceWakeCommand = new AsyncRelayCommand(DisableDeviceWakeAsync);
        LoadWakeHistoryCommand = new AsyncRelayCommand(_ => LoadWakeHistoryAsync());
        LoadHibernationStatusCommand = new AsyncRelayCommand(_ => LoadHibernationStatusAsync());
        ToggleHibernationCommand = new AsyncRelayCommand(_ => ToggleHibernationAsync());

        // #649/#651: sleep-state support (Modern Standby vs. legacy S3) fired once at startup too
        // (not just behind LoadPowerInfoCommand) - it's a single cheap `powercfg /a` shell-out
        // (same "cheap enough for a one-time startup read" tier as the firmware/PSU/power-source
        // reads elsewhere in this constructor), and #651's sleepstudy-vs-systemsleepdiagnostics
        // routing needs it available before the user ever opens this panel.
        _ = LoadPowerInfoAsync();

        // #657: persisted standby-drain trend loaded once at startup (a plain JSON read) so the
        // summary at the top of the Sleep panel isn't empty until the user clicks anything.
        RefreshStandbyDrainSummary(StandbyDrainService.Load());

        // #658: hibernation status - cheap (one powercfg /a shell-out plus a couple of registry
        // reads), same "fire once at startup too" tier as the sleep-state read just above.
        _ = LoadHibernationStatusAsync();

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

        // #618: cooldown-rate monthly trend, same categorical-month-axis shape as the idle
        // baseline chart above but binned to calendar month instead of calendar day (a cooldown
        // event is comparatively rare, so a daily axis would mostly be empty).
        CooldownXAxes = new[]
        {
            new Axis
            {
                Labels = Array.Empty<string>(),
                LabelsRotation = 0,
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = null,
            },
        };
        CooldownYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v:0}s",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        var cooldownColor = SKColors.MediumTurquoise;
        _cooldownGlow = new LineSeries<double>
        {
            Values = CooldownMonthlyHistory,
            Stroke = new SolidColorPaint(cooldownColor.WithAlpha(70), GlowStrokeWidth),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0.3, IsHoverable = false, IsVisibleAtLegend = false,
        };
        _cooldownCore = new LineSeries<double>
        {
            Values = CooldownMonthlyHistory,
            Stroke = new SolidColorPaint(cooldownColor, CoreStrokeWidth),
            Fill = new LinearGradientPaint(cooldownColor.WithAlpha(90), cooldownColor.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)),
            GeometryStroke = null, GeometryFill = null, LineSmoothness = 0.3,
        };
        CooldownSeries = new ISeries[] { _cooldownGlow, _cooldownCore };

        // #621: rail-sag-under-load scatter (system power W on X, 12V rail volts on Y) with a
        // live least-squares fit line - same scatter+ghosted-fit-line shape as the fan curve chart
        // above, just recomputed live from the rolling window instead of persisted per month.
        RailSagXAxes = new[]
        {
            new Axis
            {
                Name = "System power (W)", NameTextSize = 11,
                Labeler = v => $"{v:0}",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        RailSagYAxes = new[]
        {
            new Axis
            {
                Name = "12V rail (V)", NameTextSize = 11,
                Labeler = v => $"{v:0.###}",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        _railSagScatter = new ScatterSeries<ObservablePoint>
        {
            Values = RailSagPoints,
            Fill = new SolidColorPaint(SKColors.OrangeRed.WithAlpha(140)),
            Stroke = null,
            GeometrySize = 7,
        };
        _railSagFitSeries = new LineSeries<ObservablePoint>
        {
            Values = RailSagFitLine,
            Stroke = new SolidColorPaint(SKColors.OrangeRed, 2f),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0, IsHoverable = false, IsVisibleAtLegend = false,
        };
        RailSagSeries = new ISeries[] { _railSagFitSeries, _railSagScatter };

        // #622: Vcore-vs-package-power scatter - the effective load-line calibration curve.
        VcoreLoadXAxes = new[]
        {
            new Axis
            {
                Name = "Package power (W)", NameTextSize = 11,
                Labeler = v => $"{v:0}",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        VcoreLoadYAxes = new[]
        {
            new Axis
            {
                Name = "Vcore (V)", NameTextSize = 11,
                Labeler = v => $"{v:0.###}",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        _vcoreLoadScatter = new ScatterSeries<ObservablePoint>
        {
            Values = VcoreLoadPoints,
            Fill = new SolidColorPaint(SKColors.MediumPurple.WithAlpha(140)),
            Stroke = null,
            GeometrySize = 7,
        };
        _vcoreLoadFitSeries = new LineSeries<ObservablePoint>
        {
            Values = VcoreLoadFitLine,
            Stroke = new SolidColorPaint(SKColors.MediumPurple, 2f),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0, IsHoverable = false, IsVisibleAtLegend = false,
        };
        VcoreLoadSeries = new ISeries[] { _vcoreLoadFitSeries, _vcoreLoadScatter };

        // #623: VRM temperature history, same glow+core LineOf pattern as every other history
        // chart on this tab.
        VrmTempYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v:0}°C",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        var vrmColor = SKColors.Tomato;
        _vrmTempGlow = new LineSeries<double>
        {
            Values = VrmTempHistory,
            Stroke = new SolidColorPaint(vrmColor.WithAlpha(70), GlowStrokeWidth),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0.3, IsHoverable = false, IsVisibleAtLegend = false,
        };
        _vrmTempCore = new LineSeries<double>
        {
            Values = VrmTempHistory,
            Stroke = new SolidColorPaint(vrmColor, CoreStrokeWidth),
            Fill = new LinearGradientPaint(vrmColor.WithAlpha(90), vrmColor.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)),
            GeometryStroke = null, GeometryFill = null, LineSmoothness = 0.3,
        };
        VrmTempSeries = new ISeries[] { _vrmTempGlow, _vrmTempCore };

        // #642: battery capacity-fade chart - a numeric (not categorical) X axis in days-since-
        // first-report-period, since the projection segment below needs to extend past the real
        // data's own date range. Labeler converts back to a real date via _capacityHistoryOriginDate
        // (updated whenever RefreshCapacityHistoryChart rebuilds the series).
        CapacityHistoryXAxes = new[]
        {
            new Axis
            {
                Labeler = v => _capacityHistoryOriginDate.AddDays(v).ToString("MMM d"),
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        CapacityHistoryYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v:0} Wh",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        var capacityColor = SKColors.LightSeaGreen;
        _capacityHistoryGlow = new LineSeries<ObservablePoint>
        {
            Values = CapacityHistoryPoints,
            Stroke = new SolidColorPaint(capacityColor.WithAlpha(70), GlowStrokeWidth),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0.2, IsHoverable = false, IsVisibleAtLegend = false,
        };
        _capacityHistoryCore = new LineSeries<ObservablePoint>
        {
            Values = CapacityHistoryPoints,
            Stroke = new SolidColorPaint(capacityColor, CoreStrokeWidth),
            Fill = new LinearGradientPaint(capacityColor.WithAlpha(90), capacityColor.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)),
            GeometryStroke = null, GeometryFill = null, LineSmoothness = 0.2,
        };
        // Projection segment - same ghosted/undecorated-line treatment as the fan-curve monthly
        // overlay (#612), just in a contrasting color so "measured" vs. "projected" reads clearly
        // even without relying on a legend (every other chart on this tab hides its legend too).
        _capacityProjectionSeries = new LineSeries<ObservablePoint>
        {
            Values = CapacityProjectionLine,
            Stroke = new SolidColorPaint(SKColors.OrangeRed.WithAlpha(180), 2f),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0, IsHoverable = false, IsVisibleAtLegend = false,
        };
        CapacityHistorySeries = new ISeries[] { _capacityHistoryGlow, _capacityHistoryCore, _capacityProjectionSeries };

        // #604/#605: load persisted throttle-episode history once at startup (kept in memory and
        // updated in place as new episodes close, rather than re-read from disk every tick).
        _persistedEpisodes = ThrottleHistoryService.Load();
        foreach (var ep in _persistedEpisodes.OrderByDescending(e => e.Start).Take(10)) RecentThrottleEpisodes.Add(ep);
        RebuildWeeklyEpisodeSparkline();

        // #609: initial idle-baseline chart populate from whatever history already exists.
        RefreshIdleBaselineChart();

        // #611: cooling-degradation card populated from whatever bucket history already exists.
        RefreshCoolingDegradationCard();

        // #612: fan-curve monthly fits loaded once (kept in memory, updated in place as fits are
        // (re)computed) - same "load once at startup" shape as _persistedEpisodes above.
        _fanCurveFits = FanCurveHistoryService.Load();

        // #614: per-fan historical-RPM ceiling, loaded once at startup.
        _fanHistoricalMaxRpm = FanMaxRpmService.Load().ToDictionary(e => e.Identifier, e => e.HistoricalMaxRpm);

        // #618: initial cooldown-trend chart populate from whatever history already exists.
        RefreshCooldownChart();

        // #602: cheap enough for a one-time startup read (not per-tick) - see LoadFirmwareEventsAsync.
        _ = LoadFirmwareEventsAsync();

        // #624: user-entered PSU wattage (if any) loaded once at startup, plus a best-effort WMI
        // inventory read - the WMI read is a bit more than a trivial registry poke, so it goes
        // through the same "on-demand" AsyncRelayCommand as the power-plan/USB reads above, just
        // also kicked off once here so the card isn't empty until the user clicks the button.
        var psuSettings = PsuSettingsService.Load();
        PsuRatedWattageW = psuSettings.UserRatedWattageW;
        PsuWattageInputText = psuSettings.UserRatedWattageW is { } w ? w.ToString("0") : string.Empty;
        _ = LoadPsuInfoAsync();

        // #626: power-source-change events - same "cheap enough for startup, not per-tick" shape
        // as the firmware-limit events above.
        _ = LoadPowerSourceEventsAsync();

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

        // #664: keep the change-history baseline in sync with whatever this app itself most
        // recently observed/caused (including a user-initiated SetPowerPlanCommand switch, which
        // also calls this method) - only a change this app DIDN'T see coming (the periodic check
        // below finding a different GUID than this one) is worth logging as "my settings keep
        // reverting" material.
        if (plans.FirstOrDefault(p => p.IsActive) is { } active)
        {
            _lastKnownActivePlanGuid = active.Guid;
            _lastKnownActivePlanName = active.Name;
        }
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
    /// StorageViewModel.CheckFragmentationAsync). Also reloads #666's hub inventory and #669's PD
    /// connector readout - both cheap enough WMI work to ride along with the device list rather
    /// than needing their own buttons.</summary>
    private async Task LoadUsbDevicesAsync()
    {
        var devices = await Task.Run(() => UsbPowerService.ReadUsbSelectiveSuspend());
        ApplyUsbReenumerationCounts(devices);
        UsbDevices.Clear();
        foreach (var d in devices) UsbDevices.Add(d);

        var (hubs, _, hubStatus) = await UsbHubPowerService.ReadHubPowerInfoAsync();
        UsbHubs.Clear();
        foreach (var h in hubs) UsbHubs.Add(h);
        UsbHubStatusText = hubStatus;

        var pdConnectors = await UsbPdService.ReadPdConnectorsAsync();
        UsbPdConnectors.Clear();
        foreach (var c in pdConnectors) UsbPdConnectors.Add(c);
        OnPropertyChanged(nameof(HasUsbPdConnectors));
    }

    // #667: cached from the most recent LoadUsbEventsCommand run, so a later LoadUsbDevicesCommand
    // refresh (e.g. after a #668 fix action) keeps showing the same re-enumeration counts instead
    // of resetting them to "not scanned" - null until the event scan has run at least once this
    // session.
    private Dictionary<string, int>? _usbReenumCountsByNormalizedInstance;

    private void ApplyUsbReenumerationCounts(List<UsbDevicePowerInfo> devices)
    {
        if (_usbReenumCountsByNormalizedInstance is not { } counts) return;
        foreach (var d in devices) d.ReenumerationCount = UsbPowerService.FindReenumerationCount(d.DeviceId, counts);
    }

    /// <summary>#665/#667: on-demand USB over-current/port-reset event scan plus per-device
    /// re-enumeration counts, joined onto whatever's currently in UsbDevices - see
    /// UsbEventLogService's remarks for why this is a keyword scan rather than a fixed-EventID
    /// one.</summary>
    private async Task LoadUsbEventsAsync()
    {
        UsbEventsStatusText = "Scanning event logs (last 14 days)...";

        var overCurrentTask = Task.Run(() => UsbEventLogService.ReadOverCurrentEvents());
        var reenumTask = Task.Run(() => UsbEventLogService.ReadReenumerationEvents());
        await Task.WhenAll(overCurrentTask, reenumTask);

        var overCurrent = overCurrentTask.Result;
        UsbOverCurrentEvents.Clear();
        foreach (var e in overCurrent.Take(50)) UsbOverCurrentEvents.Add(e);

        var (_, counts) = reenumTask.Result;
        _usbReenumCountsByNormalizedInstance = counts;

        // UsbDevicePowerInfo isn't INotifyPropertyChanged (see its remarks), so mutating
        // ReenumerationCount on the existing instances wouldn't refresh the bound grid on its own -
        // rebuild the collection's contents in place instead, the same "clear + re-add" refresh
        // every other on-demand list in this app already uses.
        var refreshedDevices = UsbDevices.ToList();
        ApplyUsbReenumerationCounts(refreshedDevices);
        UsbDevices.Clear();
        foreach (var d in refreshedDevices) UsbDevices.Add(d);

        UsbEventsStatusText = overCurrent.Count == 0 && counts.Count == 0
            ? "No over-current, port-reset-failure, or re-enumeration events found in the last 14 days."
            : $"{overCurrent.Count} over-current/port-reset event(s); {counts.Values.Sum()} re-enumeration event(s) across {counts.Count} device instance(s).";
    }

    /// <summary>#668: per-device selective-suspend toggle - flips both device-power-policy
    /// registry values via UsbPowerService.SetSelectiveSuspendEnabled, then reloads the device
    /// list so the grid reflects the actual result rather than assuming success.</summary>
    private async Task ToggleUsbDeviceSuspendAsync(object? param)
    {
        if (param is not UsbDevicePowerInfo device) return;

        bool target = device.SelectiveSuspendEnabled != true; // Unknown or false -> enable; true -> disable
        var (success, error) = await Task.Run(() => UsbPowerService.SetSelectiveSuspendEnabled(device.DeviceId, target));
        UsbFixStatusText = success
            ? $"{device.Name}: selective suspend {(target ? "enabled" : "disabled")}. Unplug/replug (or restart) the device for it to take full effect."
            : $"Couldn't change selective suspend for {device.Name}: {error}";
        if (success) await LoadUsbDevicesAsync();
    }

    /// <summary>#668's plan-level shortcut: disables USB selective suspend for the whole active
    /// power plan on AC via `powercfg /setacvalueindex` on the well-known USB subgroup - the
    /// fastest fix when several devices in a suspend-fragile class are all flagged at once, rather
    /// than toggling each one individually.</summary>
    private async Task DisablePlanUsbSuspendAsync()
    {
        var (success, error) = await PowerPlanService.SetAcValueIndexAsync(
            "SCHEME_CURRENT", PowerPlanService.UsbSubgroupGuid, PowerPlanService.UsbSelectiveSuspendSettingGuid, 0);
        UsbFixStatusText = success
            ? "USB selective suspend disabled for the active power plan (AC)."
            : $"Couldn't change the plan-level USB selective-suspend setting: {error}";
    }

    // ================================================================================
    // #660-#664: power-plan-card extensions
    // ================================================================================

    /// <summary>#660: the 60-second `powercfg /energy` scan - see PowerEfficiencyService's remarks
    /// for why parsing is best-effort. IsPowerEfficiencyScanRunning drives the button's own
    /// "running" label in XAML separately from AsyncRelayCommand's built-in re-entrancy guard,
    /// since an operation this long is worth an explicit in-progress state, not just a disabled
    /// button.</summary>
    private async Task RunPowerEfficiencyScanAsync()
    {
        IsPowerEfficiencyScanRunning = true;
        try
        {
            var progress = new Progress<string>(s => PowerEfficiencyStatusText = s);
            var (findings, status, _) = await PowerEfficiencyService.RunScanAsync(progress);
            PowerEfficiencyFindings.Clear();
            foreach (var f in findings) PowerEfficiencyFindings.Add(f);
            PowerEfficiencyStatusText = status;
        }
        finally
        {
            IsPowerEfficiencyScanRunning = false;
        }
    }

    /// <summary>#661: active-plan-vs-Balanced-defaults setting diff - needs the active scheme's
    /// GUID, loading power info first if LoadPowerInfoCommand hasn't run yet this session.</summary>
    private async Task LoadPowerPlanDiffAsync()
    {
        if (PowerPlans.Count == 0) await LoadPowerInfoAsync();
        string? activeGuid = PowerPlans.FirstOrDefault(p => p.IsActive)?.Guid;
        if (activeGuid is null)
        {
            PowerPlanDiffStatusText = "Couldn't determine the active power plan.";
            return;
        }

        var (diffs, status) = await PowerPlanService.ReadPlanSettingDiffAsync(activeGuid);
        PowerPlanSettingDiffs.Clear();
        foreach (var d in diffs) PowerPlanSettingDiffs.Add(d);
        PowerPlanDiffStatusText = status;
    }

    /// <summary>#662/#663: hidden high-impact AC|DC settings - the passive-cooling flag piggybacks
    /// on this same `/qh` read rather than needing a fourth shell-out just for one setting.</summary>
    private async Task LoadHiddenPowerSettingsAsync()
    {
        var rows = await PowerPlanService.ReadHiddenPowerSettingsAsync();
        HiddenPowerSettings.Clear();
        foreach (var r in rows) HiddenPowerSettings.Add(r);
        HiddenPowerSettingsStatusText = rows.Count == 0 ? "Couldn't read hidden power settings (powercfg /qh unavailable)." : string.Empty;

        var cooling = rows.FirstOrDefault(r => r.SettingName == "System cooling policy");
        PassiveCoolingOnAcDetected = cooling is not null && cooling.AcValueText == "Passive";
    }

    /// <summary>#663: one-click fix for the passive-cooling-on-AC flag above.</summary>
    private async Task FixPassiveCoolingAsync()
    {
        var (success, error) = await PowerPlanService.SetAcValueIndexAsync(
            "SCHEME_CURRENT", PowerPlanService.SubProcessorGuid, PowerPlanService.SystemCoolingPolicyGuid, 0);
        PassiveCoolingFixStatusText = success
            ? "System cooling policy set to Active on AC."
            : $"Couldn't change system cooling policy: {error}";
        if (success) await LoadHiddenPowerSettingsAsync();
    }

    /// <summary>#664: watches for the active power-scheme GUID changing since the last check,
    /// throttled to PowerPlanCheckInterval (see the property block's remarks). The very first
    /// check just establishes/confirms a baseline - LoadPowerInfoAsync already sets one at startup
    /// (and after any switch this app itself initiates), so this only ever logs a change this app
    /// didn't already know about.</summary>
    private async Task CheckPowerPlanChangeIfDueAsync()
    {
        var now = DateTime.Now;
        if (now - _lastPowerPlanCheck < PowerPlanCheckInterval) return;
        _lastPowerPlanCheck = now;

        List<PowerPlanInfo> plans;
        try { plans = await PowerPlanService.ListPowerPlansAsync(); }
        catch { return; }

        var active = plans.FirstOrDefault(p => p.IsActive);
        if (active is null) return;

        if (_lastKnownActivePlanGuid is not null && !string.Equals(_lastKnownActivePlanGuid, active.Guid, StringComparison.OrdinalIgnoreCase))
        {
            var updated = PowerPlanHistoryService.Append(new PowerPlanChangeEvent
            {
                Timestamp = now,
                FromPlanName = _lastKnownActivePlanName,
                ToPlanName = active.Name,
            });
            PowerPlanChangeHistory.Clear();
            foreach (var e in updated) PowerPlanChangeHistory.Add(e);
        }

        _lastKnownActivePlanGuid = active.Guid;
        _lastKnownActivePlanName = active.Name;
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
        CooldownXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        CooldownYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        CooldownYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        RailSagXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        RailSagXAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        RailSagYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        RailSagYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        VcoreLoadXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        VcoreLoadXAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        VcoreLoadYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        VcoreLoadYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        VrmTempYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        VrmTempYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        CapacityHistoryXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        CapacityHistoryXAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        CapacityHistoryYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        CapacityHistoryYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
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

        // #614/#615/#616: session-max/historical-max tracking, per-fan OK/Slow/Stopped/Not-
        // reporting status, and pump-channel tagging - see EnrichFanReadings' remarks. Computed
        // from the raw readings (fanReadings) so #615's sibling comparison and #613's hunting
        // detector below both see the same un-enriched RPM values, then the *enriched* copies
        // (carrying FanStatus/HistoricalMaxRpm/StepLossDetected/IsPumpChannel) are what the UI
        // list (Fans) actually binds to.
        var fanReadings = readings.Where(r => r.Type == SensorType.Fan && r.Value.HasValue).ToList();
        var enrichedFanReadings = EnrichFanReadings(fanReadings);
        Replace(Fans, enrichedFanReadings);

        // #616: coolant-pump variance card - rebuilt from the same enriched readings.
        RefreshCoolantPumps(enrichedFanReadings);
        // #96/#620: out-of-spec voltage-rail flagging, deviation percent, and per-hour excursion
        // count - see WithVoltageSpecCheck/TrackVoltageExcursions' remarks.
        var voltageReadingsList = readings.Where(r => r.Type == SensorType.Voltage && HasNonZeroReading(r)).Select(WithVoltageSpecCheck).ToList();
        voltageReadingsList = TrackVoltageExcursions(voltageReadingsList);
        Replace(Voltages, voltageReadingsList);
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

        // #645-#648: the live half of the battery report panel, built on top of the Battery
        // sensor collection and BatteryDrainRateW/BatteryIsCharging above.
        await RefreshBatteryReportLiveStateAsync();

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

        // #611: bucket this tick's (load, power) into a decile pair and accumulate it - flushed
        // to cooling-baseline.json every few minutes rather than every tick (a JSON read-modify-
        // write on every tick would be needless I/O for data that only matters as a monthly
        // trend). See TrackCoolingBaseline's remarks.
        TrackCoolingBaseline(CpuPackageTempC, TotalPackagePowerW);

        // #81: Motherboard hardware tree only, so a GPU/drive sensor also named "System" or
        // "VRM" can't collide with this lookup - see IsGpu's remarks below for the same concern
        // on the CPU-vs-GPU temperature lookups.
        var mbTemps = tempReadings.Where(r => r.HardwareType == HardwareType.Motherboard).ToList();
        MotherboardTempC = FindByNameContains(mbTemps, "VRM", "System", "Motherboard", "PCH", "Chipset");

        // #623: VRM-specific temperature - a narrower hint list than MotherboardTempC's above (no
        // "System"/"PCH"/"Chipset" fallback), so this is null (and the tile/history hidden) unless
        // a sensor is actually named for the VRM/MOSFETs, never the generic board temperature.
        VrmTempC = FindByNameContains(mbTemps, VrmNameHints);
        VrmHotWhilePackageNotFlag = VrmTempC is { } vrmTemp && vrmTemp >= VrmHotThresholdC &&
            (CpuPackageTempC is null || CpuPackageTempC < VrmHotPackageNotHotThresholdC);

        // #29: GPU hotspot/junction vs. edge/core temperature differential - restricted to GPU
        // hardware entries specifically (unlike the CPU lookups above) since sensor names like
        // "Core" collide with per-core CPU temperature readings otherwise.
        var gpuTemps = tempReadings.Where(r => IsGpu(r.HardwareType)).ToList();
        var gpuEdge = FindByNameContains(gpuTemps, "GPU Core", "Edge", "Core");
        // #675: excludes any sensor named for the memory junction specifically - otherwise its
        // "Junction" hint below would collide with GpuMemoryJunctionTempC's own lookup and get
        // double-counted as both the die hotspot and the memory junction.
        var gpuHotspotCandidates = gpuTemps.Where(r => !r.SensorName.Contains("Memory", StringComparison.OrdinalIgnoreCase)).ToList();
        var gpuHotspot = FindByNameContains(gpuHotspotCandidates, "Hot Spot", "Junction");
        GpuHotspotDeltaC = gpuEdge.HasValue && gpuHotspot.HasValue && gpuHotspot > gpuEdge
            ? gpuHotspot - gpuEdge : null;
        GpuTempC = gpuEdge;

        // #675: memory-junction temperature - GDDR6X-only, so null (tile hidden) is the expected
        // common case, not a bug. Kept as its own lookup rather than folded into the hotspot hint
        // list above so a "Junction" sensor name can't be double-counted as both the GPU die
        // hotspot and the memory junction.
        GpuMemoryJunctionTempC = FindByNameContains(gpuTemps, "GPU Memory Junction", "Memory Junction");

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

        // #621/#625/#638: actual GPU power draw, distinct from the configured limit above -
        // excludes any sensor whose name itself says "Limit"/"TDP" so this can't accidentally
        // resolve to the same reading as GpuPowerLimitW.
        var gpuDrawCandidates = gpuWattages.Where(r =>
            !r.SensorName.Contains("Limit", StringComparison.OrdinalIgnoreCase) &&
            !r.SensorName.Contains("TDP", StringComparison.OrdinalIgnoreCase)).ToList();
        GpuPowerDrawW = FindByNameContains(gpuDrawCandidates, "GPU Power", "Power");

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

        // #623: VRM temperature history, same rolling-window shape as every other history chart.
        if (VrmTempC is { } vrmTempHist)
        {
            VrmTempHistory.Add(vrmTempHist);
            if (VrmTempHistory.Count > HistoryLength) VrmTempHistory.RemoveAt(0);
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

            // #612: fan efficiency index + monthly regression accumulation - see
            // TrackFanEfficiencyAndCurveRegression's remarks.
            TrackFanEfficiencyAndCurveRegression(primaryFan.Identifier, fanCurveTemp, primaryFan.Value!.Value);
        }

        // #95: same primary fan as the scatter above, plotted as a plain time series instead -
        // makes hunting/oscillation (RPM repeatedly ramping at a near-constant temperature)
        // directly visible in a way the scatter cloud doesn't.
        if (primaryFan is not null)
        {
            FanRpmHistory.Add(primaryFan.Value!.Value);
            if (FanRpmHistory.Count > HistoryLength) FanRpmHistory.RemoveAt(0);
        }

        // #613: fan stall/hunting detector - distinct from DeadFanDetected's exactly-0-RPM check
        // above (oscillation or sub-minimum dwell, not a stopped fan).
        DetectFanHunting(primaryFan?.SensorName ?? string.Empty);

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

        // #617: heat-soak detection (burst-vs-steady-state temperature under one sustained load).
        TrackHeatSoak(CpuPackageTempC);

        // #618: cooldown-rate measurement (seconds to fall 20°C from peak once load drops).
        TrackCooldown(CpuPackageTempC);

        // #609/#619: idle-temperature baseline drift, plus the same idle window's ambient-proxy
        // reading (lowest motherboard "System"/drive temperature) for the normalize-to-ambient
        // toggle.
        TrackIdleBaseline(CpuPackageTempC, tempReadings);

        // #621/#622: rail-sag-under-load correlation + Vcore droop/load-line sampling.
        TrackPowerDelivery(voltageReadingsList, TotalPackagePowerW, GpuPowerDrawW);

        // #624: PSU wattage sanity check - estimated draw vs. rated wattage, with a sustained-
        // above-80% brownout-risk flag.
        TrackPsuLoad(TotalPackagePowerW, GpuPowerDrawW);

        // #632: AMD PPT/TDC/EDC limit approximation - a no-op on non-AMD silicon.
        var currentReadings = readings.Where(r => r.Type == SensorType.Current && HasNonZeroReading(r)).ToList();
        TrackAmdLimitApproximation(currentReadings, TotalPackagePowerW);

        // #625/#638: coarse power-history log append, at most once a minute - see
        // PowerHistoryLogService's remarks for why this is a periodic append rather than a
        // per-tick write.
        AppendPowerHistorySampleIfDue(CpuPackageTempC, TotalPackagePowerW, GpuPowerDrawW, BatteryChargePercent);

        // #664: active-scheme-change watch, throttled to PowerPlanCheckInterval internally - see
        // its remarks for why this is safe to call every tick.
        await CheckPowerPlanChangeIfDueAsync();
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
    /// records that window's median CPU package temperature as today's baseline entry. #619:
    /// also records that same moment's ambient-temperature proxy (lowest motherboard "System"/
    /// drive reading) alongside it, and updates the live AmbientProxyC readout.</summary>
    private void TrackIdleBaseline(double? cpuTemp, List<SensorReading> tempReadings)
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

        // #619: lowest of the motherboard "System" sensor / any drive temperature at this
        // idle-window close - the sensors that track case/room temperature most closely (unlike
        // a VRM or CPU reading, which run hot even at idle). Null (feature stays hidden) when
        // neither kind of sensor is reporting.
        var ambientCandidates = tempReadings
            .Where(r => r.Value.HasValue && (
                (r.HardwareType == HardwareType.Motherboard && r.SensorName.Contains("System", StringComparison.OrdinalIgnoreCase)) ||
                r.HardwareType == HardwareType.Storage))
            .Select(r => (double)r.Value!.Value)
            .ToList();
        double? ambient = ambientCandidates.Count > 0 ? ambientCandidates.Min() : null;
        AmbientProxyC = ambient;

        ThermalBaselineService.RecordToday(median, ambient);
        RefreshIdleBaselineChart();

        // Reset so a long idle stretch records one entry per 60s window, not a continuous
        // re-trigger every tick past the first qualifying window.
        _idleWindowStartedAt = DateTime.Now;
        _idleTempSamples.Clear();
    }

    /// <summary>#619: when NormalizeToAmbient is on, plots MedianIdleTempC minus that day's
    /// AmbientProxyC instead of the raw reading - falls back to the raw reading for any day
    /// recorded before this round (or on a day with no ambient sensor available), rather than
    /// dropping the point or fabricating an ambient value for it.</summary>
    private void RefreshIdleBaselineChart()
    {
        var entries = ThermalBaselineService.Load();
        IdleBaselineHistory.Clear();
        foreach (var e in entries)
        {
            double value = NormalizeToAmbient && e.AmbientProxyC is { } ambient ? e.MedianIdleTempC - ambient : e.MedianIdleTempC;
            IdleBaselineHistory.Add(value);
        }

        int labelStep = entries.Count <= 6 ? 1 : Math.Max(1, entries.Count / 6);
        IdleBaselineXAxes[0].Labels = entries
            .Select((e, i) => i == 0 || i == entries.Count - 1 || i % labelStep == 0 ? e.Date.ToString("M/d/yy") : string.Empty)
            .ToArray();

        IdleBaselineYAxes[0].Name = NormalizeToAmbient ? "°C above ambient" : string.Empty;
        IdleBaselineYAxes[0].NameTextSize = 11;
        IdleBaselineYAxes[0].Labeler = NormalizeToAmbient ? (v => $"{v:+0.#;-0.#;0}°C") : (v => $"{v:0.#}°C");
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

    // ================================================================================
    // #611: cooling-degradation (same-load-hotter-over-months) tracking
    // ================================================================================

    /// <summary>Buckets this tick's (load, power) pair into a decile pair and accumulates the
    /// temperature sample in memory, flushing to disk (median-reduced) every
    /// <see cref="CoolingFlushInterval"/> instead of every tick.</summary>
    private void TrackCoolingBaseline(double? cpuTemp, double? powerW)
    {
        if (cpuTemp is { } temp && powerW.HasValue && PowerSessionMaxW is { } sessionMax && sessionMax > 0)
        {
            int loadDecile = Math.Clamp((int)(_performance.CpuCurrentPercent / 10.0), 0, 9);
            int powerDecile = Math.Clamp((int)(powerW.Value / sessionMax * 10.0), 0, 9);
            var key = (loadDecile, powerDecile);
            if (!_coolingBucketSamples.TryGetValue(key, out var list))
            {
                list = new List<double>();
                _coolingBucketSamples[key] = list;
            }
            list.Add(temp);
        }

        if (DateTime.Now - _lastCoolingFlush < CoolingFlushInterval) return;
        FlushCoolingBuckets();
        _lastCoolingFlush = DateTime.Now;
    }

    /// <summary>Median-reduces every touched bucket's accumulated samples and merges each into
    /// cooling-baseline.json, then recomputes the degradation card. Also called from
    /// <see cref="Dispose"/> so a partial period isn't silently dropped when the app closes.</summary>
    private void FlushCoolingBuckets()
    {
        if (_coolingBucketSamples.Count == 0) return;

        var now = DateTime.Now;
        foreach (var (key, samples) in _coolingBucketSamples)
        {
            if (samples.Count == 0) continue;
            var sorted = samples.OrderBy(v => v).ToList();
            double median = sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
            CoolingBaselineService.RecordBatch(key.Load, key.Power, now, median, sorted.Count);
        }
        _coolingBucketSamples.Clear();
        RefreshCoolingDegradationCard();
    }

    /// <summary>Rebuilds the "Cooling degradation" card: for every bucket with data spanning at
    /// least two distinct calendar months, the delta between the most recently recorded month and
    /// the earliest recorded month for that *same* bucket - comparing like-for-like workload
    /// removes "the machine's just been working harder lately" as a confound. Shows the 5 buckets
    /// with the largest degradation (temperature risen the most), smallest first excluded.</summary>
    private void RefreshCoolingDegradationCard()
    {
        var entries = CoolingBaselineService.Load();
        var rows = new List<CoolingDegradationRow>();

        foreach (var bucketGroup in entries.GroupBy(e => (e.LoadDecile, e.PowerDecile)))
        {
            var byMonth = bucketGroup.OrderBy(e => e.Year).ThenBy(e => e.Month).ToList();
            if (byMonth.Count < 2) continue;

            var earliest = byMonth[0];
            var latest = byMonth[^1];
            if (earliest.Year == latest.Year && earliest.Month == latest.Month) continue;

            double delta = latest.MedianTempC - earliest.MedianTempC;
            var earliestDate = new DateTime(earliest.Year, earliest.Month, 1);
            rows.Add(new CoolingDegradationRow
            {
                BucketText = $"Load {bucketGroup.Key.LoadDecile * 10}–{bucketGroup.Key.LoadDecile * 10 + 10}% · Power {bucketGroup.Key.PowerDecile * 10}–{bucketGroup.Key.PowerDecile * 10 + 10}%",
                DeltaC = delta,
                ComparisonText = $"since {earliestDate:MMMM yyyy}",
            });
        }

        CoolingDegradationRows.Clear();
        foreach (var row in rows.OrderByDescending(r => r.DeltaC).Take(5)) CoolingDegradationRows.Add(row);
    }

    // ================================================================================
    // #612: fan efficiency index + monthly fan-curve regression overlay
    // ================================================================================

    /// <summary>Updates the live fan-efficiency-index readout and accumulates this tick's
    /// (temp, RPM) sample into the current calendar month's reservoir, flushing a least-squares
    /// fit whenever the tracked month or primary fan changes.</summary>
    private void TrackFanEfficiencyAndCurveRegression(string fanIdentifier, double tempC, double rpm)
    {
        // Cooling delivered per RPM: package power divided by temperature-above-ambient,
        // normalized by RPM. An arbitrary-but-consistent unit (not a standardized metric, so it's
        // only meaningful as a trend on this one machine), scaled x1000 for a readable magnitude.
        // Hidden (null) until an ambient proxy (#619) has been recorded at least once this
        // session.
        FanEfficiencyIndex = AmbientProxyC is { } ambient && TotalPackagePowerW is { } power && tempC > ambient && rpm > 0
            ? power / (tempC - ambient) / rpm * 1000.0
            : null;

        var now = DateTime.Now;
        if (_fanCurveMonthYear != now.Year || _fanCurveMonthMonth != now.Month || _fanCurveMonthFanIdentifier != fanIdentifier)
        {
            // Month rolled over (or the primary fan changed) - flush whatever was accumulated
            // before starting a fresh accumulation.
            FlushFanCurveMonth();
            _fanCurveMonthYear = now.Year;
            _fanCurveMonthMonth = now.Month;
            _fanCurveMonthFanIdentifier = fanIdentifier;
            _fanCurveMonthSamples.Clear();
            _fanCurveMonthSampleCount = 0;
        }

        // Reservoir sampling so a full month of ticks (tens of thousands of samples at the
        // default poll interval) doesn't grow this list unbounded in memory.
        _fanCurveMonthSampleCount++;
        if (_fanCurveMonthSamples.Count < FanCurveMonthlyReservoirSize)
        {
            _fanCurveMonthSamples.Add((tempC, rpm));
        }
        else
        {
            long replaceIndex = _fanCurveReservoirRandom.NextInt64(_fanCurveMonthSampleCount);
            if (replaceIndex < FanCurveMonthlyReservoirSize) _fanCurveMonthSamples[(int)replaceIndex] = (tempC, rpm);
        }

        RefreshFanCurveGhostLine(fanIdentifier);
    }

    /// <summary>Least-squares-fits the current month's accumulated (temp, RPM) reservoir and
    /// persists it, provided there are enough samples for a meaningful fit. Called on month
    /// rollover, on primary-fan change, and from <see cref="Dispose"/> for a partial final month.</summary>
    private void FlushFanCurveMonth()
    {
        if (_fanCurveMonthSamples.Count < 10 || string.IsNullOrEmpty(_fanCurveMonthFanIdentifier) || _fanCurveMonthYear < 0) return;

        var (slope, intercept) = LinearRegression(_fanCurveMonthSamples);
        var fit = new FanCurveMonthlyFit
        {
            FanIdentifier = _fanCurveMonthFanIdentifier,
            Year = _fanCurveMonthYear,
            Month = _fanCurveMonthMonth,
            Slope = slope,
            Intercept = intercept,
            SampleCount = _fanCurveMonthSamples.Count,
            MinTempC = _fanCurveMonthSamples.Min(s => s.Temp),
            MaxTempC = _fanCurveMonthSamples.Max(s => s.Temp),
        };

        FanCurveHistoryService.UpsertFit(fit);
        _fanCurveFits.RemoveAll(f => f.FanIdentifier == fit.FanIdentifier && f.Year == fit.Year && f.Month == fit.Month);
        _fanCurveFits.Add(fit);
    }

    /// <summary>Ordinary least-squares fit of Rpm = Slope * Temp + Intercept.</summary>
    private static (double Slope, double Intercept) LinearRegression(IReadOnlyList<(double Temp, double Rpm)> points)
    {
        int n = points.Count;
        double sumX = points.Sum(p => p.Temp);
        double sumY = points.Sum(p => p.Rpm);
        double sumXY = points.Sum(p => p.Temp * p.Rpm);
        double sumXX = points.Sum(p => p.Temp * p.Temp);

        double denominator = (n * sumXX) - (sumX * sumX);
        if (Math.Abs(denominator) < 1e-9) return (0, sumY / n);

        double slope = ((n * sumXY) - (sumX * sumY)) / denominator;
        double intercept = (sumY - (slope * sumX)) / n;
        return (slope, intercept);
    }

    /// <summary>Rebuilds the ghosted "last month" fan-curve overlay line from whatever prior
    /// month's persisted fit exists for the given fan - two points spanning the fit's own
    /// recorded temperature domain, not extrapolated across the live chart's current range.
    /// Cleared (line hidden) when no prior month's fit is available yet.</summary>
    private void RefreshFanCurveGhostLine(string fanIdentifier)
    {
        var prior = FanCurveHistoryService.FindPriorFit(_fanCurveFits, fanIdentifier, DateTime.Now.Year, DateTime.Now.Month);
        FanCurveGhostLine.Clear();
        if (prior is null || prior.MaxTempC <= prior.MinTempC) return;

        FanCurveGhostLine.Add(new ObservablePoint(prior.MinTempC, (prior.Slope * prior.MinTempC) + prior.Intercept));
        FanCurveGhostLine.Add(new ObservablePoint(prior.MaxTempC, (prior.Slope * prior.MaxTempC) + prior.Intercept));
    }

    // ================================================================================
    // #613: fan stall / hunting detector
    // ================================================================================

    /// <summary>Scans the existing FanRpmHistory/CpuTempHistory windows for oscillation (repeated
    /// large RPM swings while temperature stays near-constant) or sub-minimum-RPM dwell under
    /// load - a failing bearing or a badly-tuned firmware fan curve, distinct from
    /// DeadFanDetected's exactly-0-RPM check. "Quick flag, not a verdict" - same tier as this
    /// app's other pattern-matched heuristics.</summary>
    private void DetectFanHunting(string fanName)
    {
        var rpmWindow = FanRpmHistory.Skip(Math.Max(0, FanRpmHistory.Count - 30)).ToList();
        var tempWindow = CpuTempHistory.Skip(Math.Max(0, CpuTempHistory.Count - 30)).ToList();
        if (rpmWindow.Count < 10 || rpmWindow.All(v => v == 0))
        {
            FanHuntingDetected = false;
            FanHuntingReason = string.Empty;
            return;
        }

        double meanRpm = rpmWindow.Average();

        // Oscillation: count sign reversals in the RPM delta series where the swing is at least
        // ~20% of the window's mean RPM, while the paired temperature window stays near-constant
        // (a low standard deviation) - a fan legitimately ramping with a rising/falling
        // temperature shouldn't be flagged as hunting.
        int reversals = 0;
        double lastNonZeroDelta = 0;
        for (int i = 1; i < rpmWindow.Count; i++)
        {
            double delta = rpmWindow[i] - rpmWindow[i - 1];
            if (Math.Abs(delta) < meanRpm * 0.2) continue;
            if (lastNonZeroDelta != 0 && Math.Sign(delta) != Math.Sign(lastNonZeroDelta)) reversals++;
            lastNonZeroDelta = delta;
        }

        double tempMean = tempWindow.Count > 0 ? tempWindow.Average() : 0;
        double tempVariance = tempWindow.Count > 0 ? tempWindow.Average(t => (t - tempMean) * (t - tempMean)) : 0;
        double tempStdDev = Math.Sqrt(tempVariance);
        bool oscillating = reversals >= 4 && tempStdDev < 3.0 && meanRpm > 0;

        // Sub-minimum dwell: RPM sitting near-zero-but-not-quite (so DeadFanDetected's exact-0
        // check doesn't already cover it) for a sustained stretch while the system is clearly
        // under load - a fan struggling to spin up, not just idle/passive cooling.
        bool anyHot = tempWindow.Count > 0 && tempWindow[^1] >= DeadFanTempThresholdC;
        int lowDwell = 0;
        for (int i = rpmWindow.Count - 1; i >= 0; i--)
        {
            if (rpmWindow[i] > 0 && rpmWindow[i] < meanRpm * 0.15) lowDwell++;
            else break;
        }
        bool dwelling = anyHot && lowDwell >= 15;

        FanHuntingDetected = oscillating || dwelling;
        string name = string.IsNullOrEmpty(fanName) ? "Fan" : fanName;
        FanHuntingReason = oscillating
            ? $"{name} RPM is oscillating (±20%+ swings) at a near-constant temperature - possible bearing wear or a badly-tuned fan curve."
            : dwelling
                ? $"{name} has been idling well below its typical RPM while the system runs warm."
                : string.Empty;
    }

    // ================================================================================
    // #614/#615/#616: per-fan status, RPM step-loss, and coolant-pump variance
    // ================================================================================

    /// <summary>Builds the enriched copies of this tick's fan readings that the Fans list binds
    /// to: session min/max RPM, the historical-max-RPM step-loss check (#614), a per-fan
    /// OK/Slow/Stopped/Not-reporting status computed against sibling channels (#615), and
    /// pump-channel tagging (#616, which also excludes pump channels from the sibling-RPM "Slow"
    /// comparison - a pump's absolute RPM isn't comparable to a case fan's).</summary>
    private List<SensorReading> EnrichFanReadings(List<SensorReading> fanReadings)
    {
        var caseFanValues = fanReadings.Where(r => !IsPumpChannel(r) && r.Value is float v && v > 0).Select(r => (double)r.Value!.Value).ToList();
        double maxCaseFanRpm = caseFanValues.Count > 0 ? caseFanValues.Max() : 0;

        var result = new List<SensorReading>(fanReadings.Count);
        foreach (var reading in fanReadings)
        {
            bool isPump = IsPumpChannel(reading);
            float? value = reading.Value;

            // #614: session max, and the persisted historical-max ceiling comparison.
            double sessionMax = value.HasValue
                ? (_fanSessionMaxRpm.TryGetValue(reading.Identifier, out var sm) ? Math.Max(sm, value.Value) : value.Value)
                : (_fanSessionMaxRpm.TryGetValue(reading.Identifier, out var sm2) ? sm2 : 0);
            if (value.HasValue) _fanSessionMaxRpm[reading.Identifier] = sessionMax;

            double? historicalMax = _fanHistoricalMaxRpm.TryGetValue(reading.Identifier, out var hm) ? hm : null;
            if (sessionMax > 0 && (historicalMax is null || sessionMax > historicalMax.Value))
            {
                _fanHistoricalMaxRpm[reading.Identifier] = sessionMax;
                FanMaxRpmService.RecordMax(reading.Identifier, sessionMax);
                historicalMax = sessionMax;
            }
            bool? stepLoss = historicalMax is { } h && h >= 300 && sessionMax > 0 && sessionMax <= h * 0.8;

            // #615: per-fan status - pump channels skip the sibling-RPM "Slow" comparison.
            string status;
            if (!value.HasValue) status = "Not reporting";
            else if (value.Value == 0f) status = "Stopped";
            else if (!isPump && maxCaseFanRpm > 300 && value.Value < maxCaseFanRpm * 0.4) status = "Slow";
            else status = "OK";

            result.Add(new SensorReading
            {
                HardwareName = reading.HardwareName,
                HardwareType = reading.HardwareType,
                SensorName = reading.SensorName,
                Type = reading.Type,
                Value = reading.Value,
                Identifier = reading.Identifier,
                SessionMin = reading.SessionMin,
                SessionMax = (float)sessionMax,
                IsVoltageOutOfSpec = reading.IsVoltageOutOfSpec,
                FanStatus = status,
                HistoricalMaxRpm = historicalMax.HasValue ? (float)historicalMax.Value : null,
                StepLossDetected = stepLoss,
                IsPumpChannel = isPump,
            });
        }
        return result;
    }

    private static bool IsPumpChannel(SensorReading reading)
        => PumpNameRegex.IsMatch(reading.SensorName) || PumpNameRegex.IsMatch(reading.HardwareName);

    /// <summary>#616: rebuilds the "Coolant pump" card from whichever fan channels matched the
    /// pump-name hints - a rolling RPM window per pump identifier, flagged "Variable" once its
    /// coefficient of variation crosses ~8% (a pump should hold a near-constant RPM, unlike a
    /// case fan). Hidden entirely (empty collection) when no pump channel is reporting.</summary>
    private void RefreshCoolantPumps(List<SensorReading> enrichedFanReadings)
    {
        var pumps = enrichedFanReadings.Where(r => r.IsPumpChannel && r.Value.HasValue).ToList();
        var seenIdentifiers = new HashSet<string>();
        var rows = new List<PumpStatus>();

        foreach (var pump in pumps)
        {
            seenIdentifiers.Add(pump.Identifier);
            if (!_pumpRpmWindows.TryGetValue(pump.Identifier, out var window))
            {
                window = new List<double>();
                _pumpRpmWindows[pump.Identifier] = window;
            }
            window.Add(pump.Value!.Value);
            while (window.Count > PumpRpmWindowSize) window.RemoveAt(0);

            double mean = window.Average();
            double variance = window.Average(v => (v - mean) * (v - mean));
            double stdDev = Math.Sqrt(variance);
            double cv = mean > 0 ? stdDev / mean : 0;

            rows.Add(new PumpStatus
            {
                Name = $"{pump.HardwareName} {pump.SensorName}".Trim(),
                CurrentRpm = pump.Value!.Value,
                MeanRpm = mean,
                StdDevRpm = stdDev,
                CoefficientOfVariation = cv,
                IsVariable = window.Count >= 5 && cv >= 0.08,
            });
        }

        // Drop rolling windows for pump channels no longer reporting, so a since-removed/renamed
        // sensor doesn't leak memory indefinitely.
        foreach (var staleKey in _pumpRpmWindows.Keys.Where(k => !seenIdentifiers.Contains(k)).ToList())
            _pumpRpmWindows.Remove(staleKey);

        CoolantPumps.Clear();
        foreach (var row in rows) CoolantPumps.Add(row);
    }

    // ================================================================================
    // #617/#618: heat-soak detection and cooldown-rate measurement
    // ================================================================================

    /// <summary>#617: compares CPU package temperature 30s into a sustained load (CPU &gt; 80%
    /// for &gt; 15s, the same sustained-load stopwatch #605 already tracks) against the
    /// temperature 10 minutes into that same load. A large late rise at flat power draw is heat
    /// soak - an undersized cooler or a hot case - which needs a different fix than a high peak.</summary>
    private void TrackHeatSoak(double? cpuTemp)
    {
        if (_sustainedLoadStartedAt is not { } start || cpuTemp is not { } temp)
        {
            // Load ended (or no temp reading) before a full 10-minute soak measurement completed -
            // discard the partial capture rather than comparing mismatched load periods.
            if (_sustainedLoadStartedAt is null) { _burstTempC = null; _soakedTempC = null; }
            return;
        }

        double elapsed = (DateTime.Now - start).TotalSeconds;
        if (_burstTempC is null && elapsed >= 30) _burstTempC = temp;

        if (_soakedTempC is null && _burstTempC is { } burst && elapsed >= 600)
        {
            _soakedTempC = temp;
            double delta = temp - burst;
            HeatSoakText = $"Burst {burst:0}°C → soaked {temp:0}°C ({delta:+0.#;-0.#;0}°C over 10 min of sustained load)";
        }
    }

    /// <summary>#618: when load drops from &gt;80% to &lt;10%, measures the seconds needed for
    /// CPU package temperature to fall from its load-period peak to peak-minus-20°C. Cooldown
    /// slope is largely workload-independent, so a slowing trend degrades measurably as thermal
    /// paste dries out - recorded per event to cooldown-history.json and charted as a monthly
    /// average.</summary>
    private void TrackCooldown(double? cpuTemp)
    {
        if (cpuTemp is not { } temp) return;
        double load = _performance.CpuCurrentPercent;

        if (load > 80)
        {
            _loadPeakTempC = Math.Max(_loadPeakTempC, temp);
            _wasUnderHeavyLoad = true;
            _cooldownStartedAt = null;
            _cooldownPeakTempC = null;
            return;
        }

        if (load < 10 && _wasUnderHeavyLoad)
        {
            _cooldownStartedAt ??= DateTime.Now;
            _cooldownPeakTempC ??= _loadPeakTempC;

            if (_cooldownPeakTempC is { } peak && temp <= peak - 20)
            {
                double seconds = (DateTime.Now - _cooldownStartedAt.Value).TotalSeconds;
                CooldownText = $"Cooldown: {FormatDuration(seconds)} to drop 20°C from {peak:0}°C";
                CooldownHistoryService.Append(new CooldownEvent { RecordedAt = DateTime.Now, PeakTempC = peak, CooldownSeconds = seconds });
                RefreshCooldownChart();

                _wasUnderHeavyLoad = false;
                _cooldownStartedAt = null;
                _cooldownPeakTempC = null;
                _loadPeakTempC = 0;
            }
            return;
        }

        // Load rebounded into the 10-80% middle band without completing a clean cooldown
        // measurement (or without ever having been under heavy load) - abandon any in-progress
        // measurement rather than mixing a partial cooldown with a fresh load ramp.
        _cooldownStartedAt = null;
        _cooldownPeakTempC = null;
    }

    /// <summary>#618: monthly average cooldown seconds, last <see cref="CooldownTrendMonths"/>
    /// months, zero-filled for months with no completed cooldown event.</summary>
    private void RefreshCooldownChart()
    {
        var events = CooldownHistoryService.Load();
        var today = DateTime.Now.Date;
        var values = new double[CooldownTrendMonths];
        var labels = new string[CooldownTrendMonths];

        for (int i = 0; i < CooldownTrendMonths; i++)
        {
            var monthDate = new DateTime(today.Year, today.Month, 1).AddMonths(i - (CooldownTrendMonths - 1));
            labels[i] = monthDate.ToString("MMM yy");
            var monthEvents = events.Where(e => e.RecordedAt.Year == monthDate.Year && e.RecordedAt.Month == monthDate.Month).ToList();
            values[i] = monthEvents.Count > 0 ? monthEvents.Average(e => e.CooldownSeconds) : 0;
        }

        for (int i = 0; i < CooldownTrendMonths && i < CooldownMonthlyHistory.Count; i++)
            CooldownMonthlyHistory[i] = values[i];
        CooldownXAxes[0].Labels = labels;
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

            // #620: signed deviation percent (negative = low, positive = high) alongside the
            // existing flat out-of-spec bool - lets the view show "6% low" instead of just a
            // color change.
            float deviationPercent = (value - nominal) / nominal * 100f;
            bool outOfSpec = Math.Abs(deviationPercent) > 5f;
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
                VoltageDeviationPercent = deviationPercent,
            };
        }

        return reading; // unrecognized rail name - IsVoltageOutOfSpec stays null, not flagged
    }

    /// <summary>#620: edge-triggered excursion counting - a rail transitioning from in-spec to
    /// out-of-spec counts as one excursion, timestamped and kept in a rolling
    /// <see cref="RailExcursionWindow"/> window per rail identifier, so a single startup glitch
    /// reads as "1 excursion/hr" rather than the same as being continuously out of spec for that
    /// whole hour. Stamps the final RailVerdictText shown in the UI onto every recognized-and-
    /// unrecognized reading alike (unrecognized rails just keep showing their hardware name, the
    /// same text VfdMeter's SubText showed before this round).</summary>
    private List<SensorReading> TrackVoltageExcursions(List<SensorReading> voltageReadings)
    {
        var now = DateTime.Now;
        var seenIdentifiers = new HashSet<string>();
        var result = new List<SensorReading>(voltageReadings.Count);

        foreach (var reading in voltageReadings)
        {
            if (reading.IsVoltageOutOfSpec is not { } outOfSpec)
            {
                // Unrecognized rail - never tracked for excursions, keep the pre-#620 fallback
                // text (the bare hardware name, same as before this round).
                result.Add(CloneVoltageReading(reading, reading.HardwareName, null));
                continue;
            }

            seenIdentifiers.Add(reading.Identifier);
            bool wasOutOfSpec = _railWasOutOfSpec.TryGetValue(reading.Identifier, out var prev) && prev;
            if (outOfSpec && !wasOutOfSpec)
            {
                if (!_railExcursionTimestamps.TryGetValue(reading.Identifier, out var timestamps))
                    _railExcursionTimestamps[reading.Identifier] = timestamps = new List<DateTime>();
                timestamps.Add(now);
            }
            _railWasOutOfSpec[reading.Identifier] = outOfSpec;

            double excursionsPerHour = 0;
            if (_railExcursionTimestamps.TryGetValue(reading.Identifier, out var window))
            {
                window.RemoveAll(t => now - t > RailExcursionWindow);
                excursionsPerHour = window.Count;
            }

            string direction = reading.VoltageDeviationPercent is { } dev && dev < 0 ? "low" : "high";
            string verdict = outOfSpec
                ? $"{Math.Abs(reading.VoltageDeviationPercent ?? 0):0}% {direction}, {excursionsPerHour:0} excursions/hr"
                : "in spec (±5%)";

            result.Add(CloneVoltageReading(reading, verdict, excursionsPerHour));
        }

        // Drop tracking state for rails no longer reporting, same housekeeping RefreshCoolantPumps
        // already does for pump windows.
        foreach (var stale in _railWasOutOfSpec.Keys.Where(k => !seenIdentifiers.Contains(k)).ToList())
            _railWasOutOfSpec.Remove(stale);
        foreach (var stale in _railExcursionTimestamps.Keys.Where(k => !seenIdentifiers.Contains(k)).ToList())
            _railExcursionTimestamps.Remove(stale);

        return result;
    }

    private static SensorReading CloneVoltageReading(SensorReading reading, string verdictText, double? excursionsPerHour) => new()
    {
        HardwareName = reading.HardwareName,
        HardwareType = reading.HardwareType,
        SensorName = reading.SensorName,
        Type = reading.Type,
        Value = reading.Value,
        Identifier = reading.Identifier,
        SessionMin = reading.SessionMin,
        SessionMax = reading.SessionMax,
        IsVoltageOutOfSpec = reading.IsVoltageOutOfSpec,
        VoltageDeviationPercent = reading.VoltageDeviationPercent,
        VoltageExcursionsPerHour = excursionsPerHour,
        RailVerdictText = verdictText,
    };

    // ================================================================================
    // #621/#622: rail-sag-under-load correlation + Vcore droop/load-line chart
    // ================================================================================

    /// <summary>Samples the recognized 12V rail alongside total system power (#621) and Vcore
    /// alongside package power alone (#622) into their respective rolling scatter windows, then
    /// recomputes each chart's live least-squares fit line and verdict text. Both stay empty/
    /// hidden until their required sensors (a recognized 12V rail; a Vcore-hinted voltage sensor)
    /// are actually present - never fabricated from an unrelated rail.</summary>
    private void TrackPowerDelivery(List<SensorReading> voltageReadings, double? packagePowerW, double? gpuPowerW)
    {
        double? rail12V = FindByNameContains(voltageReadings, Rail12VHints[0].Hints);
        double? vcoreV = FindByNameContains(voltageReadings, VcoreNameHints);

        if (rail12V is { } rail && packagePowerW is { } pkg)
        {
            double totalPowerW = pkg + (gpuPowerW ?? 0);
            RailSagPoints.Add(new ObservablePoint(totalPowerW, rail));
            while (RailSagPoints.Count > PowerDeliveryWindow) RailSagPoints.RemoveAt(0);
            RefreshRailSagFit();
        }

        if (vcoreV is { } vcore && packagePowerW is { } pkgOnly)
        {
            VcoreLoadPoints.Add(new ObservablePoint(pkgOnly, vcore));
            while (VcoreLoadPoints.Count > PowerDeliveryWindow) VcoreLoadPoints.RemoveAt(0);
            RefreshVcoreLoadFit();
        }
    }

    /// <summary>#621: fits the current rail-sag window and reports both the slope (V per W) and
    /// the Pearson correlation coefficient - a rail that's measurably dropping as power rises
    /// (negative slope, meaningful |r|) is a loaded/failing PSU rail; a noisy-but-uncorrelated
    /// reading (|r| near 0) is just a poor sensor, not a real sag.</summary>
    private void RefreshRailSagFit()
    {
        if (RailSagPoints.Count < 10)
        {
            RailSagFitLine.Clear();
            RailSagVerdictText = "Not enough samples yet to assess rail sag under load.";
            return;
        }

        var points = RailSagPoints.Select(p => (X: p.X ?? 0, Y: p.Y ?? 0)).ToList();
        var (slope, intercept) = LinearRegression(points);
        double r = PearsonR(points);

        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        RailSagFitLine.Clear();
        RailSagFitLine.Add(new ObservablePoint(minX, (slope * minX) + intercept));
        RailSagFitLine.Add(new ObservablePoint(maxX, (slope * maxX) + intercept));

        RailSagVerdictText = Math.Abs(r) >= 0.4 && slope < 0
            ? $"12V rail correlates with system power (r = {r:0.00}, slope {slope * 1000:0.#} mV/W) - measurable sag under load, a loaded or possibly failing PSU rail."
            : $"No meaningful sag correlation (r = {r:0.00}) - looks like sensor noise rather than a loaded/failing rail.";
    }

    /// <summary>#622: fits the current Vcore-vs-package-power window - the slope is the effective
    /// load-line calibration curve; an unusually steep (large-magnitude negative) slope is
    /// excessive droop, which correlates with the instability items elsewhere on this tab.</summary>
    private void RefreshVcoreLoadFit()
    {
        if (VcoreLoadPoints.Count < 10)
        {
            VcoreLoadFitLine.Clear();
            VcoreLoadSlopeText = "Not enough samples yet to chart Vcore load-line behavior.";
            return;
        }

        var points = VcoreLoadPoints.Select(p => (X: p.X ?? 0, Y: p.Y ?? 0)).ToList();
        var (slope, intercept) = LinearRegression(points);

        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        VcoreLoadFitLine.Clear();
        VcoreLoadFitLine.Add(new ObservablePoint(minX, (slope * minX) + intercept));
        VcoreLoadFitLine.Add(new ObservablePoint(maxX, (slope * maxX) + intercept));

        VcoreLoadSlopeText = $"Load-line slope: {slope * 1000:0.#} mV/W over {VcoreLoadPoints.Count} samples - a steeper (more negative) slope means more Vcore droop under load.";
    }

    /// <summary>Pearson correlation coefficient between a set of (X, Y) points - 0 when there's no
    /// variance to correlate (e.g. every sample landed at the same power reading).</summary>
    private static double PearsonR(IReadOnlyList<(double X, double Y)> points)
    {
        double meanX = points.Average(p => p.X);
        double meanY = points.Average(p => p.Y);
        double covXY = points.Sum(p => (p.X - meanX) * (p.Y - meanY));
        double varX = points.Sum(p => (p.X - meanX) * (p.X - meanX));
        double varY = points.Sum(p => (p.Y - meanY) * (p.Y - meanY));
        double denom = Math.Sqrt(varX * varY);
        return denom < 1e-9 ? 0 : covXY / denom;
    }

    // ================================================================================
    // #632: AMD PPT/TDC/EDC limit approximation
    // ================================================================================

    /// <summary>See the property block's remarks above. Called every tick from RefreshCoreAsync -
    /// a no-op (verdict text cleared) on non-AMD silicon.</summary>
    private void TrackAmdLimitApproximation(List<SensorReading> currentReadings, double? packagePowerW)
    {
        if (!IsAmdCpu)
        {
            AmdLimitVerdictText = string.Empty;
            return;
        }

        Replace(Currents, currentReadings);
        AmdCurrentA = FindByNameContains(currentReadings, AmdCurrentNameHints);
        if (AmdCurrentA is { } a)
            AmdCurrentSessionMaxA = AmdCurrentSessionMaxA is { } max ? Math.Max(max, a) : a;

        if (AmdCurrentA is null || packagePowerW is null)
        {
            AmdLimitVerdictText = "No AMD SoC/package current sensor reported by LibreHardwareMonitorLib on this system - can't approximate the PPT vs. TDC/EDC binding limit.";
            return;
        }

        var now = DateTime.Now;
        double elapsed = _lastAmdDwellTick is { } last ? Math.Max(0, (now - last).TotalSeconds) : 0;
        _lastAmdDwellTick = now;

        // #605's sustained-load stopwatch - reused rather than a second one, since "sustained
        // load" means the same thing here as it does for the throttle-episode tracking above.
        bool underLoad = _sustainedLoadStartedAt is not null;
        if (underLoad)
        {
            _amdTotalDwellSeconds += elapsed;
            if (packagePowerW >= (PowerSessionMaxW ?? 0) * AmdCeilingFraction) _amdPowerCeilingDwellSeconds += elapsed;
            if (AmdCurrentA >= (AmdCurrentSessionMaxA ?? 0) * AmdCeilingFraction) _amdCurrentCeilingDwellSeconds += elapsed;
        }

        if (_amdTotalDwellSeconds < 20)
        {
            AmdLimitVerdictText = "Not enough sustained-load data yet to approximate the binding limit.";
            return;
        }

        double powerShare = _amdPowerCeilingDwellSeconds / _amdTotalDwellSeconds * 100.0;
        double currentShare = _amdCurrentCeilingDwellSeconds / _amdTotalDwellSeconds * 100.0;
        string binding = powerShare >= currentShare ? "package power (PPT)" : "current draw (TDC/EDC proxy)";

        AmdLimitVerdictText = $"Package power at its apparent ceiling {powerShare:0}% of sustained-load time, current draw at its apparent ceiling {currentShare:0}% - {binding} looks like the more consistently binding limit. Approximate: Ryzen's real limit-reason telemetry needs vendor SMU access this app doesn't take.";
    }

    // ================================================================================
    // #624: PSU inventory + wattage sanity check
    // ================================================================================

    /// <summary>On-demand WMI read (Win32_PowerSupply / Win32_SystemEnclosure) - see PsuService's
    /// remarks for why a populated wattage is the uncommon case. When WMI reports a wattage, it
    /// takes priority over any previously user-entered figure for the sanity-check denominator
    /// (an OEM-reported figure is more trustworthy than a guess); otherwise the user-entered
    /// figure (if any) keeps being used.</summary>
    private async Task LoadPsuInfoAsync()
    {
        _psuInventory = await Task.Run(() => PsuService.ReadPsuInventory());
        if (_psuInventory is null)
        {
            PsuInventoryText = "No PSU information reported by firmware/WMI on this system - enter your PSU's rated wattage below for the sanity check.";
        }
        else if (_psuInventory.RatedWattageW is { } w)
        {
            PsuInventoryText = $"{_psuInventory.Name} - {w:0} W rated (via {_psuInventory.Source}).";
            PsuRatedWattageW = w;
            PsuWattageInputText = w.ToString("0");
        }
        else
        {
            PsuInventoryText = $"{_psuInventory.Name} (via {_psuInventory.Source}) - no wattage reported; enter your PSU's rated wattage below for the sanity check.";
        }
    }

    /// <summary>Parses and persists the user-entered PSU wattage textbox to psu.json - a plain
    /// synchronous RelayCommand since PsuSettingsService's write is a tiny local JSON file, the
    /// same "no async needed for a JSON write" shape ThrottleHistoryService/ThermalBaselineService
    /// already use.</summary>
    private void SavePsuWattage()
    {
        if (!double.TryParse(PsuWattageInputText, out var watts) || watts <= 0)
        {
            PsuInventoryText = "Enter a positive number of watts (e.g. 650) before saving.";
            return;
        }

        PsuSettingsService.Save(new PsuSettings { UserRatedWattageW = watts });
        PsuRatedWattageW = watts;
        if (_psuInventory?.RatedWattageW is null)
            PsuInventoryText = $"Using user-entered PSU wattage: {watts:0} W.";
    }

    /// <summary>#624: estimated total system draw (CPU package + GPU + a fixed platform
    /// allowance for motherboard/fans/drives/RAM) against the rated wattage from either source
    /// above - flags sustained (not momentary) draw over ~80% as a brownout/shutdown risk, tracked
    /// via a small rolling window of recent load-percent samples so one transient spike doesn't
    /// trip the flag.</summary>
    private void TrackPsuLoad(double? packagePowerW, double? gpuPowerW)
    {
        if (packagePowerW is not { } pkg)
        {
            EstimatedTotalDrawW = null;
            PsuLoadPercent = null;
            return;
        }

        double estimated = pkg + (gpuPowerW ?? 0) + PsuPlatformAllowanceW;
        EstimatedTotalDrawW = estimated;

        if (PsuRatedWattageW is not { } rated || rated <= 0)
        {
            PsuLoadPercent = null;
            PsuBrownoutRiskDetected = false;
            _psuLoadPercentSamples.Clear();
            return;
        }

        double loadPercent = estimated / rated * 100.0;
        PsuLoadPercent = loadPercent;

        _psuLoadPercentSamples.Add(loadPercent);
        while (_psuLoadPercentSamples.Count > PsuSustainedSampleCount) _psuLoadPercentSamples.RemoveAt(0);

        PsuBrownoutRiskDetected = _psuLoadPercentSamples.Count >= PsuSustainedSampleCount &&
            _psuLoadPercentSamples.All(p => p >= PsuBrownoutLoadFraction * 100.0);
    }

    // ================================================================================
    // #625/#638: coarse power-history log
    // ================================================================================

    /// <summary>Appends one sample to power-history-log.json at most once a minute - see
    /// PowerHistoryLogService's remarks for why this needs to be a persisted (not in-memory) trail
    /// at all, and why it's periodic rather than per-tick. #657: also carries the live battery
    /// percent (when present) so StandbyDrainService can later look up "battery percent right
    /// before/after" an overnight sleep from this same once-a-minute trail, without a second
    /// persisted log just for that one figure.</summary>
    private void AppendPowerHistorySampleIfDue(double? tempC, double? packagePowerW, double? gpuPowerW, double? batteryPercent)
    {
        var now = DateTime.Now;
        if (now - _lastPowerHistoryAppend < PowerHistoryAppendInterval) return;
        _lastPowerHistoryAppend = now;

        if (tempC is null && packagePowerW is null && gpuPowerW is null && batteryPercent is null) return; // nothing worth logging yet

        PowerHistoryLogService.Append(new PowerTempSample
        {
            Timestamp = now,
            TempC = tempC,
            PackagePowerW = packagePowerW,
            GpuPowerW = gpuPowerW,
            BatteryPercent = batteryPercent,
        });
    }

    // ================================================================================
    // #626: DC-jack / adapter power-source flapping
    // ================================================================================

    /// <summary>On-demand Kernel-Power 105 (AC/DC transition) event-log read - see
    /// EventLogService.ReadPowerSourceChangeEvents' remarks.</summary>
    private async Task LoadPowerSourceEventsAsync()
    {
        var events = await Task.Run(() => _eventLog.ReadPowerSourceChangeEvents());
        PowerSourceChangeEvents.Clear();
        foreach (var e in events.Take(20)) PowerSourceChangeEvents.Add(e);

        var cutoff = DateTime.Now.AddHours(-1);
        PowerSourceChangesLastHour = events.Count(e => e.TimeCreated >= cutoff);
        PowerSourceFlapWarning = PowerSourceChangesLastHour >= 3;
        PowerSourceFlapText = PowerSourceFlapWarning
            ? $"AC source changed {PowerSourceChangesLastHour} times in the last hour - rapid flapping while plugged in points at a failing barrel jack, bad USB-C PD negotiation, or a bad cable."
            : events.Count > 0
                ? $"AC source changed {PowerSourceChangesLastHour} time(s) in the last hour."
                : string.Empty;
    }

    // ================================================================================
    // #641-#648: battery report panel
    // ================================================================================

    /// <summary>#641/#643: on-demand `powercfg /batteryreport` (falling back to WMI) - a real
    /// subprocess call, so this only ever runs from LoadBatteryReportCommand, never the tick
    /// timer or app startup (see CLAUDE.md's on-demand-vs-polled convention).</summary>
    private async Task LoadBatteryReportAsync()
    {
        BatteryReportStatusText = "Reading battery report...";
        var (report, statusText) = await BatteryReportService.GetReportAsync();
        BatteryReport = report;
        BatteryReportStatusText = statusText.Length > 0
            ? statusText
            : report is null
                ? "No battery report available."
                : report.ReportGeneratedAt is { } generated
                    ? $"Report generated {generated:g} ({report.Source})."
                    : $"Report loaded ({report.Source}).";
        OnPropertyChanged(nameof(HasBatteryReport));
        RefreshCapacityHistoryChart();
    }

    /// <summary>#642: rebuilds the capacity-fade chart and its linear 50%-of-design projection
    /// from whatever BatteryReport.CapacityHistory currently holds - called once after each
    /// LoadBatteryReportAsync completes (there's no per-tick source for this, the report is only
    /// ever refreshed on demand).</summary>
    private void RefreshCapacityHistoryChart()
    {
        CapacityHistoryPoints.Clear();
        CapacityProjectionLine.Clear();
        CapacityProjectionText = string.Empty;

        var usable = (BatteryReport?.CapacityHistory ?? new List<BatteryCapacityHistoryEntry>())
            .Where(e => e.FullChargeCapacityMwh is > 0)
            .OrderBy(e => e.PeriodStart)
            .ToList();
        if (usable.Count == 0) return;

        _capacityHistoryOriginDate = usable[0].PeriodStart;
        var points = usable
            .Select(e => (X: (e.PeriodStart - _capacityHistoryOriginDate).TotalDays, Y: e.FullChargeCapacityMwh!.Value / 1000.0))
            .ToList();
        foreach (var p in points) CapacityHistoryPoints.Add(new ObservablePoint(p.X, p.Y));

        if (points.Count < 3)
        {
            CapacityProjectionText = "Not enough capacity-history entries yet for a fade projection (needs at least 3).";
            return;
        }

        double? designWh = BatteryReport?.DesignCapacityMwh is { } d && d > 0 ? d / 1000.0 : null;
        if (designWh is null)
        {
            CapacityProjectionText = "Design capacity is unknown, so a 50%-of-design projection can't be computed.";
            return;
        }

        var (slope, intercept) = LinearRegression(points);
        if (slope >= -0.0001)
        {
            CapacityProjectionText = "Capacity isn't trending downward over this history yet - no fade projection to show.";
            return;
        }

        double threshold = designWh.Value * 0.5;
        double lastX = points[^1].X;
        double lastFitY = (slope * lastX) + intercept;
        double firstFitY = (slope * points[0].X) + intercept;
        double projectedX = (threshold - intercept) / slope;

        if (projectedX <= lastX)
        {
            CapacityProjectionLine.Add(new ObservablePoint(points[0].X, firstFitY));
            CapacityProjectionLine.Add(new ObservablePoint(lastX, lastFitY));
            CapacityProjectionText = "This battery's fitted capacity trend is already at or below 50% of its design capacity.";
            return;
        }

        CapacityProjectionLine.Add(new ObservablePoint(lastX, lastFitY));
        CapacityProjectionLine.Add(new ObservablePoint(projectedX, threshold));

        var projectedDate = _capacityHistoryOriginDate.AddDays(projectedX);
        double whPerYear = Math.Abs(slope) * 365.0;
        CapacityProjectionText =
            $"At the current fade rate (~{whPerYear:0.#} Wh/year), a linear projection crosses 50% of design capacity " +
            $"({threshold:0.#} Wh) around {projectedDate:MMMM yyyy} - a rough estimate from {points.Count} report periods, not a guarantee.";
    }

    /// <summary>#644: on-demand SRUM battery-drain-by-process scan - see
    /// BatteryDrainAttributionService's remarks for why this is a real subprocess call (gated
    /// behind its own button, same as LoadBatteryReportAsync above) and why the result is an
    /// adaptively-parsed best-effort ranking rather than a calibrated watt-hour table.</summary>
    private async Task LoadBatteryDrainAttributionAsync()
    {
        BatteryDrainAttributionStatusText = "Scanning SRUM energy history (this can take a moment)...";
        var (rows, statusText) = await BatteryDrainAttributionService.ReadRecentDrainAsync();
        BatteryDrainAttribution.Clear();
        foreach (var row in rows) BatteryDrainAttribution.Add(row);
        BatteryDrainAttributionStatusText = statusText.Length > 0
            ? statusText
            : $"{rows.Count} app(s) found in the last several days of SRUM energy history (informational ranking, not calibrated Wh - see the panel's own note).";
    }

    /// <summary>#645-#648: the live (per-tick) half of the battery report panel - cheap enough for
    /// the tick timer (a single Win32_Battery WMI SELECT plus pure in-memory bookkeeping over the
    /// Battery sensor collection RefreshCoreAsync already built above), unlike LoadBatteryReportAsync/
    /// LoadBatteryDrainAttributionAsync's real subprocess calls.</summary>
    private async Task RefreshBatteryReportLiveStateAsync()
    {
        BatteryStatusService.Win32BatterySnapshot win32;
        try { win32 = await Task.Run(BatteryStatusService.Read); }
        catch { win32 = BatteryStatusService.Win32BatterySnapshot.NotPresent; }

        // #647: gauge dropout - either data source reporting a battery counts as "present". See
        // HasBattery's remarks for why the section-visibility gate is latched off this, not the
        // instantaneous Battery.Count.
        bool presentNow = Battery.Count > 0 || win32.Present;
        if (presentNow) HasBattery = true;
        if (_batteryPresentLastTick is { } wasPresent && wasPresent != presentNow)
        {
            BatteryDropoutEvents.Insert(0, new BatteryPresenceEvent { Timestamp = DateTime.Now, BecamePresent = presentNow });
            while (BatteryDropoutEvents.Count > 20) BatteryDropoutEvents.RemoveAt(BatteryDropoutEvents.Count - 1);
        }
        _batteryPresentLastTick = presentNow;

        if (!presentNow)
        {
            BatteryChargePercent = null;
            BatteryTemperatureC = null;
            _chargePercentSamples.Clear();
            _chargeCeilingSamples.Clear();
            ChargeStallDetected = false;
            WeakChargerSuspected = false;
            ChargeRateVerdictText = string.Empty;
            ChargeCeilingText = string.Empty;
            HotChargeThrottleText = string.Empty;
            RuntimeComparisonText = string.Empty;
            return;
        }

        // Charge % - prefer LibreHardwareMonitorLib's own "Charge Level" sensor (the same source
        // the drain-rate readout above uses), falling back to Win32_Battery's
        // EstimatedChargeRemaining when the sensor tree doesn't report one. Excludes "Degradation
        // Level", which is also a Level-typed Battery sensor but an entirely different figure.
        var chargeLevelReading = Battery.FirstOrDefault(r => r.Type == SensorType.Level &&
            r.SensorName.Contains("Charge", StringComparison.OrdinalIgnoreCase) &&
            !r.SensorName.Contains("Degrad", StringComparison.OrdinalIgnoreCase));
        BatteryChargePercent = chargeLevelReading?.Value ?? win32.EstimatedChargePercent;

        // #648: battery-pack temperature, if any vendor sensor reports one.
        BatteryTemperatureC = Battery.FirstOrDefault(r => r.Type == SensorType.Temperature)?.Value;

        var now = DateTime.Now;

        // ---- #645: charge-rate / charge-stall -------------------------------------------------
        if (BatteryIsCharging && BatteryDrainRateW is { } chargeW)
        {
            ChargeRateSessionMaxW = ChargeRateSessionMaxW is { } max ? Math.Max(max, chargeW) : chargeW;

            if (BatteryChargePercent is { } pctForStall)
            {
                _chargePercentSamples.Add((now, pctForStall));
                _chargePercentSamples.RemoveAll(s => s.When < now.AddMinutes(-ChargeStallWindowMinutes));
            }

            bool stalled = BatteryChargePercent is { } pct2 && pct2 < ChargeStallMaxPercent &&
                _chargePercentSamples.Count >= 2 &&
                now - _chargePercentSamples[0].When >= TimeSpan.FromMinutes(ChargeStallWindowMinutes) &&
                pct2 - _chargePercentSamples[0].Percent < ChargeStallMinDeltaPercent;
            ChargeStallDetected = stalled;

            bool weak = !stalled && ChargeRateSessionMaxW is { } sessionMax && sessionMax > 1.0 &&
                chargeW < sessionMax * WeakChargerFraction && BatteryChargePercent is < WeakChargerBelowPercent;
            WeakChargerSuspected = weak;

            string adapterRef = PsuRatedWattageW is { } rated ? $" - this system's adapter is rated {rated:0} W" : string.Empty;
            ChargeRateVerdictText = stalled
                ? $"Charging appears stalled at ~{BatteryChargePercent:0}% - no meaningful progress in the last {ChargeStallWindowMinutes:0} minutes (quick flag, not a verdict - a vendor charge-limit ceiling can look similar; see below)."
                : weak
                    ? $"Charging at {chargeW:0.#} W, well under the {ChargeRateSessionMaxW:0.#} W this battery has accepted this session{adapterRef} - a weak or wrong adapter is one possible cause (informational only, not a confirmed fault)."
                    : $"Charging at {chargeW:0.#} W.";
        }
        else
        {
            _chargePercentSamples.Clear();
            ChargeStallDetected = false;
            WeakChargerSuspected = false;
            ChargeRateVerdictText = string.Empty;
        }

        // ---- #646: design-vs-actual runtime, both at the *same* live draw rate so the gap
        // reflects capacity fade alone, not a workload difference between "now" and "when new". --
        if (!BatteryIsCharging && BatteryDrainRateW is { } drawW && drawW > 0.05 && BatteryReport is { } report)
        {
            double? designHours = report.DesignCapacityMwh is { } design && design > 0
                ? design / 1000.0 / drawW : null;
            double? actualHours = win32.EstimatedRunTime?.TotalHours ??
                (report.FullChargeCapacityMwh is { } fcc && fcc > 0 ? fcc / 1000.0 / drawW : null);

            RuntimeComparisonText = designHours is { } dh && actualHours is { } ah
                ? $"Getting an estimated {FormatHoursMinutes(ah)} of a designed ~{FormatHoursMinutes(dh)} at the current draw ({drawW:0.#} W)."
                : string.Empty;
        }
        else if (BatteryReport is null)
        {
            RuntimeComparisonText = "Run the battery report below to compare against design capacity.";
        }
        else
        {
            RuntimeComparisonText = string.Empty;
        }

        // ---- #648: charge-ceiling (vendor conservation policy) + hot-pack charge throttle -----
        if (BatteryChargePercent is { } chargePct)
        {
            _chargeCeilingSamples.Add((now, chargePct, win32.BatteryStatusText == "On AC (not charging)"));
            _chargeCeilingSamples.RemoveAll(s => s.When < now.AddMinutes(-ChargeCeilingWindowMinutes));

            if (_chargeCeilingSamples.Count >= 2 && now - _chargeCeilingSamples[0].When >= TimeSpan.FromMinutes(ChargeCeilingWindowMinutes))
            {
                double spread = _chargeCeilingSamples.Max(s => s.Percent) - _chargeCeilingSamples.Min(s => s.Percent);
                double avg = _chargeCeilingSamples.Average(s => s.Percent);
                bool mostlyNotCharging = _chargeCeilingSamples.Count(s => s.OnAcNotCharging) >= _chargeCeilingSamples.Count / 2;

                ChargeCeilingText = spread <= ChargeCeilingStableSpreadPercent && avg is >= 40 and <= 90 && mostlyNotCharging
                    ? $"Charge has held steady near {avg:0}% (~{CommonChargeCeilings.OrderBy(c => Math.Abs(c - avg)).First():0}%) for over " +
                      $"{ChargeCeilingWindowMinutes:0} minutes on AC - this looks like a vendor battery-conservation charge limit, not a charging fault or wear (quick flag, not a verdict)."
                    : string.Empty;
            }
        }
        else
        {
            _chargeCeilingSamples.Clear();
            ChargeCeilingText = string.Empty;
        }

        HotChargeThrottleText = BatteryTemperatureC is { } tempC && tempC >= HotBatteryThresholdC &&
            !BatteryIsCharging && win32.BatteryStatusText is "On AC (not charging)" or "Discharging"
            ? $"Battery pack reads {tempC:0.#}°C and isn't charging while on AC - many batteries pause or slow charging above " +
              $"~{HotBatteryThresholdC:0}°C to protect the cell. Normal protective behavior, not a fault (quick flag, not a verdict)."
            : string.Empty;
    }

    /// <summary>"2h 10m" / "45m" - hour-scale duration formatting for the #646 runtime comparison
    /// (distinct from the existing FormatDuration helper below, which is minute/second-scale for
    /// throttle/cooldown durations).</summary>
    private static string FormatHoursMinutes(double hours)
    {
        var ts = TimeSpan.FromHours(Math.Max(0, hours));
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes}m" : $"{ts.Minutes}m";
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

    // ================================================================================
    // #649-#658: Sleep panel - see the properties block above for how each of these is exposed.
    // ================================================================================

    /// <summary>#649/#650/#651: runs the sleepstudy/system-power report and its cross-session
    /// offender ranking, routed by SleepStateSupportText's Modern-Standby-vs-legacy-S3 detection -
    /// see SleepStudyService.RunAsync's remarks for exactly how that routing plays out on current
    /// Windows builds.</summary>
    private async Task LoadSleepStudyAsync()
    {
        SleepStudyStatusText = "Running sleep report (a real subprocess call - this can take a moment)...";
        bool isModernStandby = SleepStateSupportText.Contains("Modern Standby", StringComparison.OrdinalIgnoreCase);

        var (sessions, ranked, status) = await SleepStudyService.RunAsync(isModernStandby);
        SleepStudySessions.Clear();
        foreach (var s in sessions) SleepStudySessions.Add(s);
        TopStandbyOffenders.Clear();
        foreach (var o in ranked.Take(10)) TopStandbyOffenders.Add(o);
        SleepStudyStatusText = status;

        // #656: a repeated (2+ session) top offender becomes a general hint for the next
        // vetoed-transition correlation - see SleepTransitionRecord.PossibleVetoingDriverHint.
        _topStandbyOffenderHint = ranked.FirstOrDefault(o => o.SessionCount >= 2)?.Name;
    }

    /// <summary>#652: live power-request blocker list - the direct "why won't my PC sleep right
    /// now" answer.</summary>
    private async Task LoadPowerRequestsAsync()
    {
        PowerRequestsStatusText = "Reading outstanding power requests...";
        var (requests, status) = await PowerRequestService.ReadAsync();
        PowerRequests.Clear();
        foreach (var r in requests) PowerRequests.Add(r);
        PowerRequestsStatusText = status;
    }

    /// <summary>#653: wake-armed device inventory (wake_armed ∩ wake_from_any).</summary>
    private async Task LoadWakeArmedDevicesAsync()
    {
        WakeArmedDevicesStatusText = "Reading wake-armed devices...";
        var (devices, status) = await WakeDeviceService.ReadWakeArmedDevicesAsync();
        WakeArmedDevices.Clear();
        foreach (var d in devices) WakeArmedDevices.Add(d);
        WakeArmedDevicesStatusText = status;
    }

    /// <summary>#653: `powercfg /devicedisablewake &lt;name&gt;` for one row from the list above,
    /// then reloads the list so a successfully-disabled device drops out of it immediately.</summary>
    private async Task DisableDeviceWakeAsync(object? param)
    {
        if (param is not string name || string.IsNullOrWhiteSpace(name)) return;

        var (success, error) = await WakeDeviceService.DisableWakeAsync(name);
        WakeArmedDevicesStatusText = success
            ? $"Disabled wake for \"{name}\"."
            : $"Couldn't disable wake for \"{name}\": {error}";
        if (success) await LoadWakeArmedDevicesAsync();
    }

    /// <summary>#654/#655/#656/#657: one combined "Load wake history" action - wake-history
    /// attribution, wake-timer + wake-enabled-scheduled-task inventory, vetoed-transition
    /// detection, and the standby-drain reconciliation all key off the same underlying wake-history
    /// event-log read, so splitting them into separate buttons would just mean re-running that scan
    /// several times over for no benefit.</summary>
    private async Task LoadWakeHistoryAsync()
    {
        WakeHistoryStatusText = "Reading wake history (a real event-log scan and subprocess call)...";

        // #654
        var (entries, lastWakeSummary) = await WakeHistoryService.ReadAsync(_eventLog);
        WakeHistory.Clear();
        foreach (var e in entries.Take(30)) WakeHistory.Add(e);
        WakeHistoryStatusText = entries.Count == 0
            ? $"No wake-history events found in the last 30 days. {lastWakeSummary}"
            : $"{entries.Count} wake event(s) in the last 30 days. {lastWakeSummary}";

        // #655: wake timers + wake-enabled scheduled tasks, unified into one table.
        var (timers, _) = await WakeTimerService.ReadAsync();
        var wakeTasks = await ScheduledTaskService.ListWakeEnabledAsync();
        WakeSources.Clear();
        foreach (var t in timers) WakeSources.Add(t);
        foreach (var task in wakeTasks)
        {
            WakeSources.Add(new WakeSourceRow
            {
                Kind = "Scheduled task",
                Name = task.Name,
                Detail = task.IsEnabled ? "Wake-enabled" : "Wake-enabled (task currently disabled)",
            });
        }

        // #656: failed-sleep/vetoed-transition detection, reusing this same wake-history read.
        var sleepEntryEvents = await Task.Run(() => _eventLog.ReadSleepEntryEvents());
        var vetoed = SleepVetoService.Correlate(sleepEntryEvents, entries, _topStandbyOffenderHint);
        VetoedSleepTransitions.Clear();
        foreach (var v in vetoed.Take(20)) VetoedSleepTransitions.Add(v);

        // #657: reconcile the overnight standby-drain trail against this same wake-history read.
        var powerHistorySamples = PowerHistoryLogService.Load();
        var drainSessions = StandbyDrainService.ReconcileAndSave(entries, powerHistorySamples);
        RefreshStandbyDrainSummary(drainSessions);
    }

    /// <summary>#657: recomputes the drain-per-hour summary shown at the top of the Sleep panel
    /// from whatever standby-drain.json currently holds - called both at startup (from the
    /// persisted file alone) and after each LoadWakeHistoryAsync reconciliation.</summary>
    private void RefreshStandbyDrainSummary(List<StandbyDrainSession> sessions)
    {
        StandbyDrainSessions.Clear();
        foreach (var s in sessions.Take(20)) StandbyDrainSessions.Add(s);

        if (sessions.Count == 0) return;

        int sampleCount = Math.Min(7, sessions.Count);
        double avg = sessions.Take(sampleCount).Average(s => s.DrainPercentPerHour);
        string verdict = avg <= StandbyDrainSession.HealthyReferencePercentPerHour
            ? "within the healthy Modern-Standby reference range"
            : "above the healthy Modern-Standby reference range (roughly ≤1-2%/hr) - worth checking the standby-offender ranking and power-request panels above";
        StandbyDrainSummaryText = $"Average standby drain over the last {sampleCount} recorded night(s): {avg:0.#}%/hr - {verdict}.";
    }

    /// <summary>#658: hibernation status - powercfg /a plus the HibernateEnabled/HiberFileSizePercent
    /// registry values, and the on-disk hiberfil.sys size.</summary>
    private async Task LoadHibernationStatusAsync()
    {
        Hibernation = await HibernationService.ReadStatusAsync();
    }

    /// <summary>#658: enable/disable action - `powercfg /hibernate on|off`, then reloads the status
    /// above so the panel reflects the actual result rather than assuming success.</summary>
    private async Task ToggleHibernationAsync()
    {
        bool targetEnabled = Hibernation?.Enabled != true;
        var (success, error) = await HibernationService.SetHibernationEnabledAsync(targetEnabled);
        if (success)
        {
            await LoadHibernationStatusAsync();
        }
        else
        {
            var prior = Hibernation;
            Hibernation = new HibernationStatus
            {
                Enabled = prior?.Enabled,
                HiberfilSizeBytes = prior?.HiberfilSizeBytes,
                ConfiguredSizePercent = prior?.ConfiguredSizePercent,
                StatusText = $"Couldn't change hibernation: {error}",
            };
        }
    }

    public void Dispose()
    {
        _timer.Stop();

        // #611/#612: flush whatever this session accumulated but hadn't yet reached its normal
        // flush point (a 5-minute interval, or a month rollover) - best-effort, same as every
        // other persisted-JSON write in this app.
        try { FlushCoolingBuckets(); } catch { /* best-effort */ }
        try { FlushFanCurveMonth(); } catch { /* best-effort */ }

        _sensors.Dispose();
        _thermalZones.Dispose();
    }
}
