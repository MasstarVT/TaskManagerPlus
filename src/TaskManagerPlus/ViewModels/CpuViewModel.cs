using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>One NUMA node's worth of cores, for the CPU tab's per-core grid.</summary>
public sealed class CoreGroup
{
    public int NumaNode { get; init; }
    public IReadOnlyList<CoreUsage> Cores { get; init; } = Array.Empty<CoreUsage>();
}

/// <summary>#630: one NUMA node's average effective-vs-requested frequency gap (percentage
/// points), aggregated from its member cores' CoreUsage.FrequencyGapPoints. Null when none of the
/// group's cores reported a gap this tick.</summary>
public sealed class CoreGroupFrequencyGap
{
    public int NumaNode { get; init; }
    public double? GapPoints { get; init; }
}

/// <summary>
/// Backs the CPU tab. Deliberately owns no HardwareMonitorService of its own - it's a thin
/// composition over the single shared PerformanceViewModel sampler (same pattern as
/// SummaryViewModel), since CPU/Memory/Storage/Network all come from one
/// HardwareMonitorService.Sample() call per tick. Splitting each into its own sampler would
/// mean redundant PerformanceCounter instantiation for identical underlying data. It does own
/// one small timer of its own (#9's thermal-throttle flag), since that's a composite reading
/// across two other view-models (Performance and EnergyThermals) ticking on different intervals -
/// the same "cheap, no I/O of its own" tradeoff SummaryViewModel's Health Check timer makes.
/// </summary>
public sealed class CpuViewModel : ObservableObject, IDisposable
{
    private readonly EnergyThermalsViewModel _energyThermals;
    private readonly ProcessesViewModel _processes;
    private readonly ResponsivenessViewModel _responsiveness;
    private readonly DispatcherTimer _throttleTimer;
    private bool _affinityRefreshInFlight;

    // #233: "large share of residency" in the deepest package C-state (C3 - the deepest tier this
    // app reads) for the deep-idle-exit-latency flag below.
    private const double DeepCStateThresholdPercent = 40.0;

    // Matches DpcTimeToBrushConverter's own amber threshold (#202) - the task's own framing for
    // "elevated" DPC latency.
    private const double ElevatedDpcThresholdUs = 250.0;

    // "Repeatedly", not a one-off blip - a few consecutive 2s throttle-timer ticks (~6s) of both
    // conditions holding at once before the flag raises.
    private const int SustainedTicksRequired = 3;
    private int _deepIdleLatencyStreak;

    // #629: clock-stretching microbenchmark - its own slow timer (not the 2s throttle tick), since
    // it briefly pins the whole process to one core (see ClockStretchService's remarks).
    private readonly DispatcherTimer _clockStretchTimer;
    private bool _clockStretchInFlight;

    public PerformanceViewModel Performance { get; }

    /// <summary>#608/#603: exposes the shared EnergyThermalsViewModel instance so CpuView.xaml can
    /// bind directly to its thermal-headroom/thermal-zone/firmware-event properties (e.g.
    /// EnergyThermals.CpuThermalHeadroomC) without new cross-ViewModel plumbing - the same
    /// "expose the composed instance as a public property" shape Performance above already uses.</summary>
    public EnergyThermalsViewModel EnergyThermals => _energyThermals;

    /// <summary>Round 8 #25/#28/#29/#30: static CPU identification readouts (microcode,
    /// mitigation override status, instruction-set support, cache sizes) - queried once in the
    /// constructor via CpuFeatureService, not per tick, since none of it changes at runtime.</summary>
    private CpuFeatureInfo _features = new();
    public CpuFeatureInfo Features { get => _features; private set => SetProperty(ref _features, value); }

    /// <summary>Round 8 #24: best-effort core-affinity heatmap for the current top-CPU processes -
    /// see CoreAffinityService's remarks for why this is framed as "preferred/ideal core", not a
    /// live trace of actual scheduling. Refreshed on the same 2s cadence as the throttle flag,
    /// off the UI thread since it walks native per-thread calls.</summary>
    public ObservableCollection<CoreAffinityCell> CoreAffinity { get; } = new();

    /// <summary>
    /// Best-effort "is the CPU thermal-throttling right now" flag (#9) - true only when the CPU
    /// is both running hot (LibreHardwareMonitorLib package temp) AND meaningfully below its
    /// rated base clock (PerformanceViewModel.CpuVsBasePercent) while under real load. This is a
    /// heuristic, not a verified fact: Windows/LibreHardwareMonitorLib expose no "throttle reason"
    /// API on most consumer hardware (that's vendor-proprietary MSR data HWiNFO reads directly),
    /// so a CPU that's simply idle-clocked, or throttling for a non-thermal reason (power/current
    /// limit), won't necessarily show this flag correctly - the same "quick visual flag, not a
    /// verdict" tradeoff the process signature check and driver-date filtering already document.
    /// </summary>
    private bool _isThrottling;
    public bool IsThrottling { get => _isThrottling; private set => SetProperty(ref _isThrottling, value); }

    private string _throttleText = string.Empty;
    public string ThrottleText { get => _throttleText; private set => SetProperty(ref _throttleText, value); }

    /// <summary>
    /// Best-effort "the CPU looks power-limited rather than thermal-limited" flag (#35) - true
    /// when the package is pinned at (or very near) its own highest power draw seen this session
    /// while running meaningfully below base clock under load, but is NOT also reading hot -
    /// distinct from IsThrottling above, which requires a hot package temp. The practical
    /// difference matters for troubleshooting: a power ceiling points at a PSU/motherboard power
    /// limit or a vendor-set PL1/PL2 cap, not the cooler. Same heuristic tier as IsThrottling -
    /// this app has no access to the vendor-proprietary "limit reason" MSR data HWiNFO reads
    /// directly, so this is inferred from two independently-ticking view-models, not a verified fact.
    /// </summary>
    private bool _isPowerLimited;
    public bool IsPowerLimited { get => _isPowerLimited; private set => SetProperty(ref _isPowerLimited, value); }

