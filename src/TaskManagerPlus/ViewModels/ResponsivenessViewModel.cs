using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
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
    private readonly EventLogService _eventLog = new();
    private readonly DispatcherTimer _lightTimer;

    // #260: needed for the run-queue-pressure card (Processor Queue Length + logical-processor
    // count, both already sampled by PerformanceViewModel every tick) - passed in via constructor
    // like ProcessesViewModel, rather than a second HardwareMonitorService instance.
    private readonly PerformanceViewModel _performance;

    /// <summary>#260: exposed so the XAML can bind directly to Performance.ContextSwitchesPerSec
    /// next to the run-queue-pressure meter - the same "expose the composed sibling ViewModel"
    /// pattern CpuViewModel.Performance already establishes.</summary>
    public PerformanceViewModel Performance => _performance;

    // #235/#236/#237/#238/#243/#244: hung-window detection/probing/foreground-stall recording -
    // see HungWindowService's remarks. _processes backs #245's session-wide USER/GDI handle sum
    // (ProcessRow.GdiHandleCount/UserHandleCount, already polled by ProcessesViewModel - no new
    // per-process syscall needed here).
    private readonly HungWindowService _hungWindows = new();
    private readonly ProcessesViewModel _processes;

    // #236/#238/#246: the slower, background-only probe cadence - SendMessageTimeout is genuinely
    // blocking-ish (up to 250ms per window), so this rides its own timer/Task.Run loop rather than
    // the cheap _lightTimer above. Every 4s per CLAUDE's "every few seconds" guidance for this item.
    private readonly DispatcherTimer _probeTimer;
    private bool _isProbing;

    // #247/#248/#254: DWM composition timing + hardware-scheduling/TDR registry read - see
    // DwmCompositionService's remarks. #247/#248 ride the cheap _lightTimer; #254 is loaded once
    // at start-up plus LoadDisplayAuditCommand's manual refresh, alongside #253/#255 below.
    private readonly DwmCompositionService _dwm = new();

    // #249: vblank jitter probe - a dedicated background thread, Start/Stop-gated (see the class's
    // own remarks on why DwmFlush()-based polling can't run unconditionally).
    private readonly VBlankJitterService _vblank = new();

    // #250/#251/#252/#258/#259: ETW-based present monitor ("PresentMon-lite") - the expensive,
    // Start/Stop-gated path this chunk's #251/#252/#258/#259 all build on top of.
    private readonly PresentMonitorService _presentMonitor = new();
    private CancellationTokenSource? _presentCts;
    private Task? _presentLoopTask;

    // #256/#257: raw-input queue-delay/polling-rate probe - a live hidden window + message pump,
    // Start/Stop-gated like the vblank probe above.
    private readonly InputLatencyService _inputLatency = new();
    private Dictionary<string, DriverIdentityInfo> _driverIdentities = new(StringComparer.OrdinalIgnoreCase);

    // #216: bare driver filename -> best-effort device friendly name(s), loaded once alongside
    // driver identity metadata - see DeviceInterruptAttributionService's remarks.
    private Dictionary<string, string> _driverDeviceMap = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _measureCts;
    private Task? _measureLoopTask;

    public ObservableCollection<DriverDpcRow> DriverDpcRows { get; } = new();
    public ObservableCollection<DriverIsrRow> DriverIsrRows { get; } = new();
    public ObservableCollection<CoreDpcRow> CoreDpcRows { get; } = new();
    public ObservableCollection<CoreDpcQueueRow> CoreDpcQueueRows { get; } = new();
    public ObservableCollection<DpcSpikeEvent> RecentSpikes { get; } = new();

    // #215: per-core interrupt-storm detection - rides the same lightweight always-on timer as
    // the per-core DPC/interrupt-time and queue-rate rows above (cheap syscall/perf-counter reads).
    public ObservableCollection<CoreInterruptRow> CoreInterruptRows { get; } = new();

    private bool _interruptStormDetected;
    public bool InterruptStormDetected { get => _interruptStormDetected; private set => SetProperty(ref _interruptStormDetected, value); }

    private string _interruptStormStatusText = "Sampling interrupt rates...";
    public string InterruptStormStatusText { get => _interruptStormStatusText; private set => SetProperty(ref _interruptStormStatusText, value); }

    // #217/#218/#219/#224: device/IRQ topology essentially never changes tick to tick, and each of
    // these is a WMI/registry enumeration too heavy for a per-tick timer per CLAUDE.md's on-demand
    // rule - loaded once at start-up (below) plus a manual refresh button, all under one status
    // text/command since they're all "how is this machine's interrupt hardware wired up right now"
    // facets of the same refresh.
    public ObservableCollection<IrqShareRow> IrqShareRows { get; } = new();
    public ObservableCollection<DeviceInterruptRow> DeviceInterruptRows { get; } = new();
    public ObservableCollection<ProblemDeviceRow> ProblemDeviceRows { get; } = new();

    // #220: "Platform latency settings" - deliberately generic so later chunks in this same domain
    // can append more rows without a new collection.
    public ObservableCollection<PlatformLatencySettingRow> PlatformLatencySettings { get; } = new();

    private bool _isLoadingDeviceTopology;
    public bool IsLoadingDeviceTopology { get => _isLoadingDeviceTopology; private set => SetProperty(ref _isLoadingDeviceTopology, value); }

    private string _deviceTopologyStatusText = "Not loaded yet.";
    public string DeviceTopologyStatusText { get => _deviceTopologyStatusText; private set => SetProperty(ref _deviceTopologyStatusText, value); }

    public AsyncRelayCommand LoadDeviceTopologyCommand { get; }

    // #222: Wi-Fi background-scan-storm suspected cause - on-demand only (an event-log scan).
    private WifiScanStormResult? _wifiScanStorm;
    public WifiScanStormResult? WifiScanStorm { get => _wifiScanStorm; private set => SetProperty(ref _wifiScanStorm, value); }

    private bool _isCheckingWifiScanStorm;
    public bool IsCheckingWifiScanStorm { get => _isCheckingWifiScanStorm; private set => SetProperty(ref _isCheckingWifiScanStorm, value); }

    public AsyncRelayCommand CheckWifiScanStormCommand { get; }

    // #223: USB/PnP re-enumeration churn - on-demand only (an event-log scan).
    public ObservableCollection<UsbChurnRow> UsbChurnRows { get; } = new();

    private string _usbChurnStatusText = "Not scanned yet.";
    public string UsbChurnStatusText { get => _usbChurnStatusText; private set => SetProperty(ref _usbChurnStatusText, value); }

    private bool _isScanningUsbChurn;
    public bool IsScanningUsbChurn { get => _isScanningUsbChurn; private set => SetProperty(ref _isScanningUsbChurn, value); }

    public AsyncRelayCommand ScanUsbChurnCommand { get; }

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

    // #225/#234: current timer resolution + derived wake-ups/sec and coalescing inference - rides
    // the cheap always-on _lightTimer tick (see SampleLight), same as the watchdog/per-core rows.
    private TimerResolutionInfo _timerResolution = new() { StatusText = "Loading..." };
    public TimerResolutionInfo TimerResolution { get => _timerResolution; private set => SetProperty(ref _timerResolution, value); }

    // #228: QPC frequency/drift check - deliberately on-demand only (a ~1.5s blocking-ish
    // measurement), not part of the light tick. Null until "Check QPC" has been pressed once.
    private QpcDriftResult? _qpcDrift;
    public QpcDriftResult? QpcDrift { get => _qpcDrift; private set => SetProperty(ref _qpcDrift, value); }

    private bool _isCheckingQpc;
    public bool IsCheckingQpc { get => _isCheckingQpc; private set => SetProperty(ref _isCheckingQpc, value); }

    public AsyncRelayCommand CheckQpcCommand { get; }

    // #226: who's holding a raised timer-resolution request - a short (15s) /energy run merged
    // with a best-effort registry name scan, both on-demand (see PowerReportService's remarks).
    public ObservableCollection<TimerResolutionRequesterRow> TimerResolutionRequesters { get; } = new();

    private bool _isCheckingTimerRequesters;
    public bool IsCheckingTimerRequesters { get => _isCheckingTimerRequesters; private set => SetProperty(ref _isCheckingTimerRequesters, value); }

    private string _timerRequestersStatusText = "Not checked yet.";
    public string TimerRequestersStatusText { get => _timerRequestersStatusText; private set => SetProperty(ref _timerRequestersStatusText, value); }

    public AsyncRelayCommand CheckTimerRequestersCommand { get; }

    // #229: full powercfg /energy Errors/Warnings report - the slow (60s) diagnostic, always an
    // explicit button, never anything close to a timer per CLAUDE.md's on-demand rule.
    public ObservableCollection<EnergyReportFinding> EnergyReportFindings { get; } = new();

    private bool _isRunningEnergyReport;
    public bool IsRunningEnergyReport { get => _isRunningEnergyReport; private set => SetProperty(ref _isRunningEnergyReport, value); }

    private string _energyReportStatusText = "Not run yet — takes about 60 seconds.";
    public string EnergyReportStatusText { get => _energyReportStatusText; private set => SetProperty(ref _energyReportStatusText, value); }

    private string? _energyReportPath;

    public AsyncRelayCommand RunEnergyReportCommand { get; }
    public RelayCommand OpenEnergyReportCommand { get; }

    // #230: outstanding power requests (powercfg /requests) - fast/cheap, loaded at start-up plus
    // a manual refresh button, same tier as the #217-220 device-topology load.
    public ObservableCollection<PowerRequestRow> PowerRequestRows { get; } = new();

    private bool _isLoadingPowerRequests;
    public bool IsLoadingPowerRequests { get => _isLoadingPowerRequests; private set => SetProperty(ref _isLoadingPowerRequests, value); }

    private string _powerRequestsStatusText = "Not loaded yet.";
    public string PowerRequestsStatusText { get => _powerRequestsStatusText; private set => SetProperty(ref _powerRequestsStatusText, value); }

    public AsyncRelayCommand LoadPowerRequestsCommand { get; }

    // #231: modern-standby drain / top activators (powercfg /sleepstudy) - on-demand only, and the
    // whole card is hidden (not just empty) on hardware with no modern-standby support at all.
    public ObservableCollection<SleepStudyActivatorRow> SleepStudyActivators { get; } = new();

    private bool _modernStandbySupported;
    public bool ModernStandbySupported { get => _modernStandbySupported; private set => SetProperty(ref _modernStandbySupported, value); }

    private bool _isRunningSleepStudy;
    public bool IsRunningSleepStudy { get => _isRunningSleepStudy; private set => SetProperty(ref _isRunningSleepStudy, value); }

    private string _sleepStudyStatusText = string.Empty;
    public string SleepStudyStatusText { get => _sleepStudyStatusText; private set => SetProperty(ref _sleepStudyStatusText, value); }

    private string? _sleepStudyReportPath;

    public AsyncRelayCommand RunSleepStudyCommand { get; }
    public RelayCommand OpenSleepStudyReportCommand { get; }

    // #235/#236: the always-on hung-window grid - rides _lightTimer for enumeration/IsHungAppWindow
    // (#235, cheap), merged each tick with whatever ResponseMs the slower _probeTimer last measured
    // (#236). #243's WaitHintText and #244's ChainText ride along on the same rows for currently-
    // hung windows only - see HungWindowService's remarks for the cost tiering behind that split.
    public ObservableCollection<HungWindowRow> HungWindowRows { get; } = new();

    private HungWindowRow? _selectedHungWindow;
    public HungWindowRow? SelectedHungWindow { get => _selectedHungWindow; set => SetProperty(ref _selectedHungWindow, value); }

    // #242: one-click dump for whatever hung window is selected in the grid above.
    public AsyncRelayCommand CreateDumpCommand { get; }

    private string _dumpStatusText = string.Empty;
    public string DumpStatusText { get => _dumpStatusText; private set => SetProperty(ref _dumpStatusText, value); }

    // #237: rolling hang log, persisted to hang-log.json (HangLogService) so it survives a restart -
    // loaded once at start-up, appended to whenever SampleWindows reports a window that just
    // recovered. HangsToday/LongestHangText are derived from this list, not stored separately.
    public ObservableCollection<HangLogEntry> HangLog { get; } = new();
    public int HangsToday => HangLog.Count(e => e.StartTime.Date == DateTime.Now.Date);
    public double LongestHangSeconds => HangLog.Count == 0 ? 0 : HangLog.Max(e => e.DurationSeconds);
    public string LongestHangText => LongestHangSeconds > 0 ? $"{LongestHangSeconds:0.#}s" : "None recorded yet";

    // #238: per-app ranked foreground-stall history - see HungWindowService.GetRankedStalls.
    public ObservableCollection<ForegroundStallRow> ForegroundStalls { get; } = new();

    /// <summary>#238: stall threshold in ms, default 500 per the task spec - bindable so a user on
    /// a slower machine can raise it past their normal baseline response time.</summary>
    public double ForegroundStallThresholdMs
    {
        get => _hungWindows.StallThresholdMs;
        set { _hungWindows.StallThresholdMs = Math.Max(50, value); OnPropertyChanged(); }
    }

    // #245: desktop heap size (SharedSection) + session-wide USER/GDI handle totals summed from the
    // process list ProcessesViewModel already polls - see DesktopHeapService/DesktopHeapInfo.
    private DesktopHeapInfo _desktopHeap = new() { StatusText = "Loading..." };
    public DesktopHeapInfo DesktopHeap { get => _desktopHeap; private set => SetProperty(ref _desktopHeap, value); }
    private (int? Interactive, int? Noninteractive, string Status) _desktopHeapSizes;

    // #246: shell (taskbar/desktop/Explorer-frame) responsiveness probe - rides the same _probeTimer
    // cadence as #236/#238 above.
    public ObservableCollection<ShellResponsivenessRow> ShellResponsivenessRows { get; } = new();

    private string _shellExtensionNoteText = string.Empty;
    public string ShellExtensionNoteText { get => _shellExtensionNoteText; private set => SetProperty(ref _shellExtensionNoteText, value); }

    // ----- #247-259: Display & frames -----------------------------------------------------------

    // #247/#248: DWM composition timing, rides the cheap _lightTimer.
    private DwmCompositionInfo _dwmComposition = new() { StatusText = "Loading..." };
    public DwmCompositionInfo DwmComposition { get => _dwmComposition; private set => SetProperty(ref _dwmComposition, value); }

    // #248: dropped+missed composition frames/sec, glow+core LineSeries pair over the shared
    // HistoryLength window - the same pattern as DpcLatencyHistory above.
    public ObservableCollection<double> CompositionDropHistory { get; } = NewHistory(0);
    private readonly LineSeries<double> _compDropGlow;
    private readonly LineSeries<double> _compDropCore;
    public ISeries[] CompositionDropSeries { get; }
    public Axis[] CompositionDropYAxes { get; }

    // #253/#255: display-mode audit (refresh rate/colour depth per monitor) + Game DVR/fullscreen-
    // optimisation audit - fast reads, loaded once at start-up plus a manual refresh; #254's
    // hardware-scheduling/TDR read rides the same refresh since it's the same cost tier.
    private DisplayModeAudit _displayModeAudit = new() { StatusText = "Loading..." };
    public DisplayModeAudit DisplayModeAudit { get => _displayModeAudit; private set => SetProperty(ref _displayModeAudit, value); }

    private GameDvrAuditInfo _gameDvrAudit = new() { StatusText = "Loading..." };
    public GameDvrAuditInfo GameDvrAudit { get => _gameDvrAudit; private set => SetProperty(ref _gameDvrAudit, value); }

    private HardwareSchedulingInfo _hardwareScheduling = new() { StatusText = "Loading..." };
    public HardwareSchedulingInfo HardwareScheduling { get => _hardwareScheduling; private set => SetProperty(ref _hardwareScheduling, value); }

    private bool _isLoadingDisplayAudit;
    public bool IsLoadingDisplayAudit { get => _isLoadingDisplayAudit; private set => SetProperty(ref _isLoadingDisplayAudit, value); }

    public AsyncRelayCommand LoadDisplayAuditCommand { get; }

    // #249: vblank jitter probe - Start/Stop-gated.
    private bool _isMeasuringVBlank;
    public bool IsMeasuringVBlank { get => _isMeasuringVBlank; private set => SetProperty(ref _isMeasuringVBlank, value); }

    private VBlankJitterSnapshot _vBlankSnapshot = new() { StatusText = "Not running - press Start." };
    public VBlankJitterSnapshot VBlankSnapshot { get => _vBlankSnapshot; private set => SetProperty(ref _vBlankSnapshot, value); }

    public RelayCommand StartVBlankCommand { get; }
    public RelayCommand StopVBlankCommand { get; }

    // #250/#251/#252/#258/#259: the present-monitor measurement session - Start/Stop-gated exactly
    // like the DPC measurement session above.
    public bool PresentToolsAvailable => _presentMonitor.ToolsAvailable;

    private bool _isMeasuringPresent;
    public bool IsMeasuringPresent { get => _isMeasuringPresent; private set => SetProperty(ref _isMeasuringPresent, value); }

    private string _presentStatusText = "Not measuring — press Start to begin sampling present/frame-time events.";
    public string PresentStatusText { get => _presentStatusText; private set => SetProperty(ref _presentStatusText, value); }

    public ObservableCollection<PresentAppRow> PresentAppRows { get; } = new();

    /// <summary>#251: the busiest (most frames captured) app this session - the headline card's
    /// "the app" for the 1%-low/0.1%-low/stddev/hitch numbers.</summary>
    public PresentAppRow? HeadlinePresentRow => PresentAppRows.Count > 0 ? PresentAppRows[0] : null;

    // #251: frame-time-vs-index scatter for the headline app - a nice-to-have, matching
    // EnergyThermalsViewModel's fan-curve ScatterSeries<ObservablePoint> pattern (no glow/core
    // pairing - a point cloud, not a line).
    public ObservableCollection<ObservablePoint> FrameTimeScatterPoints { get; } = new();
    private readonly ScatterSeries<ObservablePoint> _frameTimeScatter;
    public ISeries[] FrameTimeScatterSeries { get; }
    public Axis[] FrameTimeScatterXAxes { get; }
    public Axis[] FrameTimeScatterYAxes { get; }

    // #259: long-running GPU packets / preemption events from the same capture, named to their
    // owning process.
    public ObservableCollection<GpuStallRow> GpuStallRows { get; } = new();

    public AsyncRelayCommand StartPresentMonitorCommand { get; }
    public RelayCommand StopPresentMonitorCommand { get; }

    // #256/#257: raw-input queue-delay/polling-rate probe - Start/Stop-gated.
    private bool _isMeasuringInput;
    public bool IsMeasuringInput { get => _isMeasuringInput; private set => SetProperty(ref _isMeasuringInput, value); }

    private InputLatencySnapshot _inputLatencySnapshot = new() { StatusText = "Not running - press Start." };
    public InputLatencySnapshot InputLatencySnapshot { get => _inputLatencySnapshot; private set => SetProperty(ref _inputLatencySnapshot, value); }

    public RelayCommand StartInputLatencyCommand { get; }
    public RelayCommand StopInputLatencyCommand { get; }

    // #258: click-to-photon estimate, pairing InputLatencyService's last raw-input arrival against
    // PresentMonitorService's next captured frame for the headline app - see PresentLoopAsync.
    // Degrades to hidden in XAML whenever IsMeasuringPresent is false (bound off the same bool
    // #250 uses), per the item's own framing.
    private double? _inputToPresentMs;
    public double? InputToPresentMs { get => _inputToPresentMs; private set => SetProperty(ref _inputToPresentMs, value); }

    // ----- #260-270: Scheduler, priority and thread-wait analysis ------------------------------

    // #260: run-queue pressure - rides the cheap _lightTimer (a single perf-counter read already
    // collected by PerformanceViewModel, see SampleLight).
    public ObservableCollection<double> RunQueuePressureHistory { get; } = NewHistory(0);
    private readonly LineSeries<double> _runQueueGlow;
    private readonly LineSeries<double> _runQueueCore;
    public ISeries[] RunQueuePressureSeries { get; }
    public Axis[] RunQueuePressureYAxes { get; }

    private RunQueuePressureInfo _runQueuePressure = new();
    public RunQueuePressureInfo RunQueuePressure { get => _runQueuePressure; private set => SetProperty(ref _runQueuePressure, value); }

    // #261-265/267: the shared system-wide thread sweep - see SchedulerService's remarks for why
    // this is one syscall feeding several consumers, and why it rides its own slower cadence
    // (_schedulerTimer) rather than the cheap _lightTimer: a full-system NtQuerySystemInformation
    // sweep plus wait-breakdown/ranking/diffing/module-resolution over potentially several thousand
    // threads is the single most expensive read in this whole chunk, the same reasoning that gave
    // HungWindowService's probe cycle its own slower cadence in the prior chunk.
    private readonly SchedulerService _scheduler = new();
    private readonly DispatcherTimer _schedulerTimer;
    private bool _isSchedulerSampling;
    private List<SchedulerService.ThreadSnapshot> _lastSchedulerSweep = new();

    public ObservableCollection<LongestBlockedThreadRow> LongestBlockedThreads { get; } = new();
    public ObservableCollection<ThreadCsRateRow> BusiestThreads { get; } = new();
    public ObservableCollection<ContextSwitchAttributionRow> ContextSwitchAttribution { get; } = new();
    public ObservableCollection<PriorityInversionHint> PriorityInversionHints { get; } = new();

    private string _schedulerStatusText = "Sampling scheduler data...";
    public string SchedulerStatusText { get => _schedulerStatusText; private set => SetProperty(ref _schedulerStatusText, value); }

    // #269: MMCSS / multimedia-scheduling audit - a fast registry+service-status read, loaded once
    // at start-up alongside the #217-220 device-topology bundle plus its manual refresh.
    private MmcssAuditInfo _mmcssAudit = new() { ServiceStatusText = "Loading..." };
    public MmcssAuditInfo MmcssAudit { get => _mmcssAudit; private set => SetProperty(ref _mmcssAudit, value); }

    public ResponsivenessViewModel(ProcessesViewModel processes, PerformanceViewModel performance)
    {
        _processes = processes;
        _performance = performance;
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

        // #248: composition dropped+missed-frames/sec, same glow+core pairing as the DPC chart above.
        CompositionDropYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v:0.#}/s",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        var compColor = SKColors.MediumPurple;
        _compDropGlow = new LineSeries<double>
        {
            Values = CompositionDropHistory,
            Stroke = new SolidColorPaint(compColor.WithAlpha(70), GlowStrokeWidth),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0.3, IsHoverable = false, IsVisibleAtLegend = false,
        };
        _compDropCore = new LineSeries<double>
        {
            Values = CompositionDropHistory,
            Name = "Dropped + missed frames/sec",
            Stroke = new SolidColorPaint(compColor, CoreStrokeWidth),
            Fill = new LinearGradientPaint(compColor.WithAlpha(90), compColor.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)),
            GeometryStroke = null, GeometryFill = null, LineSmoothness = 0.3,
        };
        CompositionDropSeries = new ISeries[] { _compDropGlow, _compDropCore };

        // #260: run-queue pressure (Processor Queue Length), same glow+core pairing as every other
        // history chart in this app.
        RunQueuePressureYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v:0.#}",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        var runQueueColor = SKColors.Goldenrod;
        _runQueueGlow = new LineSeries<double>
        {
            Values = RunQueuePressureHistory,
            Stroke = new SolidColorPaint(runQueueColor.WithAlpha(70), GlowStrokeWidth),
            Fill = null, GeometryStroke = null, GeometryFill = null,
            LineSmoothness = 0.3, IsHoverable = false, IsVisibleAtLegend = false,
        };
        _runQueueCore = new LineSeries<double>
        {
            Values = RunQueuePressureHistory,
            Name = "Processor queue length",
            Stroke = new SolidColorPaint(runQueueColor, CoreStrokeWidth),
            Fill = new LinearGradientPaint(runQueueColor.WithAlpha(90), runQueueColor.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)),
            GeometryStroke = null, GeometryFill = null, LineSmoothness = 0.3,
        };
        RunQueuePressureSeries = new ISeries[] { _runQueueGlow, _runQueueCore };

        // #251: frame-time-vs-index scatter for the headline present-monitor app - no glow pair,
        // matching EnergyThermalsViewModel's fan-curve scatter (a point cloud doesn't read well
        // with one).
        FrameTimeScatterXAxes = new[] { new Axis { Name = "Frame #", MinLimit = 0, LabelsPaint = new SolidColorPaint(AxisTextColor), SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 } } };
        FrameTimeScatterYAxes = new[] { new Axis { Name = "Frame time (ms)", MinLimit = 0, LabelsPaint = new SolidColorPaint(AxisTextColor), SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 } } };
        _frameTimeScatter = new ScatterSeries<ObservablePoint>
        {
            Values = FrameTimeScatterPoints,
            Fill = new SolidColorPaint(SKColors.DeepSkyBlue.WithAlpha(140)),
            Stroke = null,
            GeometrySize = 6,
        };
        FrameTimeScatterSeries = new ISeries[] { _frameTimeScatter };

        StartMeasurementCommand = new AsyncRelayCommand(() => StartMeasurementAsync(), () => !IsMeasuring && DpcToolsAvailable);
        StopMeasurementCommand = new RelayCommand(() => StopMeasurement(), () => IsMeasuring);
        CopySummaryCommand = new RelayCommand(() => CopySummary(), () => !string.IsNullOrEmpty(SessionSummaryText));
        CaptureWprCommand = new AsyncRelayCommand(() => CaptureWprAsync(), () => !IsCapturing && WprToolsAvailable);
        OpenCaptureCommand = new RelayCommand(() => { if (_lastEtlPath is not null) WprCaptureService.OpenInDefaultApp(_lastEtlPath); }, () => _lastEtlPath is not null);
        OpenReportCommand = new RelayCommand(() => { if (_lastReportPath is not null) WprCaptureService.OpenInDefaultApp(_lastReportPath); }, () => _lastReportPath is not null);

        LoadDeviceTopologyCommand = new AsyncRelayCommand(LoadDeviceTopologyAsync, () => !IsLoadingDeviceTopology);
        CheckWifiScanStormCommand = new AsyncRelayCommand(CheckWifiScanStormAsync, () => !IsCheckingWifiScanStorm);
        ScanUsbChurnCommand = new AsyncRelayCommand(ScanUsbChurnAsync, () => !IsScanningUsbChurn);

        CheckQpcCommand = new AsyncRelayCommand(CheckQpcAsync, () => !IsCheckingQpc);
        CheckTimerRequestersCommand = new AsyncRelayCommand(CheckTimerRequestersAsync, () => !IsCheckingTimerRequesters);
        RunEnergyReportCommand = new AsyncRelayCommand(RunEnergyReportAsync, () => !IsRunningEnergyReport);
        OpenEnergyReportCommand = new RelayCommand(() => { if (_energyReportPath is not null) WprCaptureService.OpenInDefaultApp(_energyReportPath); }, () => _energyReportPath is not null);
        LoadPowerRequestsCommand = new AsyncRelayCommand(LoadPowerRequestsAsync, () => !IsLoadingPowerRequests);
        RunSleepStudyCommand = new AsyncRelayCommand(RunSleepStudyAsync, () => !IsRunningSleepStudy && ModernStandbySupported);
        OpenSleepStudyReportCommand = new RelayCommand(() => { if (_sleepStudyReportPath is not null) WprCaptureService.OpenInDefaultApp(_sleepStudyReportPath); }, () => _sleepStudyReportPath is not null);

        CreateDumpCommand = new AsyncRelayCommand(CreateDumpAsync, () => SelectedHungWindow is not null);

        LoadDisplayAuditCommand = new AsyncRelayCommand(LoadDisplayAuditAsync, () => !IsLoadingDisplayAudit);
        StartVBlankCommand = new RelayCommand(StartVBlank, () => !IsMeasuringVBlank);
        StopVBlankCommand = new RelayCommand(StopVBlank, () => IsMeasuringVBlank);
        StartPresentMonitorCommand = new AsyncRelayCommand(StartPresentMonitorAsync, () => !IsMeasuringPresent && PresentToolsAvailable);
        StopPresentMonitorCommand = new RelayCommand(StopPresentMonitor, () => IsMeasuringPresent);
        StartInputLatencyCommand = new RelayCommand(StartInputLatency, () => !IsMeasuringInput);
        StopInputLatencyCommand = new RelayCommand(StopInputLatency, () => IsMeasuringInput);

        // #237: load the persisted hang log before the first sample so HangsToday/LongestHang read
        // correctly even before this session has produced a single new hang.
        foreach (var e in HangLogService.Load()) HangLog.Add(e);

        _lightTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _lightTimer.Tick += (_, _) => SampleLight();
        _lightTimer.Start();
        SampleLight();

        // #236/#238/#246: the slower probe cadence - SendMessageTimeout is blocking-ish, so this
        // always runs inside Task.Run (see RunProbeCycleAsync), never directly on this tick.
        _probeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
        _probeTimer.Tick += async (_, _) => await RunProbeCycleAsync();
        _probeTimer.Start();

        // #238: the foreground hook - kept alive via HungWindowService's own field, see its remarks.
        _hungWindows.StartForegroundHook();

        // #261-265/267: the shared thread sweep's own slower cadence - see the field remarks
        // above for why this doesn't ride _lightTimer. 3.5s split the difference between "fresh
        // enough to be useful" and "not so frequent it competes with the light tick's own cost".
        _schedulerTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3.5) };
        _schedulerTimer.Tick += async (_, _) => await SampleSchedulerAsync();
        _schedulerTimer.Start();
        _ = SampleSchedulerAsync();

        Watchdog = DpcWatchdogService.Read();
        _ = LoadDriverIdentitiesAsync();
        _ = LoadDeviceTopologyAsync();
        _ = LoadPowerRequestsAsync(); // #230: fast shell-out, load at start-up like the device-topology block.
        _ = CheckModernStandbySupportAsync(); // #231: gates RunSleepStudyCommand/the card's own visibility.

        // #245: SharedSection barely ever changes (it needs a reboot to take effect), so it's read
        // once here rather than every light tick - only the handle-count sum below is recomputed
        // per tick.
        var (interactiveKb, noninteractiveKb, heapStatus) = DesktopHeapService.ReadHeapSizes();
        _desktopHeapSizes = (interactiveKb, noninteractiveKb, heapStatus);

        // #246: the shell-extension count is a registry-tree walk, not a per-tick cost - loaded
        // once at start-up like the driver-identity/device-topology blocks above.
        _ = LoadShellExtensionNoteAsync();

        // #253/#254/#255: display-mode/HAGS/GameDVR audit - fast reads, loaded once at start-up
        // plus LoadDisplayAuditCommand's manual refresh, same tier as the device-topology block.
        _ = LoadDisplayAuditAsync();
    }

    /// <summary>#211/#216: driver metadata join plus the best-effort driver-file -> device-name
    /// join, both loaded once (a couple of shell-outs/one WMI query, not a per-tick cost) and
    /// re-applied to whatever driver rows already exist so the grids gain identity/device text as
    /// soon as the joins finish, even if that lands mid-session.</summary>
    private async Task LoadDriverIdentitiesAsync()
    {
        try
        {
            var identitiesTask = DriverIdentityService.LoadAsync();
            var deviceMapTask = DeviceInterruptAttributionService.LoadDriverToDeviceMapAsync();
            await Task.WhenAll(identitiesTask, deviceMapTask);
            _driverIdentities = identitiesTask.Result;
            _driverDeviceMap = deviceMapTask.Result;
            RebuildDriverRows();
            RebuildIsrRows();
        }
        catch
        {
            // best-effort - rows just keep showing bare filenames / no device attribution
        }
    }

    /// <summary>#217/#218/#219/#224: one combined on-demand load for the device/IRQ/platform-
    /// settings facets that don't fit a per-tick timer - see the properties' remarks. Runs once at
    /// start-up (constructor) and again on every LoadDeviceTopologyCommand click.</summary>
    private async Task LoadDeviceTopologyAsync()
    {
        if (IsLoadingDeviceTopology) return;
        IsLoadingDeviceTopology = true;
        DeviceTopologyStatusText = "Loading IRQ map, interrupt-management policy, platform settings, and problem devices...";
        try
        {
            var irqTask = IrqResourceService.LoadAsync();
            var interruptMgmtTask = InterruptManagementService.LoadAsync();
            var problemDevicesTask = ProblemDeviceService.LoadAsync();
            var platformSettingsTask = PowerSchemeInterruptSteeringService.ReadInterruptSteeringSettingsAsync();
            // #227/#232: plain bcdedit/powercfg -q text parses - fast enough to ride this same
            // start-up-plus-manual-refresh load rather than needing their own button.
            var bootConfigTask = BootConfigTimerService.ReadAsync();
            var latencySettingsTask = LatencyPowerSettingsService.ReadLatencySensitiveSettingsAsync();
            // #269: MMCSS/multimedia-scheduling audit - same fast-read tier as everything else in
            // this bundle (registry reads + one ServiceController status query).
            var mmcssTask = Task.Run(MmcssService.Read);
            await Task.WhenAll(irqTask, interruptMgmtTask, problemDevicesTask, platformSettingsTask, bootConfigTask, latencySettingsTask, mmcssTask);

            IrqShareRows.Clear();
            foreach (var r in irqTask.Result) IrqShareRows.Add(r);

            DeviceInterruptRows.Clear();
            foreach (var r in interruptMgmtTask.Result) DeviceInterruptRows.Add(r);

            ProblemDeviceRows.Clear();
            foreach (var r in problemDevicesTask.Result) ProblemDeviceRows.Add(r);

            PlatformLatencySettings.Clear();
            foreach (var r in platformSettingsTask.Result) PlatformLatencySettings.Add(r);
            foreach (var r in bootConfigTask.Result) PlatformLatencySettings.Add(r);
            foreach (var r in latencySettingsTask.Result) PlatformLatencySettings.Add(r);
            // #241: hang-timeout registry audit - plain registry reads (no shell-out), fast enough
            // to just call inline here rather than adding a fourth Task.WhenAll entry.
            foreach (var r in HangTimeoutRegistryService.ReadAudit()) PlatformLatencySettings.Add(r);
            // #268: Win32PrioritySeparation audit - same plain registry-read tier as #241 above.
            PlatformLatencySettings.Add(Win32PrioritySeparationService.Read());

            MmcssAudit = mmcssTask.Result;

            DeviceTopologyStatusText = $"Loaded — {IrqShareRows.Count} IRQ lines, {DeviceInterruptRows.Count} devices with interrupt policy, {ProblemDeviceRows.Count} problem device(s).";
        }
        catch (Exception ex)
        {
            DeviceTopologyStatusText = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsLoadingDeviceTopology = false;
        }
    }

    /// <summary>#222: on-demand Wi-Fi background-scan-storm check - see WifiScanStormService.</summary>
    private async Task CheckWifiScanStormAsync()
    {
        if (IsCheckingWifiScanStorm) return;
        IsCheckingWifiScanStorm = true;
        try
        {
            WifiScanStorm = await WifiScanStormService.CheckAsync();
        }
        catch (Exception ex)
        {
            WifiScanStorm = new WifiScanStormResult { Detected = false, StatusText = $"Check failed: {ex.Message}" };
        }
        finally
        {
            IsCheckingWifiScanStorm = false;
        }
    }

    /// <summary>#223: on-demand USB/PnP re-enumeration churn scan - see
    /// EventLogService.ReadUsbChurnEvents. A 30-minute lookback window: long enough to catch a
    /// device that churns every few minutes, short enough to stay a quick scan.</summary>
    private async Task ScanUsbChurnAsync()
    {
        if (IsScanningUsbChurn) return;
        IsScanningUsbChurn = true;
        UsbChurnStatusText = "Scanning the last 30 minutes of PnP arrive/remove events...";
        try
        {
            var rows = await Task.Run(() => _eventLog.ReadUsbChurnEvents(TimeSpan.FromMinutes(30)));
            UsbChurnRows.Clear();
            foreach (var r in rows) UsbChurnRows.Add(r);
            UsbChurnStatusText = rows.Count == 0
                ? "No repeated device churn found in the last 30 minutes."
                : $"{rows.Count} device(s) with repeated arrive/remove churn in the last 30 minutes.";
        }
        catch (Exception ex)
        {
            UsbChurnStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningUsbChurn = false;
        }
    }

    /// <summary>#228: on-demand QPC frequency/drift check - a quick (~1.5s) blocking-ish
    /// measurement, deliberately not folded into SampleLight's cheap tick.</summary>
    private async Task CheckQpcAsync()
    {
        if (IsCheckingQpc) return;
        IsCheckingQpc = true;
        try
        {
            QpcDrift = await TimerResolutionService.CheckQpcDriftAsync(TimeSpan.FromSeconds(1.5), CancellationToken.None);
        }
        finally
        {
            IsCheckingQpc = false;
        }
    }

    /// <summary>#226: a short (15s) powercfg /energy run focused on the "Platform Timer
    /// Resolution" finding, merged with a best-effort GlobalTimerResolutionRequests registry name
    /// scan - see PowerReportService's remarks for both sources.</summary>
    private async Task CheckTimerRequestersAsync()
    {
        if (IsCheckingTimerRequesters) return;
        IsCheckingTimerRequesters = true;
        TimerRequestersStatusText = "Running a 15-second powercfg /energy scan...";
        try
        {
            var (_, message, energyRows) = await PowerReportService.RunTimerResolutionRequestersAsync(CancellationToken.None);
            var (regPresent, regRows) = PowerReportService.ReadGlobalTimerResolutionRequestsFromRegistry();

            TimerResolutionRequesters.Clear();
            foreach (var r in energyRows) TimerResolutionRequesters.Add(r);
            foreach (var r in regRows) TimerResolutionRequesters.Add(r);

            string regNote = regPresent
                ? (regRows.Count > 0 ? $"{regRows.Count} name(s) also recovered from the registry." : "Registry value present, but no process name could be recovered from it.")
                : "GlobalTimerResolutionRequests isn't available on this Windows build.";
            TimerRequestersStatusText = $"{message} {regNote}";
        }
        catch (Exception ex)
        {
            TimerRequestersStatusText = $"Check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingTimerRequesters = false;
        }
    }

    /// <summary>#229: the full powercfg /energy Errors/Warnings report - Microsoft's documented
    /// 60s duration, always an explicit button per CLAUDE.md's on-demand rule.</summary>
    private async Task RunEnergyReportAsync()
    {
        if (IsRunningEnergyReport) return;
        IsRunningEnergyReport = true;
        EnergyReportStatusText = "Running powercfg /energy for about 60 seconds...";
        try
        {
            var (ok, message, path, findings) = await PowerReportService.RunEnergyReportAsync(CancellationToken.None);
            EnergyReportFindings.Clear();
            foreach (var f in findings) EnergyReportFindings.Add(f);
            _energyReportPath = ok ? path : null;
            OpenEnergyReportCommand.RaiseCanExecuteChanged();
            EnergyReportStatusText = message;
        }
        catch (Exception ex)
        {
            EnergyReportStatusText = $"Run failed: {ex.Message}";
        }
        finally
        {
            IsRunningEnergyReport = false;
        }
    }

    /// <summary>#230: `powercfg /requests` - fast/cheap, loaded at start-up plus this manual
    /// refresh, same tier as the #217-220 device-topology load.</summary>
    private async Task LoadPowerRequestsAsync()
    {
        if (IsLoadingPowerRequests) return;
        IsLoadingPowerRequests = true;
        try
        {
            var rows = await LatencyPowerSettingsService.ReadPowerRequestsAsync();
            PowerRequestRows.Clear();
            foreach (var r in rows) PowerRequestRows.Add(r);
            PowerRequestsStatusText = rows.Count == 0 ? "No outstanding power requests." : $"{rows.Count} outstanding power request(s).";
        }
        catch (Exception ex)
        {
            PowerRequestsStatusText = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsLoadingPowerRequests = false;
        }
    }

    /// <summary>#231: gates the modern-standby drain card's visibility and RunSleepStudyCommand -
    /// reuses PowerPlanService.ReadSleepStateSupportAsync's own `powercfg /a` parse (Round 12 #91)
    /// rather than re-implementing the same "S0 Low Power Idle" text check a second time.</summary>
    private async Task CheckModernStandbySupportAsync()
    {
        try
        {
            string support = await PowerPlanService.ReadSleepStateSupportAsync();
            ModernStandbySupported = support.StartsWith("Modern Standby", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            ModernStandbySupported = false;
        }
        finally
        {
            // AsyncRelayCommand has no RaiseCanExecuteChanged of its own (its CanExecute is
            // re-queried via CommandManager the same way RelayCommand's does) - invalidate directly
            // since ModernStandbySupported just changed outside of any command's own Execute.
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>#231: on-demand powercfg /sleepstudy (falling back to /systemsleepdiagnostics) -
    /// only reachable when ModernStandbySupported is true (RunSleepStudyCommand's own CanExecute).</summary>
    private async Task RunSleepStudyAsync()
    {
        if (IsRunningSleepStudy || !ModernStandbySupported) return;
        IsRunningSleepStudy = true;
        SleepStudyStatusText = "Running powercfg /sleepstudy...";
        try
        {
            var (ok, message, path, activators) = await PowerReportService.RunSleepStudyAsync(CancellationToken.None);
            SleepStudyActivators.Clear();
            foreach (var a in activators) SleepStudyActivators.Add(a);
            _sleepStudyReportPath = ok ? path : null;
            OpenSleepStudyReportCommand.RaiseCanExecuteChanged();
            SleepStudyStatusText = message;
        }
        catch (Exception ex)
        {
            SleepStudyStatusText = $"Run failed: {ex.Message}";
        }
        finally
        {
            IsRunningSleepStudy = false;
        }
    }

    /// <summary>#204/#205/#206: the cheap always-on tick - registry read plus two syscall/perf-
    /// counter samples, none of which need Task.Run (all are fast, non-blocking reads matching the
    /// other lightweight per-tick services in this app).</summary>
    private void SampleLight()
    {
        Watchdog = DpcWatchdogService.Read();
        OnPropertyChanged(nameof(WatchdogHeadroomPercent));

        // #225/#234: cheap NtQueryTimerResolution syscall - fine on the light tick, same tier as
        // the watchdog registry read above.
        TimerResolution = TimerResolutionService.Read();

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

        // #215: interrupt-storm detection - same cheap-tick cadence as the rows above.
        var interruptRows = _perCore.SampleInterruptStorm();
        if (interruptRows.Count > 0)
        {
            CoreInterruptRows.Clear();
            foreach (var r in interruptRows) CoreInterruptRows.Add(r);

            var storming = interruptRows.Where(r => r.IsSuspectedStorm).ToList();
            InterruptStormDetected = storming.Count > 0;
            InterruptStormStatusText = storming.Count == 0
                ? "No core is showing a suspiciously high interrupt rate relative to the others."
                : $"Core {string.Join(", ", storming.Select(s => s.CoreIndex))} showing a sustained interrupt rate far above the rest — possible interrupt storm (quick flag, not a verdict).";
        }

        // #235/#236/#237/#243/#244: hung-window enumeration - IsHungAppWindow doesn't block, so
        // this runs directly on this tick, same as everything else in SampleLight. Merged in place
        // (like ProcessesViewModel.MergeInto) rather than cleared and rebuilt, so #242's grid
        // selection survives from one tick to the next.
        var (hungRows, recovered) = _hungWindows.SampleWindows();
        MergeHungWindows(hungRows);

        if (recovered.Count > 0)
        {
            foreach (var e in recovered) HangLog.Add(e);
            while (HangLog.Count > 200) HangLog.RemoveAt(0);
            HangLogService.Save(HangLog.ToList());
            OnPropertyChanged(nameof(HangsToday));
            OnPropertyChanged(nameof(LongestHangSeconds));
            OnPropertyChanged(nameof(LongestHangText));
        }

        // #238: per-app ranked stall history, refreshed from whatever the probe loop has
        // accumulated so far - cheap (just reading an already-built dictionary under a lock).
        var stalls = _hungWindows.GetRankedStalls();
        ForegroundStalls.Clear();
        foreach (var s in stalls) ForegroundStalls.Add(s);

        // #245: session-wide USER/GDI handle totals, summed from the process list
        // ProcessesViewModel already polls (ProcessRow.GdiHandleCount/UserHandleCount) - no new
        // per-process syscall needed here, just a cheap in-memory sum every light tick.
        int totalUser = 0, totalGdi = 0;
        foreach (var p in _processes.Processes)
        {
            totalUser += p.UserHandleCount;
            totalGdi += p.GdiHandleCount;
        }
        DesktopHeap = new DesktopHeapInfo
        {
            InteractiveHeapKb = _desktopHeapSizes.Interactive,
            NoninteractiveHeapKb = _desktopHeapSizes.Noninteractive,
            StatusText = _desktopHeapSizes.Status,
            TotalUserHandles = totalUser,
            TotalGdiHandles = totalGdi,
        };

        // #247/#248: DWM composition timing - a cheap dwmapi.dll call, fine on this tick.
        DwmComposition = _dwm.Sample();
        CompositionDropHistory.Add(DwmComposition.IsAvailable ? DwmComposition.DroppedMissedPerSec : 0);
        if (CompositionDropHistory.Count > HistoryLength) CompositionDropHistory.RemoveAt(0);

        // #249: vblank jitter snapshot - cheap (bounded-list percentile math), safe every tick
        // regardless of whether the probe is currently running. The probe's own background thread
        // can stop itself (composition unavailable) without going through StopVBlank, so reconcile
        // IsMeasuringVBlank from the service's actual state here too.
        VBlankSnapshot = _vblank.GetSnapshot();
        if (IsMeasuringVBlank && !_vblank.IsRunning) IsMeasuringVBlank = false;

        // #256/#257: input-latency snapshot - same "cheap to recompute every tick" reasoning.
        InputLatencySnapshot = _inputLatency.GetSnapshot();

        // #260: run-queue pressure - System\Processor Queue Length, already sampled every tick by
        // PerformanceViewModel (no second PerformanceCounter instantiation needed). "Ready threads
        // per core" is an explicit approximation (ProcessorQueueLength / logical-processor count) -
        // Windows exposes no true per-core ready-queue counter, only this one system-wide value.
        int logicalCount = _performance.Cores.Count > 0 ? _performance.Cores.Count : Environment.ProcessorCount;
        RunQueuePressure = new RunQueuePressureInfo { ProcessorQueueLength = _performance.CpuQueueLength, LogicalProcessorCount = logicalCount };
        RunQueuePressureHistory.Add(_performance.CpuQueueLength);
        if (RunQueuePressureHistory.Count > HistoryLength) RunQueuePressureHistory.RemoveAt(0);
    }

    /// <summary>#261-265/267: the shared thread sweep's own slower cadence - see the field remarks
    /// for why this is Task.Run'd separately from _lightTimer rather than folded into SampleLight.
    /// Guarded against overlap the same way RunProbeCycleAsync/RefreshCoreAffinityAsync are - a
    /// full-system sweep can legitimately take longer than one tick on a very busy system.</summary>
    private async Task SampleSchedulerAsync()
    {
        if (_isSchedulerSampling) return;
        _isSchedulerSampling = true;
        try
        {
            var (snapshot, longestBlocked, topRates, attribution, inversions) = await Task.Run(() =>
            {
                var snap = _scheduler.Sweep();
                var kernelModules = DpcModuleMapService.GetModuleMap();
                var longest = SchedulerService.RankLongestBlocked(snap, kernelModules);
                var rates = _scheduler.ComputeContextSwitchRates(snap);
                var topRates = SchedulerService.ResolveTopModules(rates, kernelModules);
                var attrib = SchedulerService.AttributeByProcess(rates);
                var inv = _scheduler.DetectPriorityInversions(snap);
                return (snap, longest, topRates, attrib, inv);
            });

            _lastSchedulerSweep = snapshot;

            LongestBlockedThreads.Clear();
            foreach (var r in longestBlocked) LongestBlockedThreads.Add(r);

            BusiestThreads.Clear();
            foreach (var r in topRates) BusiestThreads.Add(r);

            ContextSwitchAttribution.Clear();
            foreach (var r in attribution) ContextSwitchAttribution.Add(r);

            PriorityInversionHints.Clear();
            foreach (var h in inversions) PriorityInversionHints.Add(h);

            SchedulerStatusText = snapshot.Count == 0
                ? "Scheduler sweep returned no data (unsupported Windows build, or a transient read failure)."
                : $"{snapshot.Count} threads across {snapshot.Select(t => t.Pid).Distinct().Count()} processes, sampled {DateTime.Now:T}.";
        }
        catch (Exception ex)
        {
            SchedulerStatusText = $"Scheduler sample failed: {ex.Message}";
        }
        finally
        {
            _isSchedulerSampling = false;
        }
    }

    /// <summary>#261: cheap in-memory filter over the shared sweep, for ProcessesViewModel's
    /// per-selected-process wait-reason breakdown panel - see ProcessesViewModel.Responsiveness's
    /// remarks for the cross-ViewModel wiring.</summary>
    public List<ThreadWaitBreakdownRow> GetThreadWaitBreakdown(int pid) => SchedulerService.BuildWaitBreakdown(_lastSchedulerSweep, pid);

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
                    RebuildIsrRows();

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

    /// <summary>#235/#236/#237/#243/#244: merges the latest hung-window scan into HungWindowRows
    /// in place, keyed by Hwnd - the same "update existing/remove stale/add new" pattern
    /// ProcessesViewModel.MergeInto uses, so #242's grid selection (and the DataGrid's scroll
    /// position) survives from one ~2s tick to the next.</summary>
    private void MergeHungWindows(List<HungWindowRow> latest)
    {
        var latestByHwnd = latest.ToDictionary(r => r.Hwnd);

        for (int i = HungWindowRows.Count - 1; i >= 0; i--)
        {
            var existing = HungWindowRows[i];
            if (latestByHwnd.TryGetValue(existing.Hwnd, out var fresh))
            {
                existing.IsHung = fresh.IsHung;
                existing.ResponseMs = fresh.ResponseMs;
                existing.HungFor = fresh.HungFor;
                existing.WaitHintText = fresh.WaitHintText;
                existing.ChainText = fresh.ChainText;
                // Pid/ThreadId/ProcessName/WindowTitle/Hwnd don't change for the lifetime of a
                // window - no need to reassign them every tick.
                latestByHwnd.Remove(existing.Hwnd);
            }
            else
            {
                HungWindowRows.RemoveAt(i);
            }
        }

        foreach (var fresh in latestByHwnd.Values) HungWindowRows.Add(fresh);
    }

    /// <summary>#236/#238/#246: the background-only probe cycle - #236's hung-window round-trip
    /// probe and #246's shell-specific probe both ride this same cadence/re-entrancy guard, since
    /// both are the same "genuinely blocking-ish SendMessageTimeout call" shape.</summary>
    private async Task RunProbeCycleAsync()
    {
        if (_isProbing) return;
        _isProbing = true;
        try
        {
            var shellTask = Task.Run(ShellResponsivenessService.Probe);
            await _hungWindows.RunProbeCycleAsync(CancellationToken.None);
            var shellRows = await shellTask;

            ShellResponsivenessRows.Clear();
            foreach (var r in shellRows) ShellResponsivenessRows.Add(r);
        }
        catch
        {
            // Best-effort - a failed probe cycle just leaves ResponseMs/ShellResponsivenessRows at
            // their last known values until the next tick.
        }
        finally
        {
            _isProbing = false;
        }
    }

    /// <summary>#242: writes a full user-mode dump of SelectedHungWindow's owning process via
    /// ProcessDumpService (rundll32/comsvcs.dll's MiniDump export), to a location the user picks.</summary>
    private async Task CreateDumpAsync()
    {
        if (SelectedHungWindow is not { } row) return;

        var snapshotsDir = AppPaths.GetPath("Snapshots");
        try { Directory.CreateDirectory(snapshotsDir); } catch { /* SaveFileDialog still works without a pre-created folder */ }

        var dialog = new SaveFileDialog
        {
            Title = $"Save process dump for {row.ProcessName} (pid {row.Pid})",
            Filter = "Dump files (*.dmp)|*.dmp|All files (*.*)|*.*",
            DefaultExt = ".dmp",
            FileName = $"{row.ProcessName}-{row.Pid}-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.dmp",
            InitialDirectory = snapshotsDir,
        };
        if (dialog.ShowDialog() != true) return;

        DumpStatusText = "Writing dump...";
        var (_, message) = await ProcessDumpService.CreateDumpAsync(row.Pid, dialog.FileName);
        DumpStatusText = message;
    }

    /// <summary>#246: a registry-tree walk (ShellExtensionService.List), not a per-tick cost -
    /// loaded once at start-up, same tier as the driver-identity/device-topology blocks.</summary>
    private async Task LoadShellExtensionNoteAsync()
    {
        try
        {
            ShellExtensionNoteText = await Task.Run(ShellResponsivenessService.ShellExtensionNote);
        }
        catch (Exception ex)
        {
            ShellExtensionNoteText = $"Couldn't list shell extensions: {ex.Message}";
        }
    }

    /// <summary>#253/#254/#255: display-mode audit, hardware-scheduling/TDR registry read, and the
    /// Game DVR/fullscreen-optimisation audit - three fast reads bundled into one on-demand load
    /// (start-up plus LoadDisplayAuditCommand's manual refresh), matching LoadDeviceTopologyAsync's
    /// own "several small facets of the same refresh" grouping.</summary>
    private async Task LoadDisplayAuditAsync()
    {
        if (IsLoadingDisplayAudit) return;
        IsLoadingDisplayAudit = true;
        try
        {
            var displayTask = Task.Run(DisplayModeService.ReadAudit);
            var gameDvrTask = Task.Run(DisplayModeService.ReadGameDvrAudit);
            var hwSchedTask = Task.Run(DwmCompositionService.ReadHardwareScheduling);
            await Task.WhenAll(displayTask, gameDvrTask, hwSchedTask);

            DisplayModeAudit = displayTask.Result;
            GameDvrAudit = gameDvrTask.Result;
            HardwareScheduling = hwSchedTask.Result;
        }
        catch (Exception ex)
        {
            DisplayModeAudit = new DisplayModeAudit { StatusText = $"Load failed: {ex.Message}" };
        }
        finally
        {
            IsLoadingDisplayAudit = false;
        }
    }

    /// <summary>#249: Start button - see VBlankJitterService's own remarks for why Stop has bounded
    /// (not instant) latency.</summary>
    private void StartVBlank()
    {
        if (IsMeasuringVBlank) return;
        _vblank.Start();
        IsMeasuringVBlank = true;
        VBlankSnapshot = _vblank.GetSnapshot();
    }

    private void StopVBlank()
    {
        if (!IsMeasuringVBlank) return;
        _vblank.Stop();
        IsMeasuringVBlank = false;
        VBlankSnapshot = _vblank.GetSnapshot();
    }

    /// <summary>#250: Start button - resets the session and kicks off a background loop of short
    /// present/frame-time captures until Stop is pressed, mirroring StartMeasurementAsync/
    /// MeasureLoopAsync above.</summary>
    private async Task StartPresentMonitorAsync()
    {
        if (IsMeasuringPresent || !PresentToolsAvailable) return;

        _presentMonitor.ResetSession();
        PresentAppRows.Clear();
        GpuStallRows.Clear();
        FrameTimeScatterPoints.Clear();
        InputToPresentMs = null;
        PresentStatusText = "Starting present/frame-time capture...";
        OnPropertyChanged(nameof(HeadlinePresentRow));

        _presentCts = new CancellationTokenSource();
        IsMeasuringPresent = true;
        _presentLoopTask = PresentLoopAsync(_presentCts.Token);
        await Task.CompletedTask;
    }

    private async Task PresentLoopAsync(CancellationToken ct)
    {
        var window = TimeSpan.FromSeconds(3);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // #258: snapshot the last raw-input arrival *before* this sample's window runs, so
                // the pairing below only matches a frame that could plausibly follow it.
                DateTime? lastInputUtc = _inputLatency.LastEventUtc;

                var (ok, message, parsed) = await _presentMonitor.SampleOnceAsync(window, ct);
                PresentStatusText = message;

                if (ok)
                {
                    var rows = _presentMonitor.BuildAppRows();
                    PresentAppRows.Clear();
                    foreach (var r in rows) PresentAppRows.Add(r);
                    OnPropertyChanged(nameof(HeadlinePresentRow));

                    GpuStallRows.Clear();
                    foreach (var g in _presentMonitor.GpuStalls) GpuStallRows.Add(g);

                    var headline = rows.Count > 0 ? rows[0] : null;
                    if (headline is not null)
                    {
                        var samples = _presentMonitor.GetFrameTimesMs(headline.Pid, 300);
                        FrameTimeScatterPoints.Clear();
                        for (int i = 0; i < samples.Count; i++) FrameTimeScatterPoints.Add(new ObservablePoint(i, samples[i]));

                        // #258: only meaningful while the input-latency probe (#256) is also
                        // running and has actually seen an event - null otherwise, which the view
                        // hides regardless (bound off IsMeasuringPresent), so this just stays
                        // unavailable rather than showing a stale number.
                        InputToPresentMs = lastInputUtc is { } inputUtc
                            ? _presentMonitor.EstimateInputToPresentMs(inputUtc, headline.Pid)
                            : null;
                    }
                    else
                    {
                        FrameTimeScatterPoints.Clear();
                        InputToPresentMs = null;
                    }
                }

                if (parsed == 0 && !ok)
                {
                    // A hard failure (tools missing, access denied, provider unavailable) won't fix
                    // itself by retrying - stop rather than spin forever on the same error.
                    await Application.Current.Dispatcher.InvokeAsync(StopPresentMonitor);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on Stop
        }
    }

    private void StopPresentMonitor()
    {
        if (!IsMeasuringPresent) return;
        _presentCts?.Cancel();
        IsMeasuringPresent = false;
        PresentStatusText = "Stopped.";
    }

    /// <summary>#256: Start button.</summary>
    private void StartInputLatency()
    {
        if (IsMeasuringInput) return;
        _inputLatency.Start();
        IsMeasuringInput = _inputLatency.IsRunning;
        InputLatencySnapshot = _inputLatency.GetSnapshot();
    }

    private void StopInputLatency()
    {
        if (!IsMeasuringInput) return;
        _inputLatency.Stop();
        IsMeasuringInput = false;
        InputLatencySnapshot = _inputLatency.GetSnapshot();
    }

    private void RebuildDriverRows()
    {
        DriverDpcRows.Clear();
        foreach (var row in _dpc.BuildDriverDpcRows(Enrich, DeviceFor)) DriverDpcRows.Add(row);
    }

    private void RebuildIsrRows()
    {
        DriverIsrRows.Clear();
        foreach (var row in _dpc.BuildDriverIsrRows(DeviceFor)) DriverIsrRows.Add(row);
    }

    /// <summary>#211/#212: joins a bare driver filename to its identity metadata plus the small
    /// known-offender hint table - passed into DpcLatencyService.BuildDriverDpcRows so both live
    /// entirely in the row-building step rather than being re-derived per binding.</summary>
    private (string? Hint, DriverIdentityInfo? Identity) Enrich(string driverName)
    {
        _driverIdentities.TryGetValue(driverName, out var identity);
        return (KnownOffenderDriverLookup.Hint(driverName), identity);
    }

    /// <summary>#216: best-effort driver-file -> device-name join - see
    /// DeviceInterruptAttributionService's remarks. Null (blank in the UI) on a miss, never guessed.</summary>
    private string? DeviceFor(string driverName) =>
        _driverDeviceMap.TryGetValue(driverName, out var device) ? device : null;

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
        _probeTimer.Stop();
        _schedulerTimer.Stop();
        _measureCts?.Cancel();
        _presentCts?.Cancel();
        _vblank.Dispose();
        _inputLatency.Dispose();
        _perCore.Dispose();
        _hungWindows.Dispose();
    }
}