    private string _powerLimitText = string.Empty;
    public string PowerLimitText { get => _powerLimitText; private set => SetProperty(ref _powerLimitText, value); }

    // #628: refines IsPowerLimited above from a bare "at ceiling" flag into "clamped at the
    // ceiling for N minutes" - tracks how long IsPowerLimited has been continuously true and folds
    // that duration into PowerLimitText itself once it's been sustained for at least a minute
    // (a one-tick blip shouldn't read the same as a real, sustained clamp).
    private DateTime? _powerClampStartedAt;

    // ---- #627: PL1/PL2/tau inference from a package-power dwell histogram ----------------------
    // Built only from samples taken while under sustained load (CPU >= 70%) - Intel-style PL1/PL2
    // configurations produce two distinct plateaus in that histogram (a high short-duration
    // ceiling settling to a lower sustained one). Explicitly reported as "inferred, not read from
    // MSRs" - the real registers need a kernel driver this app deliberately does not ship.
    private const double SustainedLoadPercentThreshold = 70.0;
    private const double PowerBucketWidthW = 1.0;
    private const int PowerHistogramWindowMinutes = 20;
    private DateTime? _powerHistogramLoadStartedAt;
    private readonly List<(DateTime Time, double PowerW)> _powerHistogramSamples = new();
    private readonly Dictionary<int, int> _powerHistogramBuckets = new();

    public ObservableCollection<TurboHistogramBucket> PowerDwellHistogram { get; } = new();

    private double? _inferredPl2W;
    public double? InferredPl2W { get => _inferredPl2W; private set => SetProperty(ref _inferredPl2W, value); }

    private double? _inferredPl1W;
    public double? InferredPl1W { get => _inferredPl1W; private set => SetProperty(ref _inferredPl1W, value); }

    private double? _inferredTauSeconds;
    public double? InferredTauSeconds { get => _inferredTauSeconds; private set => SetProperty(ref _inferredTauSeconds, value); }

    private string _powerPlateauText = "Not enough sustained-load samples yet to infer PL1/PL2 plateaus.";
    public string PowerPlateauText { get => _powerPlateauText; private set => SetProperty(ref _powerPlateauText, value); }

    // ---- #629: clock-stretching detector ----------------------------------------------------------
    // Compares a tiny fixed-work microbenchmark's achieved ops/sec (ClockStretchService) against
    // the reported effective clock - clock stretching (common under AMD electrical limits and on
    // some laptops) shows a normal reported frequency with reduced real throughput, a discrepancy
    // nothing else in this app can see.
    private double _bestOpsPerMhz;
    private int _lowClockStretchStreak;

    private double? _clockStretchPercent;
    public double? ClockStretchPercent { get => _clockStretchPercent; private set => SetProperty(ref _clockStretchPercent, value); }

    private string _clockStretchText = "Not measured yet this session.";
    public string ClockStretchText { get => _clockStretchText; private set => SetProperty(ref _clockStretchText, value); }

    private bool _clockStretchDetected;
    public bool ClockStretchDetected { get => _clockStretchDetected; private set => SetProperty(ref _clockStretchDetected, value); }

    // ---- #630: effective- vs. requested-frequency gap, per core group ----------------------------
    // Performance.CpuFrequencyGapPercent/CpuFrequencyGapHistory carry the session-wide aggregate
    // and trend chart; this adds the per-NUMA-node breakdown on top of the same CoreGroups this
    // view-model already builds for the per-core grid.
    public ObservableCollection<CoreGroupFrequencyGap> FrequencyGapByGroup { get; } = new();

    // ---- #631: core parking and frequency-floor misconfiguration checklist -----------------------
    public ObservableCollection<string> ProcessorPowerChecklist { get; } = new();
    public AsyncRelayCommand LoadProcessorPowerSettingsCommand { get; }
    private ProcessorPowerSettings _processorPowerSettings = new();

    private string _processorPowerSettingsStatusText = "Not checked yet - click \"Check power settings\" (runs powercfg /qh).";
    public string ProcessorPowerSettingsStatusText { get => _processorPowerSettingsStatusText; private set => SetProperty(ref _processorPowerSettingsStatusText, value); }

    // ---- #634: boost residency relative to rated clocks ------------------------------------------
    // Reuses Performance.TurboHistogram's existing "Below base" bucket (CpuVsBasePercent < 0)
    // rather than a second histogram - this adds the rated-clock reference readout and the
    // heat-correlation interpretation that bucket alone doesn't carry: long dwell below base
    // without heat is a power-plan/firmware limit, long dwell below base with heat confirms
    // thermal throttling.
    private long _belowBaseSamples;
    private long _belowBaseHotSamples;

    private string _boostResidencyText = "Not enough samples yet this session.";
    public string BoostResidencyText { get => _boostResidencyText; private set => SetProperty(ref _boostResidencyText, value); }

    // ---- #635: silicon-behavior snapshot for before/after comparison ------------------------------
    public ObservableCollection<SiliconSnapshot> SiliconSnapshots { get; } = new();
    public RelayCommand SnapshotCurrentBehaviorCommand { get; }

    private string _siliconSnapshotStatusText = string.Empty;
    public string SiliconSnapshotStatusText { get => _siliconSnapshotStatusText; private set => SetProperty(ref _siliconSnapshotStatusText, value); }

    /// <summary>
    /// #84/#603: throttle-reason breakdown - now a full Thermal/Power/Firmware/Core-parked/None
    /// classification (ThrottleClassificationService.Classify, shared with
    /// EnergyThermalsViewModel's own episode tracking, #604) rather than just the two-way
    /// Thermal/Power/None readout this was before. Still exactly as reliable as before for the
    /// Thermal/Power/Core-parked cases - this app has no access to the vendor-proprietary "limit
    /// reason" MSR data HWiNFO reads directly - except Firmware, which is corroborated by an
    /// authoritative Windows event (#602) rather than a pattern match.
    /// </summary>
    private ThrottleReasonClass _currentThrottleClass = ThrottleReasonClass.None;
    public ThrottleReasonClass CurrentThrottleClass { get => _currentThrottleClass; private set => SetProperty(ref _currentThrottleClass, value); }

    public string ThrottleReason => CurrentThrottleClass switch
    {
        ThrottleReasonClass.Thermal => "Thermal",
        ThrottleReasonClass.Power => "Power",
        ThrottleReasonClass.Firmware => "Firmware",
        ThrottleReasonClass.CoreParked => "Core-parked",
        _ => "None",
    };

    // #603: dwell time per reason class, accumulated across the session on this view-model's own
    // 2s timer, presented as a single stacked bar ("what is actually holding my clocks back")
    // rather than just the instantaneous flags above.
    private readonly Dictionary<ThrottleReasonClass, double> _dwellSeconds = new()
    {
        [ThrottleReasonClass.None] = 0,
        [ThrottleReasonClass.Thermal] = 0,
        [ThrottleReasonClass.Power] = 0,
        [ThrottleReasonClass.Firmware] = 0,
        [ThrottleReasonClass.CoreParked] = 0,
    };
    private DateTime _lastDwellTick = DateTime.Now;

    public ObservableCollection<double> NoneDwellShare { get; } = new() { 100 };
    public ObservableCollection<double> ThermalDwellShare { get; } = new() { 0 };
    public ObservableCollection<double> PowerDwellShare { get; } = new() { 0 };
    public ObservableCollection<double> FirmwareDwellShare { get; } = new() { 0 };
    public ObservableCollection<double> CoreParkedDwellShare { get; } = new() { 0 };
    public ISeries[] ThrottleDwellSeries { get; }
    public Axis[] ThrottleDwellXAxes { get; }
    public Axis[] ThrottleDwellYAxes { get; }

    private static readonly SKColor AxisTextColor = new(0x9A, 0x9A, 0xA2);
    private static readonly SKColor AxisSeparatorColor = new(0x33, 0x33, 0x3A, 160);

    /// <summary>
    /// #233: extends the existing C-state residency display (#83) with a heuristic flag - when the
    /// CPU is spending a large share of its idle time in the deepest package C-state this app reads
    /// (C3) AND the Responsiveness tab's worst observed DPC latency is also elevated (above the same
    /// 250us amber threshold DpcTimeToBrushConverter uses, #202) for several consecutive ticks in a
    /// row (not a one-off blip), flags that the deep-idle exit itself may be adding to that latency -
    /// waking from a deep C-state measurably takes longer than from a shallow one or C0, so a driver
    /// that looks "slow" in the DPC-by-driver table might really just be paying a wake-up-latency
    /// tax. "Quick flag, not a verdict" - same heuristic tier as IsThrottling/IsPowerLimited above.
    /// Always false when CStatesAvailable is false (older CPU/Windows generation), same as the
    /// residency display itself hides in that case.
    /// </summary>
    private bool _deepIdleExitLatencySuspected;
    public bool DeepIdleExitLatencySuspected { get => _deepIdleExitLatencySuspected; private set => SetProperty(ref _deepIdleExitLatencySuspected, value); }

    private string _deepIdleExitLatencyText = string.Empty;
    public string DeepIdleExitLatencyText { get => _deepIdleExitLatencyText; private set => SetProperty(ref _deepIdleExitLatencyText, value); }

    /// <summary>
    /// #267: on P-core/E-core (hybrid) systems, flags when the foreground app's threads are
    /// running predominantly on E-cores - based on each thread's preferred (ideal) processor
    /// (GetThreadIdealProcessorEx, the exact same API/honesty caveat #24's core-affinity heatmap
    /// above already uses - "preferred core", not a live trace of which core a thread is actually
    /// executing on this instant), classified P/E via Performance.Cores (already-known topology,
    /// no second CpuTopologyService query needed). A plain text flag next to the existing heatmap
    /// rather than a full heatmap P/E-tint overlay - a documented simplification (see CpuView.xaml)
    /// since surgically overlaying the existing heatmap cells would be a much larger XAML change
    /// for the same underlying signal. Links to #266 (EcoQoS) as the likely reason via plain text,
    /// since EcoQoS-throttled work is exactly what Windows steers onto E-cores.
    /// </summary>
    private string _hybridMisplacementText = string.Empty;
    public string HybridMisplacementText { get => _hybridMisplacementText; private set => SetProperty(ref _hybridMisplacementText, value); }

    private bool _hybridMisplacementSuspected;
    public bool HybridMisplacementSuspected { get => _hybridMisplacementSuspected; private set => SetProperty(ref _hybridMisplacementSuspected, value); }

    /// <summary>Share of the foreground app's threads that must be on E-cores before this flags -
    /// "predominantly", not "any at all" (a hybrid scheduler routinely puts a few background
    /// threads on E-cores even for a foreground app, which is normal and not worth flagging).</summary>
    private const double EcoreMisplacementThreshold = 0.6;

    /// <summary>Pass-through: true only on genuinely hybrid CPUs. The view should hide the
    /// P-core/E-core color distinction entirely when this is false.</summary>
    public bool HasHybridTopology => Performance.HasHybridTopology;

    /// <summary>Pass-through: true only when the system has more than one NUMA node. The view
    /// should render a single flat core grid (no group headers) when this is false.</summary>
    public bool HasMultipleNumaNodes => Performance.HasMultipleNumaNodes;

    /// <summary>
    /// Performance.Cores grouped by NUMA node, each group rendered as its own nested
    /// WrapPanel-hosted ItemsControl in the view. Built manually here (rather than via
    /// ICollectionView.GroupDescriptions + ItemsControl.GroupStyle) - NUMA node is static per
    /// core, so this only needs to rebuild when the Cores collection itself is replaced
    /// (core-count change), not per tick, and a plain nested ItemsControl avoids depending on
    /// WPF's grouping/GroupStyle machinery correctly detecting an externally-grouped
    /// CollectionView, which turned out not to lay out as a wrapping grid in practice.
    /// </summary>
    public ObservableCollection<CoreGroup> CoreGroups { get; } = new();

    public CpuViewModel(PerformanceViewModel performance, EnergyThermalsViewModel energyThermals, ProcessesViewModel processes, ResponsivenessViewModel responsiveness)
    {
        Performance = performance;
        _energyThermals = energyThermals;
        _processes = processes;
        _responsiveness = responsiveness;
        performance.Cores.CollectionChanged += OnCoresCollectionChanged;
        RebuildGroups();

        // #603: single-category stacked bar - one "Session" column, five stacked segments (one
        // per reason class) sized by that class's share of dwell time so far. StackedColumnSeries
        // instances sharing no explicit StackGroup stack together by default.
        ThrottleDwellXAxes = new[]
        {
            new Axis { Labels = new[] { "Session" }, LabelsPaint = new SolidColorPaint(AxisTextColor), SeparatorsPaint = null },
        };
        ThrottleDwellYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0, MaxLimit = 100,
                Labeler = v => $"{v:0}%",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };
        ThrottleDwellSeries = new ISeries[]
        {
            new StackedColumnSeries<double> { Values = NoneDwellShare, Name = "None", Fill = new SolidColorPaint(SKColors.Gray.WithAlpha(130)), Stroke = null, MaxBarWidth = 70 },
            new StackedColumnSeries<double> { Values = ThermalDwellShare, Name = "Thermal", Fill = new SolidColorPaint(SKColors.OrangeRed), Stroke = null, MaxBarWidth = 70 },
            new StackedColumnSeries<double> { Values = PowerDwellShare, Name = "Power", Fill = new SolidColorPaint(SKColors.Goldenrod), Stroke = null, MaxBarWidth = 70 },
            new StackedColumnSeries<double> { Values = FirmwareDwellShare, Name = "Firmware", Fill = new SolidColorPaint(SKColors.MediumPurple), Stroke = null, MaxBarWidth = 70 },
            new StackedColumnSeries<double> { Values = CoreParkedDwellShare, Name = "Core-parked", Fill = new SolidColorPaint(SKColors.DeepSkyBlue), Stroke = null, MaxBarWidth = 70 },
        };

        _throttleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _throttleTimer.Tick += (_, _) => { RefreshThrottleStatus(); RefreshDeepIdleLatencyFlag(); _ = RefreshCoreAffinityAsync(); _ = RefreshHybridMisplacementFlagAsync(); };
        _throttleTimer.Start();
        _lastDwellTick = DateTime.Now;
        RefreshThrottleStatus();
        RefreshDeepIdleLatencyFlag();

        // #25/#28/#29/#30: static, so read once in the background rather than adding this to the
        // per-tick timer above.
        _ = Task.Run(() =>
        {
            var features = CpuFeatureService.Read();
            System.Windows.Application.Current?.Dispatcher.Invoke(() => Features = features);
        });

        // #629: a slow, infrequent timer (not the 2s throttle tick) - the microbenchmark briefly
        // pins the whole process to one core (see ClockStretchService's remarks), so it can't run
        // as often as the rest of this view-model's per-tick work without being disruptive.
        _clockStretchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(45) };
        _clockStretchTimer.Tick += (_, _) => _ = RunClockStretchBenchmarkAsync();
        _clockStretchTimer.Start();

        // #631: live "cores parked under load" line populates immediately; the powercfg-derived
        // lines fill in once the user clicks "Check power settings" below.
        LoadProcessorPowerSettingsCommand = new AsyncRelayCommand(LoadProcessorPowerSettingsAsync);
        RebuildProcessorPowerChecklist();

        // #635: whatever was captured in earlier sessions, newest first.
        foreach (var s in SiliconSnapshotService.Load().OrderByDescending(s => s.Timestamp))
            SiliconSnapshots.Add(s);
        SnapshotCurrentBehaviorCommand = new RelayCommand(CaptureSiliconSnapshot);
    }

    /// <summary>#24: samples the current top-few CPU processes' threads' ideal processors and
    /// rebuilds the per-core heatmap. Guarded against overlap - the native per-thread scan can
    /// legitimately take longer than 2s on a very busy system, and this must never stack calls.</summary>
    private async Task RefreshCoreAffinityAsync()
    {
        if (_affinityRefreshInFlight) return;
        _affinityRefreshInFlight = true;
        try
        {
            var topPids = _processes.Processes.OrderByDescending(p => p.CpuPercent).Take(6).Select(p => p.Pid).ToList();
            int logicalCount = Performance.Cores.Count;

            var cells = await Task.Run(() =>
            {
                var procs = new List<System.Diagnostics.Process>();
                foreach (var pid in topPids)
                {
                    try { procs.Add(System.Diagnostics.Process.GetProcessById(pid)); }
                    catch { /* process exited between the snapshot above and here - skip it */ }
                }

                try
                {
                    var byCore = CoreAffinityService.ComputeIdealProcessorLoad(procs);
                    var list = new List<CoreAffinityCell>(logicalCount);
                    for (int i = 0; i < logicalCount; i++)
                    {
                        var entries = byCore.TryGetValue(i, out var e) ? e : new List<(string ProcessName, int Pid)>();
                        list.Add(new CoreAffinityCell
                        {
                            CoreIndex = i,
                            ProcessCount = entries.Count,
                            ProcessNames = entries.Count == 0 ? string.Empty : string.Join(", ", entries.Select(x => x.ProcessName).Distinct()),
                        });
                    }
                    return list;
                }
                finally
                {
                    foreach (var p in procs)
                    {
                        try { p.Dispose(); } catch { /* best-effort */ }
                    }
                }
            });

            CoreAffinity.Clear();
            foreach (var c in cells) CoreAffinity.Add(c);
        }
        finally
        {
            _affinityRefreshInFlight = false;
        }
    }

    private bool _hybridMisplacementRefreshInFlight;

    /// <summary>#267: see HybridMisplacementText's remarks. Guarded against overlap the same way
    /// RefreshCoreAffinityAsync above is - GetThreadIdealProcessorEx-per-thread can legitimately
    /// take a moment on a very thread-heavy foreground app.</summary>
    private async Task RefreshHybridMisplacementFlagAsync()
    {
        if (!HasHybridTopology)
        {
            HybridMisplacementSuspected = false;
            HybridMisplacementText = string.Empty;
            return;
        }
        if (_hybridMisplacementRefreshInFlight) return;
        _hybridMisplacementRefreshInFlight = true;
        try
        {
            int fgPid = ForegroundContextService.GetForegroundProcessId();
            if (fgPid <= 0)
            {
                HybridMisplacementSuspected = false;
                HybridMisplacementText = string.Empty;
                return;
            }

            // Snapshot P/E classification per core index on the UI thread before handing off to
            // Task.Run below - Performance.Cores is an ObservableCollection mutated on the UI
            // thread each Performance tick, so it isn't safe to enumerate concurrently from a
            // background thread (the same reasoning RefreshCoreAffinityAsync above already follows
            // by capturing logicalCount, a plain int, rather than touching Cores itself off-thread).
            var isPCoreByIndex = Performance.Cores.ToDictionary(c => c.Index, c => c.IsPCore);

            var result = await Task.Run<(string Name, int Total, int OnEcores)?>(() =>
            {
                System.Diagnostics.Process? proc = null;
                try { proc = System.Diagnostics.Process.GetProcessById(fgPid); }
                catch { return null; }

                try
                {
                    var idealCores = CoreAffinityService.ComputeIdealProcessorsFor(proc);
                    if (idealCores.Count == 0) return null;

                    int onEcores = 0;
                    foreach (var coreIndex in idealCores)
                    {
                        if (isPCoreByIndex.TryGetValue(coreIndex, out bool isP) && !isP) onEcores++;
                    }
                    return (proc.ProcessName, idealCores.Count, onEcores);
                }
                finally
                {
                    proc.Dispose();
                }
            });

            if (result is not { } r || r.Total == 0)
            {
                HybridMisplacementSuspected = false;
                HybridMisplacementText = string.Empty;
                return;
            }

            double share = (double)r.OnEcores / r.Total;
            bool suspected = share >= EcoreMisplacementThreshold;
            HybridMisplacementSuspected = suspected;
            HybridMisplacementText = suspected
                ? $"Foreground app \"{r.Name}\" has {share:P0} of its threads preferring E-cores — check the Processes tab's EcoQoS column (#266): Windows' power-throttling classification is the most likely reason. Based on preferred (ideal) processor, not a live trace."
                : string.Empty;
        }
        catch
        {
            // Best-effort - a failed read just leaves the flag at its last known value.
        }
        finally
        {
            _hybridMisplacementRefreshInFlight = false;
        }
    }

    private void RefreshThrottleStatus()
    {
        var temp = _energyThermals.CpuPackageTempC;
        bool hot = temp is { } t && t >= 85;
        bool highLoad = Performance.CpuCurrentPercent >= 60;
        bool belowBase = Performance.CpuVsBasePercent <= -8;

        IsThrottling = hot && highLoad && belowBase;
        ThrottleText = IsThrottling
            ? $"{temp:0}°C and {Performance.CpuVsBasePercent:0}% vs. base clock under load"
            : string.Empty;

        // #35/#628: pinned at (within 2%) its own session-high power draw, below base clock, under
        // load, but NOT also reading hot - the "power ceiling, not thermal ceiling" signature.
        var power = _energyThermals.TotalPackagePowerW;
        var powerMax = _energyThermals.PowerSessionMaxW;
        bool atPowerCeiling = power is { } p && powerMax is { } max && max > 0 && p >= max * 0.98;
        IsPowerLimited = !IsThrottling && !hot && highLoad && belowBase && atPowerCeiling;

        // #628: refines the flag above from a bare "at ceiling" instant into "clamped at the
        // ceiling for N minutes" once it's been continuously true for a while - a one-tick blip
        // shouldn't read the same as a sustained clamp.
        if (IsPowerLimited)
        {
            _powerClampStartedAt ??= DateTime.Now;
            var clampDwell = DateTime.Now - _powerClampStartedAt.Value;
            PowerLimitText = clampDwell.TotalSeconds >= 60
                ? $"{power:0.#} W (session high {powerMax:0.#} W) and {Performance.CpuVsBasePercent:0}% vs. base clock under load - clamped at the power ceiling for {FormatClampDuration(clampDwell)}"
                : $"{power:0.#} W (session high {powerMax:0.#} W) and {Performance.CpuVsBasePercent:0}% vs. base clock under load";
        }
        else
        {
            _powerClampStartedAt = null;
            PowerLimitText = string.Empty;
        }

        // #627: PL1/PL2/tau inference from a sustained-load power dwell histogram.
        TrackPowerDwellHistogram();

        // #630: per-NUMA-node effective-vs-requested frequency gap breakdown.
        RefreshFrequencyGapGroups();

        // #631: keeps the live "cores parked under load" line current every tick without needing
        // a powercfg re-check.
        RebuildProcessorPowerChecklist();

        // #634: rated-clock reference + heat-correlation interpretation for the existing
        // turbo-boost histogram's "Below base" bucket.
        UpdateBoostResidencyText();

        // #603: full reason-class verdict, using the shared classifier so this agrees with
        // EnergyThermalsViewModel's own episode-tracking classification (#604/#601/#602) - pulls
        // in the thermal-zone throttle% (#601) and firmware-limit-active snapshot (#602)
        // EnergyThermalsViewModel already owns, plus this view-model's own Performance reference
        // for parked-core count.
        var zoneThrottlePercents = _energyThermals.ThermalZones.Where(z => z.ThrottlePercent.HasValue).Select(z => z.ThrottlePercent!.Value).ToList();
        double? maxZoneThrottle = zoneThrottlePercents.Count > 0 ? zoneThrottlePercents.Max() : null;
        CurrentThrottleClass = ThrottleClassificationService.Classify(
            temp, Performance.CpuCurrentPercent, Performance.CpuVsBasePercent,
            power, powerMax, Performance.ParkedCoreCount, Performance.Cores.Count,
            maxZoneThrottle, _energyThermals.FirmwareLimitActive);

        // #603: accumulate dwell time per class since this view-model was constructed.
        var now = DateTime.Now;
        double elapsed = Math.Max(0, (now - _lastDwellTick).TotalSeconds);
        _lastDwellTick = now;
        _dwellSeconds[CurrentThrottleClass] += elapsed;
        UpdateDwellShares();

        OnPropertyChanged(nameof(ThrottleReason));
    }

    private void RefreshDeepIdleLatencyFlag()
    {
        bool eligible = Performance.CStatesAvailable
            && Performance.CpuC3Percent >= DeepCStateThresholdPercent
            && _responsiveness.HighestDpcUs >= ElevatedDpcThresholdUs;

        _deepIdleLatencyStreak = eligible ? _deepIdleLatencyStreak + 1 : 0;

        bool suspected = _deepIdleLatencyStreak >= SustainedTicksRequired;
        DeepIdleExitLatencySuspected = suspected;
        DeepIdleExitLatencyText = suspected
            ? $"Deep idle exit may be adding latency: {Performance.CpuC3Percent:0}% C3 residency alongside a {_responsiveness.HighestDpcUs:0} µs worst DPC — try testing with minimum processor state at 100% to rule this out. Quick flag, not a verdict."
            : string.Empty;
    }

    /// <summary>#603: recomputes each reason class's share of total dwell time (0-100) and pushes
    /// the new values into the stacked-bar series' backing collections.</summary>
    private void UpdateDwellShares()
    {
        double total = _dwellSeconds.Values.Sum();
        if (total <= 0) return;

        NoneDwellShare[0] = Math.Round(_dwellSeconds[ThrottleReasonClass.None] / total * 100, 1);
        ThermalDwellShare[0] = Math.Round(_dwellSeconds[ThrottleReasonClass.Thermal] / total * 100, 1);
        PowerDwellShare[0] = Math.Round(_dwellSeconds[ThrottleReasonClass.Power] / total * 100, 1);
        FirmwareDwellShare[0] = Math.Round(_dwellSeconds[ThrottleReasonClass.Firmware] / total * 100, 1);
        CoreParkedDwellShare[0] = Math.Round(_dwellSeconds[ThrottleReasonClass.CoreParked] / total * 100, 1);
    }

    private static string FormatClampDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{(int)span.TotalMinutes}m {span.Seconds}s";

    // ================================================================================
    // #627: PL1/PL2/tau inference from a package-power dwell histogram
    // ================================================================================

    /// <summary>Accumulates one bucketed power sample per tick while under sustained load (CPU
    /// &gt;= 70%), then re-infers the PL1/PL2 plateaus from the accumulated histogram. See the
    /// property block's remarks for why this is explicitly labeled "inferred, not read from MSRs".</summary>
    private void TrackPowerDwellHistogram()
    {
        bool sustained = Performance.CpuCurrentPercent >= SustainedLoadPercentThreshold;
        if (!sustained)
        {
            _powerHistogramLoadStartedAt = null;
            return;
        }
        _powerHistogramLoadStartedAt ??= DateTime.Now;

        if (_energyThermals.TotalPackagePowerW is not { } p || p <= 0) return;

        var now = DateTime.Now;
        _powerHistogramSamples.Add((now, p));
        var cutoff = now.AddMinutes(-PowerHistogramWindowMinutes);
        _powerHistogramSamples.RemoveAll(s => s.Time < cutoff);

        int bucket = (int)Math.Round(p / PowerBucketWidthW);
        _powerHistogramBuckets[bucket] = _powerHistogramBuckets.GetValueOrDefault(bucket) + 1;

        RefreshPowerPlateauInference();
    }

    /// <summary>Infers PL1 (the single most-populated bucket - the sustained/steady-state draw)
    /// and PL2 (the highest-power bucket that's both meaningfully populated and meaningfully above
    /// PL1 - a genuine second plateau, not noise around the same steady-state value), plus tau (how
    /// long the most recent sustained-load run took to settle from PL2 down to within 5% of
    /// PL1).</summary>
    private void RefreshPowerPlateauInference()
    {
        int totalSamples = _powerHistogramBuckets.Values.Sum();
        if (totalSamples < 30 || _powerHistogramBuckets.Count < 2)
        {
            PowerPlateauText = "Not enough sustained-load samples yet to infer PL1/PL2 plateaus.";
            InferredPl1W = null;
            InferredPl2W = null;
            InferredTauSeconds = null;
            RebuildPowerHistogramChart();
            return;
        }

        var pl1Bucket = _powerHistogramBuckets.OrderByDescending(kv => kv.Value).First();
        double pl1 = pl1Bucket.Key * PowerBucketWidthW;

        var pl2Candidate = _powerHistogramBuckets
            .Where(kv => kv.Value >= Math.Max(3, totalSamples * 0.03) && kv.Key * PowerBucketWidthW >= pl1 * 1.05)
            .OrderByDescending(kv => kv.Key)
            .FirstOrDefault();

        InferredPl1W = pl1;

        if (pl2Candidate.Value == 0)
        {
            InferredPl2W = null;
            InferredTauSeconds = null;
            PowerPlateauText = $"No distinct short-duration ceiling observed - sustained draw has settled around {pl1:0.#} W (inferred, not read from MSRs).";
            RebuildPowerHistogramChart();
            return;
        }

        double pl2 = pl2Candidate.Key * PowerBucketWidthW;
        InferredPl2W = pl2;

        double? tau = null;
        if (_powerHistogramLoadStartedAt is { } loadStart)
        {
            var runSamples = _powerHistogramSamples.Where(s => s.Time >= loadStart).OrderBy(s => s.Time).ToList();
            var settleSample = runSamples.FirstOrDefault(s => s.PowerW <= pl1 * 1.05);
            if (settleSample.Time != default) tau = (settleSample.Time - loadStart).TotalSeconds;
        }
        InferredTauSeconds = tau;

        PowerPlateauText = tau is { } t
            ? $"Inferred PL2 {pl2:0.#} W settling to PL1 {pl1:0.#} W over about {t:0} s (tau) - inferred from observed behavior, not read from MSRs (the real registers need a kernel driver this app deliberately does not ship)."
            : $"Inferred PL2 {pl2:0.#} W, PL1 {pl1:0.#} W - inferred from observed behavior, not read from MSRs (the real registers need a kernel driver this app deliberately does not ship).";

        RebuildPowerHistogramChart();
    }

    /// <summary>Rebuilds the displayed histogram bar list from the accumulated bucket counts,
    /// capped to the 20 most-visited power levels (in watt-wide buckets) so a machine whose power
    /// wanders continuously across a wide range doesn't produce an unbounded list.</summary>
    private void RebuildPowerHistogramChart()
    {
        int total = _powerHistogramBuckets.Values.Sum();
        PowerDwellHistogram.Clear();
        if (total == 0) return;

        foreach (var kv in _powerHistogramBuckets.OrderByDescending(kv => kv.Value).Take(20).OrderBy(kv => kv.Key))
        {
            PowerDwellHistogram.Add(new TurboHistogramBucket
            {
                Label = $"{kv.Key * PowerBucketWidthW:0}W",
                Percent = Math.Round(kv.Value / (double)total * 100.0, 1),
            });
        }
    }

    // ================================================================================
    // #629: clock-stretching detector
    // ================================================================================

    /// <summary>Runs the fixed-work microbenchmark off the UI thread and compares its achieved
    /// ops/sec (normalized by reported effective MHz) against the best such reading seen this
    /// session. Guarded against overlap the same way RefreshCoreAffinityAsync is.</summary>
    private async Task RunClockStretchBenchmarkAsync()
    {
        if (_clockStretchInFlight) return;
        _clockStretchInFlight = true;
        try
        {
            double mhzBefore = Performance.CpuCurrentClockGhz * 1000.0;
            double? opsPerSec = await Task.Run(() => ClockStretchService.RunMicrobenchmarkOpsPerSecond());
            double mhzAfter = Performance.CpuCurrentClockGhz * 1000.0;
            double reportedMhz = (mhzBefore + mhzAfter) / 2.0;

            if (opsPerSec is not { } ops || reportedMhz <= 0)
            {
                ClockStretchText = "Couldn't measure this pass (benchmark or clock reading unavailable).";
                return;
            }

            double opsPerMhz = ops / reportedMhz;
            _bestOpsPerMhz = Math.Max(_bestOpsPerMhz, opsPerMhz);
            double percent = _bestOpsPerMhz > 0 ? Math.Round(opsPerMhz / _bestOpsPerMhz * 100.0, 0) : 100;
            ClockStretchPercent = percent;

            _lowClockStretchStreak = percent < 85 ? _lowClockStretchStreak + 1 : 0;
            ClockStretchDetected = _lowClockStretchStreak >= 2;

            ClockStretchText = ClockStretchDetected
                ? $"Effective work per MHz: {percent:0}% of this session's best - sustained below baseline, consistent with clock stretching (common under AMD electrical limits or on some laptops). Quick flag - a background scheduler collision on the pinned core can also lower this reading."
                : $"Effective work per MHz: {percent:0}% of this session's best.";
        }
        finally
        {
            _clockStretchInFlight = false;
        }
    }

    // ================================================================================
    // #630: effective- vs. requested-frequency gap, per core group
    // ================================================================================

    private void RefreshFrequencyGapGroups()
    {
        FrequencyGapByGroup.Clear();
        if (!Performance.CpuFrequencyGapAvailable) return;

        foreach (var group in CoreGroups)
        {
            var values = group.Cores.Where(c => c.FrequencyGapPoints.HasValue).Select(c => c.FrequencyGapPoints!.Value).ToList();
            FrequencyGapByGroup.Add(new CoreGroupFrequencyGap
            {
                NumaNode = group.NumaNode,
                GapPoints = values.Count > 0 ? Math.Round(values.Average(), 1) : null,
            });
        }
    }

    // ================================================================================
    // #631: core parking and frequency-floor misconfiguration checklist
    // ================================================================================

    /// <summary>On-demand `powercfg /qh` read - see PowerPlanService.ReadProcessorPowerSettingsAsync's
    /// remarks.</summary>
    private async Task LoadProcessorPowerSettingsAsync()
    {
        ProcessorPowerSettingsStatusText = "Checking...";
        _processorPowerSettings = await PowerPlanService.ReadProcessorPowerSettingsAsync();
        RebuildProcessorPowerChecklist();
        ProcessorPowerSettingsStatusText = string.Empty;
    }

    /// <summary>Rebuilds the checklist from whatever's known right now - the live "cores parked
    /// under load" line (always current, called every 2s tick) plus whatever powercfg-derived
    /// lines are available (Unknown/omitted until the user checks power settings at least once).</summary>
    private void RebuildProcessorPowerChecklist()
    {
        ProcessorPowerChecklist.Clear();

        bool parkedUnderLoad = Performance.ParkedCoreCount > 0 && Performance.CpuCurrentPercent >= 60;
        ProcessorPowerChecklist.Add(parkedUnderLoad
            ? $"⚠ {Performance.ParkedCoreCount} core(s) are parked right now while overall CPU load is {Performance.CpuCurrentPercent:0}% - Windows may be leaving performance on the table under this load."
            : Performance.ParkedCoreCount > 0
                ? $"{Performance.ParkedCoreCount} core(s) currently parked (normal at low load - power saving)."
                : "No cores currently parked.");

        var settings = _processorPowerSettings;
        if (settings.MinProcessorStateAcPercent is { } minAc)
        {
            ProcessorPowerChecklist.Add(minAc >= 90
                ? $"⚠ Minimum processor state (plugged in) is {minAc}% - the CPU is being held near full clock even at idle, which runs a laptop hot and burns power for no benefit at idle."
                : $"Minimum processor state (plugged in): {minAc}%.");
        }
        if (settings.MinProcessorStateDcPercent is { } minDc)
        {
            ProcessorPowerChecklist.Add(minDc >= 90
                ? $"⚠ Minimum processor state (on battery) is {minDc}% - same idle-heat/battery-drain concern as above, but on battery."
                : $"Minimum processor state (on battery): {minDc}%.");
        }
        if (settings.MaxProcessorStateAcPercent is { } maxAc)
        {
            ProcessorPowerChecklist.Add(maxAc < 100
                ? $"⚠ Maximum processor state (plugged in) is capped at {maxAc}% - the CPU can never reach its full rated clock on this plan."
                : $"Maximum processor state (plugged in): {maxAc}%.");
        }
        if (settings.CoreParkingMinCoresAcPercent is { } parkMinAc)
        {
            int approxCores = (int)Math.Round(Performance.Cores.Count * parkMinAc / 100.0);
            ProcessorPowerChecklist.Add($"Core-parking minimum cores (plugged in): {parkMinAc}% (~{approxCores} of {Performance.Cores.Count} logical cores always kept unparked).");
        }
    }

    // ================================================================================
    // #634: boost residency relative to rated clocks
    // ================================================================================

    /// <summary>See the property's remarks above - reuses Performance.TurboHistogram's existing
    /// "Below base" bucket rather than a second histogram.</summary>
    private void UpdateBoostResidencyText()
    {
        if (Performance.CpuBaseClockGhz <= 0) return;

        if (Performance.CpuVsBasePercent < 0)
        {
            _belowBaseSamples++;
            if (_energyThermals.CpuPackageTempC is { } t && t >= 80) _belowBaseHotSamples++;
        }

        double belowBasePercent = Performance.TurboHistogram.Count > 0 ? Performance.TurboHistogram[0].Percent : 0;
        if (_belowBaseSamples < 5 || belowBasePercent < 2)
        {
            BoostResidencyText = $"Rated base clock: {Performance.CpuBaseClockGhz:0.00} GHz.";
            return;
        }

        double hotShare = _belowBaseSamples > 0 ? _belowBaseHotSamples / (double)_belowBaseSamples * 100.0 : 0;
        string cause = hotShare >= 50
            ? "mostly while running hot - consistent with thermal throttling"
            : "mostly without running hot - consistent with a power-plan or firmware limit rather than heat";
        BoostResidencyText = $"Rated base clock: {Performance.CpuBaseClockGhz:0.00} GHz. {belowBasePercent:0.#}% of this session was spent below it, {cause}.";
    }

    // ================================================================================
    // #635: silicon-behavior snapshot for before/after comparison
    // ================================================================================

    private void CaptureSiliconSnapshot()
    {
        double totalDwell = _dwellSeconds.Values.Sum();
        double throttledDwell = _dwellSeconds[ThrottleReasonClass.Thermal] + _dwellSeconds[ThrottleReasonClass.Power] +
            _dwellSeconds[ThrottleReasonClass.Firmware] + _dwellSeconds[ThrottleReasonClass.CoreParked];

        var snapshot = new SiliconSnapshot
        {
            Timestamp = DateTime.Now,
            ClockGhz = Performance.CpuCurrentClockGhz > 0 ? Performance.CpuCurrentClockGhz : null,
            VcoreV = _energyThermals.VcoreLoadPoints.Count > 0 ? _energyThermals.VcoreLoadPoints[^1].Y : null,
            PackagePowerW = _energyThermals.TotalPackagePowerW,
            TempC = _energyThermals.CpuPackageTempC,
            ThrottlePercent = totalDwell > 0 ? Math.Round(throttledDwell / totalDwell * 100.0, 1) : null,
        };

        SiliconSnapshotService.Append(snapshot);
        SiliconSnapshots.Insert(0, snapshot);
        SiliconSnapshotStatusText = $"Captured snapshot at {snapshot.Timestamp:t}.";
    }

    /// <summary>Repaints the dwell-breakdown chart's axis text/gridlines to match the active theme
    /// family - see PerformanceViewModel.ApplyAxisTheme's remarks; same SkiaSharp-outside-WPF-
    /// resources gap.</summary>
    public void ApplyAxisTheme(System.Windows.Media.Color text, System.Windows.Media.Color separator)
    {
        var textSk = new SKColor(text.R, text.G, text.B);
        var sepSk = new SKColor(separator.R, separator.G, separator.B, separator.A);
        ThrottleDwellXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        ThrottleDwellYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        ThrottleDwellYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
    }


    private void OnCoresCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Per-tick updates only mutate existing CoreUsage.Percent in place (no Add/Remove), so
        // this only fires on the initial populate or an actual core-count change - cheap to
        // rebuild fully rather than trying to patch the grouping incrementally.
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove
            or NotifyCollectionChangedAction.Reset)
        {
            RebuildGroups();
        }
    }

    private void RebuildGroups()
    {
        CoreGroups.Clear();
        foreach (var group in Performance.Cores.GroupBy(c => c.NumaNode).OrderBy(g => g.Key))
            CoreGroups.Add(new CoreGroup { NumaNode = group.Key, Cores = group.OrderBy(c => c.Index).ToList() });
    }

    public void Dispose()
    {
        _throttleTimer.Stop();
        _clockStretchTimer.Stop();
    }
}
